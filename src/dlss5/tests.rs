//! The player's DLSS 5 files: where they go, how one Add tells NVIDIA's file from the FP16 one by its content, which files are
//! refused, the record of the launcher's copies (and an Add stopped half way), replace and remove. Stand-in files of a few MB
//! with their own hash as the FP16 list (no NVIDIA file here); NVIDIA's check given as a stand-in, and on Windows the real one
//! over files a Windows PC has
use super::*;
use anyhow::Result;
use std::{
    cell::{Cell, RefCell},
    io::{Seek, SeekFrom},
    time::{Duration, SystemTime},
};

const MB: usize = 1 << 20;

/// A Windows program file (MZ) of size bytes, all fill after the MZ
fn make(dir: &Path, name: &str, fill: u8, size: usize) -> PathBuf {
    let mut bytes = vec![fill; size];
    bytes[..2].copy_from_slice(b"MZ");
    let path = dir.join(name);
    fs::create_dir_all(dir).unwrap();
    fs::write(&path, bytes).unwrap();
    path
}

fn sha256_of(path: &Path) -> String {
    hex(&Sha256::digest(fs::read(path).unwrap()))
}

fn same(a: &Path, b: &Path) -> bool {
    fs::read(a).unwrap() == fs::read(b).unwrap()
}

/// No temporary copy left anywhere under folder
fn no_temporary(folder: &Path) -> bool {
    fn walk(dir: &Path) -> bool {
        let Ok(entries) = fs::read_dir(dir) else {
            return true;
        };
        entries.flatten().all(|entry| {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().into_owned();
            !(name.starts_with(".longshot-launcher-") && name.ends_with(".tmp"))
                && (!path.is_dir() || walk(&path))
        })
    }
    walk(folder)
}

fn set_written(path: &Path, time: SystemTime) -> Result<()> {
    fs::File::options()
        .write(true)
        .open(path)?
        .set_modified(time)?;
    Ok(())
}

fn written(path: &Path) -> SystemTime {
    fs::metadata(path).unwrap().modified().unwrap()
}

/// The stand-ins: a temporary folder with ngx in it, the chosen files, and the known FP16 list (the stand-in FP16 file)
struct Files {
    _tmp: tempfile::TempDir,
    root: PathBuf,
    ngx: PathBuf,
    fp16: PathBuf,
    other_fp16: PathBuf,
    nvidia_a: PathBuf,
    nvidia_b: PathBuf,
    known: Vec<Known>,
}

impl Files {
    fn new() -> Files {
        let tmp = tempfile::tempdir().unwrap();
        let root = std::path::absolute(tmp.path()).unwrap();
        let chosen = root.join("chosen");
        let fp16 = make(&chosen, "fp16-copy.dll", 1, 2 * MB);
        let known = vec![Known {
            size: 2 * MB as u64,
            sha256: Box::leak(sha256_of(&fp16).into_boxed_str()),
        }];
        Files {
            ngx: root.join("local").join("Longshot").join("ngx"),
            other_fp16: make(&chosen, "fp16-other.dll", 2, 2 * MB),
            nvidia_a: make(&chosen, "nvngx_dlssnr.dll", 3, 2 * MB),
            nvidia_b: make(&chosen, "newer_nvngx_dlssnr.dll", 4, 3 * MB),
            fp16,
            known,
            root,
            _tmp: tmp,
        }
    }
}

/// NVIDIA's check, a stand-in: what it says of the next file it is asked about, and the names of the files it was asked about
struct Stand {
    verdict: Cell<NvidiaCheck>,
    asked: RefCell<Vec<String>>,
}

impl Stand {
    fn new(verdict: NvidiaCheck) -> Stand {
        Stand {
            verdict: Cell::new(verdict),
            asked: RefCell::new(Vec::new()),
        }
    }

    fn check(&self) -> impl Fn(&Path) -> NvidiaCheck + '_ {
        move |path| {
            self.asked
                .borrow_mut()
                .push(path.file_name().unwrap().to_string_lossy().into_owned());
            self.verdict.get()
        }
    }

    fn add(&self, files: &Files, source: &Path, folder: &Path) -> Result<Placed, Failure> {
        install(source, folder, self.check(), |_| {}, &files.known, None)
    }

    /// An Add the player confirmed: it replaces theirs, the file of theirs it stopped at
    fn replace(
        &self,
        files: &Files,
        source: &Path,
        folder: &Path,
        theirs: &InTheWay,
    ) -> Result<Placed, Failure> {
        install(
            source,
            folder,
            self.check(),
            |_| {},
            &files.known,
            Some(theirs),
        )
    }
}

fn refused(result: Result<Placed, Failure>) -> Option<Refusal> {
    match result {
        Err(Failure::Refused(why)) => Some(why),
        _ => None,
    }
}

fn failed<T: fmt::Debug>(result: Result<T, Failure>) -> bool {
    matches!(result, Err(Failure::Failed(_)))
}

