//! The stable launcher keeps builds.json. Modern builders have their own versions.json.
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use std::path::{Component, Path, PathBuf};

pub(crate) const OWN: &str = "versions.json";
pub(crate) const STABLE: &str = "builds.json";

pub(super) fn write(root: &Path, data: &Value) -> Result<()> {
    home::write_json(&root.join(OWN), data)
}

pub(crate) fn read(root: &Path, name: &str) -> Result<Value> {
    let path = root.join(name);
    if !path.try_exists()? {
        return Ok(json!({"format":1,"builds":[]}));
    }
    let data = home::read_json(&path)?;
    ensure!(
        data["format"] == 1 && data["builds"].is_array(),
        "Unsupported or invalid {name}."
    );
    Ok(data)
}

/// Stable still owns this file on a relocated portable drive. Project its paths
/// into the current location for protection without saving that projection.
pub(crate) fn stable_view(root: &Path) -> Result<Value> {
    let mut data = read(root, STABLE)?;
    if home::is_portable(root) {
        home::relocate(root, &mut data)?;
    }
    Ok(data)
}

/// Folders exclusively registered with stable. A folder also registered with this launcher remains protected.
pub(crate) fn stable_only(root: &Path) -> Result<Vec<PathBuf>> {
    if !root.join(STABLE).try_exists()? {
        return Ok(vec![]);
    }
    let stable = stable_view(root)?;
    let own = read(root, OWN)?;
    let modern: std::collections::HashSet<_> = own["builds"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|row| row["folder"].as_str())
        .collect();
    Ok(stable["builds"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|row| row["folder"].as_str())
        .filter(|folder| !modern.contains(folder))
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .collect())
}

pub(super) fn overlaps(folder: &Path, reserved: &[PathBuf]) -> bool {
    let actual = folder.canonicalize().unwrap_or_else(|_| folder.to_owned());
    reserved.iter().any(|p| {
        let path = p.canonicalize().unwrap_or_else(|_| p.clone());
        path.starts_with(&actual) || actual.starts_with(&path)
    })
}

fn in_library(path: &Path, root: &Path) -> bool {
    let Ok(relative) = path.strip_prefix(root.join("versions")) else {
        return false;
    };
    let parts: Vec<_> = relative.components().collect();
    matches!(parts.as_slice(), [Component::Normal(tag), Component::Normal(builds), Component::Normal(_)]
        if *builds == "builds" && tag.to_str().is_some_and(super::versions::valid_name))
}

fn modern(row: &Value, root: &Path) -> bool {
    let Some(folder) = row["folder"].as_str().map(Path::new) else {
        return false;
    };
    row["nupInPlace"] == true
        || in_library(folder, root)
        || folder.parent().and_then(Path::parent).is_some_and(|v| {
            folder.parent() == Some(v.join("builds").as_path()) && super::owned_folder(root, v)
        })
        || [
            "longshot_build.json",
            "longshot_builder.json",
            "longshot.json",
        ]
        .iter()
        .any(|name| players::is_file(&folder.join(name)))
}

fn set_default(rows: &mut [Value]) {
    if !rows.iter().any(|row| row["default"] == true)
        && let Some(row) = rows.iter_mut().find(|row| row.is_object())
    {
        row["default"] = true.into();
    }
}

pub(crate) fn forget_retired(root: &Path, data: &mut Value, folder: &Path) {
    if let Some(retired) = data["retiredFolders"].as_array_mut() {
        retired.retain(|path| path.as_str().is_none_or(|p| root.join(p) != folder));
    }
}

/// Publish the rollback location and the replacement row in one registry write.
pub(super) fn record_replacement(
    root: &Path,
    data: &mut Value,
    index: usize,
    old: &Path,
    tag: &str,
) -> Result<()> {
    let path = json!(old.strip_prefix(root).unwrap_or(old));
    if super::owned_folder(root, old) {
        super::prune::mark_retired(old);
        append_unique(data, "retiredFolders", path.clone())?;
    }
    let row = &mut data["builds"][index];
    append_unique(row, "previousFolders", path.clone())?;
    row["previousFolder"] = path;
    row["previousVersion"] = json!(tag);
    append_unique(row, "previousTags", json!(tag))
}

