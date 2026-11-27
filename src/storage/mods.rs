//! Ask the installed builder to review its own mod store. It knows every pin,
//! the retention period for old versions and which content blobs are shared.
use crate::{Game, MainWindow, Preferences, mods, players};
use anyhow::{Context, Result, ensure};
use serde::Deserialize;
use slint::ComponentHandle;
use std::{
    path::Path,
    sync::{Arc, Mutex},
};

#[derive(Deserialize, PartialEq, Eq)]
struct Review {
    dry_run: bool,
    removed: Vec<String>,
    freed: u64,
}

fn collect(root: &Path, dry_run: bool) -> Result<Review> {
    players::ensure_idle()?;
    let mut review: Review = serde_json::from_value(mods::store_gc(root, dry_run)?)
        .context("The builder returned an unreadable mod storage report.")?;
    ensure!(
        review.dry_run == dry_run,
        "The builder returned the wrong kind of mod storage report."
    );
    review.removed.sort();
    Ok(review)
}

enum Outcome {
    Confirm(String),
    Empty,
    Removed(String),
}

fn cleanup(root: &Path, dry_run: bool, preview: &Mutex<Option<Review>>) -> Result<Outcome> {
    let current = collect(root, true)?;
    let mut previous = preview.lock().unwrap_or_else(|e| e.into_inner());
    if dry_run {
        if current.removed.is_empty() && current.freed == 0 {
            *previous = None;
            return Ok(Outcome::Empty);
        }
        let summary = format!(
            "Remove {} unused mod versions and free {}? The builder keeps versions used by a profile.",
            current.removed.len(),
            super::format_size(current.freed)
        );
        *previous = Some(current);
        return Ok(Outcome::Confirm(summary));
    }
    ensure!(
        previous.take().as_ref() == Some(&current),
        "The mod store changed. Review unused files again."
    );
    drop(previous);
    let done = collect(root, false)?;
    Ok(Outcome::Removed(format!(
        "Removed {} unused versions and freed {}.",
        done.removed.len(),
        super::format_size(done.freed)
    )))
}

pub fn bind(ui: &MainWindow, root: &Path) {
    let preview = Arc::new(Mutex::new(None));
    let weak = ui.as_weak();
    let root = root.to_owned();
    ui.global::<Preferences>().on_unused_mods(move |dry_run| {
        let Some(ui) = weak.upgrade() else { return };
        let prefs = ui.global::<Preferences>();
        if crate::busy(&ui) || ui.global::<Game>().get_playing() {
            prefs.set_status("Finish running builds and close Longshot first.".into());
            return;
        }
        prefs.set_cleaning(true);
        prefs.set_gc_review("".into());
        prefs.set_status("Checking the mod store…".into());
        let weak = ui.as_weak();
        let root = root.clone();
        let preview = preview.clone();
        std::thread::spawn(move || {
            let result = cleanup(&root, dry_run, &preview);
            let _ = weak.upgrade_in_event_loop(move |ui| {
                let prefs = ui.global::<Preferences>();
                prefs.set_cleaning(false);
                match result {
                    Ok(Outcome::Confirm(summary)) => {
                        prefs.set_gc_review(summary.into());
                        prefs.set_status("".into());
                    }
                    Ok(Outcome::Empty) => prefs.set_status("No unused mod files to remove.".into()),
                    Ok(Outcome::Removed(summary)) => {
                        prefs.invoke_measure();
                        prefs.set_status(summary.into());
                    }
                    Err(e) => prefs.set_status(format!("{e:#}").into()),
                }
            });
        });
    });
}
