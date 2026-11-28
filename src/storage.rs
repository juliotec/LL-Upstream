//! Measure launcher-owned folders and separately account for an external game dump.
//! Only rebuildable, named caches get a removal action. A player's chosen work
//! directory can also contain unrelated files and must never be removed whole.
pub mod cleanup;
mod drives;
pub mod mods;
mod reclaim;
pub(crate) use reclaim::bind as bind_cleanup_review;
mod shaders;
pub use drives::Snapshot;
pub(crate) use drives::volume;
pub(crate) use shaders::is_game_cache;

use crate::{DiskGroup, DiskPart, home};
use anyhow::{Context, Result, bail, ensure};
use serde_json::Value;
use std::collections::{BTreeSet, HashSet};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

const PREVIOUS_CACHES: &[(&str, &str)] = &[
    ("store", "Previous compiled code and translations"),
    ("tools", "Previous build tools"),
    ("sources", "Previous builder sources"),
    ("runtimes", "Previous upscalers"),
];

#[derive(Clone, Copy)]
struct CachePart {
    subdir: &'static str,
    label: &'static str,
    group: &'static str,
}

const CACHES: &[CachePart] = &[
    CachePart {
        subdir: "longshot-builder/downloads",
        label: "Downloaded builders",
        group: "Build tools",
    },
    CachePart {
        subdir: "longshot-builder/builds",
        label: "Build cache",
        group: "Faster updates",
    },
    CachePart {
        subdir: "longshot-builder/work",
        label: "Earlier build cache",
        group: "Faster updates",
    },
    CachePart {
        subdir: "longshot-builder/store",
        label: "Compiled code and translations",
        group: "Faster updates",
    },
];

#[derive(Debug, Clone)]
pub struct Row {
    path: PathBuf,
    drive: Option<PathBuf>,
    label: String,
    group: &'static str,
    bytes: Option<u64>,
    cache: Option<&'static str>,
}

impl Row {
    pub fn view(self) -> DiskPart {
        DiskPart {
            label: self.label.into(),
            group: self.group.into(),
            size: self
                .bytes
                .map(format_size)
                .unwrap_or_else(|| "Unavailable".into())
                .into(),
            path: self.path.to_string_lossy().as_ref().into(),
            cache: self.cache.unwrap_or_default().into(),
        }
    }
}

