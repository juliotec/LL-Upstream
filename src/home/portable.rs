//! On-disk portable layouts, from the stable launcher through the two-folder layout.
use super::*;
fn portable_executable() -> Option<PathBuf> {
    variable("LONGSHOT_LAUNCHER_EXE")
        .or_else(|| variable("APPIMAGE"))
        .or_else(|| std::env::current_exe().ok())
}

fn reserved(container: &Path) -> bool {
    let same = |candidate: PathBuf| {
        #[cfg(windows)]
        {
            container
                .as_os_str()
                .eq_ignore_ascii_case(candidate.as_os_str())
        }
        #[cfg(not(windows))]
        {
            container == candidate
        }
    };
    if std::env::home_dir().is_some_and(|home| same(home.join("Games/Longshot"))) {
        return true;
    }
    #[cfg(windows)]
    if [super::system_data_folder(), super::roaming_data_folder()]
        .into_iter()
        .flatten()
        .any(|dir| same(dir.join("Longshot")))
    {
        return true;
    }
    false
}

fn portable_home_at(exe: &Path) -> Option<PathBuf> {
    #[cfg(unix)]
    let resolved = exe.canonicalize().unwrap_or_else(|_| exe.to_owned());
    #[cfg(unix)]
    let exe = resolved.as_path();
    let parent = exe.parent()?;
    // Honour an existing portable installation before looking for the new layout.
    #[cfg(windows)]
    if crate::players::is_file(&parent.join("portable")) {
        return Some(parent.join("LongshotLauncher-data"));
    }
    #[cfg(unix)]
    {
        let mut name = exe.as_os_str().to_os_string();
        name.push(".home");
        let old = PathBuf::from(name);
        if old.is_dir() {
            return Some(old);
        }
    }
    let container = parent.join("Longshot");
    if reserved(&container)
        || !crate::players::is_dir(&container)
        || !crate::players::is_file(&container.join(".portable"))
    {
        return None;
    }
    // The previous marked launcher kept its settings directly in Longshot.
    // Keep its registry alongside them so its saves can be imported on upgrade.
    if !container.join("data").join(SETTINGS_FILE).is_file()
        && [
            "settings.json",
            "settings-v2.json",
            "builds.json",
            "versions.json",
            "profiles",
            "user",
            "builds",
        ]
        .iter()
        .any(|name| container.join(name).exists())
    {
        Some(container)
    } else {
        Some(container.join("data"))
    }
}

pub(crate) fn portable_at(exe: &Path) -> bool {
    portable_home_at(exe).is_some()
}

pub(crate) fn enable_portable(exe: &Path) -> Result<()> {
    if portable_at(exe) {
        return Ok(());
    }
    #[cfg(unix)]
    let resolved = exe.canonicalize()?;
    #[cfg(unix)]
    let exe = resolved.as_path();
    let parent = exe
        .parent()
        .context("The launcher needs a parent folder.")?;
    let container = parent.join("Longshot");
    ensure!(
        !reserved(&container),
        "This folder is reserved for Longshot. Move the launcher into a folder of its own first."
    );
    // Publish both directories and their marker together. Any error cleans up
    // only our own staging directory; a pre-existing Longshot folder stays intact.
    let stage = tempfile::Builder::new()
        .prefix(".longshot-portable-")
        .tempdir_in(parent)?;
    fs::create_dir(stage.path().join("data"))?;
    fs::create_dir(stage.path().join("cache"))?;
    File::create_new(stage.path().join(".portable"))?.sync_all()?;
    if let Err(error) = crate::fsx::rename_new(stage.path(), &container) {
        if portable_at(exe) {
            return Ok(());
        }
        return Err(error).with_context(|| {
            format!(
                "Could not create {}. Put the launcher in a folder of its own to use it portably.",
                display(&container)
            )
        });
    }
    Ok(())
}

pub(super) fn portable_home() -> Option<PathBuf> {
    if crate::update::externally_managed() {
        return None;
    }
    portable_home_at(&portable_executable()?)
}

pub fn is_portable(root: &Path) -> bool {
    variable("LONGSHOT_LAUNCHER_HOME").is_none() && portable_home().as_deref() == Some(root)
}

