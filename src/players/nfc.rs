use super::{disk, profiles};
use anyhow::{Context, Result, ensure};
use std::{
    fs::{self, File},
    io::{Read, Write},
    path::{Path, PathBuf},
};
pub fn folder(home: &Path) -> Result<PathBuf> {
    if let Some(path) = std::env::var_os("LONGSHOT_NFC_DIR").filter(|s| !s.is_empty()) {
        let path = PathBuf::from(path);
        if path.is_absolute() {
            return Ok(path);
        }
        return Ok(crate::game::selected_build(home)?.join(path));
    }
    if crate::home::is_portable(home) {
        let legacy = home.join(".local/share/longshot/nfc");
        if cfg!(unix) && disk::is_dir(&legacy) {
            return Ok(legacy);
        }
        return Ok(home.join("nfc"));
    }
    Ok(profiles::data_root()?.join(if cfg!(windows) {
        "Longshot/nfc"
    } else {
        "longshot/nfc"
    }))
}
pub fn candidate(p: &Path) -> bool {
    !p.file_name()
        .unwrap_or_default()
        .to_string_lossy()
        .to_lowercase()
        .starts_with("key_")
        && p.extension()
            .and_then(|s| s.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("bin") || e.eq_ignore_ascii_case("nfc"))
}
pub fn files(dir: &Path, recursive: bool) -> Result<Vec<PathBuf>> {
    let mut files = vec![];
    let mut dirs = vec![dir.to_owned()];
    while let Some(d) = dirs.pop() {
        if !disk::is_dir(&d) {
            continue;
        }
        for entry in fs::read_dir(d)? {
            let p = entry?.path();
            if recursive && disk::is_dir(&p) {
                dirs.push(p);
            } else if disk::is_file(&p) && candidate(&p) {
                files.push(p);
            }
        }
    }
    files.sort();
    Ok(files)
}
pub fn bytes(p: &Path) -> Result<Vec<u8>> {
    disk::require_file(p)?;
    ensure!(candidate(p), "Select a .bin or .nfc tag dump.");
    let mut bytes = vec![];
    File::open(p)?.take((1 << 20) + 1).read_to_end(&mut bytes)?;
    ensure!(
        bytes.len() <= 1 << 20,
        "The file exceeds the NFC dump size limit."
    );
    Ok(bytes)
}
pub fn identify(p: &Path, bytes: &[u8]) -> Result<String> {
    let mut tag = [0u8; 540];
    if p.extension().is_some_and(|s| s.eq_ignore_ascii_case("bin")) {
        ensure!(
            matches!(bytes.len(), 532 | 540 | 572),
            "A raw NFC dump has 532, 540 or 572 bytes."
        );
        let n = tag.len().min(bytes.len());
        tag[..n].copy_from_slice(&bytes[..n]);
    } else {
        let text = String::from_utf8_lossy(bytes);
        ensure!(
            text.starts_with("Filetype:"),
            "This is not a Flipper NFC file."
        );
        let mut pages = [false; 135];
        for line in text.lines() {
            let Some((page, rest)) = line
                .trim()
                .strip_prefix("Page ")
                .and_then(|line| line.split_once(':'))
            else {
                continue;
            };
            let Some(page) = page.parse::<usize>().ok().filter(|n| *n < 135) else {
                continue;
            };
            let Ok(values) = rest
                .split_whitespace()
                .map(|v| u8::from_str_radix(v, 16))
                .collect::<Result<Vec<_>, _>>()
            else {
                continue;
            };
            if values.len() == 4 {
                tag[page * 4..page * 4 + 4].copy_from_slice(&values);
                pages[page] = true;
            }
        }
        ensure!(
            pages.iter().filter(|b| **b).count() >= 130,
            "The tag dump needs at least 130 valid pages."
        );
    }
    ensure!(
        tag[12..16] == [0xf1, 0x10, 0xff, 0xee] && tag[0x5b] == 2,
        "The file is not an NTAG215 figure dump."
    );
    Ok(tag[0x54..0x5c].iter().map(|b| format!("{b:02X}")).collect())
}
pub fn add(dir: &Path, src: &Path) -> Result<()> {
    let data = bytes(src)?;
    identify(src, &data)?;
    fs::create_dir_all(dir)?;
    let mut part = tempfile::NamedTempFile::new_in(dir)?;
    part.write_all(&data)?;
    part.as_file().sync_all()?;
    for n in 1..10000 {
        let name = if n == 1 {
            src.file_name()
                .context("The tag file has no name.")?
                .to_owned()
        } else {
            format!(
                "{} ({n}).{}",
                src.file_stem().unwrap_or_default().to_string_lossy(),
                src.extension().unwrap().to_string_lossy()
            )
            .into()
        };
        let to = dir.join(name);
        if fs::symlink_metadata(&to).is_ok() {
            if bytes(&to).is_ok_and(|b| b == data) {
                return Ok(());
            }
            continue;
        }
        match part.persist_noclobber(&to) {
            Ok(_) => return Ok(()),
            Err(e) if e.error.kind() == std::io::ErrorKind::AlreadyExists => {
                part = e.file;
            }
            Err(e) => return Err(e.into()),
        }
    }
    anyhow::bail!("The NFC folder has no free name for this tag.")
}

pub struct SelectedTag {
    path: PathBuf,
    hash: [u8; 32],
}

impl SelectedTag {
    pub fn capture(dir: &Path, name: &str) -> Result<Self> {
        ensure!(
            Path::new(name).file_name().is_some_and(|s| s == name),
            "Invalid tag name."
        );
        let path = dir.join(name);
        Ok(Self {
            hash: digest(&path)?,
            path,
        })
    }
}

fn digest(path: &Path) -> Result<[u8; 32]> {
    use sha2::{Digest, Sha256};
    Ok(Sha256::digest(bytes(path)?).into())
}

pub fn delete(dir: &Path, tags: &[SelectedTag]) -> Result<()> {
    let hold = tempfile::tempdir_in(dir)?;
    let mut moved = Vec::new();
    for tag in tags {
        let held = hold
            .path()
            .join(tag.path.file_name().context("Invalid tag path.")?);
        let result = fs::rename(&tag.path, &held)
            .map_err(anyhow::Error::from)
            .and_then(|_| {
                moved.push((tag.path.clone(), held.clone()));
                ensure!(
                    digest(&held)? == tag.hash,
                    "The tag changed before deletion. Choose it again."
                );
                Ok(())
            });
        if let Err(e) = result {
            // A new file at the same path must also survive a failed delete.
            let mut retained = false;
            for (original, held) in moved {
                if fs::hard_link(&held, original).is_err() {
                    retained = true;
                }
            }
            if retained {
                anyhow::bail!(
                    "{e}. Your files are preserved in {}.",
                    crate::home::display(&hold.keep())
                );
            }
            return Err(e);
        }
    }
    hold.close()?;
    Ok(())
}
