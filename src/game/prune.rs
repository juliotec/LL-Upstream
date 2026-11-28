//! Reclaim unused managed versions after an update, with rollback, profile and fetch protection.
use super::versions::{self, registry};
use crate::UiMutexExt as _;
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::Value;
use std::{
    collections::BTreeSet,
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::{LazyLock, Mutex},
};

const RETIRED: &str = ".longshot-retired";
const RECEIPT: &[u8] = b"Longshot Launcher retired installation, format 1\n";

/// A path in the registry alone cannot distinguish a retired copy from a new
/// installation created later in the same directory. Read-only copies stay intact.
pub(super) fn mark_retired(folder: &Path) {
    if players::is_dir(folder)
        && let Ok(mut file) = fs::File::create_new(folder.join(RETIRED))
        && file.write_all(RECEIPT).is_ok()
    {
        let _ = file.sync_all();
    }
}

pub(crate) fn is_retired(folder: &Path) -> bool {
    let receipt = folder.join(RETIRED);
    let mut data = Vec::new();
    players::is_file(&receipt)
        && fs::File::open(receipt).is_ok_and(|file| {
            file.take(RECEIPT.len() as u64 + 1)
                .read_to_end(&mut data)
                .is_ok()
                && data == RECEIPT
        })
}

static FETCHED: LazyLock<Mutex<BTreeSet<PathBuf>>> = LazyLock::new(Mutex::default);
fn activity_file(home: &Path) -> Result<fs::File> {
    fs::create_dir_all(home)?;
    let path = home.join(".version-library.lock");
    ensure!(
        !versions::linked(&path),
        "The version library lock is a link."
    );
    Ok(fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(path)?)
}
pub(crate) fn move_lease(home: &Path) -> Result<home::FileLease> {
    home::FileLease::try_acquire(activity_file(home)?, crate::fsx::LockMode::Exclusive)
        .context("A builder download or cleanup is using these folders. Wait for it to finish.")
}
pub(super) fn fetch_lease(home: &Path, cancel: &super::process::Cancel) -> Result<home::FileLease> {
    let file = activity_file(home)?;
    loop {
        cancel.check()?;
        match file.try_lock_shared() {
            Ok(()) => break,
            Err(std::fs::TryLockError::WouldBlock) => {
                std::thread::sleep(std::time::Duration::from_millis(50))
            }
            Err(std::fs::TryLockError::Error(error)) => return Err(error.into()),
        }
    }
    Ok(home::FileLease(file))
}
pub(super) fn remember(folder: &Path) {
    FETCHED.guard().insert(folder.to_owned());
}

const PENDING: &str = ".retired-cleanup-pending";

pub(crate) fn retry_retired(home: &Path) {
    if !players::is_file(&home.join(PENDING)) {
        return;
    }
    if let Err(error) = run_cleanup(home, true) {
        eprintln!("Retired version cleanup deferred: {error}");
    }
}

pub(super) fn after_update(home: &Path) -> Result<()> {
    match fs::File::create_new(home.join(PENDING)) {
        Ok(_) => (),
        Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => (),
        Err(e) => return Err(e.into()),
    }
    run_cleanup(home, false)
}

fn run_cleanup(home: &Path, retired_only: bool) -> Result<()> {
    let Ok(_exclusive) = move_lease(home) else {
        return Ok(());
    };
    players::ensure_idle()?;
    prune(home, retired_only)?;
    if registry(home)?["retiredFolders"]
        .as_array()
        .is_none_or(Vec::is_empty)
    {
        fs::remove_file(home.join(PENDING))?;
    }
    Ok(())
}

