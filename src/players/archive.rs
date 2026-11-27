use super::{disk, saves};
use anyhow::{Context, Result, ensure};
use chrono::{Datelike, TimeZone, Timelike};
use std::{
    collections::HashSet,
    fs::{self, File},
    io::{self, Read},
    path::{Path, PathBuf},
};
use zip::{ZipArchive, ZipWriter, write::SimpleFileOptions};
const TOTAL: u64 = 512 << 20;
const FILE: u64 = 64 << 20;
const ENTRIES: usize = 10_000;

fn junk(name: &str) -> bool {
    name.split('/')
        .any(|p| matches!(p, "__MACOSX" | ".DS_Store" | "Thumbs.db" | "desktop.ini"))
}
fn safe_name(name: &str) -> bool {
    let s = name.trim_end_matches('/');
    !s.is_empty()
        && s.split('/').all(|p| {
            let stem = p.split('.').next().unwrap_or("").to_ascii_uppercase();
            !p.is_empty()
                && !p.ends_with(['.', ' '])
                && !p.contains(['\\', ':', '\0', '*', '?', '<', '>', '|', '"'])
                && !p.chars().any(char::is_control)
                && !matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
                && !(stem.len() == 4
                    && (stem.starts_with("COM") || stem.starts_with("LPT"))
                    && stem.as_bytes()[3].is_ascii_digit())
        })
}
pub fn export(root: &Path, out: &Path) -> Result<()> {
    disk::require_dir(root)?;
    super::running::saves_idle(&[root, out])?;
    disk::disjoint(root, out)?;
    if root
        .file_name()
        .and_then(|s| s.to_str())
        .is_some_and(saves::slot_name)
    {
        saves::validate_slot(root)?;
    } else {
        ensure!(
            !saves::slots(&root.join(saves::UID))?.is_empty(),
            "The active game profile has no save slots."
        );
    }
    let dest =
        tempfile::NamedTempFile::new_in(out.parent().context("Choose a destination folder.")?)?;
    let mut zip = ZipWriter::new(dest.reopen()?);
    zip.set_comment("Longshot Launcher save backup")?;
    let mut stack = vec![root.to_owned()];
    let mut count = 0;
    let mut bytes = 0;
    while let Some(path) = stack.pop() {
        count += 1;
        ensure!(count <= ENTRIES, "The save folder contains too many files.");
        let rel = path.strip_prefix(root.parent().context("Invalid save folder.")?)?;
        let name = rel
            .to_str()
            .context("A save file name cannot be stored in a portable zip.")?
            .replace(std::path::MAIN_SEPARATOR, "/");
        ensure!(
            safe_name(&name),
            "A save file name cannot be stored in a portable zip."
        );
        let meta = fs::symlink_metadata(&path)?;
        let t = chrono::DateTime::<chrono::Local>::from(meta.modified()?);
        let time = zip::DateTime::from_date_and_time(
            t.year().clamp(1980, 2107) as u16,
            t.month() as u8,
            t.day() as u8,
            t.hour() as u8,
            t.minute() as u8,
            t.second() as u8,
        )
        .unwrap_or_default();
        let opt = SimpleFileOptions::default()
            .last_modified_time(time)
            .compression_method(zip::CompressionMethod::Deflated);
        if disk::is_dir(&path) {
            zip.add_directory(name, opt)?;
            let mut children = fs::read_dir(path)?
                .map(|e| e.map(|e| e.path()))
                .collect::<io::Result<Vec<_>>>()?;
            children.sort();
            stack.extend(children.into_iter().rev());
        } else if disk::is_file(&path) {
            ensure!(
                meta.len() <= FILE && bytes + meta.len() <= TOTAL,
                "The save folder exceeds the zip size limit."
            );
            zip.start_file(name, opt)?;
            let n = io::copy(&mut File::open(&path)?.take(meta.len() + 1), &mut zip)?;
            ensure!(
                n == meta.len(),
                "A save changed during export. Try again after closing the game."
            );
            bytes += n;
        }
    }
    let f = zip.finish()?;
    f.sync_all()?;
    ensure!(f.metadata()?.len() <= TOTAL, "The zip exceeds 512 MB.");
    super::running::saves_idle(&[root, out])?;
    dest.persist(out)?;
    Ok(())
}
pub struct Unpacked {
    _dir: tempfile::TempDir,
    pub whole: bool,
    pub source: PathBuf,
}
pub fn open(path: &Path) -> Result<ZipArchive<File>> {
    disk::require_file(path)?;
    let file = File::open(path)?;
    ensure!(
        file.metadata()?.len() <= TOTAL,
        "This zip exceeds the 512 MB limit."
    );
    let zip = ZipArchive::new(file)?;
    ensure!(zip.len() <= ENTRIES, "The zip has too many entries.");
    ensure!(!zip.has_duplicate_names(), "The zip repeats a file name.");
    Ok(zip)
}

