//! Filesystem checks shared by the launcher, including Windows junctions.
use std::{
    fs::{self, File, OpenOptions, TryLockError},
    io,
    path::Path,
};

pub(crate) fn is_link(meta: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        meta.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    {
        meta.is_symlink()
    }
}

pub(crate) fn linked_location(path: &Path) -> io::Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(meta) => Ok(is_link(&meta)),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(e),
    }
}

pub(crate) fn direct(path: &Path) -> bool {
    path.is_absolute()
        && path
            .ancestors()
            .all(|p| fs::symlink_metadata(p).is_ok_and(|m| !is_link(&m)))
}

/// Compare already-resolved paths in both directions.
pub(crate) fn overlaps(left: &Path, right: &Path) -> bool {
    left.starts_with(right) || right.starts_with(left)
}

pub(crate) enum LockMode {
    Exclusive,
    Shared,
}

/// Release the OS lock explicitly, even when a child briefly inherits the
/// descriptor between fork and exec.
pub(crate) struct FileLease(pub(crate) File);

impl FileLease {
    pub(crate) fn try_acquire(file: File, mode: LockMode) -> Result<Self, TryLockError> {
        match mode {
            LockMode::Exclusive => file.try_lock()?,
            LockMode::Shared => file.try_lock_shared()?,
        }
        Ok(Self(file))
    }

    pub(crate) fn try_open(path: &Path, options: &OpenOptions, mode: LockMode) -> io::Result<Self> {
        Ok(Self::try_acquire(options.open(path)?, mode)?)
    }
}

impl Drop for FileLease {
    fn drop(&mut self) {
        let _ = self.0.unlock();
    }
}

pub(crate) fn remove_file_if_present(path: &Path) -> io::Result<()> {
    match fs::remove_file(path) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        result => result,
    }
}

/// Compare streams directly, allowing the caller to interrupt a large copy check.
pub(crate) fn same_file_contents(
    left: &Path,
    right: &Path,
    mut checkpoint: impl FnMut() -> anyhow::Result<()>,
) -> anyhow::Result<bool> {
    use io::{BufRead, BufReader};
    let mut left = BufReader::new(File::open(left)?);
    let mut right = BufReader::new(File::open(right)?);
    loop {
        checkpoint()?;
        let a = left.fill_buf()?;
        let b = right.fill_buf()?;
        let count = a.len().min(b.len());
        if count == 0 {
            return Ok(a.len() == b.len());
        }
        if a[..count] != b[..count] {
            return Ok(false);
        }
        left.consume(count);
        right.consume(count);
    }
}

pub(crate) fn atomic_write(
    path: &Path,
    _mode: Option<u32>,
    _sync_directory: bool,
    write: impl FnOnce(&mut File) -> anyhow::Result<()>,
) -> anyhow::Result<()> {
    use anyhow::Context;
    let dir = path.parent().context("No parent directory")?;
    fs::create_dir_all(dir)?;
    let mut stage = tempfile::NamedTempFile::new_in(dir)?;
    write(stage.as_file_mut())?;
    #[cfg(unix)]
    if let Some(mode) = _mode {
        use std::os::unix::fs::PermissionsExt;
        stage
            .as_file()
            .set_permissions(fs::Permissions::from_mode(mode))?;
    }
    stage.as_file().sync_all()?;
    stage
        .persist(path)
        .with_context(|| format!("Cannot save {}", crate::home::display(path)))?;
    #[cfg(unix)]
    if _sync_directory {
        File::open(dir)?.sync_all()?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
pub(crate) fn rename_new(from: &Path, to: &Path) -> io::Result<()> {
    use std::{ffi::CString, os::unix::ffi::OsStrExt};
    let src = CString::new(from.as_os_str().as_bytes())?;
    let dst = CString::new(to.as_os_str().as_bytes())?;
    // Call Linux directly because the glibc wrapper postdates our AppImage baseline.
    // Both paths remain valid and NUL-terminated throughout the syscall.
    if unsafe {
        libc::syscall(
            libc::SYS_renameat2,
            libc::AT_FDCWD,
            src.as_ptr(),
            libc::AT_FDCWD,
            dst.as_ptr(),
            libc::RENAME_NOREPLACE,
        )
    } != 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

#[cfg(not(target_os = "linux"))]
pub(crate) fn rename_new(from: &Path, to: &Path) -> io::Result<()> {
    match fs::symlink_metadata(to) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => fs::rename(from, to),
        Ok(_) => Err(io::ErrorKind::AlreadyExists.into()),
        Err(error) => Err(error),
    }
}