pub(super) fn append_unique(object: &mut Value, key: &str, value: Value) -> Result<()> {
    if object[key].is_null() {
        object[key] = json!([]);
    }
    let items = object[key]
        .as_array_mut()
        .with_context(|| format!("Invalid {key} in version registry."))?;
    if !items.contains(&value) {
        items.push(value);
    }
    Ok(())
}

fn migrate_previous_update(root: &Path, data: &mut Value) -> Result<bool> {
    let settings = home::settings(root)?;
    let (Some(old), Some(new)) = (
        settings["rollbackVersion"].as_str(),
        settings["updatedToVersion"].as_str(),
    ) else {
        return Ok(false);
    };
    if old == new || !super::versions::valid_name(old) {
        return Ok(false);
    }
    let rows = data["builds"].as_array().expect("validated registry");
    let mut matches = rows
        .iter()
        .enumerate()
        .filter(|(_, row)| row["version"] == new);
    let Some((index, row)) = matches.next() else {
        return Ok(false);
    };
    if matches.next().is_some() || !row["previousFolder"].is_null() {
        return Ok(false);
    }
    let Some(library) = row["folder"]
        .as_str()
        .and_then(|p| Path::new(p).ancestors().nth(3))
    else {
        return Ok(false);
    };
    let folder = library.join(old);
    // Older launchers only recorded tag names. A replacement directory created
    // after that update belongs to a later installation.
    let Some(updated) = row["updated"]
        .as_str()
        .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
    else {
        return Ok(false);
    };
    let cutoff: std::time::SystemTime = updated.into();
    if [
        &folder,
        &folder.join(super::process::executable()),
        &folder.join("builds/default"),
    ]
    .iter()
    .any(|path| {
        std::fs::metadata(path)
            .and_then(|m| m.modified())
            .map_or(true, |modified| modified > cutoff)
    }) {
        return Ok(false);
    }
    if !super::owned_folder(root, &folder)
        || !super::versions::managed_folder(&folder)
        || rows.iter().any(|r| {
            r["folder"]
                .as_str()
                .is_some_and(|p| Path::new(p).starts_with(&folder))
        })
    {
        return Ok(false);
    }
    record_replacement(root, data, index, &folder, old)?;
    Ok(true)
}

