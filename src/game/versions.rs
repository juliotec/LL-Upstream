mod modules;
use crate::{home, players};
use anyhow::{Context, Result, bail, ensure};
pub(super) use modules::set_modules;
use serde_json::{Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
    process::{Child, Stdio},
};

use super::process::{Context as Builder, executable};

pub(super) const INSTALLING: &str = ".launcher-installing";

#[derive(Clone, Debug)]
pub(super) struct Version {
    pub tag: String,
    pub folder: PathBuf,
    pub id: String,
    pub default: bool,
    modules: Option<Vec<&'static str>>,
    registered: bool,
    retired: bool,
    previous_tags: Vec<String>,
}

impl Version {
    fn is_listed(&self) -> bool {
        self.registered
            || (!self.retired
                && !self.folder.join(INSTALLING).exists()
                && self.folder.join("builds/default").is_dir())
    }

    fn owns_build(&self, path: &Path) -> bool {
        is_build_in(path, &self.folder.join("builds"))
    }
}

pub(super) fn is_build_in(path: &Path, builds: &Path) -> bool {
    path.parent().is_some_and(|dir| {
        dir == builds
            || dir
                .canonicalize()
                .is_ok_and(|dir| builds.canonicalize().is_ok_and(|builds| dir == builds))
    })
}
pub(super) fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 128
        && name != "."
        && name != ".."
        && name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._+-".contains(&b))
}

pub(super) fn linked(path: &Path) -> bool {
    crate::fsx::linked_location(path).unwrap_or(false)
}

pub fn managed_folder(folder: &Path) -> bool {
    let program = folder.join(executable());
    folder
        .file_name()
        .and_then(|name| name.to_str())
        .is_some_and(valid_name)
        && !linked(folder)
        && folder.is_dir()
        && !linked(&program)
        && program.is_file()
}

fn managed_row(home: &Path, row: &Value) -> bool {
    let Some(builds) = row["folder"].as_str().and_then(|f| Path::new(f).parent()) else {
        return false;
    };
    builds.ends_with("builds")
        && builds
            .parent()
            .is_some_and(|v| super::owned_folder(home, v) && managed_folder(v))
}

pub(super) fn registry(home_dir: &Path) -> Result<Value> {
    super::registry::read(home_dir, crate::game::registry::OWN)
}

pub(super) fn installed(home_dir: &Path) -> Result<Vec<Version>> {
    let mut versions = Vec::new();
    for folder in super::managed_folders(home_dir)? {
        let tag = folder
            .file_name()
            .context("Invalid version folder")?
            .to_string_lossy()
            .into_owned();
        versions.push(Version {
            tag: tag.clone(),
            id: tag,
            folder,
            default: false,
            modules: None,
            registered: false,
            retired: false,
            previous_tags: vec![],
        });
    }
    let registry = registry(home_dir)?;
    for row in registry["builds"].as_array().expect("validated registry") {
        let Some(folder) = row["folder"]
            .as_str()
            .filter(|s| !s.is_empty())
            .map(PathBuf::from)
        else {
            continue;
        };
        let id = row["id"].as_str().unwrap_or_default();
        let default = row["default"].as_bool().unwrap_or(false);
        if let Some(v) = versions.iter_mut().find(|v| v.owns_build(&folder)) {
            if let Some(tag) = row["version"].as_str().filter(|tag| valid_name(tag)) {
                v.tag = tag.to_owned();
            }
            if !id.is_empty() {
                v.id = id.to_owned();
            }
            v.previous_tags = row["previousTags"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|tag| tag.as_str().map(str::to_owned))
                .collect();
            v.modules = modules::recorded(row);
            v.registered = true;
            v.default |= default;
        }
    }
    for folder in registry["retiredFolders"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
    {
        if let Some(old) = versions
            .iter_mut()
            .find(|v| !v.registered && v.folder == home_dir.join(folder))
        {
            old.retired = super::prune::is_retired(&old.folder);
        }
    }
    versions.sort_by(|a, b| {
        b.default
            .cmp(&a.default)
            .then_with(|| b.tag.cmp(&a.tag))
            .then_with(|| b.registered.cmp(&a.registered))
    });
    Ok(versions)
}

/// A prefetched release contains tools only, so it does not appear in the player's list.
pub(super) fn listed(home_dir: &Path) -> Result<Vec<Version>> {
    Ok(installed(home_dir)?
        .into_iter()
        .filter(Version::is_listed)
        .collect())
}

/// All managed version directories, including prefetched tools and retired builds.
pub(crate) fn storage_versions(home: &Path) -> Result<Vec<(PathBuf, bool)>> {
    Ok(installed(home)?
        .into_iter()
        .map(|v| {
            let listed = v.is_listed();
            (v.folder, listed)
        })
        .collect())
}

pub(super) fn current<'a>(versions: &'a [Version], profile: &Value) -> Option<&'a Version> {
    profile["version"]
        .as_str()
        .and_then(|key| {
            versions
                .iter()
                .find(|v| v.id == key)
                .or_else(|| versions.iter().find(|v| v.tag == key))
                .or_else(|| {
                    versions
                        .iter()
                        .find(|v| v.previous_tags.iter().any(|tag| tag == key))
                })
        })
        .or_else(|| versions.first())
}

