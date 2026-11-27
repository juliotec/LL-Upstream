//! Local launcher diagnostics. Events use fixed wording so file paths, URLs, player
//! names and game or save contents never enter a generated diagnostic report.
use anyhow::{Context, Result, ensure};
use std::{
    collections::VecDeque,
    fs::{self, File, OpenOptions},
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    sync::Mutex,
};

struct Log {
    path: PathBuf,
    file: Option<File>,
    recent: VecDeque<String>,
}

static LOG: Mutex<Option<Log>> = Mutex::new(None);
const FILE_LIMIT: u64 = 1 << 20;

fn open(path: &Path, rotate: bool) -> Result<File> {
    let parent = path.parent().context("Log folder is unavailable")?;
    fs::create_dir_all(parent)?;
    ensure!(
        !crate::storage::linked_location(parent)? && !crate::storage::linked_location(path)?,
        "Linked log location"
    );
    if rotate && path.try_exists()? {
        let previous = path.with_extension("1.log");
        if previous.is_file() {
            fs::remove_file(previous.as_path())?;
        }
        match fs::rename(path, previous) {
            Err(e) if e.kind() != ErrorKind::NotFound => return Err(e.into()),
            _ => {}
        }
    }
    Ok(OpenOptions::new().create(true).append(true).open(path)?)
}

pub fn start(root: &Path, play: bool) {
    let path = root
        .join("logs")
        .join(if play { "play.log" } else { "launcher.log" });
    let rotate = !play || path.metadata().is_ok_and(|m| m.len() >= FILE_LIMIT);
    let file = open(&path, rotate).ok();
    let mut state = LOG.lock().unwrap_or_else(|e| e.into_inner());
    *state = Some(Log {
        path,
        file,
        recent: VecDeque::new(),
    });
    drop(state);
    event(if play {
        "Play shortcut started"
    } else {
        "Launcher started"
    });
}

pub fn event(message: &'static str) {
    let mut state = LOG.lock().unwrap_or_else(|e| e.into_inner());
    let Some(log) = state.as_mut() else { return };
    let line = format!("{}  {message}", chrono::Utc::now().to_rfc3339());
    if log.recent.len() == 500 {
        log.recent.pop_front();
    }
    log.recent.push_back(line.clone());
    if let Some(file) = &mut log.file {
        if file.metadata().is_ok_and(|m| m.len() >= FILE_LIMIT) {
            log.file.take();
            log.file = open(&log.path, true).ok();
        }
        if let Some(file) = &mut log.file {
            let _ = writeln!(file, "{line}");
        }
    }
}

pub fn close_file() {
    if let Some(log) = LOG.lock().unwrap_or_else(|e| e.into_inner()).as_mut() {
        log.file.take();
    }
}

pub fn save(destination: &Path) -> Result<()> {
    let mut out =
        tempfile::NamedTempFile::new_in(destination.parent().context("Choose a report folder")?)?;
    writeln!(
        out,
        "Longshot Launcher {}\n{} {}\n",
        crate::update::own_tag(),
        std::env::consts::OS,
        std::env::consts::ARCH
    )?;
    let state = LOG.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(log) = state.as_ref() {
        for line in &log.recent {
            writeln!(out, "{line}")?;
        }
    }
    out.as_file().sync_all()?;
    out.persist(destination)?;
    Ok(())
}
