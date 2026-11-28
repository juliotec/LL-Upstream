//! (Windows) DLSS 5's files, which Longshot neither ships nor downloads: NVIDIA's own `nvngx_dlssnr.dll` (its FP8 model, which
//! runs on RTX 50 GPUs only) and the community FP16 conversion of it (a modified NVIDIA file, RTX 20 to 50). The player adds the
//! files they have in Settings. The launcher tells the two apart by their content and copies each into the last folder the game
//! looks in (Longshot's `gpu/vk/vk_dlss.cpp` runtime_dirs: `RX_VK_DLSS_DIR`, the game folder's `ngx`, then
//! `%LOCALAPPDATA%\Longshot\ngx`), which every version shares and no update touches: NVIDIA's as `ngx\nvngx_dlssnr.dll`, the FP16
//! one as `ngx\fp16\nvngx_dlssnr.dll`. The game chooses between them (Longshot's `docs/user/dlss5.md`).
//!
//! A copy, not a link: the player may move or delete the original, and a tool that changes it in place does not change the
//! game's. Only these two files and the record of them ([`RECORD`]) are ever written or deleted, never anything in a game
//! folder, and never through a linked folder. Longshot's guide also has players put the files there by hand: a file the record
//! does not hold as placed ([`ours`]) is the player's own, which Settings, Storage and Remove everything leave alone.
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fmt, fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::{Duration, UNIX_EPOCH},
};

#[cfg(windows)]
pub(crate) mod windows;

/// Which of the two files
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum Kind {
    Nvidia,
    Fp16,
}

impl Kind {
    pub(crate) const ALL: [Kind; 2] = [Kind::Nvidia, Kind::Fp16];

    fn name(self) -> &'static str {
        match self {
            Kind::Nvidia => "Nvidia",
            Kind::Fp16 => "Fp16",
        }
    }

    fn parse(text: &str) -> Option<Kind> {
        Kind::ALL.into_iter().find(|kind| kind.name() == text)
    }
}

pub(crate) const FILE_NAME: &str = "nvngx_dlssnr.dll";
const FP16_FOLDER: &str = "fp16";

/// The record of what the launcher placed, beside the copies. Its name starts with a dot, never `nvngx_`, so NVIDIA's
/// runtime never takes it for one of its files
pub(crate) const RECORD: &str = ".longshot-launcher-placed.txt";
/// A record line for a copy being put in place: until the move is done, both the old copy and the new one count as the
/// launcher's, so an Add stopped half way (the launcher closed, the power went) never leaves the launcher's own copy unknown
const PENDING: &str = "pending.";

/// An FP16 file the game runs: its size, then its SHA-256 (lower-case hex)
#[derive(Clone, Copy, Debug)]
pub(crate) struct Known {
    pub size: u64,
    pub sha256: &'static str,
}

/// The FP16 files the game runs: Longshot's `gpu/vk/dlss_nr_model.h` kFp16Known (`tests/test_launcher_core.py` checks the
/// two lists are one), the file the Bloodborne PC port checks for. Any other file in the fp16 folder the game does not load
pub(crate) const FP16_KNOWN: &[Known] = &[Known {
    size: 276_162_560,
    sha256: "cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed",
}];

/// No DLSS 5 model is near either size: a smaller or larger file is not one, and is not read
const LEAST: u64 = 1 << 20;
const MOST: u64 = 2 << 30;

/// The folder as the screens name it, as Longshot's `docs/user/dlss5.md` does
pub(crate) const SHOWN_FOLDER: &str = r"%LOCALAPPDATA%\Longshot\ngx";

/// The folder the copies go in, `%LOCALAPPDATA%\Longshot\ngx`; none outside Windows, where the game does not use the files
pub(crate) fn folder() -> Option<PathBuf> {
    #[cfg(windows)]
    {
        crate::home::system_data_folder()
            .ok()
            .map(|data| data.join("Longshot").join("ngx"))
    }
    #[cfg(not(windows))]
    {
        None
    }
}

