//! Reconnect a missing registry row without assigning another copy its identity.
use super::{library, registry, versions};
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::Value;
use std::{
    fs,
    path::{Path, PathBuf},
};

fn old_root(row: &Value) -> Option<&Path> {
    let builds = Path::new(row["folder"].as_str()?).parent()?;
    if builds.ends_with("builds") {
        builds.parent()
    } else {
        None
    }
}

fn missing(root: &Path) -> Result<Vec<String>> {
    Ok(versions::registry(root)?["builds"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|row| old_root(row).is_some_and(|p| p.try_exists().is_ok_and(|v| !v)))
        .filter_map(|row| row["id"].as_str().map(str::to_owned))
        .collect())
}

fn matches(root: &Path, row: &Value, folder: &Path) -> Result<bool> {
    let old = old_root(row).context("Invalid installation record.")?;
    let build = Path::new(row["folder"].as_str().unwrap())
        .file_name()
        .context("Missing build name.")?;
    if folder.parent() != old.parent()
        || old.try_exists()?
        || !versions::managed_folder(folder)
        || folder.ancestors().any(versions::linked)
        || super::is_retired(folder)
        || !row["version"].as_str().is_some_and(versions::valid_name)
        || registry::overlaps(folder, &registry::stable_only(root)?)
        || !players::is_dir(&folder.join("builds"))
        || !players::is_dir(&folder.join("builds").join(build))
        || home::read_json(&folder.join("longshot_release.json"))
            .ok()
            .is_none_or(|r| r["version"] != row["version"])
        || versions::registry(root)?["builds"]
            .as_array()
            .into_iter()
            .flatten()
            .any(|r| old_root(r) == Some(folder))
    {
        return Ok(false);
    }
    if let Some(owner) = library::recorded_owner(root, old) {
        let marker = folder.join(".longshot-launcher-owner");
        return Ok(players::is_file(&marker) && fs::read_to_string(marker)? == owner);
    }
    Ok(false)
}

fn reconnect_row(root: &Path, id: &str, folder: &Path) -> Result<()> {
    let _lease = super::move_lease(root)?;
    let folder = folder.to_owned();
    let mut data = versions::registry(root)?;
    let row = data["builds"]
        .as_array_mut()
        .unwrap()
        .iter_mut()
        .find(|r| r["id"] == id)
        .context("Installation disappeared.")?;
    ensure!(
        matches(root, row, &folder)?,
        "Choose the matching Longshot installation folder."
    );
    let old = old_root(row).unwrap().to_owned();
    let name = Path::new(row["folder"].as_str().unwrap())
        .file_name()
        .unwrap();
    let new_build = folder.join("builds").join(name);
    let token = library::recorded_owner(root, &old)
        .context("Installation ownership record disappeared.")?;
    library::record_move(root, &folder, &token)?;
    registry::append_unique(row, "previousFolders", serde_json::json!(old))?;
    row["folder"] = serde_json::json!(new_build);
    registry::write(root, &data)?;
    library::forget_moved_version(root, &old);
    Ok(())
}

pub(crate) fn reconnect(root: &Path) -> Result<()> {
    for row in versions::registry(root)?["builds"]
        .as_array()
        .into_iter()
        .flatten()
    {
        if let Some(folder) = old_root(row)
            && folder.parent() == Some(root.join("versions").as_path())
            && versions::managed_folder(folder)
            && !folder.join(".longshot-launcher-owner").exists()
        {
            let _ = library::record_moved_version(root, folder);
        }
    }
    for id in missing(root)? {
        let data = versions::registry(root)?;
        let row = data["builds"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["id"] == id)
            .context("Missing installation")?;
        let Some(base) = old_root(row).and_then(Path::parent) else {
            continue;
        };
        let Ok(entries) = fs::read_dir(base) else {
            continue;
        };
        let mut candidates = Vec::<PathBuf>::new();
        for entry in entries {
            let folder = entry?.path();
            if matches(root, row, &folder)? {
                candidates.push(folder);
            }
        }
        if let [folder] = candidates.as_slice() {
            reconnect_row(root, &id, folder)?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn rename_keeps_identity_and_move_outside_library_hides_installation() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path();
        let library = root.join("library");
        let old =
            super::super::library::new_build(root, &json!({"buildsFolder":library}), "nightly-1")?;
        super::super::fake_version(&old)?;
        home::write_json(
            &old.join("longshot_release.json"),
            &json!({"version":"nightly-1"}),
        )?;
        registry::write(
            root,
            &json!({"format":1,"builds":[{"id":"instance-a","version":"nightly-1","folder":old.join("builds/default")}]}),
        )?;
        let renamed = library.join("My renamed game");
        fs::rename(&old, &renamed)?;
        reconnect(root)?;
        let listed = versions::listed(root)?;
        assert_eq!(listed.len(), 1);
        assert_eq!(listed[0].id, "instance-a");
        assert_eq!(listed[0].folder, renamed);
        let external = root.join("another drive");
        fs::rename(&renamed, &external)?;
        reconnect(root)?;
        assert_eq!(missing(root)?, ["instance-a"]);
        assert!(versions::listed(root)?.is_empty());
        assert!(reconnect_row(root, "instance-a", &external).is_err());
        Ok(())
    }
}
