//! Stable v1.3 stored all builds' saves under one game user folder. Keep the
//! originals playable by stable and import the complete save transaction together.
use super::{disk, profiles, running, saves};
use crate::{game::registry, home};
use anyhow::{Context, Result, ensure};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
};

const PARTS: [&str; 2] = ["save", "save-work"];
const MARKER: &str = "stable-saves-v1.json";

pub(crate) fn user_folder(control: &Path) -> Result<PathBuf> {
    if home::is_portable(control) {
        #[cfg(unix)]
        {
            // Older AppImages used their own HOME for the game's shared user.
            let old = control.join(".local/share/longshot");
            if old.join("user").is_dir() {
                return Ok(old.join("user"));
            }
            if old.join("save").is_dir() {
                return Ok(old);
            }
        }
        return Ok(control.join("user"));
    }
    #[cfg(windows)]
    {
        Ok(home::roaming_data_folder()?.join("Longshot"))
    }
    #[cfg(not(windows))]
    {
        let _ = control;
        let shared = profiles::data_root()?.join("longshot");
        let user = shared.join("user");
        Ok(if user.is_dir() { user } else { shared })
    }
}

fn account(save: &Path) -> Result<Option<PathBuf>> {
    let mut accounts = Vec::new();
    for dir in disk::dirs(save)? {
        if disk::is_file(&dir.join("account.sav")) || !saves::slots(&dir)?.is_empty() {
            accounts.push(dir);
        }
    }
    // v1.3.1 Saves.ShownProfile prefers the game account, then the first save.
    Ok(accounts
        .iter()
        .find(|p| {
            p.file_name()
                .is_some_and(|id| id.eq_ignore_ascii_case(saves::UID))
        })
        .or_else(|| accounts.first())
        .cloned())
}

/// A stable install can have an empty or missing registry after its last build
/// is removed. Its own persisted settings still identify where the saves belong.
pub(crate) fn empty_stable_registry(control: &Path) -> Result<bool> {
    let settings = home::read_json(&control.join("settings.json"))?;
    if !home::stable_settings(&settings) {
        return Ok(false);
    }
    if !control.join(registry::STABLE).try_exists()? {
        return Ok(true);
    }
    let ledger = registry::read(control, registry::STABLE)?;
    Ok(ledger["builds"].as_array().is_some_and(Vec::is_empty)
        && (ledger["home"].as_str().map(Path::new) == Some(control) || home::is_portable(control)))
}

fn old_game_folders(folder: &Path, windows: bool) -> Result<Vec<PathBuf>> {
    let exe = if windows { "longshot.exe" } else { "longshot" };
    let renderer = if windows {
        "longshot_vk.dll"
    } else {
        "longshot_vk.so"
    };
    let is_game = |p: &Path| {
        if [
            "longshot_build.json",
            "longshot_builder.json",
            "longshot.json",
        ]
        .iter()
        .any(|name| disk::is_file(&p.join(name)))
        {
            return false;
        }
        disk::is_file(&p.join("longshot_version.txt"))
            || disk::is_file(&p.join("bin").join(exe))
            || disk::is_file(&p.join(exe)) && disk::is_file(&p.join(renderer))
    };
    let mut games = Vec::new();
    for found in disk::dirs(folder)? {
        if is_game(&found) {
            games.push(found);
            continue;
        }
        let name = found
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or_default();
        let track = name.split_once(" (").map_or(name, |(track, _)| track);
        if matches!(track, "stable" | "nightly" | "pr" | "Longshot") {
            games.extend(disk::dirs(&found)?.into_iter().filter(|d| is_game(d)));
        }
    }
    Ok(games)
}

/// Stable deliberately leaves older instances on disk when they still hold the
/// player's mods. Its list of installations omits these folders.
pub(crate) fn old_builds(control: &Path) -> Result<Vec<PathBuf>> {
    let settings = home::read_json(&control.join("settings.json"))?;
    let standard = if home::is_portable(control) {
        control.join("builds")
    } else {
        std::env::home_dir()
            .context("The home folder is unavailable.")?
            .join("Games/Longshot")
    };
    let library = settings["buildsFolder"]
        .as_str()
        .map(Path::new)
        .filter(|path| path.is_absolute())
        .map_or(standard, |path| crate::fsx::plain(path).into_owned());
    old_game_folders(&library, cfg!(windows))
}

