//! Keep automatic suspend from interrupting a build.
#[cfg(target_os = "linux")]
pub(super) struct BuildInhibitor(std::process::Child);

#[cfg(target_os = "linux")]
impl BuildInhibitor {
    pub(super) fn acquire() -> Option<Self> {
        use std::process::{Command, Stdio};
        // Closing the pipe also releases the inhibitor if the launcher exits.
        let mut command = Command::new("systemd-inhibit");
        super::process::restore_host_environment(&mut command);
        command
            .args([
                "--what=idle:sleep",
                "--mode=block",
                "--who=Longshot Launcher",
                "--why=Building Longshot",
                "cat",
            ])
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .ok()
            .map(Self)
    }
}

#[cfg(target_os = "linux")]
impl Drop for BuildInhibitor {
    fn drop(&mut self) {
        drop(self.0.stdin.take());
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
        while matches!(self.0.try_wait(), Ok(None)) && std::time::Instant::now() < deadline {
            std::thread::sleep(std::time::Duration::from_millis(25));
        }
        if matches!(self.0.try_wait(), Ok(None)) {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }
}

#[cfg(windows)]
pub(super) struct BuildInhibitor;

#[cfg(windows)]
impl BuildInhibitor {
    pub(super) fn acquire() -> Option<Self> {
        use windows_sys::Win32::System::Power::{
            ES_CONTINUOUS, ES_SYSTEM_REQUIRED, SetThreadExecutionState,
        };
        // This flag is thread-local and is cleared by Drop on the same build thread.
        let state = unsafe { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) };
        (state != 0).then_some(Self)
    }
}

#[cfg(windows)]
impl Drop for BuildInhibitor {
    fn drop(&mut self) {
        use windows_sys::Win32::System::Power::{ES_CONTINUOUS, SetThreadExecutionState};
        unsafe { SetThreadExecutionState(ES_CONTINUOUS) };
    }
}

#[cfg(not(any(windows, target_os = "linux")))]
pub(super) struct BuildInhibitor;

#[cfg(not(any(windows, target_os = "linux")))]
impl BuildInhibitor {
    pub(super) fn acquire() -> Option<Self> {
        None
    }
}