#[test]
fn the_files_go_where_the_game_looks() {
    let ngx = Path::new("ngx");
    assert_eq!(place(ngx, Kind::Nvidia), ngx.join("nvngx_dlssnr.dll"));
    assert_eq!(
        place(ngx, Kind::Fp16),
        ngx.join("fp16").join("nvngx_dlssnr.dll")
    );
    assert!(!FP16_KNOWN.is_empty());
    for known in FP16_KNOWN {
        assert!(
            known.sha256.len() == 64
                && known
                    .sha256
                    .bytes()
                    .all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f')),
            "The FP16 list holds SHA-256s in lower-case hex."
        );
    }
    #[cfg(windows)]
    assert!(folder().is_some_and(|f| f.ends_with(Path::new("Longshot").join("ngx"))));
    #[cfg(not(windows))]
    assert_eq!(folder(), None);
}

#[test]
fn the_section_shows_for_nvidia_rtx_and_says_what_the_gpu_runs() {
    let gpu = |nvidia, name: Option<&str>| Gpu {
        nvidia,
        name: name.map(str::to_owned),
    };
    let rtx5090 = gpu(true, Some("NVIDIA GeForce RTX 5090"));
    let laptop = gpu(true, Some("NVIDIA GeForce RTX 4050 Laptop GPU"));
    assert!(offered(true, std::slice::from_ref(&rtx5090)));
    assert!(offered(true, &[]), "No GPU found: shown.");
    assert!(
        offered(true, &[gpu(true, None)]),
        "Known only by its maker: shown."
    );
    assert!(
        !offered(false, std::slice::from_ref(&rtx5090)),
        "Never outside Windows."
    );
    assert!(!offered(true, &[gpu(false, Some("AMD Radeon RX 9070 XT"))]));
    assert!(!offered(
        true,
        &[gpu(true, Some("NVIDIA GeForce GTX 1080"))]
    ));

    assert_eq!(rtx_series(&[rtx5090]), 5);
    assert_eq!(rtx_series(&[laptop]), 4);
    assert_eq!(
        rtx_series(&[gpu(true, Some("NVIDIA GeForce RTX 2060 SUPER"))]),
        2
    );
    assert_eq!(
        rtx_series(&[
            gpu(true, Some("NVIDIA GeForce RTX 3080")),
            gpu(true, Some("NVIDIA GeForce RTX 5070 Ti"))
        ]),
        5
    );
    assert_eq!(
        rtx_series(&[gpu(true, Some("NVIDIA RTX A6000")), gpu(true, None)]),
        0,
        "A workstation card or no name: unknown."
    );
    assert_eq!(rtx_series(&[gpu(false, Some("AMD Radeon RX 7600"))]), 0);

    assert!(gpu_line(5).contains("RTX 50"));
    assert!(gpu_line(3).contains("FP16"));
    assert_eq!(gpu_line(0), "");
}

#[test]
fn a_file_that_cannot_be_one_is_refused_before_any_copy() {
    let files = Files::new();
    let chosen = files.root.join("chosen");
    let tiny = chosen.join("tiny.dll");
    fs::write(&tiny, b"MZ\0\0").unwrap();
    let text = chosen.join("text.dll");
    fs::write(&text, vec![0u8; 2 * MB]).unwrap();
    assert_eq!(check(&chosen.join("gone.dll")), Some(Refusal::Gone));
    assert_eq!(check(&tiny), Some(Refusal::NotDlss5));
    assert_eq!(check(&text), Some(Refusal::NotDlss5));
    assert_eq!(
        check(&chosen),
        Some(Refusal::NotDlss5),
        "A folder is not one."
    );
    assert_eq!(check(&files.nvidia_a), None);
    assert_eq!(check(&files.fp16), None);

    let stand = Stand::new(NvidiaCheck::DlssModel);
    assert_eq!(
        refused(stand.add(&files, &text, &files.ngx)),
        Some(Refusal::NotDlss5)
    );
    assert!(
        stand.asked.borrow().is_empty() && !files.ngx.exists(),
        "A file refused before the copy writes nothing."
    );
}

