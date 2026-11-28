//! The default library belongs to the launcher. In a custom library only folders
//! created for this installation and paired with its local ownership record are managed.
use super::versions::{linked, managed_folder, valid_name};
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{self, Write},
    path::{Path, PathBuf},
};

const LOCATIONS: &str = "version-locations";
const OWNER: &str = ".longshot-launcher-owner";

pub(crate) const FETCH_LOCK: &str = ".launcher-fetch.lock";

pub(super) fn tool_file(name: &std::ffi::OsStr) -> bool {
    name == super::process::executable()
        || matches!(
            name.to_str(),
            Some(
                OWNER
                    | FETCH_LOCK
                    | ".longshot-builder.lock"
                    | "longshot_release.json"
                    | "source.json"
            )
        )
}

pub fn install_root(home: &Path, settings: &Value) -> PathBuf {
    let data = home::data_folder(home, settings);
    if data != home {
        return data.join("versions");
    }
    settings["buildsFolder"]
        .as_str()
        .filter(|s| !s.trim().is_empty())
        .map(|s| home.join(crate::fsx::plain(Path::new(s))))
        .unwrap_or_else(|| home.join("versions"))
}

fn safe_path(path: &Path) -> bool {
    path.is_absolute() && path.ancestors().all(|p| !linked(p))
}

fn record_path(home: &Path, folder: &Path) -> PathBuf {
    let hash = Sha256::digest(folder.as_os_str().as_encoded_bytes());
    home.join(LOCATIONS).join(format!("{hash:x}.json"))
}

pub(super) fn recorded_owner(home: &Path, folder: &Path) -> Option<String> {
    let record = home::read_json(&record_path(home, folder)).ok()?;
    (record["format"] == 1 && record["folder"].as_str().map(Path::new) == Some(folder))
        .then(|| {
            record["owner"]
                .as_str()
                .filter(|s| !s.is_empty())
                .map(str::to_owned)
        })
        .flatten()
}

pub(super) fn record_move(home: &Path, folder: &Path, token: &str) -> Result<()> {
    ensure!(
        safe_path(&home.join(LOCATIONS)),
        "Invalid version locations directory."
    );
    home::write_json(
        &record_path(home, folder),
        &json!({"format":1,"folder":folder,"owner":token}),
    )
}

fn read_record(path: &Path) -> Option<PathBuf> {
    if !players::is_file(path) {
        return None;
    }
    let value = home::read_json(path).ok()?;
    let folder = PathBuf::from(value["folder"].as_str()?);
    let token = value["owner"].as_str()?;
    let owner = folder.join(OWNER);
    (value["format"] == 1
        && !token.is_empty()
        && safe_path(&folder)
        && players::is_file(&owner)
        && fs::read_to_string(owner).is_ok_and(|s| s == token))
    .then_some(folder)
}

pub fn owned_folder(home: &Path, folder: &Path) -> bool {
    if !players::is_dir(folder) {
        return false;
    }
    let default = home.join("versions");
    if folder
        .file_name()
        .and_then(|s| s.to_str())
        .is_some_and(valid_name)
        && folder.parent().is_some_and(|parent| {
            parent == default
                || (cfg!(windows) && default.canonicalize().is_ok_and(|path| path == parent))
        })
    {
        return players::is_dir(&default);
    }
    players::is_dir(&home.join(LOCATIONS))
        && read_record(&record_path(home, folder)).as_deref() == Some(folder)
}

pub fn managed_folders(home: &Path) -> Result<Vec<PathBuf>> {
    let default = home.join("versions");
    ensure!(!linked(&default), "The default versions folder is a link.");
    let mut result = Vec::new();
    match fs::read_dir(&default) {
        Ok(entries) => {
            for entry in entries {
                let folder = entry?.path();
                if owned_folder(home, &folder) && managed_folder(&folder) {
                    result.push(folder);
                }
            }
        }
        Err(e) if e.kind() == io::ErrorKind::NotFound => (),
        Err(e) => return Err(e.into()),
    }
    let locations = home.join(LOCATIONS);
    if players::is_dir(&locations) {
        for entry in fs::read_dir(locations)? {
            let path = entry?.path();
            if path.extension().is_some_and(|e| e == "json")
                && let Some(folder) = read_record(&path)
                && path == record_path(home, &folder)
                && managed_folder(&folder)
                && !result.contains(&folder)
            {
                result.push(folder);
            }
        }
    }
    if let Ok(reserved) = super::registry::stable_only(home) {
        result.retain(|folder| !super::registry::overlaps(folder, &reserved));
    }
    Ok(result)
}

pub(super) fn existing(home: &Path, settings: &Value, tag: &str) -> Result<Option<PathBuf>> {
    let playable = super::versions::listed(home)?
        .into_iter()
        .find(|v| v.tag == tag && owned_folder(home, &v.folder));
    if let Some(version) = playable {
        return Ok(Some(version.folder));
    }
    let base = install_root(home, settings);
    let here = base.canonicalize().unwrap_or(base).join(tag);
    Ok((owned_folder(home, &here)
        && managed_folder(&here)
        && !super::registry::overlaps(&here, &super::registry::stable_only(home)?))
    .then_some(here))
}