pub fn groups(rows: &[Row]) -> (String, Vec<DiskGroup>) {
    let total = rows.iter().filter_map(|row| row.bytes).sum::<u64>();
    let mut used = 0u64;
    let groups = [
        (crate::i18n::key("Game files"), 0xa9573c),
        (crate::i18n::key("Installed games"), 0xd8a840),
        (crate::i18n::key("Shader caches"), 0xd8bc8c),
        (crate::i18n::key("Faster updates"), 0x7f9d53),
        (crate::i18n::key("Build tools"), 0x56643a),
        (crate::i18n::key("Your data"), 0xf2e3c6),
        (crate::i18n::key("Previous launcher"), 0xa997c5),
    ]
    .into_iter()
    .filter(|(name, _)| *name != "Previous launcher" || rows.iter().any(|row| row.group == *name))
    .map(|(name, rgb): (&str, u32)| {
        let parts = rows
            .iter()
            .filter(|row| row.group == name)
            .cloned()
            .map(Row::view)
            .collect::<Vec<_>>();
        let size = rows
            .iter()
            .filter(|r| r.group == name)
            .filter_map(|r| r.bytes)
            .sum::<u64>();
        let offset = used;
        used += size;
        DiskGroup {
            parts: slint::ModelRc::new(slint::VecModel::from(parts)),
            offset: if total == 0 {
                0.0
            } else {
                offset as f32 / total as f32
            },
            name: name.into(),
            size: format_size(size).into(),
            fraction: if total == 0 {
                0.0
            } else {
                size as f32 / total as f32
            },
            tone: slint::Color::from_rgb_u8((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8),
        }
    })
    .collect();
    (format_size(total), groups)
}

pub fn measure(root: &Path) -> Result<Snapshot> {
    let settings = home::settings(root)?;
    let work = home::work(root, &settings);
    let stable_work = stable_work_folders(root, &work)?;
    let mut rows = Vec::new();
    let mut measured = Measured::default();
    let active_store = home::shader_store(&work.join("longshot-builder"))?;
    let active = Some(active_store.as_path()).filter(|p| *p != Path::new("0"));
    let mut caches = shaders::locations(root, &work, active)?;
    let mut previous_work = work_folders(root, &settings);
    previous_work.insert(home::previous_work(root));
    previous_work.extend(stable_work.iter().flatten().cloned());
    let standalone = (!home::is_portable(root))
        .then(standalone_shader_store)
        .flatten();
    for path in standalone.into_iter().chain(
        previous_work
            .iter()
            .map(|p| p.join("longshot-builder/shader-store")),
    ) {
        if let Some(store) = shaders::shared_store(path)
            && !caches.iter().any(|c| c.path == store.path)
        {
            caches.push(store);
        }
    }
    for cache in caches {
        let bytes = cache.files().ok().and_then(|files| {
            files.into_iter().try_fold(0_u64, |bytes, file| {
                let size = measured.size(&file.canonicalize().ok()?).ok()?;
                Some(bytes.saturating_add(size))
            })
        });
        if bytes != Some(0) {
            rows.push(Row {
                drive: volume(&cache.path),
                path: cache.path,
                label: cache.label,
                group: "Shader caches",
                bytes,
                cache: bytes.filter(|_| !cache.shared).map(|_| shaders::ACTION),
            });
        }
    }
    let previous = reclaim::previous_plan(root)?;
    let mut add =
        |path: PathBuf, label: String, group: &'static str, cache: Option<&'static str>| {
            if !path.exists() || fs::symlink_metadata(&path).is_ok_and(|m| is_link(&m)) {
                return;
            }
            let actual = fs::canonicalize(&path).unwrap_or_else(|_| path.clone());
            if actual.ancestors().any(|p| measured.roots.contains(p)) {
                return;
            }
            if cache == Some("previous-build") {
                let mut parts = std::collections::BTreeMap::new();
                for (file, entry) in previous
                    .files
                    .range(actual.clone()..)
                    .take_while(|(p, _)| p.starts_with(&actual))
                {
                    if !file.ancestors().any(|p| measured.roots.contains(p))
                        && let Ok(bytes) = measured.size(file)
                    {
                        *parts.entry(entry.category).or_insert(0u64) += bytes;
                    }
                }
                for (category, bytes) in parts {
                    rows.push(Row {
                        drive: volume(&actual),
                        path: actual.clone(),
                        label: category.to_owned(),
                        group,
                        bytes: Some(bytes),
                        cache: Some(category),
                    });
                }
            }
            let bytes = measured.size(&actual).ok();
            if cache == Some("previous-build") && bytes == Some(0) {
                return;
            }
            rows.push(Row {
                drive: volume(&actual),
                path,
                label,
                group: if group == "Previous launcher" {
                    "Your data"
                } else {
                    group
                },
                bytes,
                cache: cache.filter(|key| *key != "previous-build"),
            });
        };
    add(
        crate::players::backup_root(root)?,
        "Save backups".into(),
        "Your data",
        None,
    );
    if let Ok(path) = crate::players::nfc_folder(root) {
        add(path, "NFC tags".into(), "Your data", None);
    }
    let profiles = crate::players::list_profiles(root)?;
    for profile in &profiles {
        if let Some(game) = profile.game_path() {
            if game.is_file() {
                add(game, "Game files".into(), "Game files", None);
            } else {
                for part in ["romfs", "exefs"] {
                    add(
                        game.join(part),
                        format!("Game files · {part}"),
                        "Game files",
                        None,
                    );
                }
            }
        }
    }
    let versions = crate::game::storage_versions(root)?;
    let mut move_parents = previous_work.clone();
    move_parents.insert(home::data_folder(root, &settings));
    move_parents.insert(crate::game::install_root(root, &settings));
    move_parents.extend(
        versions
            .iter()
            .filter_map(|(path, _)| path.parent().map(Path::to_owned)),
    );
    move_parents.extend(
        settings["moveParents"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
            .map(PathBuf::from),
    );
    for parent in move_parents {
        for path in recovery_folders(&parent) {
            // A parked folder may contain the only surviving copy of user data.
            add(
                path,
                crate::i18n::key("Interrupted folder move").into(),
                "Your data",
                None,
            );
        }
    }
    let builds = crate::game::registry::read(root, crate::game::registry::OWN)?;
    for b in builds["builds"].as_array().into_iter().flatten() {
        let Some(folder) = field_path(b, "folder") else {
            continue;
        };
        let Some(version) = folder.parent().and_then(Path::parent) else {
            continue;
        };
        if folder != version.join("builds/default")
            || !versions.iter().any(|(path, _)| path == version)
        {
            continue;
        }
        for key in ["nup", "romfs", "exefs"] {
            if let Some(path) = field_path(b, key) {
                add(path, "Game files".into(), "Game files", None);
            }
        }
    }
    for store in [
        home::data_folder(root, &settings).join("mods"),
        work.join("longshot-builder/mods"),
    ] {
        add(store, "Shared mod files".into(), "Your data", None);
    }
    for profile in profiles {
        add(
            profile.dir.join("mods"),
            format!("{} · Mods", profile.name()),
            "Your data",
            None,
        );
    }
    for (path, listed) in versions {
        for build in dirs(&path.join("builds"))? {
            add(
                build.join("mods"),
                "Installed mods".into(),
                "Your data",
                None,
            );
        }
        let label = path
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned();
        let group = if listed {
            "Installed games"
        } else if path.join("builds").is_dir() {
            "Faster updates"
        } else {
            "Build tools"
        };
        add(path, label, group, None);
    }
    add(
        home::data_folder(root, &settings).join("profiles"),
        "Profiles".into(),
        "Your data",
        None,
    );
    for part in CACHES {
        add(
            work.join(part.subdir),
            part.label.into(),
            part.group,
            Some(part.subdir),
        );
    }
    for (dir, name) in [
        ("longshot-builder/tools", "Build tools"),
        ("longshot-builder/sources", "Builder sources"),
        ("longshot-builder/runtimes", "Upscalers"),
    ] {
        add(work.join(dir), name.into(), "Build tools", None);
    }
    // A switch to an empty cache can leave reusable files at the old location.
    // Attribute only known builder subfolders there, never an entire custom drive.
    let active_work = work.canonicalize().unwrap_or_else(|_| work.clone());
    for earlier in previous_work
        .into_iter()
        .filter(|p| !overlaps(p, &active_work))
    {
        let previous = stable_work
            .as_ref()
            .is_some_and(|dirs| earlier.canonicalize().is_ok_and(|p| dirs.contains(&p)));
        if previous {
            for part in ["objcache", "obj-store"] {
                add(
                    earlier.join(part),
                    "Previous compiler cache".into(),
                    "Previous launcher",
                    Some("previous-build"),
                );
            }
        }
        for (subdir, label) in [
            ("downloads", "Previous builder downloads"),
            ("builds", "Previous build cache"),
            ("work", "Earlier build cache"),
            ("shaders", "Previous prepared shaders"),
        ]
        .into_iter()
        .chain(PREVIOUS_CACHES.iter().copied())
        {
            add(
                earlier.join("longshot-builder").join(subdir),
                label.into(),
                if subdir == "shaders" {
                    "Shader caches"
                } else if previous {
                    "Previous launcher"
                } else {
                    "Faster updates"
                },
                (previous && subdir != "shaders").then_some("previous-build"),
            );
        }
    }
    for (dir, name, group) in [
        ("save-backups", "Save backups", "Your data"),
        ("logs", "Logs", "Your data"),
    ] {
        add(root.join(dir), name.into(), group, None);
    }
    add(
        home::builder_data(root),
        "Builder data".into(),
        "Your data",
        None,
    );
    for folder in crate::game::registry::stable_only(root)? {
        let name = folder
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned();
        if crate::players::is_dir(&folder) && folder.parent().is_some() && !name.is_empty() {
            add(
                folder,
                format!("Other files · {name}"),
                "Previous launcher",
                Some("previous-build"),
            );
        }
    }
    Ok(Snapshot::new(root, rows, previous))
}

fn standalone_shader_store() -> Option<PathBuf> {
    #[cfg(windows)]
    let base = home::system_data_folder().ok()?;
    #[cfg(not(windows))]
    let base = home::system_cache_folder()?;
    Some(base.join(if cfg!(windows) {
        "Longshot/shaders"
    } else {
        "longshot/shaders"
    }))
}

fn field_path(row: &Value, name: &str) -> Option<PathBuf> {
    row[name]
        .as_str()
        .filter(|s| !s.is_empty())
        .map(PathBuf::from)
        .filter(|p| p.is_absolute())
}

fn recovery_folders(parent: &Path) -> Vec<PathBuf> {
    if !crate::fsx::direct(parent) {
        return Vec::new();
    }
    dirs(parent)
        .unwrap_or_default()
        .into_iter()
        .filter(|path| {
            path.file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|name| {
                    [".longshot-move-", ".longshot-previous-"]
                        .iter()
                        .filter_map(|prefix| name.strip_prefix(prefix))
                        .any(|suffix| {
                            suffix.len() == 6 && suffix.bytes().all(|b| b.is_ascii_alphanumeric())
                        })
                })
                && crate::fsx::direct(path)
        })
        .collect()
}

fn dirs(parent: &Path) -> io::Result<Vec<PathBuf>> {
    if !parent.try_exists()? {
        return Ok(Vec::new());
    }
    let mut paths = Vec::new();
    for e in fs::read_dir(parent)? {
        let e = e?;
        if e.file_type()?.is_dir() {
            paths.push(e.path())
        }
    }
    paths.sort();
    Ok(paths)
}

pub(crate) use crate::fsx::{is_link, linked_location};

#[cfg(unix)]
fn file_identity(_: &Path, metadata: &fs::Metadata) -> Option<(u64, u64, u64)> {
    use std::os::unix::fs::MetadataExt;
    Some((metadata.dev(), metadata.ino(), metadata.nlink()))
}

#[cfg(windows)]
fn file_identity(path: &Path, _: &fs::Metadata) -> Option<(u64, u64, u64)> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle,
    };
    let file = fs::File::open(path).ok()?;
    let mut info = std::mem::MaybeUninit::<BY_HANDLE_FILE_INFORMATION>::uninit();
    // A successful query fills the record while the originating handle is still open.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), info.as_mut_ptr()) } == 0 {
        return None;
    }
    let info = unsafe { info.assume_init() };
    Some((
        u64::from(info.dwVolumeSerialNumber),
        (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow),
        u64::from(info.nNumberOfLinks),
    ))
}