pub(super) fn selected(home_dir: &Path, requested: Option<&Path>) -> Result<Version> {
    match requested {
        Some(folder) => by_folder(home_dir, folder),
        None => for_profile(home_dir, &players::current_dir(home_dir)?),
    }
}

fn require_deck_card(folder: &Path) -> Result<()> {
    if cfg!(target_os = "linux")
        && let Ok(relative) = folder.strip_prefix("/run/media/deck")
        && let Some(card) = relative.components().next()
    {
        ensure!(
            Path::new("/run/media/deck").join(card).is_dir(),
            "Reinsert the SD card that contains this Longshot version, then try again."
        );
    }
    Ok(())
}

fn by_folder(home_dir: &Path, folder: &Path) -> Result<Version> {
    let folder = std::path::absolute(folder)?;
    require_deck_card(&folder)?;
    listed(home_dir)?
        .into_iter()
        .find(|v| folder == v.folder || v.owns_build(&folder))
        .context("This folder does not belong to an installed Longshot version.")
}

fn for_profile(home_dir: &Path, profile: &Path) -> Result<Version> {
    let prefs = home::read_json(&profile.join("profile.json"))?;
    if let Some(id) = prefs["version"].as_str() {
        let registry = registry(home_dir)?;
        if let Some(folder) = registry["builds"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|row| row["id"] == id || row["version"] == id)
            .and_then(|row| row["folder"].as_str())
        {
            require_deck_card(Path::new(folder))?;
        }
    }
    current(&listed(home_dir)?, &prefs)
        .cloned()
        .context("Pick your game files and build a version of Longshot first.")
}

pub fn select_folder(home_dir: &Path, folder: &Path) -> Result<()> {
    let version = selected(home_dir, Some(folder))?;
    remember(home_dir, &version, false)
}

pub(super) fn remember(home_dir: &Path, version: &Version, played: bool) -> Result<()> {
    remember_for(&players::current_dir(home_dir)?, version, played)
}

fn remember_for(profile: &Path, version: &Version, played: bool) -> Result<()> {
    players::patch_profile(profile, |object| {
        object.insert("version".into(), json!(version.id));
        if played {
            object.insert("last_played".into(), json!(chrono::Utc::now().to_rfc3339()));
        }
    })
}

