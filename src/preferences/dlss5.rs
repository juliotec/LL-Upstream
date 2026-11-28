//! (Windows) Settings' DLSS 5 files ([`crate::dlss5`]): one Add for the player's own files, which the launcher tells apart by
//! their content and copies once to the folder every version's game looks in; a row for each with what is in place, and a
//! Remove for the launcher's own copy (a file put there by hand is the player's: shown, never removed from here, and
//! replaced by an Add only after the player confirms it in a dialog that names it). The work runs off the window's thread,
//! one Add or Remove at a time, and never while Longshot runs (it holds these files)
use crate::{
    Dlss5Note, Dlss5Row, Locale, MainWindow, Preferences,
    dlss5::{self, Failure, How, InTheWay, Kind},
    i18n,
};
use slint::ComponentHandle;
use std::{
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

/// The files an Add stopped at, waiting for the player's answer: each picked file, and the file of theirs in its way
type Waiting = Arc<Mutex<Vec<(PathBuf, InTheWay)>>>;

pub(crate) fn bind(ui: &MainWindow) {
    let p = ui.global::<Preferences>();
    p.set_dlss5_folder(dlss5::SHOWN_FOLDER.into());
    let waiting = Waiting::default();
    let weak = ui.as_weak();
    p.on_dlss5_check(move || {
        if let Some(ui) = weak.upgrade() {
            check(&ui);
        }
    });
    let (weak, held) = (ui.as_weak(), waiting.clone());
    p.on_dlss5_add(move || {
        if let Some(ui) = weak.upgrade() {
            add(&ui, &held);
        }
    });
    let (weak, held) = (ui.as_weak(), waiting.clone());
    p.on_dlss5_answer(move |replace| {
        if let Some(ui) = weak.upgrade() {
            answer(&ui, &held, replace);
        }
    });
    let weak = ui.as_weak();
    p.on_dlss5_remove(move |index| {
        if let Some(ui) = weak.upgrade() {
            remove(&ui, index);
        }
    });
}

/// What is in place for each of the two files, in [`Kind::ALL`]'s order (the rows'): its bytes (none: no file there) and
/// whether it is the launcher's copy
type InPlace = Vec<(Kind, Option<u64>, bool)>;

fn in_place(folder: &Path) -> InPlace {
    Kind::ALL
        .into_iter()
        .map(|kind| {
            let bytes = dlss5::bytes(folder, kind);
            (
                kind,
                bytes,
                bytes.is_some() && dlss5::ours(folder, kind, false),
            )
        })
        .collect()
}

fn show_rows(ui: &MainWindow, rows: InPlace) {
    let rows = rows
        .into_iter()
        .map(|(kind, bytes, ours)| {
            let (title, about) = match kind {
                Kind::Nvidia => (
                    i18n::key("NVIDIA's file"),
                    i18n::key(
                        "nvngx_dlssnr.dll as NVIDIA ships it, signed by NVIDIA. Runs on RTX 50 GPUs.",
                    ),
                ),
                Kind::Fp16 => (
                    i18n::key("Community FP16 file"),
                    i18n::key(
                        "A converted copy of NVIDIA's file, made by the community, not by NVIDIA. Runs on RTX 20, 30, 40 and 50 GPUs. Longshot uses only the known copy.",
                    ),
                ),
            };
            let (line, value) = dlss5::line(kind, bytes, ours, dlss5::FP16_KNOWN);
            Dlss5Row {
                title: title.into(),
                about: about.into(),
                line: line.into(),
                value: value.into(),
                removable: bytes.is_some() && ours,
            }
        })
        .collect();
    ui.global::<Preferences>()
        .set_dlss5_rows(super::model(rows));
}

fn note(file: &str, text: &'static str, value: String, bad: bool) -> Dlss5Note {
    Dlss5Note {
        file: file.into(),
        text: text.into(),
        value: value.into(),
        bad,
    }
}

fn close_longshot_first() -> Dlss5Note {
    note(
        "",
        i18n::key("Close Longshot first: it uses these files while it runs."),
        String::new(),
        true,
    )
}

/// The section's state: whether it shows (this PC's GPUs), what the GPU runs, and the rows; an Add stopped half way before
/// is settled first ([`dlss5::settle`])
fn check(ui: &MainWindow) {
    let Some(folder) = dlss5::folder() else {
        return;
    };
    let weak = ui.as_weak();
    std::thread::spawn(move || {
        let gpus = dlss5::gpus();
        if let Err(error) = dlss5::settle(&folder) {
            eprintln!("Could not settle the DLSS 5 files' record: {error}");
        }
        let rows = in_place(&folder);
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let p = ui.global::<Preferences>();
            p.set_dlss5_shown(dlss5::offered(cfg!(windows), &gpus));
            p.set_dlss5_gpu_line(dlss5::gpu_line(dlss5::rtx_series(&gpus)).into());
            show_rows(&ui, rows);
        });
    });
}