pub(crate) fn hard_link_id(path: &Path, meta: &fs::Metadata) -> Option<(u64, u64)> {
    let (volume, index, count) = file_identity(path, meta)?;
    (count > 1).then_some((volume, index))
}

#[derive(Default)]
struct Measured {
    roots: HashSet<PathBuf>,
    links: HashSet<(u64, u64)>,
}

impl Measured {
    fn size(&mut self, root: &Path) -> io::Result<u64> {
        let mut bytes = 0u64;
        let mut paths = vec![root.to_owned()];
        while let Some(path) = paths.pop() {
            if self.roots.contains(&path) {
                continue;
            }
            let meta = fs::symlink_metadata(&path)?;
            if is_link(&meta) {
                continue;
            }
            if meta.is_dir() {
                for entry in fs::read_dir(path)? {
                    paths.push(entry?.path());
                }
            } else if meta.is_file() {
                if let Some(key) = hard_link_id(&path, &meta)
                    && !self.links.insert(key)
                {
                    continue;
                }
                bytes = bytes.saturating_add(meta.len());
            }
        }
        self.roots.insert(root.to_owned());
        Ok(bytes)
    }
}

enum Protection {
    Bulk,
    Cache,
    ShaderInstance,
    Reclaim,
}

#[derive(Default)]
struct Protected {
    inputs: Vec<PathBuf>,
    player: Vec<PathBuf>,
    versions: Vec<PathBuf>,
}

