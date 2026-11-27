//! Windows versions are opened directly from Explorer as well as by the launcher.
//! The distributed CLI is a console program. Only the local, verified copy is made windowed.
use anyhow::{Context, Result};
use std::fs::File;
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::path::Path;

fn subsystem(file: &mut File, current: u16) -> io::Result<Option<u64>> {
    let length = file.metadata()?.len();
    if length < 64 {
        return Ok(None);
    }
    let mut dos = [0; 64];
    file.rewind()?;
    file.read_exact(&mut dos)?;
    if &dos[..2] != b"MZ" {
        return Ok(None);
    }
    let pe = u64::from(u32::from_le_bytes(dos[60..].try_into().unwrap()));
    if pe < 64 || pe + 94 > length {
        return Ok(None);
    }
    file.seek(SeekFrom::Start(pe))?;
    let mut header = [0; 94];
    file.read_exact(&mut header)?;
    // PE32 and PE32+ both store the subsystem at optional-header offset 68.
    if &header[..4] != b"PE\0\0" || header[92..94] != current.to_le_bytes() {
        return Ok(None);
    }
    Ok(Some(pe + 92))
}

pub(super) fn mark_private_copy(file: &mut File) -> io::Result<()> {
    if let Some(offset) = subsystem(file, 3)? {
        file.seek(SeekFrom::Start(offset))?;
        file.write_all(&2_u16.to_le_bytes())?;
    }
    Ok(())
}

// Only called on a disposable copy before checking the original release digest.
#[cfg(windows)]
pub(super) fn restore_console_copy(file: &mut File) -> io::Result<()> {
    if let Some(offset) = subsystem(file, 2)? {
        file.seek(SeekFrom::Start(offset))?;
        file.write_all(&3u16.to_le_bytes())?;
    }
    Ok(())
}

/// Always make a private copy before changing an existing executable: it can be a hard
/// link to an unmodified tool or download. Readers see the complete old or new executable.
fn replace_console_copy(path: &Path) -> Result<()> {
    let mut installed = File::open(path)?;
    let Some(offset) = subsystem(&mut installed, 3)? else {
        return Ok(());
    };
    let mut copy =
        tempfile::NamedTempFile::new_in(path.parent().context("Missing version folder")?)?;
    installed.rewind()?;
    io::copy(&mut installed, &mut copy)?;
    drop(installed);
    copy.seek(SeekFrom::Start(offset))?;
    copy.write_all(&2_u16.to_le_bytes())?;
    copy.as_file().sync_all()?;
    copy.persist(path)?;
    Ok(())
}

#[cfg(windows)]
pub(super) fn repair_versions(home: &Path) -> Result<()> {
    for version in super::versions::installed(home)? {
        if super::versions::linked(&version.folder.join(super::library::FETCH_LOCK)) {
            continue;
        }
        let lock = std::fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .open(version.folder.join(super::library::FETCH_LOCK))?;
        let Ok(_lease) = crate::home::FileLease::try_acquire(lock, crate::fsx::LockMode::Exclusive)
        else {
            continue;
        };
        // A running game can keep its executable locked. Retry on the next launch.
        if let Err(error) = replace_console_copy(&version.folder.join(super::process::executable()))
        {
            eprintln!("Could not update the installed builder's window mode: {error}");
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn windowed_program_is_private_and_signed_cli_bytes_stay_intact() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let source = temp.path().join("longshot-builder.exe");
        let program = temp.path().join("Longshot.exe");
        let mut console = vec![0; 384];
        console[..2].copy_from_slice(b"MZ");
        console[60..64].copy_from_slice(&128_u32.to_le_bytes());
        console[128..132].copy_from_slice(b"PE\0\0");
        console[220..222].copy_from_slice(&3_u16.to_le_bytes());
        fs::write(&source, &console)?;
        fs::hard_link(&source, &program)?;
        replace_console_copy(&program)?;
        let mut expected = console.clone();
        expected[220] = 2;
        assert_eq!(fs::read(&program)?, expected);
        assert_eq!(fs::read(&source)?, console);
        replace_console_copy(&program)?;
        assert_eq!(fs::read(&program)?, expected);
        let mut verified = tempfile::tempfile()?;
        verified.write_all(&console)?;
        mark_private_copy(&mut verified)?;
        verified.rewind()?;
        let mut installed = vec![];
        verified.read_to_end(&mut installed)?;
        assert_eq!(installed, expected);
        Ok(())
    }

    #[test]
    fn unrelated_files_and_truncated_headers_are_kept() -> Result<()> {
        for data in [
            vec![0; 8],
            vec![0; 300],
            [b"MZ".as_slice(), &[0xff; 62]].concat(),
        ] {
            let mut file = tempfile::tempfile()?;
            file.write_all(&data)?;
            mark_private_copy(&mut file)?;
            file.rewind()?;
            let mut kept = vec![];
            file.read_to_end(&mut kept)?;
            assert_eq!(kept, data);
        }
        Ok(())
    }
}
