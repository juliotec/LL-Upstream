//! The two Start menu entries target the same executable. Windows distinguishes them by AppUserModelID.
use super::{beside, windows::same_file};
use anyhow::{Context, Result, ensure};
use std::{
    env,
    ffi::OsString,
    fs,
    os::windows::ffi::OsStringExt,
    path::{Path, PathBuf},
};
use windows::{
    Win32::{
        Foundation::RPC_E_CHANGED_MODE,
        Storage::EnhancedStorage::PKEY_AppUserModel_ID,
        System::Com::{
            CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
            CoUninitialize, IPersistFile, STGM_READ, StructuredStorage::PROPVARIANT,
        },
        System::Variant::VT_LPWSTR,
        UI::Shell::{
            FOLDERID_Desktop, FOLDERID_Programs, IShellLinkW, PropertiesSystem::IPropertyStore,
            SHStrDupW, ShellLink,
        },
    },
    core::{HSTRING, Interface},
};

pub(super) const LAUNCHER_ID: &str = "MagicBean.LongshotLauncher";
const GAME_ID: &str = "MagicBean.Longshot";

fn with_link<T>(task: impl FnOnce(IShellLinkW) -> windows::core::Result<T>) -> Result<T> {
    // A successful CoInitializeEx is balanced after the shell interfaces drop. An existing
    // apartment of a different type belongs to our caller and can also create a ShellLink.
    unsafe {
        let initialized = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        if initialized != RPC_E_CHANGED_MODE {
            initialized.ok()?;
        }
        let result = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).and_then(task);
        if initialized.is_ok() {
            CoUninitialize();
        }
        result.context("Windows could not access the shortcut.")
    }
}

pub(super) fn write(
    path: &Path,
    target: &Path,
    args: &str,
    description: &str,
    app_id: &str,
) -> Result<()> {
    fs::create_dir_all(path.parent().context("Missing shortcut directory.")?)?;
    let file = HSTRING::from(path.as_os_str());
    let folder = HSTRING::from(
        target
            .parent()
            .context("Missing launcher directory.")?
            .as_os_str(),
    );
    let exe = HSTRING::from(target.as_os_str());
    with_link(|shell| unsafe {
        shell.SetPath(&exe)?;
        shell.SetWorkingDirectory(&folder)?;
        shell.SetArguments(&HSTRING::from(args))?;
        shell.SetDescription(&HSTRING::from(description))?;
        shell.SetIconLocation(&exe, 0)?;
        let store: IPropertyStore = shell.cast()?;
        // PKEY_AppUserModel_ID requires VT_LPWSTR. SHStrDupW uses the allocator
        // that PROPVARIANT's destructor releases after the shell copies the value.
        let mut id = PROPVARIANT::default();
        let value = &mut *id.Anonymous.Anonymous;
        value.Anonymous.pwszVal = SHStrDupW(&HSTRING::from(app_id))?;
        value.vt = VT_LPWSTR;
        store.SetValue(&PKEY_AppUserModel_ID, &id)?;
        store.Commit()?;
        shell.cast::<IPersistFile>()?.Save(&file, true)
    })
}

fn game_link(desktop: bool) -> Result<PathBuf> {
    let id = if desktop {
        &FOLDERID_Desktop
    } else {
        &FOLDERID_Programs
    };
    // The Shell resolves redirected desktop and Start menu folders (including OneDrive).
    Ok(crate::home::known_folder(id)?.join("Longshot.lnk"))
}

pub(super) fn owns(path: &Path, exe: &Path) -> Result<bool> {
    let file = HSTRING::from(path.as_os_str());
    let target = with_link(|shell| unsafe {
        shell.cast::<IPersistFile>()?.Load(&file, STGM_READ)?;
        let mut buf = vec![0; 32_768];
        shell.GetPath(&mut buf, std::ptr::null_mut(), 4)?; // SLGP_RAWPATH
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        Ok(PathBuf::from(OsString::from_wide(&buf[..len])))
    })?;
    // Windows tracking can follow the previous launcher when it moves to .old during the swap.
    Ok(same_file(&target, exe) || same_file(&target, &beside(exe, ".old")))
}

fn change_game(path: &Path, exe: &Path, enabled: bool) -> Result<()> {
    if path.try_exists()? {
        ensure!(
            owns(path, exe)?,
            "The existing shortcut belongs to another launcher."
        );
        if !enabled {
            fs::remove_file(path)?;
        }
    }
    if enabled {
        write(path, exe, "--play", "Play Longshot", GAME_ID)?;
    }
    Ok(())
}

pub fn game_shortcut(desktop: bool, enabled: bool) -> Result<()> {
    change_game(&game_link(desktop)?, &env::current_exe()?, enabled)
}

pub fn has_game_shortcut(desktop: bool) -> bool {
    (|| -> Result<bool> {
        let path = game_link(desktop)?;
        Ok(path.is_file() && owns(&path, &env::current_exe()?)?)
    })()
    .unwrap_or(false)
}

pub(super) fn refresh_game(exe: &Path, keep: bool) -> Result<()> {
    for desktop in [true, false] {
        let path = game_link(desktop)?;
        if path.is_file() && owns(&path, exe).unwrap_or(false) {
            if keep {
                write(&path, exe, "--play", "Play Longshot", GAME_ID)?;
            } else {
                fs::remove_file(path)?;
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_our_game_link_is_changed() -> Result<()> {
        let dir = tempfile::tempdir()?;
        let file = dir.path().join("Longshot.lnk");
        let exe = env::current_exe()?;
        change_game(&file, &exe, true)?;
        assert!(owns(&file, &exe)?);
        let other = dir.path().join("Another launcher.exe");
        fs::write(&other, b"fixture")?;
        assert!(change_game(&file, &other, false).is_err());
        change_game(&file, &exe, false)?;
        assert!(!file.exists());
        Ok(())
    }
}