/// Where kind's file is in folder
pub(crate) fn place(folder: &Path, kind: Kind) -> PathBuf {
    match kind {
        Kind::Nvidia => folder.join(FILE_NAME),
        Kind::Fp16 => folder.join(FP16_FOLDER).join(FILE_NAME),
    }
}

/// The bytes of kind's file in folder (a plain file, not a link); none when there is none
pub(crate) fn bytes(folder: &Path, kind: Kind) -> Option<u64> {
    fs::symlink_metadata(place(folder, kind))
        .ok()
        .filter(|meta| meta.is_file() && !crate::fsx::is_link(meta))
        .map(|meta| meta.len())
}

// ---- this PC's GPUs: whether to show the section, and what the GPU runs ----

/// A display adapter: an NVIDIA one or not, and its name where the system gives one. (Made by `windows.rs` only: outside
/// Windows there is no section)
#[derive(Clone, Debug, PartialEq, Eq)]
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) struct Gpu {
    pub nvidia: bool,
    pub name: Option<String>,
}

/// This PC's display adapters ([`windows::gpus`]); none outside Windows, where the section never shows
pub(crate) fn gpus() -> Vec<Gpu> {
    #[cfg(windows)]
    {
        windows::gpus()
    }
    #[cfg(not(windows))]
    {
        Vec::new()
    }
}

/// NVIDIA's check of a file ([`windows::nvidia_file`]); outside Windows, where the game does not use these files, no file
/// is NVIDIA's
pub(crate) fn nvidia_file(path: &Path) -> NvidiaCheck {
    #[cfg(windows)]
    {
        windows::nvidia_file(path)
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        NvidiaCheck::NotNvidia
    }
}

/// One Add, Remove or settling of the record at a time in this launcher: each reads the record, changes it and writes it whole
static WORK: std::sync::Mutex<()> = std::sync::Mutex::new(());

/// Held while an Add or a Remove runs (Settings and Remove everything): within this launcher, and on Windows across every
/// launcher of this user ([`windows::NamedLock`]), as a portable and an installed launcher share the folder
pub(crate) struct Exclusive {
    _local: std::sync::MutexGuard<'static, ()>,
    #[cfg(windows)]
    _shared: windows::NamedLock,
}

pub(crate) fn exclusive() -> io::Result<Exclusive> {
    Ok(Exclusive {
        _local: WORK
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner),
        #[cfg(windows)]
        _shared: windows::NamedLock::acquire(true)?
            .ok_or_else(|| io::Error::other("Could not acquire the DLSS 5 mutex"))?,
    })
}

/// [`reconcile`], unless an Add or a Remove runs now, here or in another launcher: it would take that Add's pending copy for
/// one stopped half way
pub(crate) fn settle(folder: &Path) -> io::Result<()> {
    let _local = match WORK.try_lock() {
        Ok(work) => work,
        Err(std::sync::TryLockError::Poisoned(work)) => work.into_inner(),
        Err(std::sync::TryLockError::WouldBlock) => return Ok(()),
    };
    #[cfg(windows)]
    let Some(_shared) = windows::NamedLock::acquire(false)? else {
        return Ok(());
    };
    reconcile(folder)
}

/// The section shows on Windows with an NVIDIA RTX GPU, or when no GPU is found (unknown). A GPU known only by its maker is
/// taken as able; a named one without RTX in its name (GTX, Quadro P) is not
pub(crate) fn offered(windows: bool, gpus: &[Gpu]) -> bool {
    windows
        && (gpus.is_empty()
            || gpus.iter().any(|gpu| {
                gpu.nvidia
                    && gpu
                        .name
                        .as_deref()
                        .is_none_or(|name| name.to_ascii_uppercase().contains("RTX"))
            }))
}

