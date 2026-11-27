//! Mod-capable builders read installed source mods from `<cache>/mods`, even though
//! profile pins need those files to play offline. Keep that name as a directory link.
use super::tree;
use crate::{players, storage};
use anyhow::{Context, Result, bail, ensure};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::atomic::AtomicBool,
};

fn make_link(target: &Path, link: &Path) -> Result<()> {
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, link)?;
    #[cfg(windows)]
    junction::create(target, link)?;
    Ok(())
}

fn points_to(link: &Path, target: &Path) -> Result<bool> {
    let raw = fs::read_link(link)?;
    let path = link.parent().context("Invalid mod store path.")?.join(raw);
    Ok(tree::link_path(&path) == tree::link_path(target))
}

// Park the original only once the new link is ready. On success, the caller can
// verify any legacy files against the published copy before retiring them.
fn switch_link(
    alias: &Path,
    target: &Path,
    verify: impl FnOnce(&Path) -> Result<()>,
) -> Result<()> {
    let cache = alias.parent().context("Invalid builder cache.")?;
    let stage = tempfile::tempdir_in(cache)?;
    let next = stage.path().join("next");
    let old = stage.path().join("old");
    make_link(target, &next).with_context(|| format!(
        "The cache at {} must support directory links to keep installed mods with Longshot data.",
        cache.display()
    ))?;
    if !tree::exists(alias)? {
        return Ok(crate::fsx::rename_new(&next, alias)?);
    }
    crate::fsx::rename_new(alias, &old)?;
    if let Err(err) = verify(&old).and_then(|()| Ok(crate::fsx::rename_new(&next, alias)?)) {
        if let Err(restore) = crate::fsx::rename_new(&old, alias) {
            let backup = stage.keep().join("old");
            bail!(
                "Could not update the mod store: {err}. Restore {}: {restore}",
                backup.display()
            );
        }
        return Err(err);
    }
    stage
        .close()
        .with_context(|| format!("Could not retire {}.", old.display()))
}

fn data_store(cache: &Path, data: &Path) -> Result<PathBuf> {
    let target = tree::absolute(data)?.join("mods");
    let alias = tree::absolute(cache)?.join("mods");
    ensure!(
        !target.starts_with(&alias) && !alias.starts_with(&target),
        "Keep the mod store outside the builder cache."
    );
    if tree::exists(&target)? {
        players::require_dir(&target)?;
    }
    Ok(target)
}

fn validate_store(path: &Path) -> Result<()> {
    // Only the documented builder store can be adopted. Other directories named
    // mods can contain a player's own material and must stay where they are.
    for entry in fs::read_dir(path)? {
        let entry = entry?;
        ensure!(
            match entry.file_name().to_str() {
                Some("sources.json") => players::is_file(&entry.path()),
                Some("versions" | "blobs" | "sources" | "media" | "tmp") =>
                    players::is_dir(&entry.path()),
                _ => false,
            },
            "Unrecognised mod store: {}",
            path.display()
        );
    }
    Ok(())
}

/// Mod-only commands take `--cache` as the parent of their store. If a user has
/// removed the regenerable cache, they can still browse all installed mods.
pub(crate) fn read_cache(cache: &Path, data: &Path) -> Result<PathBuf> {
    if !tree::exists(&cache.join("mods"))? && players::is_dir(&data.join("mods")) {
        validate_store(&data.join("mods"))?;
        Ok(data.to_owned())
    } else {
        Ok(cache.to_owned())
    }
}