#[test]
fn one_add_tells_the_two_files_apart_by_their_content() -> Result<()> {
    let files = Files::new();
    let ngx = &files.ngx;
    let stand = Stand::new(NvidiaCheck::NotNvidia);

    // (the FP16 file, told by its size and SHA-256 as copied: to ngx\fp16, whole, NVIDIA's check never asked)
    let last = Cell::new(0.0);
    let placed = install(
        &files.fp16,
        ngx,
        stand.check(),
        |p| last.set(p),
        &files.known,
        None,
    )
    .unwrap();
    let fp16_place = place(ngx, Kind::Fp16);
    assert_eq!(
        placed,
        Placed {
            kind: Kind::Fp16,
            path: fp16_place.clone(),
            how: How::Copied,
        }
    );
    assert!(same(&fp16_place, &files.fp16));
    assert_eq!(last.get(), 1.0);
    assert!(no_temporary(ngx) && stand.asked.borrow().is_empty());

    // (a file of the known size but other bytes is not the FP16 file: NVIDIA's check is asked, says not NVIDIA's: refused,
    // the refusal naming the known hash)
    let why = refused(stand.add(&files, &files.other_fp16, ngx)).unwrap();
    assert_eq!(why, Refusal::Neither);
    assert_eq!(
        why.value(&files.known),
        files.known[0].sha256[..8].to_ascii_uppercase()
    );
    assert_eq!(stand.asked.borrow().len(), 1);
    assert!(same(&fp16_place, &files.fp16) && !place(ngx, Kind::Nvidia).exists());
    assert!(
        no_temporary(ngx),
        "A refused file leaves no temporary file."
    );
    assert!(ours(ngx, Kind::Fp16, true) && !ours(ngx, Kind::Nvidia, false));
    assert!(ngx.join(RECORD).is_file());

    // (the rows: the launcher's copy and its size, a file put there by hand, an FP16 file the game does not know, or nothing)
    let (key, value) = line(Kind::Fp16, bytes(ngx, Kind::Fp16), true, &files.known);
    assert!(
        key.starts_with("Added") && value.contains("2"),
        "{key} {value}"
    );
    assert!(
        line(Kind::Nvidia, Some(12345), false, &files.known)
            .0
            .contains("by hand")
    );
    assert!(
        line(Kind::Fp16, Some(12345), true, &files.known)
            .0
            .contains("not the known FP16")
    );
    assert_eq!(
        line(Kind::Nvidia, None, false, &files.known).0,
        "Not added."
    );

    // (NVIDIA's file: not the FP16 file, so NVIDIA's check is asked, on the copy; the model goes to ngx\nvngx_dlssnr.dll)
    stand.verdict.set(NvidiaCheck::DlssModel);
    let placed = stand.add(&files, &files.nvidia_a, ngx).unwrap();
    let nvidia_place = place(ngx, Kind::Nvidia);
    assert_eq!(placed.kind, Kind::Nvidia);
    assert_eq!(placed.path, nvidia_place);
    assert!(same(&nvidia_place, &files.nvidia_a) && same(&fp16_place, &files.fp16));
    {
        let asked = stand.asked.borrow();
        assert!(
            asked.len() == 2
                && asked[1].starts_with(".longshot-launcher-")
                && asked[1].ends_with(".tmp"),
            "NVIDIA's check reads the copy, before it goes in place: {asked:?}"
        );
    }
    assert!(no_temporary(ngx));

    // (another file NVIDIA signed, and a file not NVIDIA's: refused, NVIDIA's file in place kept)
    stand.verdict.set(NvidiaCheck::OtherFile);
    assert_eq!(
        refused(stand.add(&files, &files.nvidia_b, ngx)),
        Some(Refusal::OtherNvidia)
    );
    stand.verdict.set(NvidiaCheck::NotNvidia);
    assert_eq!(
        refused(stand.add(&files, &files.nvidia_b, ngx)),
        Some(Refusal::Neither)
    );
    assert!(same(&nvidia_place, &files.nvidia_a) && no_temporary(ngx));

    // (the FP16 file goes to its own place whatever NVIDIA's check would say; a newer NVIDIA file replaces the old one)
    stand.verdict.set(NvidiaCheck::DlssModel);
    let again = stand.add(&files, &files.fp16, ngx).unwrap();
    assert_eq!(
        (again.kind, again.how),
        (Kind::Fp16, How::AlreadyOurs),
        "The same file again: nothing copied."
    );
    assert!(same(&nvidia_place, &files.nvidia_a));
    assert_eq!(
        stand.add(&files, &files.nvidia_b, ngx).unwrap().kind,
        Kind::Nvidia
    );
    assert_eq!(fs::metadata(&nvidia_place)?.len(), 3 * MB as u64);
    assert!(same(&fp16_place, &files.fp16) && no_temporary(ngx));

    // (a file in place is not copied over itself)
    assert_eq!(
        refused(stand.add(&files, &nvidia_place, ngx)),
        Some(Refusal::InPlace)
    );
    assert_eq!(
        refused(stand.add(&files, &fp16_place, ngx)),
        Some(Refusal::InPlace)
    );
    assert_eq!(
        added(ngx),
        vec![(Kind::Nvidia, nvidia_place), (Kind::Fp16, fp16_place)],
        "Storage and Remove everything see both of the launcher's files."
    );
    assert!(files.fp16.is_file() && files.nvidia_a.is_file() && files.nvidia_b.is_file());
    Ok(())
}

#[test]
fn a_replacement_with_the_same_size_and_time_is_the_players() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    let original = place(&files.ngx, Kind::Nvidia);
    let moved = files.root.join("moved.dll");
    let time = written(&original);
    fs::rename(&original, &moved)?;
    fs::copy(&moved, &original)?;
    set_written(&original, time)?;
    assert!(!ours(&files.ngx, Kind::Nvidia, false) && added(&files.ngx).is_empty());
    assert_eq!(
        refused_remove(remove(&files.ngx, Kind::Nvidia)),
        Some(Refusal::NotOurs)
    );
    assert!(original.is_file(), "The player's file is not removed.");
    Ok(())
}

fn refused_remove(result: Result<(), Failure>) -> Option<Refusal> {
    match result {
        Err(Failure::Refused(why)) => Some(why),
        _ => None,
    }
}