fn prepare_profile(builder: &Builder) -> Result<()> {
    fs::create_dir_all(builder.profile.join("saves"))?;
    let data = home::read_json(&builder.profile.join("profile.json"))?;
    if data["game"].as_str().is_some_and(|game| !game.is_empty()) {
        return Ok(());
    }
    let data = registry(&builder.home)?;
    let versions = listed(&builder.home)?;
    let mut rows: Vec<_> = data["builds"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|row| {
            row["folder"].as_str().is_some_and(|f| {
                versions
                    .iter()
                    .any(|version| version.owns_build(Path::new(f)))
            })
        })
        .collect();
    rows.sort_by_key(|row| std::cmp::Reverse((row["default"] == true, row["updated"].as_str())));
    let known = super::source::read(&builder.settings)
        .or_else(|| rows.into_iter().find_map(super::source::read));
    if let Some(game) = known {
        players::patch_profile(&builder.profile, |profile| {
            if profile
                .get("game")
                .and_then(Value::as_str)
                .is_none_or(str::is_empty)
            {
                profile.insert("game".into(), json!(game));
            }
        })?;
    }
    Ok(())
}

pub(crate) struct Session {
    pub(super) child: Child,
    #[cfg(windows)]
    job: Option<super::process::ChildJob>,
    pub(super) output: Option<super::launch::LaunchOutput>,
    pub(super) steam_opener: bool,
    pub(super) folder: PathBuf,
    pub(super) profile: PathBuf,
}

impl Session {
    pub(super) fn cancel_rebuild(&mut self) -> std::io::Result<()> {
        #[cfg(windows)]
        self.job
            .as_ref()
            .ok_or_else(|| std::io::Error::other("The builder has no launch job."))?
            .terminate()?;
        #[cfg(not(windows))]
        super::process::kill_tree(&mut self.child);
        Ok(())
    }
}

