//! Shader artifacts written by Longshot and its builder, across the three supported locations.
use super::{Protected, Protection, Removal, field_path, reclaim::direct};
use crate::{game, players};
use anyhow::{Context, Result, ensure};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    io::Read,
    path::{Path, PathBuf},
};

pub(super) const ACTION: &str = "shader-cache";

pub(super) const GAME_FILES: &[&str] = &[
    "longshot_programs.bin",
    "longshot_programs.idx",
    "longshot_programs.zst.bin",
    "longshot_programs.zst.idx",
    "longshot_module_ids.bin",
    "longshot_pipelines.bin.predicted",
    "longshot_pipelines.bin.predicted_keys",
    "longshot_pipelines.bin.built",
    "longshot_computes.bin",
    "longshot_compute_variants.bin",
    "longshot_pipeline_states.bin",
    "longshot_pipeline_states_v1.bin",
    "longshot_prepared.bin",
    "longshot_vk_pipelines.bin",
    "longshot_vk_pipelines.stamp",
    "longshot_vk_pipelines.base",
    "longshot_textures_drawn.bin",
    "longshot_fsr4_pipelines.bin",
];

#[derive(Clone, Copy, Ord, PartialOrd, PartialEq, Eq)]
enum Kind {
    Global,
    Builder,
    Instance,
}

pub(super) struct Cache {
    pub path: PathBuf,
    pub label: String,
    pub shared: bool,
    kinds: BTreeSet<Kind>,
}

/// The OS-wide default may still be shared by stable or standalone games.
pub(super) fn shared_store(path: PathBuf) -> Option<Cache> {
    (direct(&path) && players::is_dir(&path)).then(|| Cache {
        path: path.canonicalize().unwrap_or(path),
        label: "Shared shader store".into(),
        shared: true,
        kinds: BTreeSet::from([Kind::Global]),
    })
}

fn children(dir: &Path) -> Result<Vec<PathBuf>> {
    Ok(fs::read_dir(dir)?
        .map(|entry| Ok(entry?.path()))
        .collect::<std::io::Result<_>>()?)
}

pub(super) fn locations(root: &Path, work: &Path, global: Option<&Path>) -> Result<Vec<Cache>> {
    let mut by_path = BTreeMap::<PathBuf, Cache>::new();
    let mut add = |path: &Path, kind, label: String| -> Result<()> {
        if direct(path) && players::is_dir(path) {
            let cache = by_path
                .entry(path.canonicalize()?)
                .or_insert_with(|| Cache {
                    path: path.into(),
                    label: String::new(),
                    shared: false,
                    kinds: BTreeSet::new(),
                });
            if cache.kinds.insert(kind) {
                if !cache.label.is_empty() {
                    cache.label.push_str(" + ");
                }
                cache.label.push_str(&label);
            }
        }
        Ok(())
    };
    if let Some(path) = global {
        add(path, Kind::Global, "Global shader store".into())?;
    }
    add(
        &work.join("longshot-builder/shaders"),
        Kind::Builder,
        "Builder shader cache".into(),
    )?;
    let registry = game::registry::read(root, game::registry::OWN)?;
    let versions = game::managed_folders(root)?;
    for row in registry["builds"].as_array().into_iter().flatten() {
        let Some(folder) = field_path(row, "folder") else {
            continue;
        };
        let Some(version) = folder.parent().and_then(Path::parent) else {
            continue;
        };
        if folder.parent() == Some(version.join("builds").as_path())
            && versions.iter().any(|path| path == version)
        {
            let name = row["name"]
                .as_str()
                .or(row["version"].as_str())
                .unwrap_or_else(|| {
                    version
                        .file_name()
                        .unwrap_or_default()
                        .to_str()
                        .unwrap_or("Game")
                });
            let builds = version.join("builds");
            if !direct(&builds) || !players::is_dir(&builds) {
                continue;
            }
            for output in children(&builds)? {
                if !players::is_dir(&output) {
                    continue;
                }
                let label = if output == folder {
                    format!("Game shader cache · {name}")
                } else if players::is_file(&output.join("longshot_version.txt")) {
                    // Profiles with code mods have a distinct builder-produced game folder.
                    format!(
                        "Game shader cache · {name} · {}",
                        output.file_name().unwrap_or_default().to_string_lossy()
                    )
                } else {
                    continue;
                };
                add(&output.join("cache"), Kind::Instance, label)?;
            }
        }
    }
    Ok(by_path.into_values().collect())
}

fn header(path: &Path) -> Option<[u8; 16]> {
    let mut head = [0; 16];
    if !players::is_file(path) {
        return None;
    }
    fs::File::open(path).ok()?.read_exact(&mut head).ok()?;
    Some(head)
}