/// The series of this PC's newest NVIDIA RTX GPU by its name (RTX 5090: 5; RTX 4050 Laptop GPU: 4); 0 when none is named so
/// (a workstation card, a GPU known only by its maker)
pub(crate) fn rtx_series(gpus: &[Gpu]) -> u8 {
    let series = |name: &str| {
        let upper = name.to_ascii_uppercase();
        upper.match_indices("RTX").find_map(|(at, _)| {
            let digits: Vec<u8> = upper[at + 3..]
                .trim_start()
                .bytes()
                .take_while(u8::is_ascii_digit)
                .collect();
            match digits.as_slice() {
                [s @ b'2'..=b'9', b'0', b'5'..=b'9', b'0'] => Some(s - b'0'),
                _ => None,
            }
        })
    };
    gpus.iter()
        .filter(|gpu| gpu.nvidia)
        .filter_map(|gpu| gpu.name.as_deref().and_then(series))
        .max()
        .unwrap_or(0)
}

/// What this PC's GPU runs (rtx_series), under the rows; empty when it can't be told
pub(crate) fn gpu_line(series: u8) -> &'static str {
    match series {
        5 => crate::i18n::key(
            "This RTX 50 GPU runs both files. With both added, the game's DLSS 5 model row chooses one, after a restart.",
        ),
        2..=4 => {
            crate::i18n::key("This GPU runs the FP16 file. NVIDIA's file runs on RTX 50 GPUs only.")
        }
        _ => "",
    }
}

/// What a row says about the file in place (bytes: none, none there; ours: the launcher put it there): an English key and
/// its value for `{0}`
pub(crate) fn line(
    kind: Kind,
    bytes: Option<u64>,
    ours: bool,
    known: &[Known],
) -> (&'static str, String) {
    match bytes {
        None => (crate::i18n::key("Not added."), String::new()),
        Some(b) if kind == Kind::Fp16 && !known.iter().any(|k| k.size == b) => (
            crate::i18n::key(
                "The file in the fp16 folder is not the known FP16 file, so the game does not use it. Add the known file to replace it.",
            ),
            String::new(),
        ),
        Some(b) if !ours => (
            crate::i18n::key("Put here by hand ({0}). The launcher leaves it alone."),
            crate::storage::format_size(b),
        ),
        Some(b) => (
            crate::i18n::key("Added ({0})."),
            crate::storage::format_size(b),
        ),
    }
}

// ---- adding a file: checked, copied, told apart as copied, recorded, then put in place ----

/// What NVIDIA's check says of a file ([`windows::nvidia_file`]): NVIDIA's DLSS 5 model, another file NVIDIA signed, or not
/// NVIDIA's. (Outside Windows only NotNvidia is ever said: the first two come from `windows.rs`)
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) enum NvidiaCheck {
    DlssModel,
    OtherFile,
    NotNvidia,
}

/// Why a file is refused, in the player's words (an English key for the catalogs, and its value for `{0}`)
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Refusal {
    Gone,
    NotDlss5,
    InPlace,
    OtherNvidia,
    Neither,
    Linked,
    NotOurs,
}

impl Refusal {
    pub(crate) fn key(self) -> &'static str {
        match self {
            Refusal::Gone => crate::i18n::key("The file is not there any more."),
            Refusal::NotDlss5 => crate::i18n::key("This is not a DLSS 5 file (nvngx_dlssnr.dll)."),
            Refusal::InPlace => crate::i18n::key("This file is already in place."),
            Refusal::OtherNvidia => {
                crate::i18n::key("This NVIDIA file is not the DLSS 5 model (nvngx_dlssnr.dll).")
            }
            Refusal::Neither => crate::i18n::key(
                "This is neither NVIDIA's own DLSS 5 file (signed by NVIDIA) nor the known community FP16 file (SHA-256 {0}…). Longshot uses only these two.",
            ),
            Refusal::Linked => crate::i18n::key(
                "A folder on the way to the DLSS 5 files is a link, so the launcher does not write there.",
            ),
            Refusal::NotOurs => crate::i18n::key(
                "The file in place is not the launcher's copy, so the launcher leaves it alone.",
            ),
        }
    }

    pub(crate) fn value(self, known: &[Known]) -> String {
        match self {
            Refusal::Neither => known
                .first()
                .map(|k| k.sha256[..8].to_ascii_uppercase())
                .unwrap_or_default(),
            _ => String::new(),
        }
    }
}