pub(super) fn sources(control: &Path) -> Result<Vec<PathBuf>> {
    let builds = registry::stable_only(control)?;
    if builds.is_empty() && !empty_stable_registry(control)? {
        return Ok(Vec::new());
    }
    let mut users = vec![user_folder(control)?];
    if cfg!(windows) {
        users.extend(builds.iter().map(|build| build.join("user")));
        let portable = home::is_portable(control);
        let standard = if portable {
            control.join("builds")
        } else {
            std::env::home_dir()
                .context("The home folder is unavailable.")?
                .join("Games/Longshot")
        };
        users.extend(old_builds(control)?.iter().map(|build| build.join("user")));
        if !portable {
            users.push(standard.join("user"));
        }
    }
    let mut found = Vec::new();
    let mut taken = std::collections::HashSet::new();
    for user in users {
        let save = user.join("save");
        let key = if disk::is_dir(&save) && account(&save)?.is_some() {
            save.canonicalize()?
        } else {
            let pending = user.join("save-work");
            if !disk::is_dir(&pending) || empty(&pending)? {
                continue;
            }
            pending.canonicalize()?
        };
        if taken.insert(key) {
            found.push(user);
        }
    }
    Ok(found)
}

fn same_save(src: &Path, dst: &Path) -> Result<bool> {
    if !src.try_exists()? {
        return Ok(!dst.try_exists()?);
    }
    if !disk::is_dir(dst) {
        return Ok(false);
    }
    let preferred = account(src)?;
    let normalize = !src.join(saves::UID).try_exists()?;
    let entries = fs::read_dir(src)?.collect::<std::io::Result<Vec<_>>>()?;
    if entries.len() != fs::read_dir(dst)?.count() {
        return Ok(false);
    }
    for entry in entries {
        let expected = if normalize && Some(entry.path()).as_ref() == preferred.as_ref() {
            dst.join(saves::UID)
        } else {
            dst.join(entry.file_name())
        };
        if !disk::same_tree(&entry.path(), &expected)? {
            return Ok(false);
        }
    }
    Ok(true)
}

fn copied(user: &Path, dest: &Path) -> Result<bool> {
    if !same_save(&user.join("save"), &dest.join("save"))? {
        return Ok(false);
    }
    let (src, dst) = (user.join("save-work"), dest.join("save-work"));
    if src.try_exists()? {
        disk::same_tree(&src, &dst)
    } else {
        Ok(!dst.try_exists()?)
    }
}

fn empty(dir: &Path) -> Result<bool> {
    if !dir.try_exists()? {
        return Ok(true);
    }
    disk::require_dir(dir)?;
    for item in fs::read_dir(dir)? {
        let path = item?.path();
        if !disk::is_dir(&path) || !empty(&path)? {
            return Ok(false);
        }
    }
    Ok(true)
}

fn seed(user: &Path, dest: &Path) -> Result<()> {
    for part in PARTS {
        let src = user.join(part);
        if src.try_exists()? {
            disk::disjoint(&src, dest)?;
            disk::copy_tree(&src, &dest.join(part))?;
        }
    }
    let save = user.join("save");
    if !save.join(saves::UID).try_exists()?
        && let Some(used) = account(&save)?
    {
        fs::rename(
            dest.join("save").join(used.file_name().unwrap()),
            dest.join("save").join(saves::UID),
        )?;
    }
    running::saves_idle(&[user, dest])?;
    ensure!(
        copied(user, dest)?,
        "Stable saves changed while being copied. Open the launcher again after closing the game."
    );
    Ok(())
}

fn copy_options(user: &Path, profile: &Path) -> Result<()> {
    let source = user.join("longshot_options.ini");
    let dest = profile.join("settings.ini");
    if disk::is_file(&source) && !dest.try_exists()? {
        let staged = tempfile::NamedTempFile::new_in(profile)?;
        fs::copy(&source, staged.path())?;
        ensure!(
            disk::same_tree(&source, staged.path())?,
            "Stable game settings changed while being copied."
        );
        staged.persist_noclobber(dest)?;
    }
    Ok(())
}

