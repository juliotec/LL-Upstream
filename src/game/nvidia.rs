//! NVIDIA's Windows driver reads shader-cache capacity from its global DRS profile.
//! Linux builds let Longshot choose its own per-process cache size.
use anyhow::Result;
use std::ffi::OsString;

#[cfg(any(windows, test))]
fn enabled_capacity(enabled: Option<u32>, mb: Option<u32>) -> Option<u32> {
    mb.filter(|_| enabled.is_some_and(|v| v != 0))
}

#[cfg(any(windows, test))]
fn needs_room(mb: &u32) -> bool {
    (1..4096).contains(mb)
}

pub fn handle_nvidia_cache_argument(args: &[OsString]) -> Result<bool> {
    if args != [OsString::from("--raise-nvidia-shader-cache")] {
        return Ok(false);
    }
    #[cfg(windows)]
    {
        windows::adjust()?;
        Ok(true)
    }
    #[cfg(not(windows))]
    {
        anyhow::bail!("This driver setting is available on Windows.")
    }
}

#[cfg(windows)]
pub(super) mod windows {
    use super::{enabled_capacity, needs_room};
    use anyhow::{Context as _, Result, ensure};
    use libloading::os::windows::{LOAD_LIBRARY_SEARCH_SYSTEM32, Library};
    use slint::ComponentHandle;
    use std::{ffi::c_void, ptr};

    type Handle = *mut c_void;
    type Query = unsafe extern "C" fn(u32) -> Handle;
    type Init = unsafe extern "C" fn() -> i32;
    type Open = unsafe extern "C" fn(*mut Handle) -> i32;
    type SessionOp = unsafe extern "C" fn(Handle) -> i32;
    type Global = unsafe extern "C" fn(Handle, *mut Handle) -> i32;
    type Get = unsafe extern "C" fn(Handle, Handle, u32, *mut Setting) -> i32;
    type Set = unsafe extern "C" fn(Handle, Handle, *mut Setting) -> i32;
    type Name = unsafe extern "C" fn(u32, *mut u16) -> i32;

    const CACHE_ENABLED: u32 = 0x00198fff;
    const CACHE_SIZE: u32 = 0x00ac8497;
    const NO_SETTING: i32 = -160;
    const NEEDS_ADMIN: i32 = -137;
    const TARGET_MB: u32 = 10240;

    // NVDRS_SETTING_V1 has 4-byte alignment and two 4100-byte value unions.
    // All offsets are DWORD indices into the published NVIDIA ABI.
    #[repr(transparent)]
    struct Setting([u32; 12320 / 4]);
    impl Setting {
        const ID: usize = 4100 / 4;
        const CURRENT: usize = 8220 / 4;
        fn new() -> Self {
            let mut s = Self([0; 3080]);
            s.0[0] = (1 << 16) | std::mem::size_of::<Self>() as u32;
            s
        }
    }

    struct Driver {
        _library: Library,
        unload: Init,
        open: Open,
        destroy: SessionOp,
        load: SessionOp,
        save: SessionOp,
        global: Global,
        get: Get,
        set: Set,
        name: Name,
    }
    impl Driver {
        fn load() -> Option<Self> {
            // Load only the driver from System32. QueryInterface returns the C function with
            // each ID's published NVAPI signature (nvapi_interface.h). Driver owns the DLL.
            unsafe {
                let lib =
                    Library::load_with_flags("nvapi64.dll", LOAD_LIBRARY_SEARCH_SYSTEM32).ok()?;
                let query: Query = *lib.get::<Query>(b"nvapi_QueryInterface\0").ok()?;
                macro_rules! function {
                    ($id:literal, $signature:ty) => {{
                        let address = query($id);
                        if address.is_null() {
                            return None;
                        }
                        std::mem::transmute::<Handle, $signature>(address)
                    }};
                }
                let init = function!(0x0150e828, Init);
                let unload = function!(0xd22bdd7e, Init);
                Some(Self {
                    open: function!(0x0694d52e, Open),
                    destroy: function!(0xdad9cff8, SessionOp),
                    load: function!(0x375dbd6b, SessionOp),
                    save: function!(0xfcbc7e14, SessionOp),
                    global: function!(0x617bff9f, Global),
                    get: function!(0x73bf8338, Get),
                    set: function!(0x577dd202, Set),
                    name: function!(0xd61cbe6e, Name),
                    unload,
                    _library: {
                        if init() != 0 {
                            return None;
                        }
                        lib
                    },
                })
            }
        }
    }

