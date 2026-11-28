//! Per-user setup and removal. Only the installed launcher owns its uninstall entry.
use super::shortcuts;
use super::{beside, own_tag};
use crate::SetupWindow;
use anyhow::{Context, Result, ensure};
use slint::ComponentHandle;
use std::{
    cell::Cell,
    env,
    ffi::OsString,
    fs, io,
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::Command,
    rc::Rc,
};
use tempfile::NamedTempFile;
use windows::{Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID, core::HSTRING};
use winreg::{RegKey, enums::HKEY_CURRENT_USER};

const DISPLAY: &str = "Longshot Launcher";
const ENTRY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\LongshotLauncher";
const NO_WINDOW: u32 = 0x08000000;

struct Places {
    exe: PathBuf,
    link: PathBuf,
}
impl Places {
    fn resolve() -> Result<Self> {
        Ok(Self {
            exe: crate::home::system_data_folder()?
                .join(r"Programs\LongshotLauncher\LongshotLauncher.exe"),
            link: crate::home::roaming_data_folder()?
                .join(r"Microsoft\Windows\Start Menu\Programs\Longshot Launcher.lnk"),
        })
    }

    fn register(&self, installing: bool) -> Result<()> {
        let tag = own_tag();
        let value = tag.trim_start_matches('v');
        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        let before: Option<String> = hkcu
            .open_subkey(ENTRY)
            .ok()
            .and_then(|key| key.get_value("DisplayVersion").ok());
        // An interrupted install can leave the program in place before its
        // shortcuts or uninstall entry exist. Its next start completes registration.
        if installing || before.as_deref() != Some(value) {
            shortcuts::write(&self.link, &self.exe, "", DISPLAY, shortcuts::LAUNCHER_ID)?;
            shortcuts::refresh_game(&self.exe, true)?;
            shortcuts::refresh_icons(&self.exe, &self.link);
        }
        let (key, _) = hkcu.create_subkey(ENTRY)?;
        let exe = self.exe.to_string_lossy();
        let command = format!("\"{exe}\" --uninstall");
        for (name, value) in [
            ("DisplayName", DISPLAY),
            ("DisplayVersion", value),
            ("Publisher", "Magic Bean"),
            ("DisplayIcon", &format!("{exe},0")),
            (
                "InstallLocation",
                &*self.exe.parent().unwrap().to_string_lossy(),
            ),
            ("UninstallString", &command),
            ("QuietUninstallString", &command),
            (
                "URLInfoAbout",
                "https://git.magicbean.dev/MagicBeanDev/LongshotLauncher",
            ),
        ] {
            key.set_value(name, &value)?;
        }
        key.set_value("NoModify", &1u32)?;
        key.set_value("NoRepair", &1u32)?;
        key.set_value(
            "EstimatedSize",
            &((fs::metadata(&self.exe)?.len() / 1024).min(u32::MAX as u64) as u32),
        )?;
        Ok(())
    }

    fn powershell(&self, script: &str) -> Result<Command> {
        let system =
            PathBuf::from(env::var_os("SystemRoot").context("Missing Windows system folder.")?);
        let mut cmd = Command::new(system.join(r"System32\WindowsPowerShell\v1.0\powershell.exe"));
        cmd.creation_flags(NO_WINDOW)
            .args(["-NoProfile", "-NonInteractive", "-Command", script])
            .env("LONGSHOT_SETUP_EXE", &self.exe);
        Ok(cmd)
    }

    fn installed_order(&self) -> Option<std::cmp::Ordering> {
        let installed: Option<String> = RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey(ENTRY)
            .ok()
            .and_then(|key| key.get_value("DisplayVersion").ok());
        let stored = installed?;
        let tag = if let Some(s) = stored.strip_prefix("Nightly ") {
            format!("nightly-{}", s.replace(" · ", "-"))
        } else if stored.starts_with("nightly-") {
            stored
        } else {
            format!("v{stored}")
        };
        super::installed_order(&tag, own_tag())
    }

    fn install(&self, source: &Path) -> Result<()> {
        let dest = self.exe.parent().context("Missing install folder.")?;
        fs::create_dir_all(dest)?;
        if !same_file(source, &self.exe) {
            let mut temp = NamedTempFile::new_in(dest)?;
            io::copy(&mut fs::File::open(source)?, temp.as_file_mut())?;
            temp.as_file().sync_all()?;
            temp.persist(&self.exe)?;
        }
        self.register(true)
    }