/// Classification for the confirmation dialog only. Full path, type and size
/// validation is repeated while unpacking in a background worker.
pub fn is_whole(zip: &ZipArchive<File>) -> Result<bool> {
    let expected = format!("save/{}/", saves::UID);
    let mut has_slot = false;
    for i in 0..zip.len() {
        let item = zip.by_index_data(i)?;
        let name = item.name()?.replace('\\', "/");
        if junk(&name) {
            continue;
        }
        if !name.starts_with("save/") {
            return Ok(false);
        }
        if let Some((slot, "progress.sav")) =
            name.strip_prefix(&expected).and_then(|p| p.split_once('/'))
        {
            has_slot |= saves::slot_name(slot);
        }
    }
    Ok(has_slot)
}

pub fn unpack(mut zip: ZipArchive<File>) -> Result<Unpacked> {
    let stage = tempfile::tempdir()?;
    let mut seen = HashSet::new();
    let mut bytes = 0u64;
    let mut under_save = true;
    for i in 0..zip.len() {
        let mut item = zip.by_index(i)?;
        let name = item.name()?.replace('\\', "/");
        if junk(&name) {
            continue;
        }
        ensure!(
            safe_name(&name) && item.enclosed_name().is_some(),
            "The zip contains an unsafe path."
        );
        ensure!(
            !item.is_symlink()
                && item
                    .unix_mode()
                    .is_none_or(|m| matches!(m & 0o170000, 0 | 0o100000 | 0o040000)),
            "The zip contains a link or a special file."
        );
        ensure!(
            seen.insert(name.trim_end_matches('/').to_lowercase()),
            "The zip repeats a file name."
        );
        under_save &= name.starts_with("save/");
        let target = stage.path().join(&name);
        if item.is_dir() {
            fs::create_dir_all(target)?;
            continue;
        }
        let size = item.size();
        ensure!(
            size <= FILE && size <= TOTAL - bytes,
            "The uncompressed save is too large."
        );
        fs::create_dir_all(target.parent().unwrap())?;
        let mut dst = File::create_new(target)?;
        let n = io::copy(&mut (&mut item).take(size + 1), &mut dst)?;
        ensure!(n == size, "The zip's file sizes do not match its contents.");
        bytes += n;
        if let Some(t) = item.last_modified()
            && let Some(time) = chrono::Local
                .with_ymd_and_hms(
                    t.year().into(),
                    t.month().into(),
                    t.day().into(),
                    t.hour().into(),
                    t.minute().into(),
                    t.second().into(),
                )
                .earliest()
        {
            dst.set_modified(time.into())?;
        }
    }
    let save = stage.path().join("save");
    let whole = under_save && !saves::slots(&save.join(saves::UID))?.is_empty();
    let source = if whole {
        save
    } else {
        let mut dir = stage.path().to_owned();
        // Strip only common enclosing directories, stopping before the slot. A
        // file outside that chain still belongs to the archive and must validate.
        loop {
            let mut entries = Vec::new();
            for e in fs::read_dir(&dir)? {
                let p = e?.path();
                let name = p
                    .file_name()
                    .and_then(|s| s.to_str())
                    .context("Invalid save file name.")?;
                if name == "__MACOSX" && p.is_dir() {
                    fs::remove_dir_all(p)?;
                    continue;
                }
                if matches!(
                    name,
                    ".DS_Store" | "Thumbs.db" | "desktop.ini" | "longshot-save.json"
                ) && p.is_file()
                {
                    fs::remove_file(p)?;
                    continue;
                }
                entries.push(p);
            }
            if entries.len() == 1
                && disk::is_dir(&entries[0])
                && !entries[0]
                    .file_name()
                    .and_then(|s| s.to_str())
                    .is_some_and(saves::slot_name)
            {
                dir = entries.remove(0);
            } else {
                break;
            }
        }
        saves::validate_input(&dir)?;
        dir
    };

    Ok(Unpacked {
        _dir: stage,
        whole,
        source,
    })
}
pub fn install_whole(src: &Path, dst: &Path, home: &Path, keep: bool) -> Result<()> {
    super::running::saves_idle(&[src, dst])?;
    saves::pending(dst)?;
    disk::disjoint(src, dst)?;
    saves::SaveEdit::begin(dst)?.transact(|stage| {
        disk::copy_tree(src, stage)?;
        if dst.try_exists()? {
            saves::backup(dst, home, "import-zip")?;
            if keep {
                for p in disk::dirs(dst)? {
                    let opt = p.join("option.sav");
                    if !disk::is_file(&opt) {
                        continue;
                    }
                    let dest = stage.join(p.file_name().unwrap());
                    fs::create_dir_all(&dest)?;
                    let to = dest.join("option.sav");
                    if to.exists() {
                        fs::remove_file(&to)?;
                    }
                    disk::copy_new(&opt, &to)?;
                }
            }
        }
        super::running::saves_idle(&[src, dst])?;
        Ok(())
    })
}