/// Whole-directory pruning is only safe for a tools-only installation. Storage can
/// review individual generated files in an older build without touching player files.
fn only_tools(folder: &Path, retired: bool) -> Result<bool> {
    for entry in fs::read_dir(folder)? {
        let entry = entry?;
        let path = entry.path();
        if players::is_dir(&path) && entry.file_name() == "builds" {
            for build in fs::read_dir(path)? {
                let build = build?.path();
                if !retired
                    || !players::is_dir(&build)
                    || build.extension().is_some_and(|ext| ext == "part")
                    || fs::read_dir(build)?.any(|e| {
                        !e.is_ok_and(|e| {
                            e.file_name() == ".longshot-builder.lock" && players::is_file(&e.path())
                        })
                    })
                {
                    return Ok(false);
                }
            }
        } else if !players::is_file(&path)
            || !(super::library::tool_file(&entry.file_name())
                || retired && entry.file_name() == versions::INSTALLING
                || entry.file_name() == RETIRED && is_retired(folder))
        {
            return Ok(false);
        }
    }
    Ok(true)
}

pub(in crate::game) fn generated_runtime_cache(relative: &Path) -> bool {
    let Some(name) = relative.file_name().and_then(|n| n.to_str()) else {
        return false;
    };
    match relative.parent() {
        Some(path) if path == Path::new("cache") => {
            name == "longshot_prepared_key.txt" || crate::storage::is_game_cache(name)
        }
        Some(path) if path == Path::new("cache/prompts") => matches!(
            name,
            "Common.bntx.zs"
                | "Common.blarc.zs"
                | "Common.key"
                | "Common.blarc.key"
                | "External.bfotf.zs"
                | "rsizetable.mod.zs"
                | "rsizetable.zs"
        ),
        _ => false,
    }
}

/// Remove only byte-identical outputs recorded by the builder. Unknown files
/// and locally edited outputs are preserved.
fn clean_recorded_builds(folder: &Path) -> Result<()> {
    use sha2::{Digest, Sha256};
    let Ok(_guards) = versions::safe_tree(folder) else {
        return Ok(());
    };
    let fetch = fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(false)
        .open(folder.join(super::library::FETCH_LOCK))?;
    let Ok(_fetch_lock) = home::FileLease::try_acquire(fetch, crate::fsx::LockMode::Exclusive)
    else {
        return Ok(());
    };
    let builds = folder.join("builds");
    if !players::is_dir(&builds) {
        return Ok(());
    }
    for entry in fs::read_dir(builds)? {
        let build = entry?.path();
        let manifest = build.join("longshot_build.json");
        if !players::is_dir(&build) || !players::is_file(&manifest) {
            continue;
        }
        let Ok(record) = home::read_json(&manifest) else {
            continue;
        };
        let Some(files) = record["files"]
            .as_object()
            .filter(|_| record["format"] == 1)
        else {
            continue;
        };
        let mut dirs = BTreeSet::new();
        let mut modified = false;
        for (name, data) in files {
            let rel = Path::new(name);
            if !rel
                .components()
                .all(|c| matches!(c, std::path::Component::Normal(_)))
            {
                continue;
            }
            let path = build.join(rel);
            if !players::is_file(&path) || path.ancestors().any(versions::linked) {
                continue;
            }
            let mut hash = Sha256::new();
            std::io::copy(&mut fs::File::open(&path)?, &mut hash)?;
            if data["sha256"].as_str() != Some(&format!("{:x}", hash.finalize())) {
                modified = true;
                continue;
            }
            fs::remove_file(&path)?;
            dirs.extend(
                path.ancestors()
                    .skip(1)
                    .take_while(|p| *p != build)
                    .map(Path::to_owned),
            );
        }
        for dir in dirs.into_iter().rev() {
            let _ = fs::remove_dir(dir);
        }
        // An in-place upgrade has already transferred the instance's runtime state.
        // Its old per-build cache is regenerable. Other nonempty files stay protected.
        if players::is_file(&folder.join(versions::INSTALLING)) && is_retired(folder) {
            for dir in [build.join("cache"), build.join("cache/prompts")] {
                if players::is_dir(&dir) && !crate::storage::linked_location(&dir)? {
                    for entry in fs::read_dir(&dir)? {
                        let path = entry?.path();
                        if players::is_file(&path)
                            && generated_runtime_cache(path.strip_prefix(&build)?)
                        {
                            fs::remove_file(path)?;
                        }
                    }
                }
            }
            remove_empty_children(&build)?;
        }
        // Keep residual runtime data and personal files on the same volume.
        // Moving them out allows the now-unused builder to be reclaimed.
        let has_residuals = fs::read_dir(&build)?.any(|e| {
            !e.is_ok_and(|e| {
                e.file_name() == ".longshot-builder.lock" || e.file_name() == "longshot_build.json"
            })
        });
        if has_residuals {
            let base = folder.parent().context("Version has no library")?;
            let preserved = tempfile::Builder::new()
                .prefix("preserved-files-")
                .tempdir_in(base)?;
            let destination = preserved.path().join(folder.file_name().unwrap());
            fs::create_dir(&destination)?;
            let target = destination.join(build.file_name().unwrap());
            fs::rename(&build, &target)?;
            let _ = preserved.keep();
            if !modified {
                fs::remove_file(target.join("longshot_build.json"))?;
            }
        } else {
            fs::remove_file(manifest)?;
        }
    }
    Ok(())
}