#[test]
fn an_edit_in_place_is_caught_before_any_delete() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    let original = place(&files.ngx, Kind::Nvidia);
    let time = written(&original);
    {
        let mut file = fs::File::options().read(true).write(true).open(&original)?;
        file.seek(SeekFrom::Start(100))?;
        let mut byte = [0u8];
        file.read_exact(&mut byte)?;
        file.seek(SeekFrom::Start(100))?;
        file.write_all(&[byte[0] ^ 1])?;
        file.set_modified(time)?;
    }
    // (the same file, size and time: the quick check that the rows use can't tell; the full one before a delete does)
    assert!(ours(&files.ngx, Kind::Nvidia, false));
    assert!(!ours(&files.ngx, Kind::Nvidia, true));
    assert_eq!(
        refused_remove(remove(&files.ngx, Kind::Nvidia)),
        Some(Refusal::NotOurs)
    );
    assert!(original.is_file());
    Ok(())
}

#[test]
fn a_record_that_cannot_be_written_stops_the_add() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    fs::remove_file(files.ngx.join(RECORD))?;
    fs::create_dir(files.ngx.join(RECORD))?;
    assert!(failed(stand.add(&files, &files.nvidia_b, &files.ngx)));
    assert!(
        same(&place(&files.ngx, Kind::Nvidia), &files.nvidia_a) && no_temporary(&files.ngx),
        "The old copy stays in place, and no temporary file."
    );
    Ok(())
}

/// An Add for kind stopped half way: a copy of source in folder, the record holding it as pending, and moved in place when
/// moved; the record's current entry is not changed
fn stopped_add(folder: &Path, kind: Kind, source: &Path, moved: bool) -> Result<()> {
    let copy = folder.join(".longshot-launcher-stopped.tmp");
    fs::copy(source, &copy)?;
    let mut record = Record::read(folder)?;
    record.pending.insert(kind, Entry::of(&copy, None)?);
    record.write(folder)?;
    if moved {
        fs::rename(&copy, place(folder, kind))?;
    } else {
        fs::remove_file(&copy)?;
    }
    Ok(())
}

#[test]
fn an_add_stopped_before_the_move_keeps_the_old_copy_the_launchers() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    stopped_add(&files.ngx, Kind::Nvidia, &files.nvidia_b, false)?;
    assert!(
        ours(&files.ngx, Kind::Nvidia, true),
        "The old copy is still the launcher's."
    );
    assert!(!Record::read(&files.ngx)?.pending.is_empty());
    reconcile(&files.ngx)?;
    let record = Record::read(&files.ngx)?;
    assert!(record.pending.is_empty() && record.current.contains_key(&Kind::Nvidia));
    assert!(ours(&files.ngx, Kind::Nvidia, true));
    remove(&files.ngx, Kind::Nvidia).unwrap();
    assert!(!files.ngx.exists());
    Ok(())
}

#[test]
fn an_add_stopped_after_the_move_makes_the_new_copy_the_launchers() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    stopped_add(&files.ngx, Kind::Nvidia, &files.nvidia_b, true)?;
    let nvidia_place = place(&files.ngx, Kind::Nvidia);
    assert!(same(&nvidia_place, &files.nvidia_b));
    assert!(
        ours(&files.ngx, Kind::Nvidia, true),
        "The new copy is the launcher's."
    );
    reconcile(&files.ngx)?;
    let record = Record::read(&files.ngx)?;
    assert!(record.pending.is_empty());
    assert_eq!(record.current[&Kind::Nvidia].bytes, 3 * MB as u64);
    assert!(ours(&files.ngx, Kind::Nvidia, true));

    // (an Add stopped half way for a place that had no copy: once the move is done, the new copy is the launcher's)
    fs::create_dir_all(files.ngx.join("fp16"))?;
    stopped_add(&files.ngx, Kind::Fp16, &files.fp16, true)?;
    assert!(ours(&files.ngx, Kind::Fp16, true));
    reconcile(&files.ngx)?;
    assert!(Record::read(&files.ngx)?.current.contains_key(&Kind::Fp16));

    // (nothing pending: the record is not written)
    let time = written(&files.ngx.join(RECORD));
    std::thread::sleep(Duration::from_millis(20));
    reconcile(&files.ngx)?;
    assert_eq!(written(&files.ngx.join(RECORD)), time);
    Ok(())
}

#[test]
fn a_pending_copy_that_is_not_in_place_is_dropped() -> Result<()> {
    let files = Files::new();
    fs::create_dir_all(&files.ngx)?;
    stopped_add(&files.ngx, Kind::Nvidia, &files.nvidia_a, false)?;
    assert!(!ours(&files.ngx, Kind::Nvidia, false));
    reconcile(&files.ngx)?;
    assert!(
        !files.ngx.join(RECORD).exists(),
        "A record left with nothing is deleted."
    );
    Ok(())
}

