//! Keep automatic suspend from interrupting a build.
#[cfg(target_os = "linux")]
pub(super) struct BuildInhibitor(std::process::Child);

#[cfg(target_os = "linux")]
impl BuildInhibitor {
    pub(super) fn acquire() -> Option<Self> {
        use std::process::{Command, Stdio};
        // Closing the pipe also releases the inhibitor if the launcher exits.
        Command::new("systemd-inhibit")
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
        let _ = self.0.wait();
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