/// An Add or a Remove that did not happen: refused (the file is not one, or not the launcher's), stopped at a file of the
/// player's in the way (an Add replaces it only when the player says so), or failed (it could not be read, copied or put in
/// place: the game holds its files while it runs)
#[derive(Debug)]
pub(crate) enum Failure {
    Refused(Refusal),
    InTheWay(Box<InTheWay>),
    Failed(io::Error),
}

impl fmt::Display for Failure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Failure::Refused(why) => f.write_str(why.key()),
            Failure::InTheWay(file) => write!(
                f,
                "A file put there by hand is in the way: {}",
                file.path.display()
            ),
            Failure::Failed(error) => error.fmt(f),
        }
    }
}

/// A file in the place an Add's copy would go that is not the launcher's (put there by hand, or changed since) and not the
/// same as the copy: the player's. The Add replaces it only when the player confirms, and only this file as it was then
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct InTheWay {
    pub kind: Kind,
    pub path: PathBuf,
    pub bytes: u64,
    found: Entry,
}

/// How an Add placed its file: copied over or into an empty place; the same file already the launcher's (nothing copied);
/// or the same file put there by hand, taken as the launcher's copy without a copy (the file added from stays as it is)
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum How {
    Copied,
    AlreadyOurs,
    TakenOver,
}

impl From<io::Error> for Failure {
    fn from(error: io::Error) -> Self {
        Failure::Failed(error)
    }
}

/// Where a file went: as which of the two, its place, and how
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Placed {
    pub kind: Kind,
    pub path: PathBuf,
    pub how: How,
}

/// Why source can't be a DLSS 5 file before any copy (none: it may be one): it must be there and a Windows program file (MZ)
/// of a DLSS 5 model's size. Which of the two it is, its content says once copied ([`install`])
pub(crate) fn check(source: &Path) -> Option<Refusal> {
    let Ok(meta) = fs::metadata(source) else {
        return Some(Refusal::Gone);
    };
    if !meta.is_file() || !(LEAST..=MOST).contains(&meta.len()) {
        return Some(Refusal::NotDlss5);
    }
    let mut head = [0u8; 2];
    match fs::File::open(source).and_then(|mut f| f.read_exact(&mut head)) {
        Ok(()) if head == *b"MZ" => None,
        Ok(()) => Some(Refusal::NotDlss5),
        Err(_) => Some(Refusal::Gone),
    }
}

/// Refused when path, or a folder on the way to it from the Longshot folder that holds folder down, is a link or a junction:
/// the launcher writes and deletes only through plain folders, so a link can't send a delete somewhere else. A link above
/// the Longshot folder (a profile or AppData folder moved to another drive with a junction) is the system's, and allowed
fn check_unlinked(folder: &Path, path: &Path) -> Result<(), Failure> {
    let top = folder.parent().unwrap_or(folder);
    for at in path.ancestors() {
        if crate::fsx::linked_location(at)? {
            return Err(Failure::Refused(Refusal::Linked));
        }
        if at == top {
            break;
        }
    }
    Ok(())
}

/// A temporary copy older than this is one an Add never finished (the launcher stopped mid-copy): an Add takes seconds
const STALE: Duration = Duration::from_secs(10 * 60);

