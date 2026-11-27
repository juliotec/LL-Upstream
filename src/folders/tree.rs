//! Stage data beside its destination. Copy links as links, preserve executable bits and file times,
//! and use hard links on the same filesystem to keep large build stores cheap to relocate.
use crate::fsx::plain;
pub(super) use crate::fsx::resolve_path as absolute;
use crate::storage::{hard_link_id, is_link};
use anyhow::{Context, Result, bail, ensure};
use std::{
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, Ordering},
};

pub(super) fn running(cancel: &AtomicBool) -> Result<()> {
    ensure!(!cancel.load(Ordering::Acquire), "Folder change cancelled.");
    Ok(())
}

pub(super) fn exists(path: &Path) -> Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(_) => Ok(true),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(e.into()),
    }
}

fn relocated_link(
    path: &Path,
    raw: &Path,
    old: &Path,
    new: &Path,
    entries: &[super::Entry],
) -> Result<PathBuf> {
    // Relative internal links remain valid at the new location. An external relative link must
    // keep pointing at its original target even if the new folder is on another drive.
    let absolute = if raw.is_absolute() {
        raw.to_owned()
    } else {
        path.parent().context("Missing link parent.")?.join(raw)
    };
    let mut actual = PathBuf::new();
    for c in absolute.components() {
        match c {
            std::path::Component::ParentDir => {
                actual.pop();
            }
            std::path::Component::CurDir => (),
            other => actual.push(other.as_os_str()),
        }
    }
    if let Ok(tail) = plain(&actual).strip_prefix(plain(old).as_ref()) {
        return Ok(if raw.is_absolute() {
            new.join(tail)
        } else {
            raw.to_owned()
        });
    }
    for entry in entries {
        if let Ok(tail) = plain(&actual).strip_prefix(plain(&entry.old).as_ref()) {
            return Ok(entry.new.join(tail));
        }
    }
    Ok(actual)
}

fn write_link(old: &Path, to: &Path, target: &Path) -> Result<()> {
    #[cfg(unix)]
    {
        let _ = old;
        std::os::unix::fs::symlink(target, to)?;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::{FileTypeExt, symlink_dir, symlink_file};
        let kind = fs::symlink_metadata(old)?.file_type();
        if junction::get_target(old).is_ok() {
            junction::create(target, to)?;
        } else if kind.is_symlink_dir() {
            symlink_dir(target, to)?;
        } else if kind.is_symlink_file() {
            symlink_file(target, to)?;
        } else {
            bail!("An unsupported Windows reparse point was found.")
        }
    }
    Ok(())
}

fn builder_lock(name: &std::ffi::OsStr) -> bool {
    matches!(
        name.to_str(),
        Some(
            "longshot-builder.lock"
                | ".longshot-builder.lock"
                | crate::game::FETCH_LOCK
                | "longshot_programs.bin.lock"
        )
    )
}