impl Protected {
    fn gather(root: &Path, scope: Protection) -> Result<Self> {
        let reclaim = matches!(scope, Protection::Reclaim);
        let player = if reclaim {
            player_paths_without_extractions(root)?
        } else {
            player_data_paths(root)?
        };
        let versions = match scope {
            Protection::Bulk | Protection::Cache => crate::game::managed_folders(root)?,
            Protection::ShaderInstance | Protection::Reclaim => Vec::new(),
        };
        Ok(Self {
            inputs: protected_inputs(root, !reclaim)?,
            player,
            versions,
        })
    }

    fn blocks(
        &self,
        path: &Path,
        player_exception: Option<&Path>,
        version_exception: Option<&Path>,
    ) -> bool {
        self.inputs.iter().any(|input| overlaps(path, input))
            || self.player.iter().any(|player| {
                let player = player.canonicalize().unwrap_or_else(|_| player.to_owned());
                Some(player.as_path()) != player_exception && crate::fsx::overlaps(path, &player)
            })
            || self.versions.iter().any(|version| {
                Some(version.as_path()) != version_exception && overlaps(path, version)
            })
    }

    fn into_paths(self) -> impl Iterator<Item = PathBuf> {
        self.inputs
            .into_iter()
            .chain(self.player)
            .chain(self.versions)
    }
}

/// Resolve a named child without allowing a junction or symlink to redirect the deletion.
fn removal_target(base: &Path, name: &Path) -> Result<Option<PathBuf>> {
    let path = base.join(name);
    if !path.try_exists()? {
        return Ok(None);
    }
    let actual = path.canonicalize()?;
    if is_link(&fs::symlink_metadata(base)?) || actual != base.canonicalize()?.join(name) {
        return Ok(None);
    }
    Ok(Some(actual))
}

enum Removal {
    File,
    Directory,
}