/// Call only for a confirmed folder change or an operation that installs mods.
/// Reading the library and measuring storage leave pre-existing caches untouched.
pub(crate) fn ensure(cache: &Path, data: &Path) -> Result<()> {
    let target = data_store(cache, data)?;
    let alias = cache.join("mods");
    fs::create_dir_all(cache)?;
    if !tree::exists(&alias)? {
        if tree::exists(&target)? {
            validate_store(&target)?;
        }
        fs::create_dir_all(&target)?;
        return switch_link(&alias, &target, |_| Ok(()));
    }
    let meta = fs::symlink_metadata(&alias)?;
    if storage::is_link(&meta) {
        ensure!(
            points_to(&alias, &target)?,
            "The cache links to another mod store."
        );
        players::require_dir(&target)?;
        return Ok(());
    }
    players::require_dir(&alias)?;
    validate_store(&alias)?;
    let cancel = AtomicBool::new(false);
    let same = |old: &Path| -> Result<()> {
        ensure!(
            tree::same(old, &target, &alias, &target, &[], false, &cancel)?,
            "Both folders contain different mod stores. Choose another folder."
        );
        Ok(())
    };
    if !tree::exists(&target)? {
        fs::create_dir_all(data)?;
        let stage = tempfile::tempdir_in(data)?;
        let copy = stage.path().join("mods");
        tree::copy(
            &alias,
            &copy,
            &target,
            &[],
            &cancel,
            &mut Vec::new(),
            &mut Default::default(),
        )?;
        crate::fsx::rename_new(&copy, &target)?;
    }
    switch_link(&alias, &target, |parked| {
        if fs::read_dir(parked)?.next().transpose()?.is_none() {
            Ok(())
        } else {
            same(parked)
        }
    })
}

/// Switch only the builder's known alias. A stale or third-party link is never replaced.
pub(crate) fn repoint(cache: &Path, old_data: &Path, new_data: &Path) -> Result<()> {
    let target = data_store(cache, new_data)?;
    let alias = cache.join("mods");
    if !tree::exists(&alias)? {
        if tree::exists(&target)? {
            ensure(cache, new_data)?;
        }
        return Ok(());
    }
    // A portable drive can return with a different drive letter. Its previous
    // absolute junction target can be verified even if that volume is offline.
    let old_target = old_data
        .canonicalize()
        .unwrap_or_else(|_| old_data.to_owned())
        .join("mods");
    let check = |path: &Path| -> Result<()> {
        ensure!(
            storage::is_link(&fs::symlink_metadata(path)?) && points_to(path, &old_target)?,
            "The cache does not link to the previous mod store."
        );
        Ok(())
    };
    if storage::is_link(&fs::symlink_metadata(&alias)?) && points_to(&alias, &target)? {
        return Ok(());
    }
    check(&alias)?;
    if tree::exists(&target)? {
        validate_store(&target)?;
    }
    fs::create_dir_all(&target)?;
    switch_link(&alias, &target, check)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn installed_blobs_and_source_pins_follow_data_across_cache_changes() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let cache = tmp.path().join("cache");
        let data = tmp.path().join("data");
        let alias = cache.join("mods");
        fs::create_dir_all(alias.join("blobs/ab"))?;
        fs::write(alias.join("blobs/ab/content"), b"offline mod payload")?;
        fs::write(alias.join("sources.json"), b"synthetic source order")?;
        ensure(&cache, &data)?;
        let other_cache = tmp.path().join("second-cache");
        ensure(&other_cache, &data)?;
        let erased_cache = tmp.path().join("erased-cache");
        assert_eq!(read_cache(&erased_cache, &data)?, data);
        assert!(!erased_cache.exists());
        let source = other_cache.join("mods/sources.json");
        assert_eq!(fs::read(&source)?, b"synthetic source order");
        let moved = tmp.path().join("moved-data");
        fs::rename(&data, &moved)?;
        repoint(&other_cache, &data, &moved)?;
        fs::write(&source, b"changed order")?;
        assert_eq!(fs::read(moved.join("mods/sources.json"))?, b"changed order");
        assert_eq!(
            fs::read(other_cache.join("mods/blobs/ab/content"))?,
            b"offline mod payload"
        );
        Ok(())
    }

    #[test]
    fn conflicting_existing_stores_remain_intact() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let cache = tmp.path().join("cache");
        let data = tmp.path().join("data");
        for dir in [&cache, &data] {
            fs::create_dir_all(dir.join("mods/versions"))?;
            fs::write(
                dir.join("mods/versions/sample.json"),
                dir.as_os_str().as_encoded_bytes(),
            )?;
        }
        assert!(ensure(&cache, &data).is_err());
        for dir in [&cache, &data] {
            let folder = dir.join("mods");
            assert!(players::is_dir(&folder));
            assert_eq!(
                fs::read(folder.join("versions/sample.json"))?,
                dir.as_os_str().as_encoded_bytes()
            );
        }
        Ok(())
    }
}