/// The files the player picks (one, or both from one folder), each copied as what its content says it is
fn add(ui: &MainWindow, waiting: &Waiting) {
    let p = ui.global::<Preferences>();
    if dlss5::folder().is_none() || p.get_dlss5_busy() || crate::busy(ui) {
        return;
    }
    let language = ui.global::<Locale>().get_language();
    let Some(picked) = rfd::FileDialog::new()
        .set_title(i18n::lookup(
            &language,
            "Your DLSS 5 file (nvngx_dlssnr.dll)",
        ))
        .add_filter(i18n::lookup(&language, "DLSS 5 files"), &["dll"])
        .pick_files()
    else {
        return;
    };
    if !picked.is_empty() {
        run(
            ui,
            waiting,
            picked.into_iter().map(|file| (file, None)).collect(),
        );
    }
}

/// The player's answer about the files of theirs an Add stopped at: replaced (each as it was shown), or kept
fn answer(ui: &MainWindow, waiting: &Waiting, replace: bool) {
    let p = ui.global::<Preferences>();
    let held = std::mem::take(&mut *waiting.lock().unwrap_or_else(|e| e.into_inner()));
    p.set_dlss5_replace("".into());
    if held.is_empty() || p.get_dlss5_busy() {
        return;
    }
    if replace {
        run(
            ui,
            waiting,
            held.into_iter()
                .map(|(file, theirs)| (file, Some(theirs)))
                .collect(),
        );
        return;
    }
    let named = held.len() > 1;
    let kept = held
        .iter()
        .map(|(file, _)| {
            note(
                &shown(file, named),
                i18n::key("Kept the file put there by hand. Nothing was copied."),
                String::new(),
                false,
            )
        })
        .collect();
    p.set_dlss5_notes(super::model(kept));
}

/// files added one after the other, with the copy's progress, each replacing only the file of the player's it names (none:
/// none); then what each Add did, and the question about the files of the player's in the way
fn run(ui: &MainWindow, waiting: &Waiting, files: Vec<(PathBuf, Option<InTheWay>)>) {
    let Some(folder) = dlss5::folder() else {
        return;
    };
    let p = ui.global::<Preferences>();
    p.set_dlss5_busy(true);
    p.set_dlss5_percent(0);
    p.set_dlss5_notes(super::model(Vec::new()));
    let language = ui.global::<Locale>().get_language().to_string();
    let (weak, waiting) = (ui.as_weak(), waiting.clone());
    std::thread::spawn(move || {
        let named = files.len() > 1;
        let mut notes = Vec::new();
        let mut in_the_way = Vec::new();
        let mut locked = true;
        {
            let _work = dlss5::exclusive();
            if let Err(error) = &_work {
                locked = false;
                eprintln!("Could not lock the DLSS 5 files: {error}");
                notes.push(note(
                    "",
                    i18n::key("The file could not be copied: {0}"),
                    error.to_string(),
                    true,
                ));
            } else if let Err(error) = crate::players::ensure_idle() {
                eprintln!("DLSS 5 files: {error:#}");
                notes.push(close_longshot_first());
            } else {
                for (file, replace) in files {
                    match add_one(&weak, &folder, &file, replace.as_ref()) {
                        Ok(text) => {
                            notes.push(note(&shown(&file, named), text, String::new(), false))
                        }
                        Err(Failure::InTheWay(theirs)) => in_the_way.push((file, *theirs)),
                        Err(Failure::Refused(why)) => notes.push(note(
                            &shown(&file, named),
                            why.key(),
                            why.value(dlss5::FP16_KNOWN),
                            true,
                        )),
                        Err(Failure::Failed(error)) => {
                            eprintln!(
                                "Could not copy the DLSS 5 file {}: {error}",
                                crate::home::display(&file)
                            );
                            notes.push(note(
                                &shown(&file, named),
                                i18n::key("The file could not be copied: {0}"),
                                error.to_string(),
                                true,
                            ));
                        }
                    }
                }
            }
        }
        let question = replace_question(&language, &in_the_way);
        *waiting.lock().unwrap_or_else(|e| e.into_inner()) = in_the_way;
        let rows = locked.then(|| in_place(&folder));
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let p = ui.global::<Preferences>();
            p.set_dlss5_busy(false);
            p.set_dlss5_notes(super::model(notes));
            p.set_dlss5_replace(question.into());
            if let Some(rows) = rows {
                show_rows(&ui, rows);
            }
        });
    });
}

