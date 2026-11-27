//! The Linux application-menu entry belongs to the launcher. Longshot's game
//! shortcut is a separate desktop entry with a different filename.
use crate::home;
use anyhow::{Context, Result, ensure};
use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
};

pub struct Menu {
    entry: PathBuf,
    icon: PathBuf,
}

impl Menu {
    pub fn locate() -> Option<Self> {
        let data = home::system_data_folder().ok()?;
        Some(Self::in_directory(&data))
    }

    pub(crate) fn in_directory(data: &Path) -> Self {
        Self {
            entry: data.join("applications/longshot-launcher.desktop"),
            icon: data.join("icons/hicolor/512x512/apps/longshot-launcher.png"),
        }
    }

    pub fn installed_target(&self) -> Option<PathBuf> {
        let text = fs::read_to_string(&self.entry).ok()?;
        text.lines()
            .find_map(|line| line.strip_prefix("TryExec="))
            .map(PathBuf::from)
    }

    pub(crate) fn register_install(&self, program: &Path, prior_targets: &[&Path]) -> Result<()> {
        if self.entry.try_exists()? {
            let target = self.installed_target();
            ensure!(
                target
                    .as_deref()
                    .is_some_and(|p| same_program(p, program) || prior_targets.contains(&p)),
                "The applications-menu entry points elsewhere. Remove or rename {} before installing.",
                home::display(&self.entry)
            );
        }
        self.install(program)
    }

    pub(crate) fn install(&self, appimage: &Path) -> Result<()> {
        let quoted = quote_desktop(appimage)?;
        let image = appimage.to_str().context("Invalid AppImage path")?;
        let icon = self.icon.to_str().context("Invalid icon path")?;
        ensure!(!icon.contains(['\n', '\r']), "Invalid icon path");
        let entry = format!(
            "[Desktop Entry]\nType=Application\nName=Longshot Launcher\nComment=Install Longshot's builder, build the game from your own copy, and play\nExec={quoted}\nTryExec={image}\nIcon={icon}\nTerminal=false\nCategories=Game;\nStartupWMClass=longshot-launcher\n"
        );
        write(&self.icon, include_bytes!("../assets/longshot.png"), false)?;
        write(&self.entry, entry.as_bytes(), true)
    }

    pub(crate) fn belongs_to(&self, program: &Path) -> bool {
        self.installed_target()
            .is_some_and(|target| same_program(&target, program))
    }

    fn remove_for(&self, program: &Path) -> Result<()> {
        ensure!(
            self.belongs_to(program),
            "This copy does not own the applications-menu entry."
        );
        for path in [&self.entry, &self.icon] {
            crate::fsx::remove_file_if_present(path)?;
        }
        Ok(())
    }
}

pub(crate) fn write(path: &Path, bytes: &[u8], executable: bool) -> Result<()> {
    crate::fsx::atomic_write(
        path,
        Some(if executable { 0o744 } else { 0o644 }),
        false,
        |file| {
            file.write_all(bytes)?;
            Ok(())
        },
    )
}

pub fn change(remove: bool) -> Result<()> {
    let menu = Menu::locate().context("Applications directory not found")?;
    let program =
        home::variable("APPIMAGE").context("Only the AppImage can change the menu entry.")?;
    if remove {
        menu.remove_for(&program)
    } else {
        menu.register_install(&program, &[])
    }
}

fn same_program(left: &Path, right: &Path) -> bool {
    left == right
        || left
            .canonicalize()
            .ok()
            .zip(right.canonicalize().ok())
            .is_some_and(|(left, right)| left == right)
}

fn quote_desktop(path: &Path) -> Result<String> {
    let value = path
        .to_str()
        .context("The launcher path cannot be written in a desktop entry")?;
    ensure!(
        !value.contains(['\n', '\r']),
        "The launcher path contains a line break"
    );
    let mut quoted = String::from("\"");
    for c in value.chars() {
        match c {
            '"' | '$' | '`' => {
                quoted.push_str(r"\\");
                quoted.push(c);
            }
            '\\' => quoted.push_str(r"\\\\"),
            '%' => quoted.push_str("%%"),
            _ => quoted.push(c),
        }
    }
    quoted.push('"');
    Ok(quoted)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stable_launcher_entry_can_move_without_touching_the_game_shortcut() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let menu = Menu::in_directory(tmp.path());
        let game = tmp.path().join("applications/longshot.desktop");
        fs::create_dir_all(game.parent().unwrap())?;
        fs::write(&game, "game shortcut")?;
        let old = tmp.path().join("old.AppImage");
        let moved = tmp.path().join("new 100%.AppImage");
        menu.install(&old)?;
        assert_eq!(menu.installed_target().as_ref(), Some(&old));
        menu.install(&moved)?;
        assert_eq!(menu.installed_target().as_ref(), Some(&moved));
        assert!(fs::read_to_string(&menu.entry)?.contains("100%%.AppImage"));
        assert!(menu.remove_for(&old).is_err());
        assert!(menu.register_install(&old, &[]).is_err());
        assert!(menu.belongs_to(&moved));
        menu.remove_for(&moved)?;
        assert_eq!(fs::read_to_string(game)?, "game shortcut");
        assert!(!menu.icon.exists());
        Ok(())
    }
}