fn remove_empty_children(dir: &Path) -> Result<()> {
    for entry in fs::read_dir(dir)? {
        let path = entry?.path();
        if players::is_dir(&path) {
            remove_empty_children(&path)?;
            if fs::read_dir(&path)?.next().is_none() {
                fs::remove_dir(path)?;
            }
        }
    }
    Ok(())
}

#[cfg(test)]
fn prune_when_idle(home: &Path) -> Result<()> {
    prune(home, false)
}

fn prune(home: &Path, retired_only: bool) -> Result<()> {
    let profiles = players::list_profiles(home)?;
    if profiles.iter().any(|p| p.problem.is_some()) {
        return Ok(());
    }
    let folders = super::managed_folders(home)?;
    let mut keep = BTreeSet::<PathBuf>::new();
    let fetched = FETCHED.guard().clone();
    let records = registry(home)?;
    let rows = records["builds"].as_array().expect("validated registry");
    let mut keep_tags: BTreeSet<&str> = profiles
        .iter()
        .filter_map(|p| p.file["version"].as_str())
        .filter(|key| !rows.iter().any(|r| r["id"] == *key))
        .collect();
    let settings = home::settings(home)?;
    // A launcher without instance history recorded only the previous tag.
    if let Some(tag) = settings["rollbackVersion"].as_str()
        && !rows.iter().any(|row| {
            row["version"] == settings["updatedToVersion"]
                && row["previousVersion"] == tag
                && row["previousFolder"].is_string()
        })
    {
        keep_tags.insert(tag);
    }
    for row in rows {
        if row["previousFolder"].is_null()
            && let Some(tag) = row["previousVersion"].as_str()
        {
            keep_tags.insert(tag);
        }
        for key in ["folder", "nup", "romfs", "exefs"] {
            if let Some(path) = row[key].as_str() {
                keep.insert(home.join(path));
            }
        }
    }
    let retired: BTreeSet<_> = records["retiredFolders"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .map(|path| home.join(path))
        .collect();
    let mut expected = records.clone();
    let private = crate::storage::protected_paths(home)?;
    for folder in folders {
        if retired_only && !retired.contains(&folder) {
            continue;
        }
        if folder
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|tag| keep_tags.contains(tag))
            || keep_tags.contains(versions::discovered_id(&folder).as_str())
            || keep.iter().any(|path| path.starts_with(&folder))
            || super::library::removable(home, &folder).is_err()
            || folder.extension().is_some_and(|s| s == "part")
            || crate::storage::overlaps_private(&folder, &private)?
        {
            continue;
        }
        let was_retired = retired.contains(&folder);
        if (!was_retired && fetched.contains(&folder)) || (was_retired && !is_retired(&folder)) {
            continue;
        }
        if was_retired {
            if registry(home)? != expected {
                break;
            }
            clean_recorded_builds(&folder)?;
        }
        if only_tools(&folder, was_retired)? {
            remove_tools(home, &folder, was_retired, &mut expected)?;
        }
    }
    Ok(())
}