    fn uninstall(&self, running: &Path) -> Result<()> {
        ensure!(
            same_file(running, &self.exe),
            "Uninstall Longshot Launcher from Apps & features or from its installed executable."
        );
        let choice = dialogue(Prompt::Uninstall)?;
        if !matches!(choice, Choice::Confirm | Choice::ReviewData) {
            return Ok(());
        }
        let home = crate::home::system_data_folder()?.join("LongshotLauncher");
        let _lock =
            crate::home::acquire(&home)?.context("Close the launcher before uninstalling it.")?;
        if choice == Choice::ReviewData {
            let plan = crate::storage::cleanup::plan(&home, false)?;
            let paths = plan.review();
            if !paths.is_empty() {
                if dialogue(Prompt::Review(&paths))? != Choice::Confirm {
                    return Ok(());
                }
                plan.execute()?;
            }
        }
        shortcuts::refresh_game(&self.exe, false)?;
        if self.link.is_file() && shortcuts::owns(&self.link, &self.exe).unwrap_or(false) {
            crate::fsx::remove_file_if_present(&self.link)?;
        }
        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        match hkcu.delete_subkey_all(ENTRY) {
            Ok(()) => {}
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        }
        for suffix in [
            ".old",
            ".new",
            ".new.part",
            ".new.zip.part",
            ".rolledback",
            ".update-lock",
        ] {
            crate::fsx::remove_file_if_present(&beside(&self.exe, suffix))?;
        }
        self.powershell(
            r#"
Wait-Process -Id $env:LONGSHOT_SETUP_PID -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $env:LONGSHOT_SETUP_EXE); $i++) {
    Remove-Item -LiteralPath $env:LONGSHOT_SETUP_EXE -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 250
}
$dir = [IO.Path]::GetDirectoryName($env:LONGSHOT_SETUP_EXE)
if (-not (Get-ChildItem -LiteralPath $dir -Force)) { [IO.Directory]::Delete($dir) }
"#,
        )?
        .env("LONGSHOT_SETUP_PID", std::process::id().to_string())
        .spawn()?;
        Ok(())
    }
}

pub(super) fn same_file(a: &Path, b: &Path) -> bool {
    match (a.canonicalize(), b.canonicalize()) {
        (Ok(a), Ok(b)) => {
            a == b
                || a.to_string_lossy()
                    .eq_ignore_ascii_case(&b.to_string_lossy())
        }
        _ => false,
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Choice {
    Close,
    Confirm,
    ReviewData,
}

enum Prompt<'a> {
    Uninstall,
    Review(&'a str),
}

fn dialogue(prompt: Prompt<'_>) -> Result<Choice> {
    use slint::winit_030::{EventResult, WinitWindowAccessor};

    let window = SetupWindow::new()?;
    let weak = window.as_weak();
    let mut cursor = None;
    window.window().on_winit_window_event(move |_, event| {
        if let Some(visible) = crate::window_events::focus_visibility(event, &mut cursor)
            && let Some(ui) = weak.upgrade()
        {
            ui.global::<crate::Ink>().set_focus_visible(visible);
        }
        EventResult::Propagate
    });
    window.set_removing(true);
    if let Prompt::Review(paths) = prompt {
        window.set_removal_list(paths.into());
    }
    let settings = crate::home::resolve()
        .and_then(|root| crate::home::settings(&root))
        .unwrap_or_default();
    let lang = crate::i18n::bind(&window.global::<crate::Locale>(), &settings);
    let description = match prompt {
        Prompt::Review(_) => crate::i18n::lookup(
            lang,
            "Uninstall will remove these files and folders. Your profiles and save backups will be kept.",
        ),
        Prompt::Uninstall => crate::i18n::lookup(
            lang,
            "This removes the launcher, its Start menu shortcut, the Longshot shortcuts it made, and its entry in Apps & features. Your builds (the game folders) and your saves stay where they are.",
        ),
    };
    window.set_description(description.into());
    let choice = Rc::new(Cell::new(Choice::Close));
    let answer = |answer| {
        let choice = choice.clone();
        let weak = window.as_weak();
        move || {
            choice.set(answer);
            if let Some(window) = weak.upgrade() {
                let _ = window.hide();
            }
        }
    };
    window.on_confirmed(answer(Choice::Confirm));
    window.on_secondary(answer(Choice::Close));
    window.run()?;
    if matches!(prompt, Prompt::Uninstall)
        && choice.get() == Choice::Confirm
        && window.get_cleanup()
    {
        Ok(Choice::ReviewData)
    } else {
        Ok(choice.get())
    }
}

pub(super) fn startup(current: &Path, args: &[OsString]) -> Result<bool> {
    // The game's Start menu tile has the same executable but its own AppUserModelID.
    unsafe {
        let _ = SetCurrentProcessExplicitAppUserModelID(&HSTRING::from(shortcuts::LAUNCHER_ID));
    }
    let places = Places::resolve()?;
    if args.iter().any(|s| s == "--uninstall") {
        places.uninstall(current)?;
        return Ok(true);
    }
    let install = args.iter().any(|s| s == "--install");
    if same_file(current, &places.exe) {
        places.register(install)?;
        return Ok(false);
    }
    super::first_launch(
        current,
        &places.exe,
        args,
        places.exe.is_file(),
        places.installed_order(),
        || places.install(current),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn downloaded_copy_cannot_uninstall_the_installed_launcher() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let places = Places {
            exe: temp.path().join("installed.exe"),
            link: temp.path().join("Longshot Launcher.lnk"),
        };
        let downloaded = temp.path().join("downloaded.exe");
        fs::write(&places.exe, b"installed launcher")?;
        fs::write(&downloaded, b"downloaded launcher")?;
        let error = places.uninstall(&downloaded).unwrap_err();
        assert!(error.to_string().contains("installed executable"));
        assert_eq!(fs::read(&places.exe)?, b"installed launcher");
        Ok(())
    }
}