pub(super) fn has_workspace_data(root: &Path) -> Result<bool> {
    let mut dirs = vec![root.to_owned()];
    while let Some(dir) = dirs.pop() {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            let metadata = fs::symlink_metadata(entry.path())?;
            if is_link(&metadata) {
                return Ok(true);
            }
            if metadata.is_dir() {
                dirs.push(entry.path());
            } else if !builder_lock(&entry.file_name()) || metadata.len() != 0 {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

pub(super) fn copy(
    src: &Path,
    dst: &Path,
    final_path: &Path,
    entries: &[super::Entry],
    cancel: &AtomicBool,
    locks: &mut Vec<crate::home::FileLease>,
    hardlinks: &mut std::collections::HashMap<(u64, u64), PathBuf>,
) -> Result<()> {
    let mut directories = vec![(src.to_owned(), dst.to_owned())];
    let mut modes = Vec::new();
    while let Some((from, to)) = directories.pop() {
        running(cancel)?;
        fs::create_dir(&to)?;
        modes.push((to.clone(), fs::metadata(&from)?.permissions()));
        for entry in fs::read_dir(&from)? {
            running(cancel)?;
            let entry = entry?;
            let old = entry.path();
            let new = to.join(entry.file_name());
            let meta = fs::symlink_metadata(&old)?;
            if is_link(&meta) {
                write_link(
                    &old,
                    &new,
                    &relocated_link(&old, &fs::read_link(&old)?, src, final_path, entries)?,
                )?;
            } else if meta.is_dir() {
                directories.push((old, new));
            } else if meta.is_file() {
                if builder_lock(&entry.file_name()) {
                    locks.push(
                        crate::home::FileLease::try_acquire(
                            fs::File::open(&old)?,
                            crate::fsx::LockMode::Shared,
                        )
                        .with_context(|| {
                            format!("A game or builder is using {}.", crate::home::display(&old))
                        })?,
                    );
                }
                if fs::hard_link(&old, &new).is_err() {
                    let key = hard_link_id(&old, &meta);
                    if let Some(earlier) = key.and_then(|key| hardlinks.get(&key))
                        && fs::hard_link(earlier, &new).is_ok()
                    {
                        continue;
                    }
                    let mut from = fs::File::open(&old)?;
                    let mut to = fs::File::create_new(&new)?;
                    let mut buf = vec![0; 128 * 1024];
                    loop {
                        running(cancel)?;
                        let n = from.read(&mut buf)?;
                        if n == 0 {
                            break;
                        }
                        to.write_all(&buf[..n])?;
                    }
                    to.set_modified(meta.modified()?)?;
                    to.set_permissions(meta.permissions())?;
                    to.sync_all()?;
                    if let Some(key) = key {
                        hardlinks.insert(key, new);
                    }
                }
            } else {
                bail!("Cannot move a special file: {}", crate::home::display(&old));
            }
        }
    }
    for (path, mode) in modes.into_iter().rev() {
        fs::set_permissions(path, mode)?;
    }
    Ok(())
}

pub(super) fn same(
    src: &Path,
    dst: &Path,
    original: &Path,
    final_path: &Path,
    entries: &[super::Entry],
    ignore_owner: bool,
    cancel: &AtomicBool,
) -> Result<bool> {
    let mut pairs = vec![(src.to_owned(), dst.to_owned())];
    while let Some((from, to)) = pairs.pop() {
        running(cancel)?;
        let a = fs::symlink_metadata(&from)?;
        let b = fs::symlink_metadata(&to)?;
        if is_link(&a) || is_link(&b) {
            if !(is_link(&a) && is_link(&b)) {
                return Ok(false);
            }
            // A source directory can be parked under a temporary name during final cleanup.
            let logical = original.join(from.strip_prefix(src)?);
            let raw = fs::read_link(&from)?;
            let expected = relocated_link(&logical, &raw, original, final_path, entries)?;
            if plain(&fs::read_link(&to)?) != plain(&expected) {
                return Ok(false);
            }
        } else if a.is_dir() && b.is_dir() {
            let names = |p: &Path| -> io::Result<std::collections::BTreeSet<_>> {
                fs::read_dir(p)?.map(|e| e.map(|v| v.file_name())).collect()
            };
            let mut left = names(&from)?;
            let mut right = names(&to)?;
            if ignore_owner && from == src {
                left.remove(std::ffi::OsStr::new(".longshot-launcher-owner"));
                right.remove(std::ffi::OsStr::new(".longshot-launcher-owner"));
            }
            if left != right {
                return Ok(false);
            }
            pairs.extend(
                left.into_iter()
                    .map(|name| (from.join(&name), to.join(name))),
            );
        } else if a.is_file() && b.is_file() && a.len() == b.len() {
            if let Some(key) = hard_link_id(&from, &a)
                && Some(key) == hard_link_id(&to, &b)
            {
                continue;
            }
            if !crate::fsx::same_file_contents(&from, &to, || running(cancel))? {
                return Ok(false);
            }
        } else {
            return Ok(false);
        }
    }
    Ok(true)
}