    impl Drop for Driver {
        fn drop(&mut self) {
            // Balance this handle's initialization before releasing the DLL.
            unsafe {
                (self.unload)();
            }
        }
    }

    #[derive(Debug)]
    struct DriverError(i32);

    impl std::fmt::Display for DriverError {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            write!(f, "NVIDIA's driver returned status {}.", self.0)
        }
    }

    impl std::error::Error for DriverError {}

    fn checked(status: i32) -> Result<()> {
        if status != 0 {
            return Err(DriverError(status).into());
        }
        Ok(())
    }

    struct Session<'a> {
        api: &'a Driver,
        handle: Handle,
    }
    impl Drop for Session<'_> {
        fn drop(&mut self) {
            // The handle was created by the same live driver.
            unsafe {
                (self.api.destroy)(self.handle);
            }
        }
    }

    impl Session<'_> {
        fn dword(&self, profile: Handle, id: u32, default: u32) -> Result<Option<u32>> {
            let mut setting = Setting::new();
            // This is a live DRS session and a V1 buffer with its size and version set.
            let status = unsafe { (self.api.get)(self.handle, profile, id, &mut setting) };
            if status == NO_SETTING {
                let mut name = [0u16; 2048];
                let known = unsafe { (self.api.name)(id, name.as_mut_ptr()) } == 0;
                return Ok(known.then_some(default));
            }
            checked(status)?;
            Ok(Some(setting.0[Setting::CURRENT]))
        }
    }

    fn with_profile<T>(
        operation: impl FnOnce(&Session<'_>, Handle) -> Result<T>,
    ) -> Result<Option<T>> {
        let Some(api) = Driver::load() else {
            return Ok(None);
        };
        let mut handle = ptr::null_mut();
        // Session owns the handle until the profile operation completes.
        unsafe {
            checked((api.open)(&mut handle))?;
        }
        let session = Session { api: &api, handle };
        let mut profile = ptr::null_mut();
        unsafe {
            checked((api.load)(handle))?;
            checked((api.global)(handle, &mut profile))?;
        }
        operation(&session, profile).map(Some)
    }

    fn cache_size(session: &Session<'_>, profile: Handle) -> Result<Option<u32>> {
        Ok(enabled_capacity(
            session.dword(profile, CACHE_ENABLED, 1)?,
            session.dword(profile, CACHE_SIZE, 1024)?,
        ))
    }

    fn probe() -> Result<Option<u32>> {
        Ok(with_profile(cache_size)?.flatten())
    }

    pub fn adjust() -> Result<bool> {
        let changed = with_profile(|session, profile| {
            if cache_size(session, profile)?.filter(needs_room).is_none() {
                return Ok(false);
            }
            let mut setting = Setting::new();
            setting.0[Setting::ID] = CACHE_SIZE;
            setting.0[Setting::CURRENT] = TARGET_MB;
            unsafe {
                checked((session.api.set)(session.handle, profile, &mut setting))?;
                checked((session.api.save)(session.handle))?;
            }
            Ok(true)
        })?;
        Ok(changed.unwrap_or(false))
    }

    // Only elevate the dedicated helper if an explicit request was refused for insufficient privileges.
    fn raise() -> Result<()> {
        match adjust() {
            Err(e)
                if e.downcast_ref::<DriverError>()
                    .is_some_and(|e| e.0 == NEEDS_ADMIN) =>
            {
                request_elevation()
            }
            result => result.map(|_| ()),
        }
    }

    fn request_elevation() -> Result<()> {
        use std::os::windows::{
            ffi::OsStrExt,
            io::{AsRawHandle, FromRawHandle, OwnedHandle},
        };
        use windows_sys::Win32::{
            Foundation::WAIT_OBJECT_0,
            System::Threading::{GetExitCodeProcess, INFINITE, WaitForSingleObject},
            UI::{
                Shell::{
                    SEE_MASK_NOASYNC, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW,
                },
                WindowsAndMessaging::SW_SHOWNORMAL,
            },
        };
        let exe: Vec<_> = std::env::current_exe()?
            .as_os_str()
            .encode_wide()
            .chain([0])
            .collect();
        let verb: Vec<_> = "runas\0".encode_utf16().collect();
        let arg: Vec<_> = "--raise-nvidia-shader-cache\0".encode_utf16().collect();
        let mut info = SHELLEXECUTEINFOW {
            cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
            fMask: SEE_MASK_NOASYNC | SEE_MASK_NOCLOSEPROCESS,
            lpVerb: verb.as_ptr(),
            lpFile: exe.as_ptr(),
            lpParameters: arg.as_ptr(),
            nShow: SW_SHOWNORMAL,
            ..Default::default()
        };
        // ShellExecuteExW transfers this process handle to us. Wait on the worker thread
        // so the UI stays responsive while UAC or the driver is processing the request.
        unsafe {
            if ShellExecuteExW(&mut info) == 0 {
                return Err(std::io::Error::last_os_error())
                    .context("Windows did not complete the NVIDIA shader cache request.");
            }
            ensure!(
                !info.hProcess.is_null(),
                "Windows did not return the driver helper process."
            );
            let child = OwnedHandle::from_raw_handle(info.hProcess);
            ensure!(
                WaitForSingleObject(child.as_raw_handle(), INFINITE) == WAIT_OBJECT_0,
                "Windows could not wait for the driver helper."
            );
            let mut code = 0;
            ensure!(
                GetExitCodeProcess(child.as_raw_handle(), &mut code) != 0 && code == 0,
                "The driver helper could not raise NVIDIA's shader cache."
            );
        }
        Ok(())
    }

    pub(crate) fn bind(ui: &crate::MainWindow) {
        let weak = ui.as_weak();
        ui.global::<crate::Game>().on_check_nvidia_cache(move || {
            if let Some(ui) = weak.upgrade() {
                refresh(&ui, false);
            }
        });
        let weak = ui.as_weak();
        ui.global::<crate::Game>().on_raise_nvidia_cache(move || {
            if let Some(ui) = weak.upgrade() {
                refresh(&ui, true);
            }
        });
    }

    fn refresh(ui: &crate::MainWindow, change: bool) {
        let game = ui.global::<crate::Game>();
        if game.get_nvidia_cache_busy() || (change && game.get_nvidia_cache_mb() == 0) {
            return;
        }
        game.set_nvidia_cache_busy(true);
        let weak = ui.as_weak();
        std::thread::spawn(move || {
            let update = if change { raise() } else { Ok(()) };
            let capacity = probe();
            let _ = weak.upgrade_in_event_loop(move |ui| {
                let game = ui.global::<crate::Game>();
                game.set_nvidia_cache_busy(false);
                let low = capacity.as_ref().ok().copied().flatten().filter(needs_room);
                game.set_nvidia_cache_mb(low.unwrap_or(0) as i32);
                if change {
                    let message = match update.and(capacity) {
                        Ok(Some(TARGET_MB)) => {
                            "NVIDIA’s global shader cache is now 10 GB.".to_owned()
                        }
                        Ok(Some(size)) if needs_room(&size) => {
                            format!("NVIDIA’s driver still reports a {size} MB shader cache.")
                        }
                        Ok(Some(0 | u32::MAX) | None) => {
                            "NVIDIA’s driver no longer reports a limited shader cache.".to_owned()
                        }
                        Ok(Some(size)) => {
                            format!("NVIDIA’s driver reports a {size} MB shader cache.")
                        }
                        Err(e) => format!("{e:#}"),
                    };
                    ui.set_notice(message.into());
                }
            });
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn respects_disabled_unknown_unlimited_and_larger_caches() {
        for (enabled, size) in [
            (Some(0), Some(1024)),
            (None, Some(1024)),
            (Some(1), None),
            (Some(1), Some(0)),
            (Some(1), Some(u32::MAX)),
            (Some(1), Some(4096)),
        ] {
            assert!(enabled_capacity(enabled, size).filter(needs_room).is_none());
        }
        assert_eq!(
            enabled_capacity(Some(1), Some(1024)).filter(needs_room),
            Some(1024)
        );
    }
}