fn import(control: &Path, user: &Path) -> Result<String> {
    running::saves_idle(&[user])?;
    let all = profiles::list(control)?;
    let root = home::data_dir(control)?.join("profiles");
    for profile in &all {
        if profile.problem.is_none() && copied(user, &profile.dir.join("saves"))? {
            return Ok(profile.id.clone());
        }
    }
    if let Some(profile) = all
        .iter()
        .find(|p| p.id == "default" && p.problem.is_none())
    {
        let dest = profile.dir.join("saves");
        running::saves_idle(&[&dest])?;
        let edit = saves::SaveEdit::begin(&dest)?;
        if empty(&dest)? {
            edit.transact(|stage| {
                seed(user, stage)?;
                running::saves_idle(&[user, &dest])?;
                ensure!(
                    empty(&dest)?,
                    "The destination saves changed during import."
                );
                copy_options(user, &profile.dir)
            })?;
            return Ok(profile.id.clone());
        }
    }
    ensure!(
        all.len() < 16,
        "All 16 profiles are in use. Stable saves are available in Saves → Import."
    );
    let name = (1..)
        .map(|n| {
            if n == 1 {
                "Stable saves".into()
            } else {
                format!("Stable saves ({n})")
            }
        })
        .find(|name| !all.iter().any(|p| p.name().eq_ignore_ascii_case(name)))
        .unwrap();
    let stage = tempfile::tempdir_in(&root)?;
    let dest = stage.path().join("saves");
    fs::create_dir(&dest)?;
    seed(user, &dest)?;
    copy_options(user, stage.path())?;
    home::write_json(
        &stage.path().join("profile.json"),
        &profiles::default_file(&name),
    )?;
    profiles::publish_new(&root, stage)
}

#[derive(Default, Deserialize, Serialize)]
#[serde(default)]
struct Migration {
    complete: bool,
    sources: Vec<Imported>,
}

#[derive(Deserialize, Serialize)]
struct Imported {
    user: PathBuf,
    profile: String,
}

fn progress(control: &Path) -> Result<Migration> {
    Ok(serde_json::from_value(home::read_json(
        &control.join(MARKER),
    )?)?)
}

pub(crate) fn profile_for_user(control: &Path, user: &Path) -> Result<Option<String>> {
    let actual = user.canonicalize().ok();
    Ok(progress(control)?
        .sources
        .into_iter()
        .find(|record| {
            record.user == user
                || actual
                    .as_ref()
                    .is_some_and(|p| record.user.canonicalize().as_ref().ok() == Some(p))
        })
        .map(|record| record.profile))
}

pub(crate) fn selected_profile(control: &Path) -> Result<Option<String>> {
    Ok(progress(control)?
        .sources
        .into_iter()
        .next()
        .map(|source| source.profile))
}

pub(crate) fn migrate(control: &Path) -> Result<()> {
    let mut progress = progress(control)?;
    if progress.complete {
        return Ok(());
    }
    let users = sources(control)?;
    if users.is_empty() {
        return Ok(());
    }
    let marker = control.join(MARKER);
    for user in users {
        if progress.sources.iter().any(|record| record.user == user) {
            continue;
        }
        let profile = import(control, &user)?;
        progress.sources.push(Imported { user, profile });
        home::write_json(&marker, &serde_json::to_value(&progress)?)?;
    }
    progress.complete = true;
    home::write_json(&marker, &serde_json::to_value(&progress)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_only_game_folders_in_stables_pre_gather_windows_layout() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let direct = root.join("old game");
        let nested = root.join("nightly (2)/v2.0.0");
        fs::create_dir_all(direct.join("bin"))?;
        fs::write(direct.join("bin/longshot.exe"), [])?;
        fs::create_dir_all(&nested)?;
        fs::write(nested.join("longshot_version.txt"), "v2.0.0")?;
        fs::create_dir_all(root.join("unrelated/save"))?;
        fs::write(root.join("unrelated/longshot.exe"), [])?;
        let mut expected = vec![direct, nested];
        expected.sort();
        let mut found = old_game_folders(root, true)?;
        found.sort();
        assert_eq!(found, expected);
        Ok(())
    }
}
