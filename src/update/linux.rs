//! Per-user AppImage installation. Keep legacy installs working in their original location.
use super::{beside, executable, first_launch, installed_order, own_tag};
use crate::desktop::Menu;
use anyhow::{Context, Result, ensure};
use std::{
    ffi::OsString,
    fs,
    io::{self, Read},
    path::{Path, PathBuf},
};
use tempfile::NamedTempFile;

struct Places {
    program: PathBuf,
    previous: PathBuf,
    menu: Menu,
}

impl Places {
    fn new(data: &Path) -> Self {
        Self {
            program: data.join("longshot/LongshotLauncher.AppImage"),
            previous: data.join("LongshotLauncher/LongshotLauncher.AppImage"),
            menu: Menu::in_directory(data),
        }
    }

    fn registered(&self, path: &Path) -> bool {
        if !crate::players::is_file(path) {
            return false;
        }
        self.menu.belongs_to(path)
            || fs::read_to_string(beside(path, ".version")).is_ok_and(|text| {
                let tag = text.trim();
                tag == "dev"
                    || ["stable", "nightly"]
                        .iter()
                        .any(|channel| super::channel_order(tag, channel, 1).is_some())
            })
    }

    fn destination(&self, explicit: bool) -> &Path {
        if !self.registered(&self.program) && !explicit && self.registered(&self.previous) {
            &self.previous
        } else {
            &self.program
        }
    }

    fn install(&self, current: &Path, migrate: bool) -> Result<()> {
        let program = &self.program;
        let dir = program.parent().context("Missing install directory.")?;
        fs::create_dir_all(dir)?;
        let _lock = super::update_lock(program)?;
        let _old_lock = migrate
            .then(|| super::update_lock(&self.previous))
            .transpose()?;
        let replacing = self.registered(program);
        ensure!(
            replacing
                || program
                    .symlink_metadata()
                    .is_err_and(|e| e.kind() == io::ErrorKind::NotFound),
            "A different file already occupies the install location {}.",
            crate::home::display(program)
        );
        let redirect = if migrate {
            // Prepare before publishing so a read-only previous installation can
            // never turn a reinstallation into a second executable.
            let stage = tempfile::tempdir_in(
                self.previous
                    .parent()
                    .context("Missing previous install directory")?,
            )?;
            std::os::unix::fs::symlink(program, stage.path().join("launcher"))?;
            Some(stage)
        } else {
            None
        };
        let mut copy = NamedTempFile::new_in(dir)?;
        io::copy(&mut fs::File::open(current)?, copy.as_file_mut())?;
        executable(copy.as_file())?;
        self.menu
            .register_install(program, &[current, &self.previous])?;
        crate::desktop::write(
            &beside(program, ".version"),
            format!("{}\n", own_tag()).as_bytes(),
            false,
        )?;
        // Publish last so a failed menu registration leaves the download retryable.
        if replacing {
            copy.persist(program)?;
        } else {
            copy.persist_noclobber(program)?;
        }
        if let Some(stage) = redirect {
            // Existing desktop and Steam shortcuts still resolve to the same binary.
            fs::rename(stage.path().join("launcher"), &self.previous)?;
        }
        Ok(())
    }
}

fn updated_appimage(path: &Path) -> bool {
    let backup = beside(path, ".old");
    if !crate::players::is_file(&backup) {
        return false;
    }
    let mut header = [0; 11];
    fs::File::open(backup)
        .and_then(|mut file| file.read_exact(&mut header))
        .is_ok()
        && header.starts_with(b"\x7fELF")
        && &header[8..] == b"AI\x02"
}

fn has_standard_home(data: &Path) -> bool {
    let control = data.join("longshot/launcher");
    [
        "settings.json",
        crate::home::SETTINGS_FILE,
        "builds.json",
        "versions.json",
    ]
    .iter()
    .any(|name| crate::players::is_file(&control.join(name)))
        || [data.join("longshot/profiles"), control.join("profiles")]
            .iter()
            .any(|profiles| {
                fs::read_dir(profiles).is_ok_and(|mut entries| {
                    entries.any(|entry| {
                        entry.is_ok_and(|e| crate::players::is_file(&e.path().join("profile.json")))
                    })
                })
            })
}