pub fn play(
    home_dir: &Path,
    folder: Option<&Path>,
    profile: Option<&Path>,
    confirm: impl FnOnce(&[crate::mods::trust::Grant]) -> Result<bool>,
) -> Result<Option<Session>> {
    let launch = if profile.is_some() {
        LaunchMode::PinnedShortcut
    } else {
        LaunchMode::Shortcut
    };
    let profile = match profile {
        Some(path) => Builder::for_profile(home_dir, path)?.profile,
        None => players::current_dir(home_dir)?,
    };
    let version = match folder {
        Some(folder) => by_folder(home_dir, folder)?,
        None => for_profile(home_dir, &profile)?,
    };
    let exe = version.folder.join(executable());
    let grants = crate::mods::trust::pending(home_dir, &exe, &profile)?;
    if !grants.is_empty() {
        if !confirm(&grants)? {
            return Ok(None);
        }
        crate::mods::trust::approve_all(home_dir, &exe, &profile, &grants)?;
    }
    start_for(home_dir, &version.folder, &profile, launch).map(Some)
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum LaunchMode {
    Window,
    Shortcut,
    /// A Steam entry for a specific profile must run directly to avoid reopening itself.
    PinnedShortcut,
}

pub(super) fn start_for(
    home_dir: &Path,
    folder: &Path,
    profile: &Path,
    mode: LaunchMode,
) -> Result<Session> {
    let builder = Builder::for_profile(home_dir, profile)?;
    let version = by_folder(home_dir, folder)?;
    builder.prepare_mod_store()?;
    prepare_profile(&builder)?;
    let exe = version.folder.join(executable());
    // A busy or unwritable profile must not turn a successful spawn into another Play request.
    let remember_play = || {
        if let Err(e) = remember_for(profile, &version, true) {
            eprintln!("Could not remember the last played version: {e}");
        }
    };
    if mode != LaunchMode::PinnedShortcut
        && let Some(opener) = super::steam::route(&builder, &exe)
    {
        crate::diagnostics::event("Game launched through Steam");
        remember_play();
        return Ok(Session {
            child: opener,
            #[cfg(windows)]
            job: None,
            output: None,
            steam_opener: true,
            folder: version.folder,
            profile: builder.profile,
        });
    }
    super::sharing::before_play(
        &builder,
        &exe,
        &super::info::launch_folder(&builder, &version.folder)?,
    )?;
    let enabled = version.modules()?;
    modules::prepare_licenses(&builder, &enabled)?;
    let mut cmd = builder.command_for(&exe, true, mode == LaunchMode::Window)?;
    if !cfg!(windows)
        && enabled.contains(&"fsr4")
        && let Some(library) = super::upscaler::library_for(&version.folder)
        && let Some(dir) = library.parent()
    {
        cmd.env("RX_VK_FSR_DIR", dir);
    }
    let output = if cfg!(windows) && mode != LaunchMode::Window {
        "user"
    } else {
        "json"
    };
    cmd.arg("--profile")
        .arg(&builder.profile)
        .args(["--output", output, "--cache"])
        .arg(&builder.cache)
        .arg("--no-self-update")
        .arg("--upscaler")
        .arg(if enabled.is_empty() {
            "none".into()
        } else {
            enabled.join(",")
        });
    if mode == LaunchMode::Window {
        cmd.env("LONGSHOT_LAUNCHER_STARTUP", "1");
    }
    if builder.statistics() {
        cmd.arg("--send-build-statistics");
    }
    cmd.stdin(Stdio::null());
    let output = Some(
        super::launch::LaunchOutput::attach(&mut cmd, &builder.home.join("logs"))
            .context("Could not open game launch output")?,
    );
    #[cfg(windows)]
    let (job, child) = if mode == LaunchMode::Window {
        // Explicit cancellation can stop the tree. Closing the launcher releases only its handle.
        let (job, child) = super::process::ChildJob::spawn(&mut cmd, false)
            .context("Longshot could not start in its Windows job")?;
        (Some(job), child)
    } else {
        (
            None,
            super::process::spawn_builder(&mut cmd).context("Longshot could not start")?,
        )
    };
    #[cfg(not(windows))]
    let child = super::process::spawn_builder(&mut cmd).context("Longshot could not start")?;
    crate::diagnostics::event("Game builder started");
    remember_play();
    Ok(Session {
        child,
        #[cfg(windows)]
        job,
        output,
        steam_opener: false,
        folder: version.folder,
        profile: builder.profile,
    })
}

fn version_row(id: &str, tag: &str, folder: &Path, now: &str, default: bool) -> Value {
    json!({
        "id": id,
        "name": tag,
        "version": tag,
        "folder": folder,
        "created": now,
        "updated": now,
        "default": default,
        "channel": super::channel::Channel::from_prefix(tag).registry_name()
    })
}

pub(super) fn register(
    home_dir: &Path,
    tag: &str,
    game: &Path,
    folder: &Path,
    upscalers: &[&str],
    replaces: Option<&Version>,
) -> Result<()> {
    let mut data = registry(home_dir)?;
    data["home"] = json!(home_dir);
    let rows = data["builds"].as_array_mut().expect("validated registry");
    let now = chrono::Utc::now().to_rfc3339();
    let old = replaces.and_then(|version| {
        rows.iter().position(|row| {
            row["folder"]
                .as_str()
                .is_some_and(|f| version.owns_build(Path::new(f)))
        })
    });
    let existing = rows.iter().position(|row| {
        row["folder"]
            .as_str()
            .is_some_and(|p| Path::new(p) == folder)
    });
    ensure!(
        replaces.is_none_or(|v| !v.registered || old.is_some()),
        "The installation changed during the update. Choose it again."
    );
    ensure!(
        replaces.is_none() || existing.is_none() || existing == old,
        "Another installation already uses this build folder."
    );
    let pos = existing.or(old);
    let first = !rows.iter().any(|r| managed_row(home_dir, r));
    let index = match pos {
        Some(i) => i,
        None => {
            // Each destination has one row. Preserve its identity on updates.
            let preferred = replaces.map_or(tag, |old| old.id.as_str());
            let mut id = preferred.to_owned();
            let mut suffix = 2;
            while rows.iter().any(|r| r["id"] == id) {
                id = format!("{preferred}-{suffix}");
                suffix += 1;
            }
            rows.push(version_row(&id, tag, folder, &now, first));
            rows.len() - 1
        }
    };
    if let Some(old) = replaces.filter(|v| !v.owns_build(folder)) {
        super::registry::record_replacement(home_dir, &mut data, index, &old.folder, &old.tag)?;
    }
    if let Some(active) = folder.parent().and_then(Path::parent)
        && let Some(retired) = data["retiredFolders"].as_array_mut()
    {
        retired.retain(|path| path.as_str().is_none_or(|p| home_dir.join(p) != active));
    }
    let row = &mut data["builds"][index];
    row["channel"] = json!(super::channel::Channel::from_prefix(tag).registry_name());
    row["version"] = json!(tag);
    row["folder"] = json!(folder);
    row["updated"] = json!(now);
    for (key, value) in super::source::fields(game)? {
        row[key] = value;
    }
    row["nupInPlace"] = json!(!game.is_dir());
    modules::write(row, upscalers);
    if !cfg!(windows)
        && let Some(old) = replaces
        && old.stored_modules()?.contains(&"xess")
    {
        row["xess"] = json!(true);
    }
    for key in ["bytes", "counted", "frees"] {
        row[key] = Value::Null;
    }
    crate::game::registry::write(home_dir, &data)
}

pub(super) fn make_default(home_dir: &Path, selected: &Version) -> Result<()> {
    let mut registry = registry(home_dir)?;
    let rows = registry["builds"].as_array_mut().unwrap();
    let pos = rows.iter().position(|r| {
        r["id"] == selected.id
            && r["version"] == selected.tag
            && r["folder"]
                .as_str()
                .is_some_and(|f| selected.owns_build(Path::new(f)))
    });
    let index = match pos {
        Some(i) => i,
        None => {
            let now = chrono::Utc::now().to_rfc3339();
            rows.push(version_row(
                &selected.tag,
                &selected.tag,
                &selected.folder.join("builds/default"),
                &now,
                true,
            ));
            rows.len() - 1
        }
    };
    for (i, row) in rows.iter_mut().enumerate() {
        if i == index || managed_row(home_dir, row) {
            row["default"] = json!(i == index);
        }
    }
    crate::game::registry::write(home_dir, &registry)
}

pub(super) fn safe_tree(folder: &Path) -> Result<Vec<home::FileLease>> {
    let mut locks = Vec::new();
    ensure!(
        !linked(folder) && folder.is_dir(),
        "This version folder is missing or is a link."
    );
    let mut dirs = vec![folder.to_owned()];
    while let Some(dir) = dirs.pop() {
        for e in fs::read_dir(&dir)? {
            let e = e?;
            let name = e.file_name().to_string_lossy().to_ascii_lowercase();
            ensure!(
                !matches!(
                    name.as_str(),
                    "save"
                        | "save-work"
                        | "saves"
                        | "user"
                        | "profile"
                        | "profiles"
                        | "profile.json"
                        | "settings.ini"
                ) && !name.ends_with(".nup")
                    && !name.ends_with(".nx"),
                "This folder contains saves, settings, or a game copy. Its contents have been kept."
            );
            let kind = e.file_type()?;
            ensure!(
                !linked(&e.path()) || !e.path().is_dir(),
                "A folder here is a symbolic link."
            );
            if kind.is_dir() {
                dirs.push(e.path());
            }
            if name == ".longshot-builder.lock" && kind.is_file() {
                locks.push(
                    home::FileLease::try_acquire(
                        fs::File::open(e.path())?,
                        crate::fsx::LockMode::Exclusive,
                    )
                    .context("This version is being used by a builder.")?,
                );
            }
        }
    }
    Ok(locks)
}

pub(super) fn overlaps_private(folder: &Path, sources: &[PathBuf]) -> Result<bool> {
    let folder = folder.canonicalize()?;
    Ok(sources.iter().any(|source| {
        let path = source.canonicalize().unwrap_or_else(|_| source.clone());
        crate::fsx::overlaps(&folder, &path)
    }))
}
pub(super) fn delete(home_dir: &Path, version: &Version) -> Result<()> {
    let _library = super::move_lease(home_dir)?;
    players::ensure_idle()?;
    delete_when_idle(home_dir, version)
}

pub(super) fn delete_when_idle(home_dir: &Path, version: &Version) -> Result<()> {
    let mut data = registry(home_dir)?;
    super::registry::forget_retired(home_dir, &mut data, &version.folder);
    let rows = data["builds"].as_array_mut().unwrap();
    rows.retain(|r| {
        let path = r["folder"].as_str().map(Path::new);
        !path.is_some_and(|p| p.starts_with(version.folder.join("builds")))
    });
    if !rows
        .iter()
        .any(|v| managed_row(home_dir, v) && v["default"] == true)
        && let Some(v) = rows.iter_mut().find(|row| managed_row(home_dir, row))
    {
        v["default"] = Value::Bool(true);
    }
    ensure!(
        super::owned_folder(home_dir, &version.folder) && managed_folder(&version.folder),
        "Only managed version folders can be deleted."
    );
    ensure!(
        !overlaps_private(&version.folder, &crate::storage::protected_paths(home_dir)?)?,
        "This version contains a registered game source and has been kept."
    );
    super::library::removable(home_dir, &version.folder)?;
    let guards = safe_tree(&version.folder)?;
    let parent = version.folder.parent().unwrap();
    let quarantine = tempfile::Builder::new()
        .prefix(".removing-")
        .tempdir_in(parent)?;
    let staged = quarantine.path().join(&version.tag);
    let guards = stage_for_removal(&version.folder, &staged, guards)
        .context("Could not move the version folder before removal")?;
    if let Err(e) = crate::game::registry::write(home_dir, &data) {
        // Retain the entire staged folder if a concurrent mover prevents rollback.
        if fs::rename(&staged, &version.folder).is_err() {
            let kept = quarantine.keep();
            bail!(
                "{e}. The version folder was kept at {}.",
                home::display(&kept)
            );
        }
        return Err(e);
    }
    drop(guards);
    quarantine.close().context(
        "The version was removed from the launcher, but its files could not all be deleted.",
    )?;
    Ok(())
}

pub(super) fn stage_for_removal(
    folder: &Path,
    staged: &Path,
    mut guards: Vec<home::FileLease>,
) -> std::io::Result<Vec<home::FileLease>> {
    // Windows refuses to move a directory while files inside it are open.
    // Check the leases first, then let the rename reject any remaining opens.
    if cfg!(windows) {
        guards.clear();
    }
    fs::rename(folder, staged)?;
    Ok(guards)
}

#[cfg(test)]
mod removal {
    use super::*;
    #[test]
    fn removes_an_idle_version_with_a_builder_lock_file() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path();
        let folder = home.join("versions/nightly-1");
        crate::game::fake_version(&folder)?;
        let build = folder.join("builds/default");
        let lock = build.join(".longshot-builder.lock");
        let guard = home::FileLease::try_acquire(
            fs::File::create(&lock)?,
            crate::fsx::LockMode::Exclusive,
        )?;
        register(home, "nightly-1", home, &build, &[], None)?;
        let version = installed(home)?.pop().unwrap();
        let before = registry(home)?;
        let error = delete_when_idle(home, &version).unwrap_err();
        assert_eq!(
            error.to_string(),
            "This version is being used by a builder."
        );
        assert!(build.is_dir());
        assert_eq!(registry(home)?, before);

        drop(guard);
        delete_when_idle(home, &version)?;
        assert!(!folder.exists());
        assert!(registry(home)?["builds"].as_array().unwrap().is_empty());
        Ok(())
    }

    #[test]
    fn preserves_saves_held_inside_a_version() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let save = temp.path().join("builds/default/user/save");
        fs::create_dir_all(&save)?;
        fs::write(save.join("slot.sav"), [])?;
        assert!(safe_tree(temp.path()).is_err());
        assert!(save.join("slot.sav").is_file());
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn downloaded_tools_are_not_a_playable_game() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let version = temp.path().join("versions/v3.2.0");
        fs::create_dir_all(&version)?;
        fs::write(version.join(executable()), [])?;
        assert!(managed_folder(&version));
        assert_eq!(installed(temp.path())?.len(), 1);
        assert!(listed(temp.path())?.is_empty());
        fs::create_dir_all(version.join("builds/default"))?;
        assert_eq!(listed(temp.path())?.len(), 1);
        Ok(())
    }

    #[test]
    fn updating_one_instance_keeps_other_instances_and_profile_selections() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let install = |name: &str| -> Result<PathBuf> {
            let folder = root.join("versions").join(name);
            crate::game::fake_version(&folder)?;
            Ok(folder.join("builds/default"))
        };
        let old = install("nightly-1")?;
        let other = install("nightly-2")?;
        register(root, "nightly-1", root, &old, &[], None)?;
        register(root, "nightly-2", root, &other, &[], None)?;
        let selected = selected(root, Some(&old))?;
        assert!(register(root, "nightly-2", root, &other, &[], Some(&selected)).is_err());
        let replacement = install("nightly-2-instance")?;
        register(root, "nightly-2", root, &replacement, &[], Some(&selected))?;
        let versions = listed(root)?;
        assert_eq!(versions.len(), 2);
        for (id, folder) in [("nightly-1", &replacement), ("nightly-2", &other)] {
            let v = current(&versions, &json!({"version": id})).unwrap();
            assert_eq!(v.tag, "nightly-2");
            assert!(v.owns_build(folder));
            assert_eq!(v.id, id);
        }
        assert!(old.exists());
        Ok(())
    }

    #[test]
    fn earlier_launcher_update_is_imported_as_one_instance() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        for tag in ["nightly-1", "nightly-2"] {
            let folder = root.join("versions").join(tag);
            crate::game::fake_version(&folder)?;
        }
        let dest = root.join("versions/nightly-2/builds/default");
        register(root, "nightly-2", root, &dest, &[], None)?;
        home::patch_settings(
            root,
            &json!({"rollbackVersion":"nightly-1", "updatedToVersion":"nightly-2"}),
        )?;
        let registry_file = root.join(super::super::registry::OWN);
        let original = registry(root)?;
        let mut stale = original.clone();
        stale["builds"][0]["updated"] = json!("1970-01-01T00:00:00Z");
        home::write_json(&registry_file, &stale)?;
        super::super::registry::prepare(root, false)?;
        assert_eq!(
            listed(root)?.len(),
            2,
            "a later installation at the old path is independent"
        );
        home::write_json(&registry_file, &original)?;
        super::super::registry::prepare(root, false)?;
        let versions = listed(root)?;
        assert_eq!(versions.len(), 1);
        assert!(
            current(&versions, &json!({"version":"nightly-1"}))
                .unwrap()
                .owns_build(&dest)
        );
        Ok(())
    }

    #[test]
    fn changing_modules_is_scoped_to_the_selected_installation() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path();
        let tag = "nightly-42-aa";
        let a = home.join("versions").join(tag);
        let b = home.join("versions/nightly-42-aa.instance-2");
        for folder in [&a, &b] {
            crate::game::fake_version(folder)?;
            home::write_json(
                &folder.join("builds/default/longshot_build.json"),
                &json!({"upscalers":["dlss"]}),
            )?;
        }
        register(
            home,
            tag,
            &home.join("input.nup"),
            &a.join("builds/default"),
            &[],
            None,
        )?;
        assert!(by_folder(home, &a)?.modules()?.is_empty());
        assert_eq!(by_folder(home, &b)?.modules()?, ["dlss"]);
        set_modules(home, &b, &["fsr4"])?;
        assert!(by_folder(home, &a)?.modules()?.is_empty());
        assert_eq!(by_folder(home, &b)?.modules()?, ["fsr4"]);
        assert!(set_modules(home, &b, &["unsupported"]).is_err());
        assert_eq!(by_folder(home, &b)?.modules()?, ["fsr4"]);
        if !cfg!(windows) {
            let mut rows = registry(home)?;
            rows["builds"][1]["xess"] = json!(true);
            home::write_json(&home.join(crate::game::registry::OWN), &rows)?;
            assert_eq!(by_folder(home, &b)?.modules()?, ["fsr4"]);
            assert!(set_modules(home, &b, &["xess"]).is_err());
            set_modules(home, &b, &["dlss"])?;
            assert_eq!(by_folder(home, &b)?.modules()?, ["dlss"]);
            assert_eq!(registry(home)?["builds"][1]["xess"], true);
        }
        Ok(())
    }

    #[test]
    fn registry_roundtrip_preserves_unknown_entries_and_fields() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path();
        let folder = home.join("versions/v3.2.0/builds/default");
        fs::create_dir_all(&folder)?;
        let version = home.join("versions/v3.2.0");
        fs::write(version.join(executable()), [])?;
        let external = home.join("earlier");
        fs::create_dir_all(&external)?;
        let marker = external.join("keep.txt");
        fs::write(&marker, "external folder")?;
        let old = json!({"id":"old","folder":home.join("earlier"),"version":"v3.1.0","default":true,"futureSetting":"yes"});
        let row = json!({"id":"existing","folder":folder,"version":"v3.2.0","future":[1,2]});
        home::write_json(
            &home.join(crate::game::registry::OWN),
            &json!({"format":1,"future":4,"builds":[old,row]}),
        )?;
        register(
            home,
            "v3.2.0",
            &home.join("owned.nup"),
            &folder,
            &["dlss"],
            None,
        )?;
        let list = registry(home)?;
        assert_eq!(list["builds"][0], old);
        assert_eq!(list["builds"][1]["id"], "existing");
        assert_eq!(list["builds"][1]["future"], json!([1, 2]));
        assert_eq!(list["future"], 4);
        let versions = installed(home)?;
        assert_eq!(versions.len(), 1);
        assert_eq!(current(&versions, &json!({})).unwrap().tag, "v3.2.0");
        assert_eq!(
            current(&versions, &json!({"version":"existing"}))
                .unwrap()
                .tag,
            "v3.2.0"
        );
        assert!(selected(home, Some(&home.join("earlier"))).is_err());
        assert!(crate::game::remove_managed(home, &home.join("earlier")).is_err());
        assert_eq!(fs::read_to_string(marker)?, "external folder");
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn linked_versions_root_never_owns_external_files() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let external = tempfile::tempdir()?;
        let version = external.path().join("v3.4.0");
        crate::game::fake_version(&version)?;
        std::os::unix::fs::symlink(external.path(), temp.path().join("versions"))?;
        assert!(installed(temp.path()).is_err());
        assert!(
            crate::game::remove_managed(temp.path(), &temp.path().join("versions/v3.4.0")).is_err()
        );
        assert!(version.join(executable()).is_file());
        Ok(())
    }
    #[test]
    fn extraction_registered_inside_version_keeps_the_original_copy() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path();
        let dir = home.join("versions/v3.3.0");
        let game = dir.join("input");
        fs::create_dir_all(game.join("romfs"))?;
        fs::create_dir_all(game.join("exefs"))?;
        crate::game::fake_version(&dir)?;
        fs::write(game.join("exefs/main"), b"synthetic fixture")?;
        register(
            home,
            "v3.3.0",
            &game,
            &dir.join("builds/default"),
            &[],
            None,
        )?;
        assert!(crate::game::remove_managed(home, &dir).is_err());
        assert_eq!(fs::read(game.join("exefs/main"))?, b"synthetic fixture");
        Ok(())
    }
    #[test]
    fn future_registry_is_left_intact() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let path = temp.path().join(crate::game::registry::OWN);
        fs::write(&path, r#"{"format":99,"builds":[]}"#)?;
        assert!(register(temp.path(), "v3.2.0", temp.path(), temp.path(), &[], None).is_err());
        assert_eq!(fs::read_to_string(path)?, r#"{"format":99,"builds":[]}"#);
        Ok(())
    }
}
