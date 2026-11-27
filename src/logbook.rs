//! Local session logs from the launcher and selected build. Logs are only read on this page.
mod entries;
mod transcript;
use crate::{Game, LogEntry, Logbook, MainWindow, game, players};
use anyhow::{Context, Result, ensure};
use slint::{ComponentHandle, Model, ModelRc, VecModel};
use std::{
    fs::{self, File},
    io::{Read, Seek, SeekFrom},
    path::{Path, PathBuf},
};
pub(crate) use transcript::{BuildLog, Transcript};

fn visible_build_log(ui: &MainWindow) -> BuildLog {
    if ui.get_page() == crate::Page::Launch {
        BuildLog::Launch
    } else {
        BuildLog::Build
    }
}

const SESSIONS: [(&str, &str); 6] = [
    ("Launcher · current session", "launcher.log"),
    ("Build · latest session", "last-build.log"),
    ("Launch · latest session", "last-launch.log"),
    ("Launcher · previous session", "launcher.1.log"),
    ("Play shortcut", "play.log"),
    ("Play shortcut · previous session", "play.1.log"),
];

fn add_files(
    rows: &mut Vec<(&'static str, PathBuf)>,
    folder: &Path,
    names: &[(&'static str, &str)],
) {
    if !players::is_dir(folder) {
        return;
    }
    rows.extend(names.iter().filter_map(|&(label, filename)| {
        let path = folder.join(filename);
        players::is_file(&path).then_some((label, path))
    }));
}

fn available(root: &Path) -> Vec<(&'static str, PathBuf)> {
    let mut rows = Vec::new();
    if let Ok(build) = game::selected_build(root) {
        add_files(
            &mut rows,
            &build.join("logs"),
            &[
                ("Game · latest session", "longshot_log.txt"),
                ("Game · previous session", "longshot_log.1.txt"),
                ("Game · update", "longshot_update.log"),
            ],
        );
    }
    add_files(&mut rows, &root.join("logs"), &SESSIONS);
    rows
}

const PREVIEW_BYTES: u64 = 1 << 20;

fn read_session(path: &Path) -> Result<(ModelRc<LogEntry>, bool)> {
    ensure!(
        path.parent().is_some_and(players::is_dir) && players::is_file(path),
        "Choose a regular session log."
    );
    let mut file = File::open(path)?;
    let len = file.metadata()?.len();
    let start = len.saturating_sub(PREVIEW_BYTES);
    let mut partial = false;
    if start > 0 {
        file.seek(SeekFrom::Start(start - 1))?;
        let mut previous = [0u8];
        file.read_exact(&mut previous)?;
        partial = previous[0] != b'\n';
    }
    let mut bytes = Vec::new();
    file.take(PREVIEW_BYTES).read_to_end(&mut bytes)?;
    let session = match path.file_name().and_then(|s| s.to_str()) {
        Some("last-build.log" | "last-launch.log") => "build",
        Some("launcher.log" | "launcher.1.log" | "play.log" | "play.1.log") => "launcher",
        _ => "game",
    };
    let mut reader = entries::Reader::new(session);
    let rows = String::from_utf8_lossy(&bytes)
        .lines()
        .skip(usize::from(partial))
        .filter_map(|line| reader.line(line))
        .collect::<Vec<_>>();
    Ok((ModelRc::new(VecModel::from(rows)), start != 0))
}

fn raw(ui: &MainWindow, path: &Path) -> slint::SharedString {
    let result = || -> Result<String> {
        ensure!(
            path.parent().is_some_and(players::is_dir) && players::is_file(path),
            "There is no local session log to copy."
        );
        Ok(String::from_utf8_lossy(&fs::read(path)?).into_owned())
    };
    match result() {
        Ok(text) => text.into(),
        Err(e) => {
            ui.set_notice(e.to_string().into());
            Default::default()
        }
    }
}

fn selected(ui: &MainWindow, home: &Path) -> Option<PathBuf> {
    let book = ui.global::<Logbook>();
    let name = book
        .get_sessions()
        .row_data(usize::try_from(book.get_selected()).ok()?)?;
    available(home)
        .into_iter()
        .find(|(label, _)| *label == name.as_str())
        .map(|(_, path)| path)
}
pub fn refresh(ui: &MainWindow, root: &Path) -> Result<()> {
    let book = ui.global::<Logbook>();
    let sessions = available(root);
    let labels: Vec<_> = sessions.iter().map(|(label, _)| (*label).into()).collect();
    book.set_sessions(ModelRc::new(VecModel::from(labels)));
    let index = usize::try_from(book.get_selected())
        .unwrap_or(0)
        .min(sessions.len().saturating_sub(1));
    if let Some((_, path)) = sessions.get(index) {
        book.set_selected(index as i32);
        let (rows, tail) = read_session(path)?;
        book.set_rows(rows);
        book.set_tail(tail);
    } else {
        book.set_selected(-1);
        book.set_rows(Default::default());
        book.set_tail(false);
    }
    Ok(())
}

pub fn bind(ui: &MainWindow, root: &Path) {
    let book = ui.global::<Logbook>();
    let (weak, path) = (ui.as_weak(), root.to_owned());
    book.on_reload(move || {
        if let Some(ui) = weak.upgrade()
            && let Err(error) = refresh(&ui, &path)
        {
            ui.set_notice(error.to_string().into());
        }
    });
    let weak = ui.as_weak();
    book.on_select(move |index| {
        if let Some(ui) = weak.upgrade() {
            let book = ui.global::<Logbook>();
            book.set_selected(index);
            book.invoke_reload();
        }
    });
    let (weak, folder) = (ui.as_weak(), root.join("logs"));
    book.on_folder(move || {
        if let Err(e) = open::that_detached(&folder)
            && let Some(ui) = weak.upgrade()
        {
            ui.set_notice(e.to_string().into());
        }
    });
    let (weak, home) = (ui.as_weak(), root.to_owned());
    book.on_raw(move || {
        weak.upgrade()
            .and_then(|ui| selected(&ui, &home).map(|p| raw(&ui, &p)))
            .unwrap_or_default()
    });
    let (weak, home) = (ui.as_weak(), root.to_owned());
    book.on_save(move || {
        let Some(ui) = weak.upgrade() else { return };
        let Some(source) = selected(&ui, &home) else {
            return;
        };
        let Some(path) = rfd::FileDialog::new()
            .set_file_name("longshot-log.txt")
            .save_file()
        else {
            return;
        };
        let save = || -> Result<()> {
            let mut input = File::open(&source)?;
            let mut output = tempfile::NamedTempFile::new_in(
                path.parent().context("Choose a destination folder")?,
            )?;
            std::io::copy(&mut input, &mut output)?;
            output.persist(path)?;
            Ok(())
        };
        if let Err(e) = save() {
            ui.set_notice(format!("Saving the log: {e}").into());
        }
    });
    book.on_filter(|rows, search, level| {
        if search.is_empty() && level == "DEBUG" {
            return rows;
        }
        let needle = search.to_lowercase();
        let filtered = rows
            .iter()
            .filter(|row| {
                (match level.as_str() {
                    "ERROR" => row.level == "ERROR",
                    "WARN" => matches!(row.level.as_str(), "WARN" | "ERROR"),
                    "INFO" => row.level != "DEBUG",
                    _ => true,
                }) && [&row.time, &row.level, &row.source, &row.message]
                    .iter()
                    .any(|part| part.to_lowercase().contains(&needle))
            })
            .collect::<Vec<_>>();
        ModelRc::new(VecModel::from(filtered))
    });
    let live = ui.global::<Game>();
    let (weak, logs) = (ui.as_weak(), root.join("logs"));
    let previous = std::cell::Cell::new(None);
    live.on_refresh_log(move || {
        let Some(ui) = weak.upgrade() else { return };
        let kind = visible_build_log(&ui);
        let path = logs.join(kind.filename());
        if !path.parent().is_some_and(players::is_dir) || !players::is_file(&path) {
            return;
        }
        let stamp = path
            .metadata()
            .ok()
            .map(|m| (kind, m.len(), m.modified().ok()));
        let game = ui.global::<Game>();
        if stamp != previous.get() || game.get_log_rows().row_count() == 0 {
            if let Ok((rows, tail)) = read_session(&path) {
                game.set_log_rows(rows);
                game.set_log_tail(tail);
            }
            previous.set(stamp);
        }
    });
    let (weak, logs) = (ui.as_weak(), root.join("logs"));
    live.on_raw_log(move || {
        weak.upgrade()
            .map(|ui| raw(&ui, &logs.join(visible_build_log(&ui).filename())))
            .unwrap_or_default()
    });
}
