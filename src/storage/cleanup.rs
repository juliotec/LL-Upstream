//! Bulk cleanup enumerates the launcher's own files first. A custom work folder,
//! a registered external installation, and a source dump can all share a drive.
use super::{CACHES, Protected, Protection, Removal, is_link, removal_target};
use crate::{dlss5, game, home, players};
use anyhow::{Context, Result, ensure};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Kind {
    Cache,
    Version,
    UserData,
    File,
    Preferences,
    /// (Windows) one of the DLSS 5 files the launcher copied ([`dlss5::added`]), removed only while it is still the launcher's
    Dlss5(dlss5::Kind),
}

#[derive(Debug, PartialEq, Eq)]
pub struct Plan {
    root: PathBuf,
    include_profiles: bool,
    dlss5: Option<PathBuf>,
    entries: BTreeMap<PathBuf, Kind>,
}

impl Plan {
    pub fn review(&self) -> String {
        self.entries
            .keys()
            .map(|p| crate::home::display(p))
            .collect::<Vec<_>>()
            .join("\n")
    }

    pub fn execute(self) -> Result<()> {
        players::ensure_idle()?;
        ensure!(
            self == plan_with(&self.root, self.include_profiles, self.dlss5.as_deref())?,
            "The folders changed. Review the list again before removing anything."
        );
        crate::diagnostics::close_file();
        // Keep settings until all directory removals succeed so a retry uses the same paths.
        for (path, kind) in &self.entries {
            match kind {
                Kind::Version => game::remove_managed(&self.root, path)?,
                Kind::Cache | Kind::UserData => {
                    Removal::Directory.execute(path)?;
                }
                Kind::File | Kind::Preferences | Kind::Dlss5(_) => {}
            }
        }
        for (path, kind) in &self.entries {
            match kind {
                Kind::Preferences => home::reset_preferences(&self.root)?,
                Kind::File if path.try_exists()? => Removal::File.execute(path)?,
                Kind::Dlss5(dlss5) => {
                    if let Some(folder) = &self.dlss5 {
                        remove_dlss5(folder, *dlss5)?;
                    }
                }
                _ => {}
            }
        }
        Ok(())
    }
}

/// kind's DLSS 5 file removed, once a full check says it is still the launcher's copy; one put there by hand since (or
/// changed) is the player's, and stays
fn remove_dlss5(folder: &Path, kind: dlss5::Kind) -> Result<()> {
    let _work = dlss5::exclusive().context("Could not lock the DLSS 5 files.")?;
    match dlss5::remove(folder, kind) {
        Ok(()) | Err(dlss5::Failure::Refused(_) | dlss5::Failure::InTheWay(_)) => Ok(()),
        Err(dlss5::Failure::Failed(error)) => Err(error).context("Could not remove a DLSS 5 file."),
    }
}

pub fn plan(root: &Path, include_profiles: bool) -> Result<Plan> {
    plan_with(root, include_profiles, dlss5::folder().as_deref())
}

/// The plan, with the DLSS 5 files' folder (none: none on this system)
fn plan_with(root: &Path, include_profiles: bool, dlss5: Option<&Path>) -> Result<Plan> {
    let anchor = root.canonicalize()?;
    ensure!(
        anchor.parent().is_some(),
        "The launcher home cannot be a drive root for cleanup."
    );
    for key in ["HOME", "USERPROFILE"] {
        if let Some(user) = home::variable(key).and_then(|path| path.canonicalize().ok()) {
            ensure!(
                !user.starts_with(&anchor),
                "The launcher home cannot contain your user folder for cleanup."
            );
        }
    }
    let (preferences_file, settings) = home::preferences_file(root)?;
    let protected = Protected::gather(root, Protection::Bulk)?;
    let mut entries = BTreeMap::new();
    let mut add = |base: &Path, name: &Path, kind: Kind| -> Result<()> {
        let path = base.join(name);
        let Some(actual) = removal_target(base, name)? else {
            return Ok(());
        };
        let player = (kind == Kind::UserData).then_some(actual.as_path());
        let version = (kind == Kind::Version).then_some(path.as_path());
        if protected.blocks(&actual, player, version) {
            return Ok(());
        }
        let meta = fs::symlink_metadata(&path)?;
        let file = matches!(kind, Kind::File | Kind::Preferences);
        if !is_link(&meta) && ((file && meta.is_file()) || (!file && meta.is_dir())) {
            entries.insert(path, kind);
        }
        Ok(())
    };
    for work in super::work_folders(root, &settings) {
        for cache in CACHES {
            add(&work, Path::new(cache.subdir), Kind::Cache)?;
        }
        for name in [
            "longshot-builder/tools",
            "longshot-builder/sources",
            "longshot-builder/runtimes",
        ] {
            add(&work, Path::new(name), Kind::Cache)?;
        }
    }
    for version in &protected.versions {
        if let (Some(library), Some(name)) = (version.parent(), version.file_name()) {
            add(library, Path::new(name), Kind::Version)?;
        }
    }
    for name in ["builder-data", "logs"] {
        add(root, Path::new(name), Kind::Cache)?;
    }
    add(root, Path::new("longshot.png"), Kind::File)?;
    let data = home::data_folder(root, &settings);
    // Profiles kept on another drive still need their configured location on the next start.
    if include_profiles || data == root {
        let name = preferences_file
            .file_name()
            .context("Missing settings file name.")?;
        add(root, Path::new(name), Kind::Preferences)?;
    }
    if include_profiles {
        add(&data, Path::new("profiles"), Kind::UserData)?;
        let backups = players::backup_root(root)?;
        add(
            backups.parent().context("Missing backup parent.")?,
            Path::new("save-backups"),
            Kind::UserData,
        )?;
        add(root, Path::new("save-backups"), Kind::UserData)?;
    }
    // (outside the launcher's home: only the copies the launcher placed, never a file the player put there by hand)
    if let Some(folder) = dlss5 {
        for (kind, path) in dlss5::added(folder) {
            entries.insert(path, Kind::Dlss5(kind));
        }
    }
    Ok(Plan {
        root: root.into(),
        include_profiles,
        dlss5: dlss5.map(Path::to_owned),
        entries,
    })
}