// LONGSHOT_SHADER_STORE can point anywhere. The embedded magic, version and
// shared generation identify Longshot's pair even in a directory of unrelated files.
fn store_files(folder: &Path) -> Result<Vec<PathBuf>> {
    let pack = folder.join("spirv.pack");
    let idx = folder.join("spirv.idx");
    let (Some(p), Some(i)) = (header(&pack), header(&idx)) else {
        return Ok(vec![]);
    };
    if &p[..4] != b"LSPK"
        || &i[..4] != b"LSPI"
        || p[4..8] != 1u32.to_le_bytes()
        || p[8..12] == [0; 4]
        || p[4..12] != i[4..12]
    {
        return Ok(vec![]);
    }
    let mut files = vec![pack, idx];
    for file in children(folder)? {
        let Some(name) = file.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        let Some((kind, stamp)) = name.split_once(".programs-") else {
            continue;
        };
        if !matches!(kind, "spirv.pack" | "spirv.idx") || stamp.len() != 16 {
            continue;
        }
        let Ok(stamp) = u64::from_str_radix(stamp, 16) else {
            continue;
        };
        if let Some(h) = header(&file)
            && h[8..16] == stamp.to_le_bytes()
            && match kind {
                "spirv.pack" => matches!(&h[..4], b"HSP1" | b"HSP2"),
                _ => &h[..4] == b"HPIX",
            }
        {
            files.push(file);
        }
    }
    Ok(files)
}

fn is_game_cache(name: &str) -> bool {
    let name = name.strip_suffix(".part").unwrap_or(name);
    GAME_FILES.contains(&name)
        || name
            .strip_prefix("longshot_vk_pipelines_play_")
            .and_then(|n| n.strip_suffix(".bin"))
            .is_some_and(|n| !n.is_empty() && n.bytes().all(|b| b.is_ascii_digit()))
}

fn game_files(folder: &Path) -> Result<Vec<PathBuf>> {
    Ok(children(folder)?
        .into_iter()
        .filter(|p| {
            players::is_file(p)
                && p.file_name()
                    .and_then(|n| n.to_str())
                    .is_some_and(is_game_cache)
        })
        .collect())
}

impl Cache {
    pub(super) fn files(&self) -> Result<BTreeSet<PathBuf>> {
        let mut files = BTreeSet::new();
        for kind in &self.kinds {
            match kind {
                Kind::Global => files.extend(store_files(&self.path)?),
                Kind::Instance => files.extend(game_files(&self.path)?),
                Kind::Builder => {
                    for stamp in children(&self.path)? {
                        if !players::is_dir(&stamp)
                            || !stamp.file_name().and_then(|s| s.to_str()).is_some_and(|s| {
                                s.len() <= 128 && s.bytes().all(|b| b.is_ascii_alphanumeric())
                            })
                        {
                            continue;
                        }
                        for driver in children(&stamp)? {
                            if players::is_dir(&driver)
                                && driver
                                    .file_name()
                                    .and_then(|s| s.to_str())
                                    .is_some_and(|s| {
                                        s.len() == 16 && s.bytes().all(|b| b.is_ascii_hexdigit())
                                    })
                            {
                                files.extend(game_files(&driver)?);
                            }
                        }
                    }
                }
            }
        }
        Ok(files)
    }
}