/// Called under the home lock. Import compatible rows once, keeping stable's registry intact.
pub(crate) fn prepare(root: &Path, portable: bool) -> Result<()> {
    let own = root.join(OWN);
    let stable = root.join(STABLE);
    if !own.try_exists()?
        && stable.try_exists()?
        && let Ok(mut data) = read(root, STABLE)
    {
        if portable {
            home::relocate(root, &mut data)?;
        }
        let rows = data["builds"].as_array_mut().expect("validated registry");
        rows.retain(|row| modern(row, root));
        set_default(rows);
        home::write_json(&own, &data)?;
    }
    if own.try_exists()? {
        let mut data = read(root, OWN)?;
        let relocated = portable && home::relocate(root, &mut data)?;
        if migrate_previous_update(root, &mut data)? || relocated {
            home::write_json(&own, &data)?;
        }
    }
    super::recovery::reconnect(root)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::fs;

    #[test]
    fn mixed_registry_imports_only_compatible_builders_and_preserves_stable() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path().join("launcher");
        let recorded = temp.path().join("externally-recorded");
        fs::create_dir_all(&recorded)?;
        fs::write(recorded.join("longshot_builder.json"), "{}")?;
        let version = root.join("versions/nightly-22-abcdef01/builds/default");
        let old = json!({"id":"stable-id","folder":temp.path().join("stable"),"default":true,"version":"v3.0.0","unknown":7});
        let gone = json!({"id":"gone-pr","folder":temp.path().join("gone"), "channel":"pr"});
        let builder = json!({"id":"rust-preview","folder":version,"version":"nightly-22-abcdef01","extension":"preserved","default":true});
        let record = json!({"id":"record","folder":recorded});
        let marked = json!({"id":"marked","folder":temp.path().join("earlier"),"nupInPlace":true});
        let mixed =
            json!({"format":1,"unknownRoot":"preserved","builds":[old,gone,builder,record,marked]});
        home::write_json(&root.join(STABLE), &mixed)?;
        prepare(&root, false)?;
        let mine = read(&root, OWN)?;
        assert_eq!(mine["builds"], json!([builder, record, marked]));
        assert_eq!(mine["unknownRoot"], "preserved");
        assert_eq!(read(&root, STABLE)?, mixed);
        prepare(&root, false)?;
        assert_eq!(read(&root, STABLE)?, mixed);
        assert_eq!(
            stable_only(&root)?,
            vec![temp.path().join("stable"), temp.path().join("gone")]
        );
        Ok(())
    }

    #[test]
    fn stable_only_and_unreadable_registries_are_kept_byte_for_byte() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        prepare(root, false)?;
        assert!(!root.join(OWN).exists());
        for raw in [
            b"{ unreadable".as_slice(),
            br#"{"format":99,"builds":[]}"#,
            br#"{"format":1, "builds": [{"id":"old","folder":"missing"}]}"#,
        ] {
            fs::write(root.join(STABLE), raw)?;
            prepare(root, false)?;
            assert_eq!(fs::read(root.join(STABLE))?, raw);
        }
        assert_eq!(read(root, OWN)?["builds"], json!([]));
        Ok(())
    }

    #[test]
    fn moved_portable_reads_previous_locations_without_rewriting_stable_paths() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let old = temp.path().join("before/LongshotLauncher");
        let root = temp.path().join("after/LongshotLauncher");
        let relative = Path::new("versions/nightly-30-0123abcd/builds/default");
        let source = temp.path().join("after/my-game.nup");
        fs::create_dir_all(root.join(relative))?;
        fs::write(&source, b"local fixture")?;
        let old_row = json!({"folder":old.join("legacy/v3"),"id":"stable","default":true});
        let modern = json!({"folder":old.join(relative),"nup":temp.path().join("before/my-game.nup"),"id":"keep-id"});
        home::write_json(
            &root.join(STABLE),
            &json!({"format":1,"home":old,"builds":[old_row, modern]}),
        )?;
        prepare(&root, true)?;
        let mine = read(&root, OWN)?;
        assert_eq!(mine["home"], json!(root));
        assert_eq!(mine["builds"][0]["folder"], json!(root.join(relative)));
        assert_eq!(mine["builds"][0]["nup"], json!(source));
        assert_eq!(mine["builds"][0]["id"], "keep-id");
        assert_eq!(mine["builds"][0]["default"], true);
        assert_eq!(read(&root, STABLE)?["builds"], json!([old_row, modern]));
        assert_eq!(read(&root, STABLE)?["home"], json!(old));
        Ok(())
    }

    #[test]
    fn stable_folder_cannot_be_adopted_rebuilt_or_pruned() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let version = root.join("versions/v3.1.0");
        fs::create_dir_all(version.join("builds/default"))?;
        fs::write(
            version.join(super::super::process::executable()),
            b"fixture",
        )?;
        home::write_json(&root.join(OWN), &json!({"format":1,"builds":[]}))?;
        let stable = json!({"format":1,"builds":[{"folder":version,"default":true}]});
        home::write_json(&root.join(STABLE), &stable)?;
        assert!(super::super::versions::listed(root)?.is_empty());
        assert!(super::super::library::destination(root, &json!({}), "v3.1.0").is_err());
        assert!(crate::storage::protected_paths(root)?.contains(&version));
        assert_eq!(read(root, STABLE)?, stable);
        Ok(())
    }
}