#[test]
fn a_file_put_there_by_hand_is_the_players() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.fp16, &files.ngx).unwrap();
    let nvidia_place = place(&files.ngx, Kind::Nvidia);
    fs::copy(&files.nvidia_a, &nvidia_place)?;
    set_written(
        &nvidia_place,
        SystemTime::UNIX_EPOCH + Duration::from_secs(1_793_000_000),
    )?;
    assert!(!ours(&files.ngx, Kind::Nvidia, false) && ours(&files.ngx, Kind::Fp16, false));
    assert_eq!(
        added(&files.ngx),
        vec![(Kind::Fp16, place(&files.ngx, Kind::Fp16))]
    );
    let (key, value) = line(
        Kind::Nvidia,
        bytes(&files.ngx, Kind::Nvidia),
        ours(&files.ngx, Kind::Nvidia, false),
        &files.known,
    );
    assert!(key.contains("by hand") && !value.is_empty());
    assert_eq!(
        refused_remove(remove(&files.ngx, Kind::Nvidia)),
        Some(Refusal::NotOurs)
    );
    assert!(nvidia_place.is_file());
    // (adding another file there stops at it; once the player confirms, it is replaced and the place is the launcher's)
    let Err(Failure::InTheWay(theirs)) = stand.add(&files, &files.nvidia_b, &files.ngx) else {
        panic!("An Add over a file put there by hand must ask first.");
    };
    assert!(same(&nvidia_place, &files.nvidia_a) && no_temporary(&files.ngx));
    assert_eq!(
        stand
            .replace(&files, &files.nvidia_b, &files.ngx, &theirs)
            .unwrap()
            .how,
        How::Copied
    );
    assert!(ours(&files.ngx, Kind::Nvidia, true) && same(&nvidia_place, &files.nvidia_b));

    // (a damaged record holds nothing as the launcher's, and keeps the lines it can't read; a line from a newer launcher too)
    fs::write(
        files.ngx.join(RECORD),
        "Nvidia=x y\nnot a line\npending.Fp16=1 2\nFuture=9 9 w9 x\n",
    )?;
    assert!(!ours(&files.ngx, Kind::Nvidia, false) && !ours(&files.ngx, Kind::Fp16, false));
    let record = Record::read(&files.ngx)?;
    assert!(record.current.is_empty() && record.pending.is_empty());
    assert_eq!(record.other.len(), 4);
    // (an Add, confirmed over the file no record holds now, writes its own entry in place of the damaged line for its place,
    // and keeps the rest)
    let Err(Failure::InTheWay(theirs)) = stand.add(&files, &files.nvidia_a, &files.ngx) else {
        panic!("With a damaged record the file in place is not the launcher's.");
    };
    stand
        .replace(&files, &files.nvidia_a, &files.ngx, &theirs)
        .unwrap();
    let text = fs::read_to_string(files.ngx.join(RECORD))?;
    assert!(
        !text.contains("Nvidia=x y")
            && text.contains("not a line")
            && text.contains("Future=9 9 w9 x")
    );
    assert!(ours(&files.ngx, Kind::Nvidia, true));
    // (a record that is there but can't be read stops an Add: it would be written again without what it holds)
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        let _held = fs::File::options()
            .read(true)
            .share_mode(0)
            .open(files.ngx.join(RECORD))?;
        assert!(failed(stand.add(&files, &files.nvidia_b, &files.ngx)));
        assert!(!ours(&files.ngx, Kind::Nvidia, false));
    }
    assert!(same(&nvidia_place, &files.nvidia_a));
    Ok(())
}

#[test]
fn the_file_in_place_picked_again_by_another_path_is_not_copied_over_itself() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    // (a hard link is the same file under another name, as an 8.3 name or a path through a junction would be)
    let other_name = files.root.join("chosen").join("same-file.dll");
    fs::hard_link(place(&files.ngx, Kind::Nvidia), &other_name)?;
    assert_eq!(
        refused(stand.add(&files, &other_name, &files.ngx)),
        Some(Refusal::InPlace)
    );
    assert!(ours(&files.ngx, Kind::Nvidia, true));
    Ok(())
}

#[test]
fn an_add_settles_a_replace_stopped_before_it() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    stopped_add(&files.ngx, Kind::Nvidia, &files.nvidia_b, true)?;
    // (the next Add, even one refused after the copy, settles it first: the moved copy becomes the placed one, so a second
    // stopped Add can never leave the launcher's copy unknown)
    stand.verdict.set(NvidiaCheck::NotNvidia);
    assert_eq!(
        refused(stand.add(&files, &files.other_fp16, &files.ngx)),
        Some(Refusal::Neither)
    );
    let record = Record::read(&files.ngx)?;
    assert!(record.pending.is_empty());
    assert_eq!(record.current[&Kind::Nvidia].bytes, 3 * MB as u64);
    Ok(())
}

#[test]
fn a_temporary_copy_left_by_an_unfinished_add_is_cleaned_up() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    let old = files.ngx.join(".longshot-launcher-old.tmp");
    let fresh = files.ngx.join(".longshot-launcher-fresh.tmp");
    let players = files.ngx.join(".players-own.tmp");
    for path in [&old, &fresh, &players] {
        fs::write(path, b"partial copy")?;
    }
    set_written(&old, SystemTime::now() - Duration::from_secs(3600))?;
    set_written(&players, SystemTime::now() - Duration::from_secs(3600))?;
    // (reconcile, as settle and every Add and Remove run it; settle itself skips while a parallel test holds the lock)
    reconcile(&files.ngx)?;
    assert!(
        !old.exists(),
        "An old temporary copy of the launcher's goes."
    );
    assert!(fresh.exists(), "One an Add may still be writing stays.");
    assert!(
        players.exists(),
        "A file of another name is not the launcher's."
    );
    Ok(())
}