pub fn bind(ui: &crate::MainWindow, root: &Path) {
    super::mods::bind(ui, root);
    use crate::Preferences;
    use slint::ComponentHandle;
    use std::{cell::RefCell, rc::Rc};

    let reviewed = Rc::new(RefCell::new(None));
    let stored = Rc::clone(&reviewed);
    let (weak, root) = (ui.as_weak(), root.to_owned());
    ui.global::<Preferences>()
        .on_review_removal(move |profiles| {
            let Some(ui) = weak.upgrade() else { return };
            let prefs = ui.global::<Preferences>();
            stored.borrow_mut().take();
            prefs.set_removal_list("".into());
            match plan(&root, profiles) {
                Ok(plan) => {
                    prefs.set_removal_list(plan.review().into());
                    *stored.borrow_mut() = Some(plan);
                }
                Err(error) => prefs.set_status(error.to_string().into()),
            }
        });
    let weak = ui.as_weak();
    ui.global::<Preferences>().on_remove_reviewed(move || {
        let Some(ui) = weak.upgrade() else { return };
        if crate::busy(&ui) || ui.global::<crate::Game>().get_playing() {
            return;
        }
        let Some(plan) = reviewed.borrow_mut().take() else {
            return;
        };
        let prefs = ui.global::<Preferences>();
        prefs.set_cleaning(true);
        prefs.set_status("Removing the listed launcher data…".into());
        let weak = ui.as_weak();
        std::thread::spawn(move || {
            let result = plan.execute();
            let _ = weak.upgrade_in_event_loop(move |ui| {
                let prefs = ui.global::<Preferences>();
                prefs.set_cleaning(false);
                prefs.set_removal_list("".into());
                match result {
                    Ok(()) => ui.invoke_request_exit(),
                    Err(error) => prefs.set_status(format!("{error:#}").into()),
                }
            });
        });
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn review_excludes_originals_external_builds_and_profiles_by_default() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("launcher");
        let work = tmp.path().join("work");
        let source = work.join("longshot-builder/store/game.nup");
        fs::create_dir_all(source.parent().unwrap())?;
        fs::write(&source, b"fixture")?;
        crate::players::initialize(&root)?;
        fs::create_dir_all(root.join("logs"))?;
        fs::create_dir_all(work.join("longshot-builder/work"))?;
        home::write_json(
            &root.join(home::SETTINGS_FILE),
            &json!({"format":2, "work":work}),
        )?;
        home::write_json(
            &root.join("builds.json"),
            &json!({"format":1, "builds":[{"nup":source}]}),
        )?;
        let keep = plan(&root, false)?;
        assert!(!keep.entries.contains_key(source.parent().unwrap()));
        assert!(!keep.entries.contains_key(&root.join("profiles")));
        assert!(keep.entries.contains_key(&root.join("logs")));
        assert!(
            keep.entries
                .contains_key(&work.join("longshot-builder/work"))
        );
        assert!(
            plan(&root, true)?
                .entries
                .contains_key(&root.join("profiles"))
        );
        Ok(())
    }
    #[test]
    fn external_player_data_survives_cache_cleanup_and_default_bulk_removal() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("control");
        let cache = tmp.path().join("prior-cache");
        let store = cache.join("longshot-builder/store");
        let data = store.join("player-data");
        home::patch_settings(&root, &json!({"dataFolder":data,"work":cache}))?;
        players::initialize(&root)?;
        fs::create_dir_all(data.join("mods/example"))?;
        fs::write(data.join("mods/example/pinned"), b"installed mod")?;
        fs::create_dir_all(root.join("profiles/older"))?;
        fs::create_dir_all(root.join("logs"))?;
        let snapshot = super::super::measure(&root)?;
        assert!(
            snapshot
                .rows
                .iter()
                .any(|row| row.path == data.join("mods") && row.group == "Your data")
        );
        assert!(
            snapshot
                .rows
                .iter()
                .any(|row| row.path == data.join("profiles"))
        );
        let entries = plan(&root, false)?.entries;
        assert!(entries.contains_key(&root.join("logs")));
        for kept in [
            &store,
            &data.join("profiles"),
            &root.join(home::SETTINGS_FILE),
        ] {
            assert!(!entries.contains_key(kept));
        }
        assert!(super::super::clear(&root, "longshot-builder/store", &store).is_err());
        let reviewed = plan(&root, true)?.entries;
        assert!(reviewed.contains_key(&data.join("profiles")));
        assert!(!reviewed.contains_key(&root.join("profiles")));
        assert!(!reviewed.contains_key(&store));
        assert_eq!(
            fs::read(data.join("mods/example/pinned"))?,
            b"installed mod"
        );
        Ok(())
    }

    #[test]
    fn remove_everything_takes_the_launchers_dlss5_copies_and_leaves_the_players() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("launcher");
        crate::players::initialize(&root)?;
        let ngx = tmp.path().join("local/Longshot/ngx");
        let chosen = tmp.path().join("chosen.dll");
        let mut bytes = vec![3u8; 2 << 20];
        bytes[..2].copy_from_slice(b"MZ");
        fs::write(&chosen, &bytes)?;
        let placed = dlss5::install(
            &chosen,
            &ngx,
            |_| dlss5::NvidiaCheck::DlssModel,
            |_| {},
            dlss5::FP16_KNOWN,
            None,
        )
        .map_err(|e| anyhow::anyhow!("{e}"))?;
        // (the FP16 place holds a file put there by hand: the player's)
        let by_hand = dlss5::place(&ngx, dlss5::Kind::Fp16);
        fs::create_dir_all(by_hand.parent().unwrap())?;
        fs::write(&by_hand, &bytes)?;

        let reviewed = plan_with(&root, false, Some(&ngx))?;
        assert_eq!(reviewed.dlss5.as_deref(), Some(ngx.as_path()));
        assert_eq!(
            reviewed.entries.get(&placed.path),
            Some(&Kind::Dlss5(dlss5::Kind::Nvidia))
        );
        assert!(!reviewed.entries.contains_key(&by_hand));
        assert!(reviewed.review().contains("nvngx_dlssnr.dll"));
        assert!(
            !plan_with(&root, false, None)?
                .entries
                .contains_key(&placed.path)
        );

        remove_dlss5(&ngx, dlss5::Kind::Nvidia)?;
        remove_dlss5(&ngx, dlss5::Kind::Fp16)?;
        assert!(!placed.path.exists() && by_hand.is_file() && chosen.is_file());
        Ok(())
    }

    #[test]
    fn cache_cleanup_keeps_installed_versions_when_work_and_library_overlap() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let home = tmp.path();
        crate::players::initialize(home)?;
        let version = home.join("versions/test-release");
        let store = version.join("longshot-builder/store");
        fs::create_dir_all(&store)?;
        let builder = if cfg!(windows) {
            "Longshot.exe"
        } else {
            "Longshot.x86_64"
        };
        fs::write(version.join(builder), b"synthetic builder")?;
        let keep = store.join("fixture");
        fs::write(&keep, b"synthetic cache in installed version")?;
        crate::home::patch_settings(home, &json!({"work":version}))?;
        assert!(super::super::clear(home, "longshot-builder/store", &store).is_err());
        assert!(super::super::after_build(home, false).is_err());
        assert_eq!(fs::read(keep)?, b"synthetic cache in installed version");
        let entries = plan(home, false)?.entries;
        assert!(!entries.contains_key(&store));
        assert!(entries.contains_key(&version));
        Ok(())
    }
}