/// Stage the verified directory before publishing its removal. A registry write
/// failure restores the original folder, including its receipt and lock files.
fn remove_tools(home: &Path, folder: &Path, retired: bool, expected: &mut Value) -> Result<()> {
    let Some(base) = folder.parent() else {
        return Ok(());
    };
    let Ok(mut guards) = versions::safe_tree(folder) else {
        return Ok(());
    };
    let lock = fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(false)
        .open(folder.join(super::library::FETCH_LOCK))?;
    let Ok(lock) = home::FileLease::try_acquire(lock, crate::fsx::LockMode::Exclusive) else {
        return Ok(());
    };
    let quarantine = tempfile::Builder::new()
        .prefix(".obsolete-")
        .tempdir_in(base)?;
    let staged = quarantine.path().join("version");
    guards.push(lock);
    let Ok(guards) = versions::stage_for_removal(folder, &staged, guards) else {
        return Ok(());
    };
    let publish = (|| -> Result<()> {
        ensure!(
            only_tools(&staged, retired)? && (!retired || is_retired(&staged)),
            "The installation changed during cleanup."
        );
        if retired {
            let mut current = registry(home)?;
            ensure!(
                &current == expected,
                "The version library changed during cleanup."
            );
            super::registry::forget_retired(home, &mut current, folder);
            super::registry::write(home, &current)?;
            *expected = current;
        }
        Ok(())
    })();
    if let Err(error) = publish {
        if fs::rename(&staged, folder).is_err() {
            let kept = quarantine.keep();
            anyhow::bail!(
                "The old installation was preserved in {}: {error:#}",
                home::display(&kept)
            );
        }
        return Err(error);
    }
    drop(guards);
    quarantine.close()?;
    super::library::forget_moved_version(home, folder);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn protects_profiles_rows_fetches_rollback_partial_and_player_files() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path();
        let root = home.join("versions");
        let tags = [
            "v3.0.1", "v3.0.2", "v3.0.3", "v3.0.4", "v3.0.5", "v3.0.6", "v3.0.7",
        ];
        for tag in tags {
            let dir = root.join(tag);
            fs::create_dir_all(&dir)?;
            fs::write(dir.join(super::super::process::executable()), [])?;
        }
        let profile = home.join("profiles/default");
        fs::create_dir_all(&profile)?;
        home::write_json(
            &profile.join("profile.json"),
            &serde_json::json!({"format":1,"name":"Test","version":tags[0]}),
        )?;
        home::write_json(
            &home.join(crate::game::registry::OWN),
            &serde_json::json!({"format":1,"builds":[{"folder":root.join(tags[1]).join("builds/default")}]}),
        )?;
        home::patch_settings(home, &serde_json::json!({"rollbackVersion":tags[2]}))?;
        remember(&root.join(tags[3]));
        fs::create_dir_all(root.join(tags[4]).join("builds/incoming.part"))?;
        fs::create_dir_all(root.join(tags[5]).join("saves"))?;
        let untouched = root.join(tags[6]);
        let broken = home.join("profiles/p-12345678");
        fs::create_dir_all(&broken)?;
        fs::write(broken.join("profile.json"), "{")?;
        prune_when_idle(home)?;
        assert!(untouched.exists());
        fs::remove_dir_all(broken)?;
        prune_when_idle(home)?;
        assert!(!untouched.exists());
        for keep in &tags[..6] {
            assert!(root.join(keep).is_dir());
        }
        Ok(())
    }
    #[test]
    fn retired_build_keeps_personal_files_and_reclaims_verified_outputs() -> Result<()> {
        use sha2::{Digest, Sha256};
        let tmp = tempfile::tempdir()?;
        let home = tmp.path();
        players::initialize(home)?;
        let old = home.join("versions/old");
        let current = home.join("versions/new");
        for folder in [&old, &current] {
            super::super::fake_version(folder)?;
        }
        let game = old.join("builds/default");
        fs::write(game.join("game.bin"), b"built locally")?;
        fs::write(game.join("my-notes.txt"), b"preserve me")?;
        fs::write(old.join(versions::INSTALLING), b"")?;
        fs::create_dir_all(game.join("cache"))?;
        fs::write(game.join("cache/personal.bin"), b"custom")?;
        home::write_json(
            &game.join("longshot_build.json"),
            &serde_json::json!({"format":1,"files":{"game.bin":{"sha256":format!("{:x}",Sha256::digest(b"built locally"))}}}),
        )?;
        mark_retired(&old);
        super::super::registry::write(
            home,
            &serde_json::json!({"format":1,"retiredFolders":[old],"builds":[{"id":"old","version":"new","previousFolder":old,"folder":current.join("builds/default")}]}),
        )?;
        prune_when_idle(home)?;
        assert!(!old.exists());
        assert!(current.join("builds/default").is_dir());
        let kept = fs::read_dir(home.join("versions"))?
            .filter_map(Result::ok)
            .map(|e| e.path())
            .find(|p| p.join("old/default/my-notes.txt").is_file())
            .unwrap();
        assert_eq!(
            fs::read(kept.join("old/default/my-notes.txt"))?,
            b"preserve me"
        );
        assert_eq!(
            fs::read(kept.join("old/default/cache/personal.bin"))?,
            b"custom"
        );
        assert!(!kept.join("old/default/game.bin").exists());
        Ok(())
    }
    #[test]
    fn keeps_each_rollback_and_unregistered_playable_installs() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path();
        let root = home.join("versions");
        players::initialize(home)?;
        let build = |tag: &str| -> Result<PathBuf> {
            let dir = root.join(tag);
            let game = dir.join("builds/default");
            fs::create_dir_all(&game)?;
            fs::write(dir.join(super::super::process::executable()), [])?;
            fs::write(game.join("game.bin"), b"fixture")?;
            Ok(dir)
        };
        let discovered = build("nightly-unregistered")?;
        let first = build("nightly-current-a")?;
        let second = build("nightly-current-b")?;
        let previous_a = build("nightly-previous-a")?;
        let previous_b = build("nightly-previous-b")?;
        let obsolete = build("nightly-obsolete")?;
        fs::remove_file(obsolete.join("builds/default/game.bin"))?;
        let customized = build("nightly-with-local-files")?;
        fs::write(customized.join("player-notes.txt"), b"diary")?;
        let recreated = build("nightly-recreated")?;
        for old in [&obsolete, &previous_a, &previous_b, &first, &customized] {
            mark_retired(old);
        }
        let retired = [
            &obsolete,
            &previous_a,
            &previous_b,
            &first,
            &recreated,
            &customized,
        ]
        .map(|folder| folder.strip_prefix(home).unwrap());
        let records = serde_json::json!({
            "format":1,
            "builds":[
                {"id":"first","version":"a","folder":first.join("builds/default"),
                    "previousVersion":"shared-tag","previousFolder":previous_a.strip_prefix(home)?},
                {"id":"second","version":"b","folder":second.join("builds/default"),
                    "previousVersion":"shared-tag","previousFolder":previous_b}
            ],
            "retiredFolders": retired
        });
        home::write_json(&home.join(crate::game::registry::OWN), &records)?;
        prune_when_idle(home)?;
        for path in [
            &discovered,
            &first,
            &second,
            &previous_a,
            &previous_b,
            &recreated,
        ] {
            assert!(
                path.join("builds/default/game.bin").is_file(),
                "{}",
                path.display()
            );
        }
        assert_eq!(fs::read(customized.join("player-notes.txt"))?, b"diary");
        assert!(!obsolete.exists());
        let stored = registry(home)?;
        assert!(
            !stored["retiredFolders"]
                .as_array()
                .unwrap()
                .contains(&serde_json::json!(obsolete.strip_prefix(home)?))
        );
        assert!(
            stored["retiredFolders"]
                .as_array()
                .unwrap()
                .contains(&serde_json::json!(previous_a.strip_prefix(home)?))
        );
        let replaced_on_disk = build("nightly-obsolete")?;
        prune_when_idle(home)?;
        assert!(replaced_on_disk.join("builds/default/game.bin").is_file());
        Ok(())
    }
}