/// The launcher's own temporary copies in folder that an Add left behind (by their name, and old), deleted
fn drop_stale_copies(folder: &Path) {
    let Ok(entries) = fs::read_dir(folder) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if !(name.starts_with(".longshot-launcher-") && name.ends_with(".tmp")) {
            continue;
        }
        let stale = entry
            .metadata()
            .ok()
            .filter(|meta| meta.is_file() && !crate::fsx::is_link(meta))
            .and_then(|meta| meta.modified().ok())
            .and_then(|time| time.elapsed().ok())
            .is_some_and(|age| age > STALE);
        if stale {
            let _ = fs::remove_file(entry.path());
        }
    }
}

/// source copied into folder as whichever of the two files its content says it is, replacing the one there. Read once,
/// hashed as it is copied into a temporary file in folder (named so that NVIDIA's runtime, which looks for `nvngx_*` files,
/// never takes it), then told apart as copied: one of the FP16 files the game runs (its size and SHA-256) is the FP16 file;
/// else nvidia ([`windows::nvidia_file`] on Windows: NVIDIA's signature and the DLSS 5 model's description; the tests give a
/// stand-in) must say NVIDIA's model.
///
/// The file in that place is then read whole: the same file needs no copy (the launcher's already, or one put there by hand,
/// which becomes the launcher's: [`How`]); a different one that is not the launcher's is the player's, and the Add stops
/// there ([`Failure::InTheWay`]) unless replace names that very file as the player confirmed it.
///
/// Then the record takes the copy as a pending replace, the copy is moved over that file's old one in one step, and the record
/// takes it as placed. A refused or failed Add leaves the files in place and no temporary file; one stopped between the steps
/// leaves a record that still knows whichever copy is in place ([`ours`], [`reconcile`]). progress: the share copied so far,
/// 0 to 1. Off the window's thread
pub(crate) fn install(
    source: &Path,
    folder: &Path,
    nvidia: impl Fn(&Path) -> NvidiaCheck,
    mut progress: impl FnMut(f64),
    known: &[Known],
    replace: Option<&InTheWay>,
) -> Result<Placed, Failure> {
    if let Some(why) = check(source) {
        return Err(Failure::Refused(why));
    }
    let full = std::path::absolute(source)?;
    // (a file in place picked again, by any spelling of its path or through a link: the same file, not copied over itself)
    let picked = fs::File::open(&full).and_then(|file| identity(&file))?;
    if Kind::ALL.into_iter().any(|kind| {
        fs::File::open(place(folder, kind))
            .and_then(|file| identity(&file))
            .is_ok_and(|placed| placed == picked)
    }) {
        return Err(Failure::Refused(Refusal::InPlace));
    }
    check_unlinked(folder, folder)?;
    fs::create_dir_all(folder)?;
    check_unlinked(folder, folder)?;
    // (an Add stopped half way before is settled first, so its pending copy is never taken for this one's)
    reconcile(folder)?;
    let mut copy = tempfile::Builder::new()
        .prefix(".longshot-launcher-")
        .suffix(".tmp")
        .tempfile_in(folder)?;
    let sha256 = {
        let mut from = fs::File::open(&full)?;
        let length = from.metadata()?.len();
        let mut hash = Sha256::new();
        let mut buffer = vec![0u8; 1 << 20];
        let mut done = 0u64;
        loop {
            let n = from.read(&mut buffer)?;
            if n == 0 {
                break;
            }
            hash.update(&buffer[..n]);
            copy.write_all(&buffer[..n])?;
            done += n as u64;
            progress(if length == 0 {
                1.0
            } else {
                done as f64 / length as f64
            });
        }
        copy.as_file().sync_all()?;
        hex(&hash.finalize())
    };
    let size = copy.as_file().metadata()?.len();
    // (closed, still deleted when dropped: Windows' signature check and the record read the copy by its path)
    let copy = copy.into_temp_path();
    let kind = if known.iter().any(|k| k.size == size && k.sha256 == sha256) {
        Kind::Fp16
    } else {
        match nvidia(&copy) {
            NvidiaCheck::DlssModel => Kind::Nvidia,
            NvidiaCheck::OtherFile => return Err(Failure::Refused(Refusal::OtherNvidia)),
            NvidiaCheck::NotNvidia => return Err(Failure::Refused(Refusal::Neither)),
        }
    };
    let destination = place(folder, kind);
    let parent = destination.parent().unwrap_or(folder);
    fs::create_dir_all(parent)?;
    check_unlinked(folder, &destination)?;
    let mut record = Record::read(folder)?;
    // (the file in that place now, read whole: the launcher's when it is the record's entry exactly)
    if destination.is_file() {
        let now = Entry::of(&destination, None)?;
        let ours = record
            .current
            .get(&kind)
            .into_iter()
            .chain(record.pending.get(&kind))
            .any(|entry| *entry == now);
        if now.sha256 == sha256 {
            if ours {
                return Ok(Placed {
                    kind,
                    path: destination,
                    how: How::AlreadyOurs,
                });
            }
            record.current.insert(kind, now);
            record.pending.remove(&kind);
            record.write(folder)?;
            return Ok(Placed {
                kind,
                path: destination,
                how: How::TakenOver,
            });
        }
        let confirmed = replace
            .is_some_and(|file| file.kind == kind && file.path == destination && file.found == now);
        if !ours && !confirmed {
            return Err(Failure::InTheWay(Box::new(InTheWay {
                kind,
                path: destination,
                bytes: now.bytes,
                found: now,
            })));
        }
    }
    // (the record first: one that can't be written stops the Add with the old copy in place. Until the move is done it holds
    // the old copy and the new one, so whichever is in place after an interruption is still the launcher's)
    let entry = Entry::of(&copy, Some(sha256))?;
    record.pending.insert(kind, entry.clone());
    record.write(folder)?;
    if let Err(error) = copy.persist(&destination) {
        record.pending.remove(&kind);
        let _ = record.write(folder);
        return Err(error.error.into());
    }
    record.current.insert(kind, entry);
    record.pending.remove(&kind);
    // (the copy is in place; a record that can't be written now still holds it as pending: the next check settles it)
    let _ = record.write(folder);
    Ok(Placed {
        kind,
        path: destination,
        how: How::Copied,
    })
}