/// Update launcher-owned path references after moving a portable installation.
/// Paths outside the portable installation remain as configured.
pub(super) fn relocate_state(root: &Path) -> Result<()> {
    let mut settings = super::settings(root)?;
    let previous = settings["portableHome"].as_str().map(PathBuf::from);
    if previous.as_deref() == Some(root) {
        return Ok(());
    }
    let patch = |value: &mut Value| {
        let Some(old_home) = previous.as_deref() else {
            return;
        };
        let (Some(old_base), Some(new_base)) = (old_home.parent(), root.parent()) else {
            return;
        };
        let Some(old) = value.as_str().map(Path::new) else {
            return;
        };
        if let Ok(relative) = old.strip_prefix(old_base)
            && relative
                .components()
                .all(|part| matches!(part, std::path::Component::Normal(_)))
        {
            let new = new_base.join(relative);
            if new.exists() {
                *value = json!(new);
            }
        }
    };
    for key in [
        "work",
        "dataFolder",
        "buildsFolder",
        "nup",
        "exefs",
        "romfs",
    ] {
        if let Some(value) = settings.get_mut(key) {
            patch(value);
        }
    }
    // Stale cache entries must not point cleanup at a different copy of this launcher.
    if let Some(earlier) = settings["earlierWork"].as_array_mut() {
        for dir in earlier {
            patch(dir);
        }
    }
    let data = super::data_folder(root, &settings);
    if let Some(old) = previous.as_deref() {
        let profiles = data.join("profiles");
        if crate::players::is_dir(&profiles) {
            for entry in fs::read_dir(profiles)? {
                let entry = entry?;
                if !entry.file_type()?.is_dir()
                    || !entry
                        .file_name()
                        .to_str()
                        .is_some_and(crate::players::valid_id)
                {
                    continue;
                }
                let profile = entry.path();
                if let Ok(record) = crate::players::read_profile(&profile)
                    && let Some(mut game) = record.get("game").cloned()
                {
                    patch(&mut game);
                    if game != record["game"] {
                        crate::players::edit_profile(&profile, |state| {
                            if state.get("game") == record.get("game") {
                                state.insert("game".into(), game);
                            }
                            Ok(())
                        })?;
                    }
                }
            }
        }
        let cache = super::work(root, &settings).join("longshot-builder");
        let alias = cache.join("mods");
        if let Some(old_base) = old.parent()
            && let Some(new_base) = root.parent()
            && cache.starts_with(new_base)
            && let Ok(relative) = data.strip_prefix(new_base)
            && alias
                .symlink_metadata()
                .is_ok_and(|meta| crate::storage::is_link(&meta))
        {
            crate::mod_store::repoint(&cache, &old_base.join(relative), &data)?;
        }
    }
    settings["portableHome"] = json!(root);
    super::write_json(&root.join(SETTINGS_FILE), &settings)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::players;

    #[test]
    fn portable_locations_follow_the_download_and_survive_moving_it() -> Result<()> {
        if let Some(phase) = variable("LONGSHOT_TEST_PORTABLE_PHASE") {
            let exe = portable_executable().unwrap();
            let container = exe.parent().unwrap().join("Longshot");
            if phase == Path::new("create") {
                fs::create_dir(&container)?;
                assert!(enable_portable(&exe).is_err());
                assert!(!portable_at(&exe));
                fs::remove_dir(&container)?;
                enable_portable(&exe)?;
                assert!(container.join("data").is_dir());
                assert!(container.join("cache").is_dir());
                enable_portable(&exe)?;
            }
            let root = resolve()?;
            assert_eq!(root, container.join("data"));
            assert!(is_portable(&root));
            let _lease = acquire(&root)?.unwrap();
            let cache = work(&root, &settings(&root)?);
            assert_eq!(cache, container.join("cache"));
            players::initialize(&root)?;
            let profile = players::current_dir(&root)?;
            assert_eq!(profile, root.join("profiles/default"));
            let nfc = root.join("nfc");
            assert_eq!(players::nfc_folder(&root)?, nfc);
            assert_eq!(players::stable_user(&root)?, root.join("user"));
            assert_eq!(players::backup_root(&root)?, root.join("save-backups"));
            let cmd = crate::game::mod_command_for(&root, &exe, &profile)?;
            let env: std::collections::HashMap<_, _> = cmd.get_envs().collect();
            assert_eq!(
                env[std::ffi::OsStr::new("LONGSHOT_NFC_DIR")],
                Some(nfc.as_os_str())
            );
            let builder_cache = cache.join("longshot-builder");
            assert_eq!(
                env[std::ffi::OsStr::new("LONGSHOT_CACHE")],
                Some(builder_cache.as_os_str())
            );
            let source = root.join("fixture.nup");
            if phase == Path::new("create") {
                fs::write(&source, b"fixture")?;
                players::patch_profile(&profile, |state| {
                    state.insert("game".into(), json!(source));
                })?;
                patch_settings(&root, &json!({"nup":source}))?;
                crate::mod_store::ensure(&builder_cache, &root)?;
            } else {
                assert_eq!(players::read_profile(&profile)?["game"], json!(source));
                assert_eq!(settings(&root)?["nup"], json!(source));
                assert_eq!(
                    builder_cache.join("mods").canonicalize()?,
                    root.join("mods").canonicalize()?
                );
            }
            return Ok(());
        }

        let sandbox = tempfile::tempdir()?;
        let original = sandbox.path().join("before");
        let moved = sandbox.path().join("after");
        fs::create_dir(&original)?;
        fs::write(original.join("launcher"), b"fixture")?;
        for (phase, location) in [("create", &original), ("reopen", &moved)] {
            if phase == "reopen" {
                fs::rename(&original, &moved)?;
            }
            let status = std::process::Command::new(std::env::current_exe()?)
                .args(["--exact", "home::portable::tests::portable_locations_follow_the_download_and_survive_moving_it", "--nocapture"])
                .env_remove("LONGSHOT_LAUNCHER_HOME").env_remove("LONGSHOT_NFC_DIR")
                .env("LONGSHOT_LAUNCHER_EXE", location.join("launcher"))
                .env("LONGSHOT_TEST_PORTABLE_PHASE", phase)
                .env("XDG_DATA_HOME", sandbox.path().join("unrelated-system-data"))
                .status()?;
            ensure!(status.success(), "Portable {phase} failed");
        }
        assert!(!sandbox.path().join("unrelated-system-data").exists());
        let exe = moved.join("launcher");
        let container = moved.join("Longshot");
        fs::remove_file(container.join("data").join(SETTINGS_FILE))?;
        fs::write(container.join("settings.json"), "{}")?;
        assert_eq!(portable_home_at(&exe), Some(container));
        #[cfg(windows)]
        let old = {
            fs::write(moved.join("portable"), b"")?;
            moved.join("LongshotLauncher-data")
        };
        #[cfg(unix)]
        let old = {
            let old = moved.join("launcher.home");
            fs::create_dir(&old)?;
            old
        };
        assert_eq!(portable_home_at(&exe), Some(old));
        Ok(())
    }
}
