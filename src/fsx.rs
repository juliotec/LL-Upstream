//! Filesystem checks shared by the launcher, including Windows junctions.
use std::{
    borrow::Cow,
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

/// The plain spelling of a Windows path: `X:\...` for the verbatim `\\?\X:\...` that
/// `fs::canonicalize` and a junction's target give, `\\server\share\...` for `\\?\UNC\server\share\...`;
/// paths that require verbatim semantics are kept. Folders are saved and passed on in this spelling: clang, run by
/// longshot-builder in the build files folder, finds no header from a verbatim path (#297).
pub(crate) fn plain(path: &Path) -> Cow<'_, Path> {
    #[cfg(windows)]
    {
        use std::{os::windows::ffi::OsStrExt as _, path::PathBuf};
        let Some(text) = path.to_str() else {
            return Cow::Borrowed(path);
        };
        if let Some(rest) = text.strip_prefix(r"\\?\") {
            let candidate = if let Some(unc) = rest.strip_prefix(r"UNC\") {
                ordinary_components(unc).then(|| PathBuf::from(format!(r"\\{unc}")))
            } else if rest.as_bytes().first().is_some_and(u8::is_ascii_alphabetic)
                && rest.get(1..3) == Some(r":\")
                && ordinary_components(&rest[3..])
            {
                Some(PathBuf::from(rest))
            } else {
                None
            };
            if let Some(candidate) = candidate
                && candidate.as_os_str().encode_wide().count() < 260
            {
                return Cow::Owned(candidate);
            }
        }
    }
    Cow::Borrowed(path)
}

#[cfg(windows)]
fn ordinary_components(tail: &str) -> bool {
    tail.trim_end_matches('\\').split('\\').all(|part| {
        if part.ends_with(['.', ' ']) || part.chars().any(|c| c < ' ' || "<>:\"/|?*".contains(c)) {
            return false;
        }
        let stem = part
            .split('.')
            .next()
            .unwrap_or_default()
            .trim_end_matches(' ');
        let stem = stem.to_ascii_uppercase();
        let device_port = stem
            .strip_prefix("COM")
            .or_else(|| stem.strip_prefix("LPT"))
            .is_some_and(|n| n.chars().count() == 1 && "123456789¹²³".contains(n));
        !matches!(
            stem.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        ) && !device_port
    })
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

#[cfg(all(test, windows))]
mod plain_tests {
    use super::*;

    #[test]
    fn converts_only_paths_valid_in_the_ordinary_namespace() {
        assert_eq!(
            plain(Path::new(r"\\?\X:\Build Files")).as_ref(),
            Path::new(r"X:\Build Files")
        );
        assert_eq!(
            plain(Path::new(r"\\?\UNC\server\share\cache")).as_ref(),
            Path::new(r"\\server\share\cache")
        );
        for name in [
            r"\\?\X:\Cache.",
            r"\\?\X:\Cache ",
            r"\\?\X:\NUL.txt.log\cache",
            r"\\?\UNC\server\share\Cache.\build",
        ] {
            let path = Path::new(name);
            assert_eq!(plain(path).as_ref(), path);
        }
        let long = format!(r"\\?\X:\{}\{}", "c".repeat(150), "d".repeat(150));
        assert_eq!(plain(Path::new(&long)).as_ref(), Path::new(&long));
    }
}