#[test]
fn a_link_above_the_longshot_folder_is_the_systems() -> Result<()> {
    // (a profile or AppData folder moved to another drive with a junction: the folders from Longshot down are plain)
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    let real = files.root.join("real-local");
    let local = files.root.join("linked-local");
    fs::create_dir_all(&real)?;
    link_folder(&real, &local)?;
    let ngx = local.join("Longshot").join("ngx");
    assert_eq!(
        stand.add(&files, &files.fp16, &ngx).unwrap().kind,
        Kind::Fp16
    );
    assert!(ours(&ngx, Kind::Fp16, true));
    remove(&ngx, Kind::Fp16).unwrap();
    assert!(!ngx.exists() && real.join("Longshot").is_dir());
    Ok(())
}

#[test]
fn an_add_asks_before_it_replaces_a_file_put_there_by_hand() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    let nvidia_place = place(&files.ngx, Kind::Nvidia);
    fs::create_dir_all(&files.ngx)?;
    fs::copy(&files.nvidia_a, &nvidia_place)?;

    // (a different file of the same kind: stopped, naming the file in the way; nothing changed, no copy left)
    let Err(Failure::InTheWay(theirs)) = stand.add(&files, &files.nvidia_b, &files.ngx) else {
        panic!("An Add over a file put there by hand must ask first.");
    };
    assert_eq!(
        (theirs.kind, &theirs.path, theirs.bytes),
        (Kind::Nvidia, &nvidia_place, 2 * MB as u64)
    );
    assert!(same(&nvidia_place, &files.nvidia_a) && no_temporary(&files.ngx));
    assert!(!files.ngx.join(RECORD).exists());

    // (the file changes before the player answers: the answer was about the old one, so it asks again)
    fs::write(&nvidia_place, fs::read(&files.fp16)?)?;
    let Err(Failure::InTheWay(again)) = stand.replace(&files, &files.nvidia_b, &files.ngx, &theirs)
    else {
        panic!("A confirmation for another file must not replace this one.");
    };
    assert!(same(&nvidia_place, &files.fp16));
    // (a confirmation for the other place does not count either)
    let mut elsewhere = again.clone();
    elsewhere.kind = Kind::Fp16;
    assert!(matches!(
        stand.replace(&files, &files.nvidia_b, &files.ngx, &elsewhere),
        Err(Failure::InTheWay(_))
    ));

    // (confirmed for the file as it is: replaced, the launcher's copy now; the file added from stays)
    let placed = stand
        .replace(&files, &files.nvidia_b, &files.ngx, &again)
        .unwrap();
    assert_eq!((placed.kind, placed.how), (Kind::Nvidia, How::Copied));
    assert!(same(&nvidia_place, &files.nvidia_b) && ours(&files.ngx, Kind::Nvidia, true));
    assert!(files.nvidia_b.is_file() && no_temporary(&files.ngx));
    Ok(())
}

#[test]
fn the_same_file_already_in_place_is_not_copied_again() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    let nvidia_place = place(&files.ngx, Kind::Nvidia);
    let copy_of_a = files.root.join("chosen").join("copy-of-a.dll");
    fs::copy(&files.nvidia_a, &copy_of_a)?;

    // (the same file put there by hand: no question, no copy; it becomes the launcher's copy, Remove and all)
    fs::create_dir_all(&files.ngx)?;
    fs::copy(&files.nvidia_a, &nvidia_place)?;
    let before = identity(&fs::File::open(&nvidia_place)?)?;
    let placed = stand.add(&files, &copy_of_a, &files.ngx).unwrap();
    assert_eq!((placed.kind, placed.how), (Kind::Nvidia, How::TakenOver));
    assert_eq!(
        identity(&fs::File::open(&nvidia_place)?)?,
        before,
        "Nothing copied."
    );
    assert!(ours(&files.ngx, Kind::Nvidia, true) && no_temporary(&files.ngx));

    // (the same file again once it is the launcher's: nothing copied, nothing written)
    let record = fs::read(files.ngx.join(RECORD))?;
    let placed = stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    assert_eq!(placed.how, How::AlreadyOurs);
    assert_eq!(identity(&fs::File::open(&nvidia_place)?)?, before);
    assert_eq!(fs::read(files.ngx.join(RECORD))?, record);

    // (taking it over never touches the file added from; Remove deletes the launcher's copy only)
    remove(&files.ngx, Kind::Nvidia).unwrap();
    assert!(copy_of_a.is_file() && files.nvidia_a.is_file() && !nvidia_place.exists());
    Ok(())
}