/// kind's file in folder deleted, once a full check (its SHA-256 too) says it is the launcher's copy, and taken out of the
/// record; then the fp16 folder and folder when they are left empty. Nothing else, never anything in a game folder, never a
/// file put there by hand ([`Refusal::NotOurs`]). Fails when it can't go (the game holds its files while it runs)
pub(crate) fn remove(folder: &Path, kind: Kind) -> Result<(), Failure> {
    let path = place(folder, kind);
    check_unlinked(folder, &path)?;
    reconcile(folder)?;
    if !ours(folder, kind, true) {
        return Err(Failure::Refused(Refusal::NotOurs));
    }
    fs::remove_file(&path)?;
    let mut record = Record::read(folder)?;
    record.current.remove(&kind);
    record.pending.remove(&kind);
    record.write(folder)?;
    // (an empty fp16 folder, then folder: only plain folders, never a link the player made there)
    for empty in [folder.join(FP16_FOLDER), folder.to_owned()] {
        if !crate::fsx::linked_location(&empty).unwrap_or(true)
            && fs::read_dir(&empty).is_ok_and(|mut entries| entries.next().is_none())
        {
            let _ = fs::remove_dir(&empty);
        }
    }
    Ok(())
}

/// The launcher's copies in folder ([`ours`], quick), for Storage and Remove everything; never a file put there by hand
pub(crate) fn added(folder: &Path) -> Vec<(Kind, PathBuf)> {
    Kind::ALL
        .into_iter()
        .filter(|&kind| ours(folder, kind, false))
        .map(|kind| (kind, place(folder, kind)))
        .collect()
}