pub(super) fn clear(
    root: &Path,
    work: &Path,
    global: Option<&Path>,
    reviewed: &Path,
) -> Result<()> {
    let cache = locations(root, work, global)?
        .into_iter()
        .find(|c| c.path == reviewed)
        .context("The shader cache location changed. Refresh Storage and try again.")?;
    let files = cache.files()?;
    if files.is_empty() {
        return Ok(());
    }
    let scope = if cache.kinds.contains(&Kind::Instance) {
        Protection::ShaderInstance
    } else {
        Protection::Cache
    };
    let protected = Protected::gather(root, scope)?;
    for file in &files {
        ensure!(
            direct(file) && !protected.blocks(file, None, None),
            "A shader file is linked or overlaps protected player data."
        );
    }
    let mut lock_paths = BTreeSet::new();
    if cache.kinds.contains(&Kind::Global) {
        lock_paths.insert(cache.path.join("spirv.lock"));
    }
    for file in &files {
        if file
            .file_name()
            .and_then(|n| n.to_str())
            .is_some_and(is_game_cache)
        {
            let folder = file.parent().unwrap();
            lock_paths.insert(folder.join("longshot_programs.bin.lock"));
            lock_paths.insert(folder.join("longshot_programs.zst.bin.lock"));
        }
    }
    let mut locks = Vec::new();
    for lock in lock_paths {
        // A restored cache can contain artifacts before the first game opens its lock.
        // Create the same lock a concurrently starting game will acquire.
        let file = match fs::File::create_new(&lock) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                ensure!(
                    direct(&lock) && players::is_file(&lock),
                    "The shader cache lock is linked."
                );
                fs::OpenOptions::new().read(true).write(true).open(lock)?
            }
            Err(error) => return Err(error.into()),
        };
        locks.push(
            crate::home::FileLease::try_acquire(file, crate::fsx::LockMode::Exclusive)
                .context("The shader cache is busy.")?,
        );
    }
    ensure!(
        cache.files()? == files,
        "The shader cache changed. Refresh Storage and try again."
    );
    for file in files {
        Removal::File.execute(&file)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::home;
    use serde_json::json;

    fn store_pair(folder: &Path) -> Result<()> {
        fs::create_dir_all(folder)?;
        for (name, magic) in [("spirv.pack", b"LSPK"), ("spirv.idx", b"LSPI")] {
            let mut head = vec![0; 16];
            head[..4].copy_from_slice(magic);
            head[4..8].copy_from_slice(&1_u32.to_le_bytes());
            head[8..12].copy_from_slice(&17_u32.to_le_bytes());
            fs::write(folder.join(name), head)?;
        }
        fs::write(folder.join("spirv.lock"), b"")?;
        Ok(())
    }

    #[test]
    fn previous_global_store_is_reported_but_cannot_be_cleared() -> Result<()> {
        let t = tempfile::tempdir()?;
        let previous = t.path().join("standalone");
        store_pair(&previous)?;
        let row = shared_store(previous.clone()).unwrap();
        assert!(row.shared);
        assert_eq!(row.files()?.len(), 2);
        let work = t.path().join("work");
        let active = work.join("longshot-builder/shader-store");
        assert!(clear(t.path(), &work, Some(&active), &previous).is_err());
        assert!(previous.join("spirv.pack").is_file());
        Ok(())
    }

    #[test]
    fn custom_global_store_clears_only_identified_files_and_obeys_lock() -> Result<()> {
        let t = tempfile::tempdir()?;
        let work = t.path().join("work");
        let folder = work.join("longshot-builder/shaders");
        store_pair(&folder)?;
        let other = folder.join("spirv.pack.personal");
        fs::write(&other, b"personal")?;
        let caches = locations(t.path(), &work, Some(&folder))?;
        assert_eq!(caches.len(), 1);
        assert_eq!(caches[0].kinds.len(), 2);
        let lock = fs::File::open(folder.join("spirv.lock"))?;
        lock.try_lock()?;
        assert!(clear(t.path(), &work, Some(&folder), &folder).is_err());
        assert!(folder.join("spirv.pack").is_file());
        lock.unlock()?;
        drop(lock);
        fs::remove_file(folder.join("spirv.lock"))?;
        clear(t.path(), &work, Some(&folder), &folder)?;
        assert!(folder.join("spirv.lock").is_file());
        assert!(!folder.join("spirv.pack").exists());
        assert_eq!(fs::read(other)?, b"personal");
        fs::write(folder.join("spirv.pack"), b"private document")?;
        clear(t.path(), &work, Some(&folder), &folder)?;
        assert_eq!(fs::read(folder.join("spirv.pack"))?, b"private document");
        Ok(())
    }

    #[test]
    fn registered_game_and_builder_use_scoped_file_names() -> Result<()> {
        let t = tempfile::tempdir()?;
        let work = t.path().join("work");
        let builder = work.join("longshot-builder/shaders");
        let driver = builder.join("Stamp123/1e2f34567890abcd");
        let build = t.path().join("versions/nightly-42/builds/default");
        let cache = build.join("cache");
        for folder in [&cache, &driver] {
            fs::create_dir_all(folder)?;
            fs::write(folder.join("longshot_programs.bin"), b"generated")?;
            fs::write(folder.join("longshot_pipelines.bin"), b"shipped notes")?;
            fs::write(folder.join("save.dat"), b"private")?;
        }
        let builder_exe = if cfg!(windows) {
            "Longshot.exe"
        } else {
            "Longshot.x86_64"
        };
        fs::write(
            t.path().join("versions/nightly-42").join(builder_exe),
            b"fixture",
        )?;
        let outside = t.path().join("private");
        fs::write(&outside, b"untouched")?;
        #[cfg(unix)]
        std::os::unix::fs::symlink(&outside, cache.join("longshot_prepared.bin"))?;
        let registry = t.path().join(game::registry::OWN);
        home::write_json(
            &registry,
            &json!({"format":1,"builds":[{"folder":build,"name":"My nightly"}]}),
        )?;
        assert!(
            locations(t.path(), &work, None)?
                .iter()
                .any(|c| c.label.contains("My nightly"))
        );
        let lock_path = cache.join("longshot_programs.bin.lock");
        fs::write(&lock_path, b"")?;
        let lock = fs::File::open(&lock_path)?;
        lock.try_lock()?;
        assert!(clear(t.path(), &work, None, &cache).is_err());
        lock.unlock()?;
        clear(t.path(), &work, None, &cache)?;
        clear(t.path(), &work, None, &builder)?;
        for folder in [&cache, &driver] {
            assert!(!folder.join("longshot_programs.bin").exists());
            assert!(folder.join("save.dat").exists());
            assert!(folder.join("longshot_pipelines.bin").exists());
        }
        assert_eq!(fs::read(&outside)?, b"untouched");
        fs::write(cache.join("longshot_programs.bin"), b"generated")?;
        home::write_json(&registry, &json!({"format":1,"builds":[]}))?;
        assert!(clear(t.path(), &work, None, &cache).is_err());
        assert!(cache.join("longshot_programs.bin").is_file());
        Ok(())
    }
}