#[test]
fn remove_takes_only_the_launchers_files_and_their_empty_folders() -> Result<()> {
    let files = Files::new();
    let ngx = &files.ngx;
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.fp16, ngx).unwrap();
    stand.add(&files, &files.nvidia_a, ngx).unwrap();
    let players = ngx.join("nvngx_dlss.dll");
    fs::write(&players, "the player's own")?;

    remove(ngx, Kind::Fp16).unwrap();
    assert!(!place(ngx, Kind::Fp16).exists() && !ngx.join("fp16").exists());
    assert!(place(ngx, Kind::Nvidia).is_file());
    assert_eq!(
        refused_remove(remove(ngx, Kind::Fp16)),
        Some(Refusal::NotOurs),
        "A file that is not there is not removed."
    );
    remove(ngx, Kind::Nvidia).unwrap();
    assert!(ngx.is_dir() && players.is_file() && !ngx.join(RECORD).exists());

    fs::remove_file(&players)?;
    stand.add(&files, &files.nvidia_a, ngx).unwrap();
    remove(ngx, Kind::Nvidia).unwrap();
    assert!(!ngx.exists() && ngx.parent().unwrap().is_dir());
    assert!(
        files.fp16.is_file() && files.nvidia_a.is_file() && files.nvidia_b.is_file(),
        "The player's own files are never moved or deleted."
    );
    Ok(())
}

/// A folder link at link to target: a junction on Windows, a symbolic link elsewhere
fn link_folder(target: &Path, link: &Path) -> Result<()> {
    #[cfg(windows)]
    junction::create(target, link)?;
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, link)?;
    Ok(())
}

#[test]
fn a_linked_folder_on_the_way_is_never_written_through() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    for part in ["longshot", "ngx", "fp16"] {
        let parent = files.root.join(format!("link-{part}"));
        let target = files.root.join(format!("target-{part}"));
        let longshot = parent.join("Longshot");
        let ngx = longshot.join("ngx");
        let (kind, source) = if part == "fp16" {
            (Kind::Fp16, &files.fp16)
        } else {
            (Kind::Nvidia, &files.nvidia_a)
        };
        // (added through plain folders, so the record holds the copy)
        stand.add(&files, source, &ngx).unwrap();
        // (then a folder on the way moved elsewhere and a link put in its place: the same files, reached through a link)
        let swapped = match part {
            "longshot" => longshot.clone(),
            "ngx" => ngx.clone(),
            _ => ngx.join("fp16"),
        };
        fs::rename(&swapped, &target)?;
        link_folder(&target, &swapped)?;
        let behind = match part {
            "longshot" => place(&target.join("ngx"), kind),
            "ngx" => place(&target, kind),
            _ => target.join(FILE_NAME),
        };
        assert!(same(&behind, source), "{part}");

        assert!(
            !ours(&ngx, kind, false),
            "{part}: not the launcher's through a link"
        );
        assert!(!added(&ngx).iter().any(|(k, _)| *k == kind), "{part}");
        assert_eq!(
            refused_remove(remove(&ngx, kind)),
            Some(Refusal::Linked),
            "{part}"
        );
        // (the same kind added again: refused, nothing written behind the link)
        let again = files.root.join("chosen").join(format!("again-{part}.dll"));
        fs::copy(
            if kind == Kind::Fp16 {
                &files.fp16
            } else {
                &files.nvidia_b
            },
            &again,
        )?;
        assert_eq!(
            refused(stand.add(&files, &again, &ngx)),
            Some(Refusal::Linked),
            "{part}"
        );
        assert!(
            same(&behind, source),
            "{part}: the file behind the link stays."
        );
        assert!(no_temporary(&target) && no_temporary(&parent), "{part}");
    }
    Ok(())
}

#[test]
fn remove_leaves_a_linked_fp16_folder_alone() -> Result<()> {
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    // (the player's fp16 folder is a link to an empty folder of theirs)
    let theirs = files.root.join("their-fp16");
    fs::create_dir_all(&theirs)?;
    let fp16 = files.ngx.join("fp16");
    link_folder(&theirs, &fp16)?;
    remove(&files.ngx, Kind::Nvidia).unwrap();
    assert!(crate::fsx::linked_location(&fp16)?, "The link stays.");
    assert!(theirs.is_dir() && files.ngx.is_dir());
    Ok(())
}

#[cfg(windows)]
#[test]
fn a_file_the_game_holds_is_neither_replaced_nor_removed() -> Result<()> {
    use std::os::windows::fs::OpenOptionsExt;
    let files = Files::new();
    let stand = Stand::new(NvidiaCheck::DlssModel);
    stand.add(&files, &files.nvidia_a, &files.ngx).unwrap();
    let held = place(&files.ngx, Kind::Nvidia);
    {
        // (as the game holds the model it loaded: open, shared for reading only)
        let _game = fs::File::options().read(true).share_mode(1).open(&held)?;
        assert!(failed(stand.add(&files, &files.nvidia_b, &files.ngx)));
        assert!(failed(remove(&files.ngx, Kind::Nvidia)));
    }
    assert!(same(&held, &files.nvidia_a) && no_temporary(&files.ngx));
    assert!(ours(&files.ngx, Kind::Nvidia, true) && Record::read(&files.ngx)?.pending.is_empty());
    Ok(())
}