// ---- the record of what the launcher put there ----

/// A copy as placed: its bytes, last write time, file identity (the volume and file index on Windows, the device and inode
/// elsewhere: the same file, not another one with the same size and time) and SHA-256
#[derive(Clone, Debug, PartialEq, Eq)]
struct Entry {
    bytes: u64,
    written: u128,
    identity: String,
    sha256: String,
}

impl Entry {
    /// The file at path as it is now; sha256: given (already known), else read whole
    fn of(path: &Path, sha256: Option<String>) -> io::Result<Entry> {
        let mut file = fs::File::open(path)?;
        let (bytes, written, identity) = Self::facts(&file)?;
        let sha256 = match sha256 {
            Some(known) => known,
            None => {
                let mut hash = Sha256::new();
                io::copy(&mut file, &mut hash)?;
                hex(&hash.finalize())
            }
        };
        Ok(Entry {
            bytes,
            written,
            identity,
            sha256,
        })
    }

    fn facts(file: &fs::File) -> io::Result<(u64, u128, String)> {
        let meta = file.metadata()?;
        let written = meta
            .modified()?
            .duration_since(UNIX_EPOCH)
            .map_err(io::Error::other)?
            .as_nanos();
        Ok((meta.len(), written, identity(file)?))
    }

    fn line(&self) -> String {
        format!(
            "{} {} {} {}",
            self.bytes, self.written, self.identity, self.sha256
        )
    }

    fn parse(text: &str) -> Option<Entry> {
        let mut parts = text.split(' ');
        let entry = Entry {
            bytes: parts.next()?.parse().ok()?,
            written: parts.next()?.parse().ok()?,
            identity: parts
                .next()
                .filter(|id| !id.is_empty() && id.bytes().all(|b| b.is_ascii_alphanumeric()))?
                .to_owned(),
            sha256: parts
                .next()
                .filter(|sha| {
                    sha.len() == 64 && sha.bytes().all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
                })?
                .to_owned(),
        };
        parts.next().is_none().then_some(entry)
    }
}

#[cfg(windows)]
fn identity(file: &fs::File) -> io::Result<String> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle,
    };
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // (the handle stays open for the call; info is written whole or the call fails)
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(format!(
        "w{:08x}{:08x}{:08x}",
        info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow
    ))
}

#[cfg(unix)]
fn identity(file: &fs::File) -> io::Result<String> {
    use std::os::unix::fs::MetadataExt;
    let meta = file.metadata()?;
    Ok(format!("u{:016x}{:016x}", meta.dev(), meta.ino()))
}

/// The record: the copies placed, those being put in place (pending), and the lines this launcher can't read (damaged, or
/// from a newer launcher), kept as they are
#[derive(Debug, Default, PartialEq, Eq)]
struct Record {
    current: BTreeMap<Kind, Entry>,
    pending: BTreeMap<Kind, Entry>,
    other: Vec<String>,
}