impl Removal {
    fn execute(self, path: &Path) -> Result<()> {
        let meta = fs::symlink_metadata(path)?;
        ensure!(!is_link(&meta), "The removal target is linked.");
        let result = match self {
            Self::File => fs::remove_file(path),
            Self::Directory => fs::remove_dir_all(path),
        };
        result.with_context(|| format!("Could not remove {}.", home::display(path)))
    }
}

pub(crate) fn protected_paths(root: &Path) -> Result<Vec<PathBuf>> {
    protected_inputs(root, true)
}

pub(crate) fn overlaps_private(folder: &Path, sources: &[PathBuf]) -> Result<bool> {
    Ok(protected_overlap(&folder.canonicalize()?, sources).is_some())
}

fn protected_overlap(folder: &Path, sources: &[PathBuf]) -> Option<PathBuf> {
    sources
        .iter()
        .find(|source| {
            let path = source.canonicalize().unwrap_or_else(|_| (*source).clone());
            crate::fsx::overlaps(folder, &path)
        })
        .cloned()
}

pub(crate) enum InstallationProtection {
    StableRegistry(PathBuf),
    Other(PathBuf),
}

impl InstallationProtection {
    pub(crate) fn path(&self) -> &Path {
        match self {
            Self::StableRegistry(path) | Self::Other(path) => path,
        }
    }
}

pub(crate) fn installation_protection(
    root: &Path,
    folder: &Path,
) -> Result<Option<InstallationProtection>> {
    let folder = folder.canonicalize()?;
    let stable = crate::game::registry::stable_view(root)?;
    let stable_folders = stable["builds"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|row| field_path(row, "folder"))
        .collect::<Vec<_>>();
    if let Some(path) = protected_overlap(&folder, &stable_folders) {
        return Ok(Some(InstallationProtection::StableRegistry(path)));
    }
    Ok(protected_overlap(
        &folder,
        &protected_inputs_from_registry(root, &stable, false)?,
    )
    .map(InstallationProtection::Other))
}

fn protected_inputs(root: &Path, include_stable_folders: bool) -> Result<Vec<PathBuf>> {
    let stable = crate::game::registry::stable_view(root)?;
    protected_inputs_from_registry(root, &stable, include_stable_folders)
}

fn protected_inputs_from_registry(
    root: &Path,
    stable: &Value,
    include_stable_folders: bool,
) -> Result<Vec<PathBuf>> {
    use crate::game::registry::{self, OWN};
    let mut protected = Vec::new();
    for row in stable["builds"].as_array().unwrap() {
        for key in ["folder", "nup", "romfs", "exefs"] {
            if key == "folder" && !include_stable_folders {
                continue;
            }
            if let Some(path) = field_path(row, key) {
                protected.push(path);
            }
        }
    }
    let registry = registry::read(root, OWN)?;
    for row in registry["builds"].as_array().into_iter().flatten() {
        for key in ["nup", "romfs", "exefs"] {
            if let Some(path) = field_path(row, key) {
                protected.push(path);
            }
        }
        if let Some(path) = field_path(row, "folder")
            && !path.parent().and_then(Path::parent).is_some_and(|version| {
                path.parent() == Some(version.join("builds").as_path())
                    && crate::game::owned_folder(root, version)
            })
        {
            protected.push(path);
        }
    }
    if home::data_dir(root)?.join("profiles").try_exists()? {
        for profile in crate::players::list_profiles(root)? {
            ensure!(
                profile.problem.is_none(),
                "Repair unreadable profile {} before clearing storage.",
                profile.name()
            );
            if let Some(game) = profile.game_path() {
                protected.push(game);
            }
        }
    }
    Ok(protected)
}

