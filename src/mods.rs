//! The selected game’s builder owns the mod store, load order and code grants.
use crate::UiMutexExt as _;
mod actions;
mod builder;
pub(crate) mod local;
mod stable;
pub(crate) mod trust;
pub(crate) use stable::migrate as migrate_stable;
#[cfg(test)]
mod tests;
mod view;

use crate::{MainWindow, Mods};
use anyhow::{Context, Result, ensure};
pub use builder::store_gc;
use serde_json::Value;
use slint::ComponentHandle;
use std::{
    ffi::OsString,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

#[derive(Clone, Copy, Default, PartialEq)]
enum Section {
    #[default]
    Installed,
    Discover,
    Manual,
    Sources,
    Emulators,
}

impl Section {
    fn name(self) -> &'static str {
        match self {
            Self::Installed => "installed",
            Self::Discover => "discover",
            Self::Manual => "manual",
            Self::Sources => "sources",
            Self::Emulators => "emulators",
        }
    }
}

#[derive(Default)]
struct Screen {
    deadline: Option<std::time::Instant>,
    section: Section,
    basic: bool,
    pending_permissions: Vec<trust::Grant>,
    rows: Vec<Value>,
    catalog: Vec<Value>,
    categories: Vec<String>,
    category: usize,
    selection: usize,
    query: String,
    review: Option<Review>,
    details: String,
    showing_details: bool,
    dependency: Option<Dependency>,
    message: String,
    identity: Option<(PathBuf, PathBuf)>,
}

enum Dependency {
    Enable(String),
    Find(String),
}

struct Review {
    text: String,
    args: Vec<OsString>,
    preview: Option<(Vec<OsString>, Value)>,
}

impl Screen {
    fn run(&self, root: &Path, words: &[OsString]) -> Result<(Value, String)> {
        builder::run(root, words, self.identity.as_ref(), self.deadline)
    }

    // Catch while the caller still holds the lock. A panicking operation
    // discards its partial screen state without poisoning the UI mutex.
    fn run_job(&mut self, action: impl FnOnce(&mut Self) -> Result<()>) {
        self.deadline = Some(std::time::Instant::now() + builder::ACTION_TIMEOUT);
        match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| action(self))) {
            Ok(Ok(())) => {}
            Ok(Err(error)) => self.message = format!("{error:#}"),
            Err(_) => {
                *self = Self::default();
                self.message =
                    "The mod operation stopped unexpectedly. Open Mods to try again.".into();
                crate::diagnostics::event("Mod worker panicked");
            }
        }
        self.deadline = None;
    }
}

pub(crate) fn review_paragraphs(text: slint::SharedString) -> slint::ModelRc<slint::SharedString> {
    let sections: Vec<_> = text
        .split("\n\n")
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(slint::SharedString::from)
        .collect();
    slint::ModelRc::from(sections.as_slice())
}

pub fn bind(ui: &MainWindow, root: &Path) {
    ui.global::<Mods>().on_review_paragraphs(review_paragraphs);
    let screen = Arc::new(Mutex::new(Screen::default()));
    let weak = ui.as_weak();
    let filtering = Arc::clone(&screen);
    ui.global::<Mods>().on_filter_category(move |index| {
        let Some(ui) = weak.upgrade() else { return };
        if ui.global::<Mods>().get_busy() {
            return;
        }
        if let Ok(i) = usize::try_from(index) {
            let mut state = filtering.guard();
            state.category(i);
            state.paint(&ui);
        }
    });
    let weak = ui.as_weak();
    let selection = Arc::clone(&screen);
    ui.global::<Mods>().on_select(move |index| {
        let Some(ui) = weak.upgrade() else { return };
        if ui.global::<Mods>().get_busy() {
            return;
        }
        let mut screen = selection.guard();
        if let Ok(i) = usize::try_from(index)
            && i < screen.rows.len()
        {
            screen.select(i);
            screen.paint(&ui);
        }
    });
    let root = root.to_owned();
    let weak = ui.as_weak();
    ui.global::<Mods>().on_action(move |action| {
        let Some(ui) = weak.upgrade() else { return };
        let m = ui.global::<Mods>();
        if m.get_busy() || crate::busy(&ui) {
            return;
        }
        let entry = m.get_entry().to_string();
        let selected = match action.as_str() {
            "file" => rfd::FileDialog::new()
                .add_filter("Longshot mods", &["lsmod", "zip"])
                .pick_file(),
            "other-install" => rfd::FileDialog::new()
                .set_title("Choose a mod folder from the other installation")
                .pick_folder(),
            "directory" => rfd::FileDialog::new().pick_folder(),
            "drop" => Some(PathBuf::from(&entry)),
            _ => None,
        };
        let action = if matches!(
            action.as_str(),
            "file" | "directory" | "drop" | "other-install"
        ) {
            if selected.is_none() {
                return;
            }
            "add".to_owned()
        } else {
            action.to_string()
        };
        if matches!(
            action.as_str(),
            "manual" | "installed" | "sources" | "discover" | "add"
        ) {
            m.set_entry("".into());
        }
        m.set_busy(true);
        let root = root.clone();
        let weak = ui.as_weak();
        let screen = Arc::clone(&screen);
        std::thread::spawn(move || {
            {
                let mut state = screen.guard();
                state.run_job(|state| state.act(&root, &action, &entry, selected));
            }
            let _ = weak.upgrade_in_event_loop(move |ui| {
                ui.global::<Mods>().set_busy(false);
                screen.lock().unwrap_or_else(|e| e.into_inner()).paint(&ui);
            });
        });
    });
}

pub fn refresh(ui: &MainWindow) {
    ui.global::<Mods>().invoke_action("installed".into());
}

pub fn back(ui: &MainWindow) -> bool {
    let mods = ui.global::<Mods>();
    let action = if !mods.get_review().is_empty() {
        "cancel"
    } else if mods.get_showing_details() {
        "back-to-list"
    } else if mods.get_section().as_str() != "installed" {
        "installed"
    } else {
        return false;
    };
    mods.invoke_action(action.into());
    true
}

pub fn dropped(ui: &MainWindow, path: &Path) -> Result<()> {
    ensure!(!crate::busy(ui), "Finish the current operation first.");
    let mods = ui.global::<Mods>();
    mods.set_entry(
        path.to_str()
            .context("This mod's path cannot be displayed.")?
            .into(),
    );
    mods.invoke_action("drop".into());
    Ok(())
}