impl Record {
    /// The record in folder; a missing one holds nothing. A line it can't read holds no copy, and is kept. Fails when the
    /// record is there but can't be read, so an Add or a Remove never writes it again without the copies it holds
    fn read(folder: &Path) -> io::Result<Record> {
        let mut record = Record::default();
        let bytes = match fs::read(folder.join(RECORD)) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(record),
            Err(error) => return Err(error),
        };
        for line in String::from_utf8_lossy(&bytes).lines() {
            let parsed = line.split_once('=').and_then(|(key, value)| {
                let (pending, name) = match key.strip_prefix(PENDING) {
                    Some(name) => (true, name),
                    None => (false, key),
                };
                Some((pending, Kind::parse(name)?, Entry::parse(value)?))
            });
            match parsed {
                Some((true, kind, entry)) => {
                    record.pending.insert(kind, entry);
                }
                Some((false, kind, entry)) => {
                    record.current.insert(kind, entry);
                }
                None if !line.trim().is_empty() => record.other.push(line.to_owned()),
                None => {}
            }
        }
        Ok(record)
    }

    /// Written whole or not at all; a record left with nothing is deleted. A kept line for a place this record now holds is
    /// left out: the new entry stands for it
    fn write(&self, folder: &Path) -> io::Result<()> {
        let path = folder.join(RECORD);
        let mut text = String::new();
        let mut keys = Vec::new();
        for (kind, entry) in &self.current {
            keys.push(kind.name().to_owned());
            text.push_str(&format!("{}={}\n", kind.name(), entry.line()));
        }
        for (kind, entry) in &self.pending {
            keys.push(format!("{PENDING}{}", kind.name()));
            text.push_str(&format!("{PENDING}{}={}\n", kind.name(), entry.line()));
        }
        for line in &self.other {
            let key = line.split_once('=').map_or(line.as_str(), |(key, _)| key);
            if !keys.iter().any(|written| written == key) {
                text.push_str(line);
                text.push('\n');
            }
        }
        if text.is_empty() {
            return crate::fsx::remove_file_if_present(&path);
        }
        crate::fsx::atomic_write(&path, None, false, |file| {
            file.write_all(text.as_bytes())?;
            Ok(())
        })
        .map_err(io::Error::other)
    }
}

/// kind's file in folder is the launcher's copy: it matches the record's entry, or a pending replace's (an Add stopped half
/// way), by its bytes, last write time and identity; deep also by its SHA-256 (read whole: off the window's thread, and before
/// any delete). False for a file put there by hand, one changed since, a link, or one through a linked folder
pub(crate) fn ours(folder: &Path, kind: Kind, deep: bool) -> bool {
    let path = place(folder, kind);
    if check_unlinked(folder, &path).is_err() {
        return false;
    }
    let Ok(record) = Record::read(folder) else {
        return false;
    };
    let candidates: Vec<&Entry> = record
        .current
        .get(&kind)
        .into_iter()
        .chain(record.pending.get(&kind))
        .collect();
    if candidates.is_empty() {
        return false;
    }
    let Ok(file) = fs::File::open(&path) else {
        return false;
    };
    let Ok((bytes, written, identity)) = Entry::facts(&file) else {
        return false;
    };
    let matching: Vec<&&Entry> = candidates
        .iter()
        .filter(|e| e.bytes == bytes && e.written == written && e.identity == identity)
        .collect();
    if matching.is_empty() {
        return false;
    }
    if !deep {
        return true;
    }
    drop(file);
    Entry::of(&path, None).is_ok_and(|now| matching.iter().any(|e| e.sha256 == now.sha256))
}

/// The record settled after an Add stopped half way: a pending copy that is in place becomes the placed one; a pending copy
/// that is not (the move never happened, or the file changed since) is dropped; a temporary copy an unfinished Add left is
/// deleted. Nothing to do: nothing written. Nothing at all through a linked folder. Run off the window's thread, never
/// while another Add or Remove runs ([`settle`]; [`install`] and [`remove`] run it first)
fn reconcile(folder: &Path) -> io::Result<()> {
    if check_unlinked(folder, folder).is_err() {
        return Ok(());
    }
    drop_stale_copies(folder);
    let mut record = Record::read(folder)?;
    if record.pending.is_empty() {
        return Ok(());
    }
    for (kind, entry) in std::mem::take(&mut record.pending) {
        let path = place(folder, kind);
        let now = fs::File::open(&path).and_then(|file| Entry::facts(&file));
        if now.is_ok_and(|(bytes, written, identity)| {
            entry.bytes == bytes && entry.written == written && entry.identity == identity
        }) && check_unlinked(folder, &path).is_ok()
        {
            record.current.insert(kind, entry);
        }
    }
    record.write(folder)
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(test)]
mod tests;
