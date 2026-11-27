//! Keep launcher-owned Windows builds and all their children in one job.
use std::{
    io,
    os::windows::{
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
        process::CommandExt,
    },
    process::{Child, Command},
    ptr,
};
use windows_sys::Win32::{
    Foundation::{ERROR_NO_MORE_FILES, HANDLE, INVALID_HANDLE_VALUE},
    System::{
        Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, TH32CS_SNAPTHREAD, THREADENTRY32, Thread32First, Thread32Next,
        },
        JobObjects::{
            AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
            SetInformationJobObject, TerminateJobObject,
        },
        Threading::{
            CREATE_NO_WINDOW, CREATE_SUSPENDED, OpenThread, ResumeThread, THREAD_SUSPEND_RESUME,
        },
    },
};

pub(in crate::game) struct ChildJob(OwnedHandle);

impl ChildJob {
    pub(in crate::game) fn spawn(
        command: &mut Command,
        kill_on_close: bool,
    ) -> io::Result<(Self, Child)> {
        // An unnamed job with a non-inheritable handle held only by the launcher.
        let job = Self(owned(unsafe {
            CreateJobObjectW(ptr::null(), ptr::null())
        })?);
        if kill_on_close {
            let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            // SAFETY: both the handle and the initialized limit structure live through the call.
            if unsafe {
                SetInformationJobObject(
                    job.0.as_raw_handle(),
                    JobObjectExtendedLimitInformation,
                    (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                    size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
                )
            } == 0
            {
                return Err(io::Error::last_os_error());
            }
        }

        // The builder cannot spawn a retrying shader worker before it belongs to the job.
        let mut child = command
            .creation_flags(CREATE_NO_WINDOW | CREATE_SUSPENDED)
            .spawn()?;
        if let Err(error) = job.start(&child) {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
        Ok((job, child))
    }

    pub(in crate::game) fn terminate(&self) -> io::Result<()> {
        // SAFETY: the job handle is owned for the duration of the call.
        if unsafe { TerminateJobObject(self.0.as_raw_handle(), 1) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    fn start(&self, child: &Child) -> io::Result<()> {
        // SAFETY: both handles are owned for this entire call.
        if unsafe { AssignProcessToJobObject(self.0.as_raw_handle(), child.as_raw_handle()) } == 0 {
            return Err(io::Error::last_os_error());
        }
        // std cannot expose the primary thread handle on our pinned Rust. The
        // suspended process has not run application code, so find its initial thread.
        let thread = initial_thread(child.id())?;
        // SAFETY: the thread handle is open and has THREAD_SUSPEND_RESUME access.
        if unsafe { ResumeThread(thread.as_raw_handle()) } == u32::MAX {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }
}

fn owned(handle: HANDLE) -> io::Result<OwnedHandle> {
    if handle.is_null() || handle == INVALID_HANDLE_VALUE {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: callers transfer ownership of a fresh Win32 handle.
    Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
}

fn initial_thread(pid: u32) -> io::Result<OwnedHandle> {
    let snapshot = owned(unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) })?;
    let mut entry = THREADENTRY32 {
        dwSize: size_of::<THREADENTRY32>() as u32,
        ..Default::default()
    };
    // SAFETY: the snapshot is live and entry is a writable THREADENTRY32.
    let mut found = unsafe { Thread32First(snapshot.as_raw_handle(), &mut entry) };
    while found != 0 {
        if entry.th32OwnerProcessID == pid {
            return owned(unsafe { OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID) });
        }
        entry.dwSize = size_of::<THREADENTRY32>() as u32;
        found = unsafe { Thread32Next(snapshot.as_raw_handle(), &mut entry) };
    }
    let error = io::Error::last_os_error();
    if error.raw_os_error() == Some(ERROR_NO_MORE_FILES as i32) {
        Err(io::Error::new(
            io::ErrorKind::NotFound,
            "The suspended builder has no initial thread",
        ))
    } else {
        Err(error)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        env, fs,
        path::Path,
        thread,
        time::{Duration, Instant},
    };
    use windows_sys::Win32::{
        Foundation::{WAIT_OBJECT_0, WAIT_TIMEOUT},
        System::Threading::{OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject},
    };

    fn fixture(phase: &str, marker: &Path) -> io::Result<Command> {
        let mut command = Command::new(env::current_exe()?);
        let (_, module) = module_path!().split_once("::").expect("nested test module");
        command
            .arg("--exact")
            .arg(format!("{module}::job_fixture"))
            .arg("--ignored")
            .env("LONGSHOT_JOB_TEST_PHASE", phase)
            .env("LONGSHOT_JOB_TEST_MARKER", marker);
        Ok(command)
    }

    #[test]
    #[ignore = "started by the Windows job test"]
    fn job_fixture() -> io::Result<()> {
        let phase = env::var_os("LONGSHOT_JOB_TEST_PHASE").expect("fixture phase");
        let marker = env::var_os("LONGSHOT_JOB_TEST_MARKER").expect("fixture marker");
        if phase == "builder" {
            let mut worker = fixture("shader", Path::new(&marker))?.spawn()?;
            fs::write(marker, worker.id().to_le_bytes())?;
            worker.wait()?;
        } else {
            thread::sleep(Duration::from_secs(30));
        }
        Ok(())
    }

    #[test]
    fn closing_the_job_stops_the_builder_and_its_shader_worker() -> io::Result<()> {
        let temp = tempfile::tempdir()?;
        let marker = temp.path().join("shader-pid");
        let (job, child) = ChildJob::spawn(&mut fixture("builder", &marker)?, true)?;
        let deadline = Instant::now() + Duration::from_secs(10);
        let pid = loop {
            match fs::read(&marker) {
                Ok(data) if data.len() == 4 => break u32::from_le_bytes(data.try_into().unwrap()),
                Ok(_) => {}
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(error),
            }
            assert!(
                Instant::now() < deadline,
                "the builder did not start a shader worker"
            );
            thread::sleep(Duration::from_millis(10));
        };
        // Keep a handle to the exact worker so a reused process ID cannot pass the test.
        let shader = owned(unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) })?;
        assert_eq!(
            unsafe { WaitForSingleObject(shader.as_raw_handle(), 0) },
            WAIT_TIMEOUT
        );
        drop(job);
        for handle in [child.as_raw_handle(), shader.as_raw_handle()] {
            assert_eq!(unsafe { WaitForSingleObject(handle, 5_000) }, WAIT_OBJECT_0);
        }
        Ok(())
    }
}
