use anyhow::{Context, Result, ensure};
use std::{
    fs::{self, File, OpenOptions},
    io,
    path::{Path, PathBuf},
};

pub fn is_file(p: &Path) -> bool {
    fs::symlink_metadata(p).is_ok_and(|m| !crate::fsx::is_link(&m) && m.is_file())
}
pub fn is_dir(p: &Path) -> bool {
    fs::symlink_metadata(p).is_ok_and(|m| !crate::fsx::is_link(&m) && m.is_dir())
}
pub fn require_dir(p: &Path) -> Result<()> {
    ensure!(
        is_dir(p),
        "{} must be a regular folder.",
        crate::home::display(p)
    );
    Ok(())
}
pub fn require_file(p: &Path) -> Result<()> {
    ensure!(
        is_file(p),
        "{} must be a regular file.",
        crate::home::display(p)
    );
    Ok(())
}
pub fn dirs(p: &Path) -> Result<Vec<PathBuf>> {
    if !p.try_exists()? {
        return Ok(Vec::new());
    }
    require_dir(p)?;
    let mut dirs = Vec::new();
    for e in fs::read_dir(p)? {
        let p = e?.path();
        if is_dir(&p) {
            dirs.push(p);
        }
    }
    dirs.sort();
    Ok(dirs)
}
pub fn copy_new(from: &Path, to: &Path) -> Result<()> {
    require_file(from)?;
    let mut src = File::open(from)?;
    let mut dst = OpenOptions::new().write(true).create_new(true).open(to)?;
    io::copy(&mut src, &mut dst)?;
    dst.set_modified(src.metadata()?.modified()?)?;
    dst.sync_all()?;
    Ok(())
}
pub fn copy_tree(from: &Path, to: &Path) -> Result<()> {
    copy_tree_filtered(from, to, &|_| true)
}

pub(super) fn copy_tree_filtered(
    from: &Path,
    to: &Path,
    include: &impl Fn(&Path) -> bool,
) -> Result<()> {
    require_dir(from)?;
    if !to.try_exists()? {
        fs::create_dir(to)?;
    }
    require_dir(to)?;
    for e in fs::read_dir(from)? {
        let e = e?;
        let name = e.file_name();
        let src = e.path();
        if !include(&src) {
            continue;
        }
        let dst = to.join(name);
        if is_dir(&src) {
            copy_tree_filtered(&src, &dst, include)?;
        } else {
            copy_new(&src, &dst)?;
        }
    }
    Ok(())
}
pub(super) fn resolve(p: &Path) -> Result<PathBuf> {
    match fs::canonicalize(p) {
        Ok(path) => return Ok(path),
        Err(err) if err.kind() == io::ErrorKind::NotFound => (),
        Err(err) => return Err(err.into()),
    }
    match fs::symlink_metadata(p) {
        Ok(_) => anyhow::bail!("A linked destination cannot be resolved."),
        Err(err) if err.kind() == io::ErrorKind::NotFound => (),
        Err(err) => return Err(err.into()),
    }
    let parent = p.parent().context("The folder path is incomplete.")?;
    Ok(resolve(parent)?.join(p.file_name().context("The folder name is missing.")?))
}

pub fn disjoint(from: &Path, to: &Path) -> Result<()> {
    let a = resolve(from)?;
    let b = resolve(to)?;
    ensure!(
        !crate::fsx::overlaps(&a, &b),
        "Choose separate source and destination folders."
    );
    Ok(())
}

pub fn same_tree(a: &Path, b: &Path) -> Result<bool> {
    if is_file(a) && is_file(b) {
        return crate::fsx::same_file_contents(a, b, || Ok(()));
    }
    if !is_dir(a) || !is_dir(b) {
        return Ok(false);
    }
    let entries = |path| {
        fs::read_dir(path)?
            .map(|e| e.map(|v| v.file_name()))
            .collect::<io::Result<std::collections::BTreeSet<_>>>()
    };
    let names = entries(a)?;
    if names != entries(b)? {
        return Ok(false);
    }
    for name in names {
        if !same_tree(&a.join(&name), &b.join(&name))? {
            return Ok(false);
        }
    }
    Ok(true)
}

/// Remove a source only after its captured contents compare equal to the copy.
/// If another process replaces the source mid-operation, keep both copies.
pub fn remove_after_copy(source: &Path, copied: &Path) -> Result<()> {
    let parent = source.parent().context("The source has no parent.")?;
    let hold = tempfile::Builder::new()
        .prefix(".save-removal-")
        .tempdir_in(parent)?;
    let held = hold.path().join("original");
    fs::rename(source, &held)?;
    if same_tree(&held, copied).unwrap_or(false) {
        hold.close()?;
        return Ok(());
    }
    if source.try_exists().unwrap_or(true) || fs::rename(&held, source).is_err() {
        anyhow::bail!(
            "The source changed before removal. Its contents are preserved in {}.",
            crate::home::display(&hold.keep())
        );
    }
    anyhow::bail!(
        "The source changed during the copy and was kept. Check its contents before trying again."
    )
}