#[cfg(test)]
pub(super) fn destination(home: &Path, settings: &Value, tag: &str) -> Result<PathBuf> {
    ensure!(valid_name(tag), "Invalid builder version.");
    if let Some(existing) = existing(home, settings, tag)? {
        return Ok(existing);
    }
    reserve(home, settings, tag)
}

// Tools can be shared across installations. Game builds need their own output directory.
pub(super) fn new_build(home: &Path, settings: &Value, tag: &str) -> Result<PathBuf> {
    ensure!(valid_name(tag), "Invalid builder version.");
    use std::hash::{BuildHasher, RandomState};
    let name = format!("{:016x}", RandomState::new().hash_one(tag));
    reserve_inner(home, settings, &name, true)
}

pub(super) fn build_output_exists(folder: &Path) -> Result<bool> {
    let builds = folder.join("builds");
    if linked(&builds) {
        return Ok(true);
    }
    match fs::read_dir(builds) {
        Ok(mut children) => Ok(children.next().transpose()?.is_some()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(e.into()),
    }
}

#[cfg(test)]
fn reserve(home: &Path, settings: &Value, name: &str) -> Result<PathBuf> {
    reserve_inner(home, settings, name, false)
}

fn reserve_inner(home: &Path, settings: &Value, name: &str, fresh: bool) -> Result<PathBuf> {
    ensure!(valid_name(name), "Invalid builder version.");
    ensure!(
        !super::registry::overlaps(
            &install_root(home, settings).join(name),
            &super::registry::stable_only(home)?
        ),
        "This folder belongs to the stable launcher. Choose another installation folder."
    );
    let base = install_root(home, settings);
    ensure!(
        !linked(&base),
        "Choose a regular folder for the build library."
    );
    let managed = managed_folders(home)?;
    let ancestor = base.ancestors().find_map(|p| p.canonicalize().ok());
    ensure!(
        !ancestor.as_ref().is_some_and(|parent| managed
            .iter()
            .any(|p| p.canonicalize().is_ok_and(|p| parent.starts_with(p)))),
        "Choose a library outside installed versions."
    );
    fs::create_dir_all(&base)?;
    if base == home.join("versions") {
        let folder = base.join(name);
        ensure!(!linked(&folder), "The version folder is a link.");
        if fresh {
            fs::create_dir(&folder)?;
        } else {
            fs::create_dir_all(&folder)?;
        }
        return Ok(folder);
    }
    // Canonicalize once so a parent symlink can never silently redirect a recorded install.
    let base = fs::canonicalize(base)?;
    let folder = base.join(name);
    let locations = home.join(LOCATIONS);
    ensure!(
        !linked(&locations),
        "The version locations folder is a link."
    );
    fs::create_dir_all(&locations)?;
    let lock_path = locations.join("reserve.lock");
    ensure!(!linked(&lock_path), "The version locations lock is a link.");
    let lock = fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(false)
        .open(lock_path)?;
    lock.lock()?;
    if !fresh && owned_folder(home, &folder) {
        return Ok(folder);
    }
    // Never adopt an existing directory in a user-selected library. It may belong to another application.
    let mut temp = tempfile::NamedTempFile::new_in(&locations)?;
    fs::create_dir(&folder).with_context(|| {
        format!(
            "Cannot create {}. Choose an empty location for this version.",
            crate::home::display(&folder)
        )
    })?;
    let token = temp
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    let owner = folder.join(OWNER);
    let store = || -> Result<()> {
        fs::File::create_new(&owner)?.write_all(token.as_bytes())?;
        serde_json::to_writer(
            &mut temp,
            &json!({"format":1,"folder":folder,"owner":token}),
        )?;
        temp.flush()?;
        temp.as_file().sync_all()?;
        temp.persist(record_path(home, &folder))?;
        Ok(())
    };
    if let Err(e) = store() {
        let _ = fs::remove_file(owner);
        let _ = fs::remove_dir(&folder);
        return Err(e);
    }
    Ok(folder)
}

pub(crate) fn check_move_record(home: &Path, folder: &Path) -> Result<()> {
    ensure!(
        !linked(&home.join(LOCATIONS)),
        "The version locations folder is a link."
    );
    let path = record_path(home, folder);
    match fs::symlink_metadata(&path) {
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(err) => Err(err.into()),
        Ok(_) => anyhow::bail!(
            "A version ownership record already exists for {}.",
            folder.display()
        ),
    }
}

/// Record a newly published copy. The move owns this directory and has already refused
/// collisions. A copied marker must not remain linked to the original folder.
pub(crate) fn record_moved_version(home: &Path, folder: &Path) -> Result<()> {
    let locations = home.join(LOCATIONS);
    ensure!(
        !linked(&locations),
        "The version locations folder is a link."
    );
    let dest = record_path(home, folder);
    if fs::symlink_metadata(&dest).is_ok() {
        ensure!(
            read_record(&dest).as_deref() == Some(folder),
            "The version ownership record changed during publication."
        );
        return Ok(());
    }
    let owner = folder.join(OWNER);
    if fs::symlink_metadata(&owner).is_ok() {
        fs::remove_file(&owner)?;
    }
    fs::create_dir_all(&locations)?;
    let mut record = tempfile::NamedTempFile::new_in(&locations)?;
    let token = record
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    let mut marker = fs::File::create_new(&owner)?;
    marker.write_all(token.as_bytes())?;
    marker.sync_all()?;
    serde_json::to_writer(
        &mut record,
        &json!({"format":1,"folder":folder,"owner":token}),
    )?;
    record.flush()?;
    record.as_file().sync_all()?;
    record.persist_noclobber(&dest)?;
    Ok(())
}
pub(crate) fn forget_moved_version(home: &Path, old: &Path) {
    if !old.exists() {
        let _ = fs::remove_file(record_path(home, old));
    }
}

/// A custom library can contain other programs. Only the private version directory
/// and the top-level files written by the launcher and builder may be reclaimed.
pub(super) fn removable(home: &Path, folder: &Path) -> Result<()> {
    ensure!(
        owned_folder(home, folder),
        "Only managed version folders can be deleted."
    );
    if folder.parent() == Some(home.join("versions").as_path()) {
        return Ok(());
    }
    for item in fs::read_dir(folder)? {
        let name = item?.file_name();
        ensure!(
            tool_file(&name)
                || matches!(name.to_str(), Some(".longshot-retired" | "builds"))
                || (name == super::versions::INSTALLING && super::is_retired(folder)),
            "This custom version folder contains additional files and has been kept."
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::game::process::executable;
    #[test]
    fn multiple_libraries_keep_only_owned_versions() -> Result<()> {
        let home = tempfile::tempdir()?;
        let outside = tempfile::tempdir()?;
        let base = outside.path().join("custom");
        let another = outside.path().join("new");
        let settings = json!({"buildsFolder":base});
        let data = outside.path().join("data");
        let moved = json!({"dataFolder": data, "buildsFolder": base});
        assert_eq!(install_root(home.path(), &moved), data.join("versions"));
        let relocated = new_build(home.path(), &moved, "v4.0.0")?;
        assert_eq!(
            relocated.parent().unwrap(),
            data.canonicalize()?.join("versions")
        );
        let id = relocated.file_name().unwrap().to_str().unwrap();
        assert_eq!(id.len(), 16);
        assert!(id.bytes().all(|b| b.is_ascii_hexdigit()));
        assert_ne!(relocated, new_build(home.path(), &moved, "v4.0.0")?);
        assert!(owned_folder(home.path(), &relocated));
        let old = destination(home.path(), &json!({}), "v3.0.0")?;
        crate::game::fake_version(&old)?;
        let folder = destination(home.path(), &settings, "v3.1.0")?;
        assert_eq!(folder, base.canonicalize()?.join("v3.1.0"));
        crate::game::fake_version(&folder)?;
        let neighbor = base.join("v3.2.0");
        fs::create_dir(&neighbor)?;
        fs::write(neighbor.join(executable()), "unrelated")?;
        assert!(destination(home.path(), &settings, "v3.2.0").is_err());
        assert!(!owned_folder(home.path(), &neighbor));
        let changed = json!({"buildsFolder":another});
        assert_eq!(destination(home.path(), &changed, "v3.1.0")?, folder);
        assert_eq!(destination(home.path(), &changed, "v3.0.0")?, old);
        assert_eq!(managed_folders(home.path())?.len(), 2);
        let newer = destination(home.path(), &changed, "v3.2.0")?;
        assert_eq!(newer.parent(), Some(another.canonicalize()?.as_path()));
        fs::write(folder.join("my-notes.txt"), "keep")?;
        assert!(removable(home.path(), &folder).is_err());
        assert!(folder.join("my-notes.txt").is_file());
        fs::remove_file(folder.join("my-notes.txt"))?;
        assert!(removable(home.path(), &folder).is_ok());
        fs::write(newer.join(executable()), [])?;
        assert!(crate::game::remove_managed(home.path(), &neighbor).is_err());
        let installed = crate::game::versions::installed(home.path())?
            .into_iter()
            .find(|v| v.folder == newer)
            .unwrap();
        crate::game::versions::delete_when_idle(home.path(), &installed)?;
        assert!(!newer.exists());
        fs::remove_file(folder.join(OWNER))?;
        assert!(!owned_folder(home.path(), &folder));
        assert_eq!(managed_folders(home.path())?, vec![old]);
        assert_eq!(
            fs::read_to_string(neighbor.join(executable()))?,
            "unrelated"
        );
        Ok(())
    }
}