pub(super) fn startup(current: &Path, args: &[OsString]) -> Result<bool> {
    let data = crate::home::system_data_folder()?;
    let places = Places::new(&data);
    let explicit = args.iter().any(|arg| arg == "--install");
    let destination = places.destination(explicit);
    let receipt = beside(destination, ".version");
    if destination.canonicalize().is_ok_and(|path| path == current) {
        if explicit {
            places
                .menu
                .register_install(destination, &[&places.previous])?;
        }
        if !fs::read_to_string(&receipt).is_ok_and(|version| version.trim() == own_tag()) {
            crate::desktop::write(&receipt, format!("{}\n", own_tag()).as_bytes(), false)?;
        }
        return Ok(false);
    }
    // The .NET updater can replace an AppImage at any filename. That is an
    // existing installation even when it has never been added to the menu.
    if !explicit && (updated_appimage(current) || places.registered(current)) {
        return Ok(false);
    }
    let previous = places.registered(&places.previous);
    if explicit
        && previous
        && !places.program.exists()
        && fs::read_to_string(beside(&places.previous, ".version")).is_ok_and(|s| {
            installed_order(s.trim(), own_tag()) == Some(std::cmp::Ordering::Greater)
        })
    {
        super::launcher_command(&places.previous)
            .args(args.iter().filter(|arg| *arg != "--install"))
            .spawn()?;
        return Ok(true);
    }
    if !explicit && !places.registered(destination) && has_standard_home(&data) {
        return Ok(false);
    }
    let order = fs::read_to_string(&receipt)
        .ok()
        .and_then(|tag| installed_order(tag.trim(), own_tag()));
    first_launch(
        current,
        destination,
        args,
        places.registered(destination),
        order,
        || places.install(current, previous),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn standard_home_requires_state_and_recognizes_profiles() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let data = temp.path();
        let control = data.join("longshot/launcher");
        let profile = data.join("longshot/profiles/default");
        fs::create_dir_all(&control)?;
        fs::create_dir_all(&profile)?;
        assert!(!has_standard_home(data));
        fs::write(control.join("settings.json"), b"{}")?;
        assert!(has_standard_home(data));
        fs::remove_file(control.join("settings.json"))?;
        fs::write(profile.join("profile.json"), br#"{"v":1}"#)?;
        assert!(has_standard_home(data));
        Ok(())
    }

    #[test]
    fn fresh_and_previous_installations_follow_the_registered_program() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let places = Places::new(temp.path());
        let release = temp.path().join("download.AppImage");
        fs::write(&release, b"new launcher")?;
        assert_eq!(
            places.destination(false),
            temp.path().join("longshot/LongshotLauncher.AppImage")
        );

        fs::create_dir_all(places.previous.parent().unwrap())?;
        fs::write(&places.previous, b"another file")?;
        assert_eq!(places.destination(false), places.program);
        fs::write(beside(&places.previous, ".version"), "v1.4.0\n")?;
        places.menu.install(&places.previous)?;
        assert_eq!(places.destination(false), places.previous);
        assert_eq!(places.destination(true), places.program);
        fs::create_dir_all(places.program.parent().unwrap())?;
        fs::write(&places.program, b"registered lowercase install")?;
        fs::write(beside(&places.program, ".version"), "v2.0.0\n")?;
        assert_eq!(places.destination(false), places.program);
        places.install(&release, true)?;
        assert_eq!(
            places.menu.installed_target().as_ref(),
            Some(&places.program)
        );
        assert_eq!(
            places.previous.canonicalize()?,
            places.program.canonicalize()?
        );
        assert_eq!(fs::read(&places.program)?, b"new launcher");
        assert_eq!(places.destination(false), places.program);
        Ok(())
    }

    #[test]
    fn moving_data_and_reviewing_removal_keep_the_installed_launcher() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let places = Places::new(temp.path());
        let data = temp.path().join("longshot");
        let control = data.join("launcher");
        let moved = temp.path().join("player-data");
        let source = temp.path().join("download.AppImage");
        fs::write(&source, b"fixture executable")?;
        places.install(&source, false)?;
        crate::home::patch_settings(
            &control,
            &serde_json::json!({"dataFolder":data,"work":temp.path().join("cache")}),
        )?;
        crate::players::initialize(&control)?;
        let defaults = data.join("profiles/default/profile.json");
        assert!(defaults.is_file());
        for include_profiles in [false, true] {
            let review = crate::storage::cleanup::plan(&control, include_profiles)?.review();
            for item in review.lines().map(Path::new) {
                assert!(!places.program.starts_with(item));
                assert!(!beside(&places.program, ".version").starts_with(item));
                assert!(
                    !temp
                        .path()
                        .join("applications/longshot-launcher.desktop")
                        .starts_with(item)
                );
            }
        }
        crate::folders::Change::prepare(&control, "dataFolder", Some(&moved))?
            .apply(true, &std::sync::atomic::AtomicBool::new(false))?;
        assert!(moved.join("profiles/default/profile.json").is_file());
        assert!(!defaults.exists());
        assert_eq!(fs::read(&places.program)?, b"fixture executable");
        assert!(beside(&places.program, ".version").is_file());
        assert_eq!(
            places.menu.installed_target().as_ref(),
            Some(&places.program)
        );
        Ok(())
    }

    #[test]
    fn installation_preserves_unrelated_programs_and_menu_entries() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let places = Places::new(temp.path());
        let release = temp.path().join("download.AppImage");
        fs::write(&release, b"new launcher")?;
        fs::create_dir_all(places.program.parent().unwrap())?;
        fs::write(&places.program, b"existing unrelated file")?;
        assert!(places.install(&release, false).is_err());
        assert_eq!(fs::read(&places.program)?, b"existing unrelated file");
        fs::remove_file(&places.program)?;
        let other = temp.path().join("different-launcher");
        places.menu.install(&other)?;
        assert!(places.install(&release, false).is_err());
        assert_eq!(
            places.menu.installed_target().as_deref(),
            Some(other.as_path())
        );
        assert!(!places.program.exists());
        Ok(())
    }
}