#[cfg(windows)]
#[test]
fn one_launcher_at_a_time_across_processes() {
    use super::windows::NamedLock;
    use std::sync::mpsc;
    // (another thread stands for another launcher: the named mutex belongs to the thread that took it)
    let (taken, held) = (mpsc::channel(), mpsc::channel::<()>());
    let other = std::thread::spawn(move || {
        let lock = NamedLock::acquire(true)
            .expect("mutex wait")
            .expect("the named mutex");
        taken.0.send(()).unwrap();
        held.1.recv().unwrap();
        drop(lock);
    });
    taken.1.recv().unwrap();
    assert!(
        NamedLock::acquire(false).unwrap().is_none(),
        "Settling waits for no one: it skips while another launcher works."
    );
    held.0.send(()).unwrap();
    other.join().unwrap();
    assert!(NamedLock::acquire(true).unwrap().is_some());
}

#[cfg(windows)]
#[test]
fn windows_tells_nvidias_signed_files() -> Result<()> {
    use super::windows::{adapter_of, nvidia_file, signer_organization};
    let files = Files::new();
    assert_eq!(signer_organization(&files.nvidia_a), None);
    assert_eq!(
        nvidia_file(&files.nvidia_a),
        NvidiaCheck::NotNvidia,
        "A file with no signature."
    );

    let system = PathBuf::from(std::env::var_os("SystemRoot").unwrap_or("C:\\Windows".into()))
        .join("System32");
    for name in ["ntdll.dll", "kernel32.dll", "wintrust.dll"] {
        let path = system.join(name);
        if let Some(organization) = signer_organization(&path) {
            assert_ne!(organization, "NVIDIA Corporation");
            assert_eq!(
                nvidia_file(&path),
                NvidiaCheck::NotNvidia,
                "{name}: another maker's file."
            );
        }
    }
    // (an NVIDIA driver's own nvapi64.dll is NVIDIA's but not the DLSS 5 model, also as a copy with the temporary name)
    let nvapi = system.join("nvapi64.dll");
    if signer_organization(&nvapi).as_deref() == Some("NVIDIA Corporation") {
        assert_eq!(nvidia_file(&nvapi), NvidiaCheck::OtherFile);
        let copy = files.root.join(".longshot-launcher-nvapi.tmp");
        fs::copy(&nvapi, &copy)?;
        assert_eq!(nvidia_file(&copy), NvidiaCheck::OtherFile);
    }

    let nvidia = adapter_of(r"PCI\VEN_10DE&DEV_2684", Some(" NVIDIA GeForce RTX 4090 "));
    assert_eq!(
        nvidia,
        Some(Gpu {
            nvidia: true,
            name: Some("NVIDIA GeForce RTX 4090".into())
        })
    );
    assert_eq!(
        adapter_of(r"pci\ven_1002&dev_744c", Some("")).map(|g| (g.nvidia, g.name)),
        Some((false, None))
    );
    assert_eq!(
        adapter_of(
            r"root\basicdisplay",
            Some("Microsoft Basic Display Adapter")
        ),
        None
    );
    assert_eq!(adapter_of(r"pci\ven_1414&dev_008e", None), None);
    let _ = super::windows::gpus();
    Ok(())
}

/// The real files on a PC that has them: `LONGSHOT_DLSS5_NVIDIA` (NVIDIA's nvngx_dlssnr.dll) and `LONGSHOT_DLSS5_FP16` (the
/// FP16 file), each optional; `cargo test real_dlss5_files -- --ignored`. Copies about 540 MB into a temporary folder
#[cfg(windows)]
#[test]
#[ignore]
fn real_dlss5_files() {
    let files = Files::new();
    let nvidia = std::env::var_os("LONGSHOT_DLSS5_NVIDIA").map(PathBuf::from);
    let fp16 = std::env::var_os("LONGSHOT_DLSS5_FP16").map(PathBuf::from);
    assert!(nvidia.is_some() || fp16.is_some(), "Name a file.");
    if let Some(nvidia) = &nvidia {
        assert_eq!(super::windows::nvidia_file(nvidia), NvidiaCheck::DlssModel);
        let placed = install(
            nvidia,
            &files.ngx,
            super::windows::nvidia_file,
            |_| {},
            FP16_KNOWN,
            None,
        )
        .unwrap();
        assert_eq!(placed.kind, Kind::Nvidia);
    }
    if let Some(fp16) = &fp16 {
        assert_eq!(
            super::windows::nvidia_file(fp16),
            NvidiaCheck::NotNvidia,
            "The FP16 file is not NVIDIA's signed file."
        );
        let placed = install(
            fp16,
            &files.ngx,
            super::windows::nvidia_file,
            |_| {},
            FP16_KNOWN,
            None,
        )
        .unwrap();
        assert_eq!(placed.kind, Kind::Fp16);
    }
    for (kind, _) in added(&files.ngx) {
        remove(&files.ngx, kind).unwrap();
    }
    assert!(!files.ngx.exists());
}