/// One file added: what it did, in the player's words (an English key)
fn add_one(
    weak: &slint::Weak<MainWindow>,
    folder: &Path,
    file: &Path,
    replace: Option<&InTheWay>,
) -> Result<&'static str, Failure> {
    // (the share copied, on the line once per whole percent)
    let mut shown = -1;
    let placed = dlss5::install(
        file,
        folder,
        dlss5::nvidia_file,
        |share| {
            let percent = (share * 100.0) as i32;
            if percent != shown {
                shown = percent;
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    ui.global::<Preferences>().set_dlss5_percent(percent);
                });
            }
        },
        dlss5::FP16_KNOWN,
        replace,
    )?;
    Ok(match (placed.how, placed.kind) {
        (How::Copied, Kind::Nvidia) => {
            i18n::key("Added as NVIDIA's file. The game uses it from its next start.")
        }
        (How::Copied, Kind::Fp16) => {
            i18n::key("Added as the community FP16 file. The game uses it from its next start.")
        }
        (How::AlreadyOurs, _) => i18n::key("This file is already in place."),
        (How::TakenOver, _) => i18n::key(
            "The same file was already in place, put there by hand. Nothing was copied: it is the launcher's copy now.",
        ),
    })
}

/// The dialog's text for the files of the player's in the way (empty: none, no dialog): what Replace does, then each file
/// with its size, a paragraph each
fn replace_question(language: &str, in_the_way: &[(PathBuf, InTheWay)]) -> String {
    if in_the_way.is_empty() {
        return String::new();
    }
    let mut text = i18n::lookup(
        language,
        "The files below are in the DLSS 5 folder, put there by hand, and differ from the files you picked. Replace deletes them and copies the files you picked in their place. The files you picked are not touched.",
    );
    for (_, theirs) in in_the_way {
        text.push_str(&format!(
            "\n\n{} ({})",
            crate::home::display(&theirs.path),
            crate::storage::format_size(theirs.bytes)
        ));
    }
    text
}

/// A picked file's name on a note, when several were picked; none for one
fn shown(file: &Path, named: bool) -> String {
    if named {
        crate::home::display(file)
    } else {
        String::new()
    }
}

/// Row index's file removed, once a full check says it is the launcher's copy
fn remove(ui: &MainWindow, index: i32) {
    let p = ui.global::<Preferences>();
    let (Some(folder), Some(&kind)) = (
        dlss5::folder(),
        usize::try_from(index).ok().and_then(|i| Kind::ALL.get(i)),
    ) else {
        return;
    };
    if p.get_dlss5_busy() || crate::busy(ui) {
        return;
    }
    p.set_dlss5_busy(true);
    // (no copy's progress to show)
    p.set_dlss5_percent(-1);
    p.set_dlss5_notes(super::model(Vec::new()));
    let weak = ui.as_weak();
    std::thread::spawn(move || {
        let mut locked = true;
        let said = {
            let _work = dlss5::exclusive();
            if let Err(error) = &_work {
                locked = false;
                eprintln!("Could not lock the DLSS 5 files: {error}");
                note(
                    "",
                    i18n::key("The file could not be removed: {0}"),
                    error.to_string(),
                    true,
                )
            } else if let Err(error) = crate::players::ensure_idle() {
                eprintln!("DLSS 5 files: {error:#}");
                close_longshot_first()
            } else {
                match dlss5::remove(&folder, kind) {
                    Ok(()) => note(
                        "",
                        i18n::key("Removed. The file you added it from is not touched."),
                        String::new(),
                        false,
                    ),
                    Err(Failure::Refused(why)) => {
                        note("", why.key(), why.value(dlss5::FP16_KNOWN), true)
                    }
                    Err(Failure::InTheWay(_)) => {
                        note("", dlss5::Refusal::NotOurs.key(), String::new(), true)
                    }
                    Err(Failure::Failed(error)) => {
                        eprintln!("Could not remove the DLSS 5 file: {error}");
                        note(
                            "",
                            i18n::key("The file could not be removed: {0}"),
                            error.to_string(),
                            true,
                        )
                    }
                }
            }
        };
        let rows = locked.then(|| in_place(&folder));
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let p = ui.global::<Preferences>();
            p.set_dlss5_busy(false);
            p.set_dlss5_notes(super::model(vec![said]));
            if let Some(rows) = rows {
                show_rows(&ui, rows);
            }
        });
    });
}