fn work_folders(root: &Path, settings: &Value) -> BTreeSet<PathBuf> {
    let mut folders = BTreeSet::from([home::work(root, settings)]);
    folders.extend(
        settings["earlierWork"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
            .map(PathBuf::from)
            .filter(|p| p.is_absolute()),
    );
    folders
}

/// A Rust-only installation can also have earlier cache folders. Attribute a path
/// to stable only when its own settings or registry identifies that installation.
fn stable_work_folders(root: &Path, active: &Path) -> Result<Option<BTreeSet<PathBuf>>> {
    let settings = home::read_json(&root.join("settings.json"))?;
    let stable_settings = home::stable_settings(&settings);
    if !stable_settings && crate::game::registry::stable_only(root)?.is_empty() {
        return Ok(None);
    }
    let mut folders = BTreeSet::from([home::previous_work(root)]);
    if stable_settings {
        folders.extend(field_path(&settings, "work"));
        folders.extend(
            settings["earlierWork"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(Value::as_str)
                .map(PathBuf::from)
                .filter(|p| p.is_absolute()),
        );
    }
    let active = active.canonicalize().unwrap_or_else(|_| active.to_owned());
    Ok(Some(
        folders
            .into_iter()
            .filter(|p| reclaim::direct(p))
            .filter_map(|p| p.canonicalize().ok())
            .filter(|p| !overlaps(p, &active))
            .collect(),
    ))
}

fn player_data_paths(root: &Path) -> Result<Vec<PathBuf>> {
    let mut paths = player_paths_without_extractions(root)?;
    paths.extend(
        crate::game::registry::stable_only(root)?
            .iter()
            .map(|game| game.join("romfs")),
    );
    Ok(paths)
}

fn player_paths_without_extractions(root: &Path) -> Result<Vec<PathBuf>> {
    let settings = home::settings(root)?;
    let data = home::data_folder(root, &settings);
    let mut paths = vec![
        data.join("profiles"),
        data.join("mods"),
        root.join("profiles"),
        root.join("save-backups"),
        crate::players::backup_root(root)?,
        crate::players::nfc_folder(root)?,
    ];
    let shared = crate::players::stable_user(root)?;
    for dir in ["save", "save-work", "mods"] {
        paths.push(shared.join(dir));
    }
    for build in crate::game::registry::stable_only(root)? {
        for dir in ["user", "mods", "exefs"] {
            paths.push(build.join(dir));
        }
    }
    let mut folders = work_folders(root, &settings);
    folders.insert(home::previous_work(root));
    for work in folders {
        let mods = work.join("longshot-builder/mods");
        if mods.try_exists()? {
            paths.push(mods);
        }
    }
    Ok(paths)
}

fn overlaps(actual: &Path, path: &Path) -> bool {
    let other = path.canonicalize().unwrap_or_else(|_| path.to_owned());
    crate::fsx::overlaps(actual, &other)
}

fn clear_part(work: &Path, part: &str, protected: &Protected) -> Result<()> {
    let path = work.join(part);
    if !path.try_exists()? {
        return Ok(());
    }
    let actual = removal_target(work, Path::new(part))?.context("The cache location is linked.")?;
    ensure!(
        !protected.blocks(&actual, None, None),
        "This cache contains a registered game or source file and has been kept."
    );
    let kind = if fs::symlink_metadata(&path)?.is_dir() {
        Removal::Directory
    } else {
        Removal::File
    };
    kind.execute(&path)
}

fn hold_locks(work: &Path) -> Result<Vec<home::FileLease>> {
    let mut locks = Vec::new();
    let builds = dirs(&work.join("longshot-builder/builds"))?;
    let previous = dirs(&work.join("longshot-builder/work"))?;
    for folder in builds.into_iter().chain(previous) {
        let lock = folder.join("longshot-builder.lock");
        if lock.try_exists()? {
            locks.push(
                home::FileLease::try_acquire(fs::File::open(lock)?, crate::fsx::LockMode::Shared)
                    .context("A builder is using this cache. Wait for it to finish.")?,
            );
        }
    }
    Ok(locks)
}

pub fn clear(root: &Path, subdir: &str, measured_path: &Path) -> Result<()> {
    let _lease = crate::game::move_lease(root)?;
    if subdir == shaders::ACTION {
        crate::players::ensure_shader_idle()?;
        let work = home::work(root, &home::settings(root)?);
        let _locks = hold_locks(&work)?;
        let store = home::shader_store(&work.join("longshot-builder"))?;
        let active = Some(store.as_path()).filter(|path| *path != Path::new("0"));
        return shaders::clear(root, &work, active, measured_path);
    }
    let Some(part) = CACHES.iter().find(|part| part.subdir == subdir) else {
        bail!("This folder is not a rebuildable cache.")
    };
    let work = home::work(root, &home::settings(root)?);
    ensure!(
        work.join(part.subdir) == measured_path,
        "The work folder changed. Refresh Storage before removing anything."
    );
    let _locks = hold_locks(&work)?;
    clear_part(
        &work,
        part.subdir,
        &Protected::gather(root, Protection::Cache)?,
    )
}

pub fn after_build(root: &Path, keep: bool) -> Result<()> {
    if keep {
        return Ok(());
    }
    let work = home::work(root, &home::settings(root)?);
    let protected = Protected::gather(root, Protection::Cache)?;
    let _locks = hold_locks(&work)?;
    let shader_store = home::shader_store(&work.join("longshot-builder"))?;
    clear_build_files(&work, &protected, &shader_store)
}

fn clear_build_files(work: &Path, protected: &Protected, shader_store: &Path) -> Result<()> {
    for part in [
        "longshot-builder/builds",
        "longshot-builder/work",
        "longshot-builder/store",
    ] {
        // An explicit shader store may share one of these folders.
        if shader_store != Path::new("0")
            && let Ok(actual) = work.join(part).canonicalize()
            && overlaps(&actual, shader_store)
        {
            continue;
        }
        clear_part(work, part, protected)?;
    }
    Ok(())
}

pub fn folder_bytes(path: &Path) -> Option<u64> {
    Measured::default().size(path).ok()
}

pub fn format_size(bytes: u64) -> String {
    if bytes < 1024 {
        return format!("{bytes} B");
    }
    let mut size = bytes as f64;
    for unit in ["KiB", "MiB", "GiB", "TiB"] {
        size /= 1024.;
        if size < 1024. || unit == "TiB" {
            return format!("{size:.1} {unit}");
        }
    }
    unreachable!()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn all_mod_files_have_one_storage_row_per_profile() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let profile = root.join("profiles/default");
        let mods = profile.join("mods");
        fs::create_dir_all(mods.join("First mod"))?;
        fs::create_dir_all(mods.join("Second mod"))?;
        home::write_json(
            &profile.join("profile.json"),
            &json!({"v":1,"name":"Player"}),
        )?;
        fs::write(mods.join("First mod/patch"), b"123")?;
        fs::write(mods.join("Second mod/patch"), b"45")?;
        fs::write(mods.join("standalone"), b"6")?;
        let snapshot = measure(root)?;
        let rows: Vec<_> = snapshot
            .rows
            .iter()
            .filter(|row| row.path.starts_with(&mods))
            .collect();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].path, mods);
        assert_eq!(rows[0].label, "Player · Mods");
        assert_eq!(rows[0].bytes, Some(6));
        Ok(())
    }

    #[test]
    fn preloaded_builders_and_left_behind_caches_are_counted_once() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("control");
        let old = tmp.path().join("earlier");
        let active = tmp.path().join("current");
        fs::create_dir_all(root.join("profiles"))?;
        home::patch_settings(&root, &json!({"work": old}))?;
        home::patch_settings(&root, &json!({"work": active}))?;
        let downloaded = active.join("longshot-builder/downloads");
        let old_store = old.join("longshot-builder/store");
        fs::create_dir_all(&downloaded)?;
        fs::create_dir_all(&old_store)?;
        fs::write(downloaded.join("builder"), b"12345")?;
        fs::hard_link(downloaded.join("builder"), old_store.join("shared"))?;
        fs::write(old_store.join("compiled"), b"678")?;
        fs::write(old.join("someone-elses-file"), b"do not count")?;
        let snapshot = measure(&root)?;
        let current = snapshot.rows.iter().find(|r| r.path == downloaded).unwrap();
        let earlier = snapshot.rows.iter().find(|r| r.path == old_store).unwrap();
        assert_eq!((current.group, current.bytes), ("Build tools", Some(5)));
        assert_eq!(
            (earlier.group, earlier.bytes, earlier.cache),
            ("Faster updates", Some(3), None)
        );
        assert!(snapshot.rows.iter().all(|r| r.path != old));
        Ok(())
    }

    #[test]
    fn installed_games_excludes_prefetched_tools_and_previous_launcher_games() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("launcher");
        fs::create_dir_all(root.join("profiles"))?;
        let active = root.join("versions/active");
        let prefetched = root.join("versions/prefetched");
        let stable = tmp.path().join("previous-game");
        let tool = if cfg!(windows) {
            "Longshot.exe"
        } else {
            "Longshot.x86_64"
        };
        for path in [&active, &prefetched] {
            fs::create_dir_all(path)?;
            fs::write(path.join(tool), b"builder")?;
        }
        fs::create_dir_all(active.join("builds/default"))?;
        fs::create_dir_all(&stable)?;
        fs::write(stable.join("longshot"), b"previous executable")?;
        home::write_json(
            &root.join("builds.json"),
            &json!({"format":1,"builds":[{"folder":stable}]}),
        )?;
        let snapshot = measure(&root)?;
        let group = |path: &Path| {
            snapshot
                .rows
                .iter()
                .find(|r| r.path == path)
                .map(|r| r.group)
        };
        assert_eq!(group(&active), Some("Installed games"));
        assert_eq!(group(&prefetched), Some("Build tools"));
        assert_eq!(group(&stable), Some("Previous launcher"));
        Ok(())
    }

    #[test]
    fn discovers_recoverable_move_folders_without_claiming_siblings() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let previous = tempfile::Builder::new()
            .prefix(".longshot-previous-")
            .tempdir_in(tmp.path())?;
        let staged = tempfile::Builder::new()
            .prefix(".longshot-move-")
            .tempdir_in(tmp.path())?;
        fs::create_dir(tmp.path().join(".longshot-move-my-files"))?;
        let found: BTreeSet<_> = recovery_folders(tmp.path()).into_iter().collect();
        assert_eq!(
            found,
            BTreeSet::from([previous.path().to_owned(), staged.path().to_owned()])
        );
        Ok(())
    }

    #[test]
    fn clearing_a_cache_preserves_the_parent_and_private_files() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path();
        let work = root.join("a folder chosen by the player");
        let cache = work.join("longshot-builder/store");
        fs::create_dir_all(&cache)?;
        fs::write(work.join("keep"), "private")?;
        fs::write(cache.join("object"), "cache")?;
        home::write_json(
            &root.join(home::SETTINGS_FILE),
            &json!({"format":2, "work":work}),
        )?;
        assert!(clear(root, "../", root).is_err());
        assert!(clear(root, "longshot-builder/store", &work).is_err());
        clear(root, "longshot-builder/store", &cache)?;
        assert_eq!(fs::read(work.join("keep"))?, b"private");
        Ok(())
    }
    #[test]
    fn nested_shader_caches_and_hard_linked_game_files_are_counted_once() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let version = tmp.path().join("version");
        let cache = version.join("cache");
        fs::create_dir_all(&cache)?;
        fs::write(cache.join("shader"), b"ABCD")?;
        fs::hard_link(cache.join("shader"), version.join("second-name"))?;
        fs::write(version.join("other"), b"AB")?;
        let mut measured = Measured::default();
        assert_eq!(measured.size(&cache)?, 4);
        assert_eq!(measured.size(&version)?, 2);
        Ok(())
    }
    #[test]
    fn build_cleanup_only_touches_current_builder_work_and_store() -> Result<()> {
        let root = tempfile::tempdir()?;
        let work = root.path().join("work");
        for part in [
            "longshot-builder/builds",
            "longshot-builder/work",
            "longshot-builder/store",
            "obj-store",
            "lift-store",
            "exefs",
            "objcache",
        ] {
            fs::create_dir_all(work.join(part))?;
            fs::write(work.join(part).join("fixture"), b"untouched")?;
        }
        home::write_json(
            &root.path().join(home::SETTINGS_FILE),
            &json!({"format":2,"work":work}),
        )?;
        after_build(root.path(), false)?;
        for part in ["obj-store", "lift-store", "exefs", "objcache"] {
            assert!(work.join(part).join("fixture").is_file());
        }
        assert!(
            !work.join("longshot-builder/builds").exists()
                && !work.join("longshot-builder/work").exists()
                && !work.join("longshot-builder/store").exists()
        );
        Ok(())
    }

    #[test]
    fn build_cleanup_preserves_a_shader_store_inside_the_compiler_store() -> Result<()> {
        let root = tempfile::tempdir()?;
        let builder = root.path().join("longshot-builder");
        let shaders = builder.join("store/custom-shaders");
        fs::create_dir_all(&shaders)?;
        fs::write(shaders.join("fixture"), b"shader")?;
        fs::create_dir_all(builder.join("work"))?;
        clear_build_files(root.path(), &Protected::default(), &shaders)?;
        assert_eq!(fs::read(shaders.join("fixture"))?, b"shader");
        assert!(!builder.join("work").exists());
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn linked_cache_parent_is_refused_and_sizes_count_hard_links_once() -> Result<()> {
        use std::os::unix::fs::symlink;
        let tmp = tempfile::tempdir()?;
        let root = tmp.path();
        let work = root.join("work");
        let external = root.join("external");
        fs::create_dir_all(external.join("store"))?;
        fs::create_dir(&work)?;
        fs::write(external.join("store/one"), b"1234")?;
        fs::hard_link(external.join("store/one"), external.join("store/two"))?;
        symlink(&external, work.join("longshot-builder"))?;
        home::write_json(
            &root.join(home::SETTINGS_FILE),
            &json!({"format":2, "work":work}),
        )?;
        assert!(
            clear(
                root,
                "longshot-builder/store",
                &work.join("longshot-builder/store")
            )
            .is_err()
        );
        assert_eq!(Measured::default().size(&external)?, 4);
        assert_eq!(Measured::default().size(&work)?, 0);
        Ok(())
    }
}
