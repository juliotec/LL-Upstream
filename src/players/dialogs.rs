mod commit;
mod import;

use super::{
    add_tags, archive, background, current_dir, disk, imports, labels, nfc, patch_profile,
    profile_id, profiles, refresh, saves, slot_label, start_slot,
};
use crate::{MainWindow, Players, home};
use anyhow::{Context, Result, bail, ensure};
use serde_json::{Value, json};
use slint::{ComponentHandle, Model};
use std::{
    cell::RefCell,
    fs,
    path::{Path, PathBuf},
    rc::Rc,
};

enum Pending {
    Profile {
        mode: String,
        id: String,
    },
    DeleteProfile(String),
    ImportBrowser(Vec<imports::Location>),
    ImportSlot {
        source: PathBuf,
        dst: PathBuf,
        id: String,
    },
    Archive {
        zip: zip::ZipArchive<fs::File>,
        dst: PathBuf,
        whole: bool,
    },
    Slot {
        from: PathBuf,
        name: String,
        targets: Vec<(PathBuf, bool)>,
    },
    DeleteTags {
        dir: PathBuf,
        tags: Vec<nfc::SelectedTag>,
    },
}
type State = Rc<RefCell<Option<Pending>>>;
fn clear(ui: &MainWindow, state: &State) {
    state.borrow_mut().take();
    let p = ui.global::<Players>();
    p.set_prompt("".into());
    p.set_browsing_saves(false);
    p.set_import_sources(Default::default());
    p.set_question("".into());
    p.set_choices(Default::default());
    p.set_choice(0);
    p.set_entry("".into());
}
fn prompt(
    ui: &MainWindow,
    state: &State,
    title: &str,
    question: &str,
    choices: Vec<String>,
    next: Pending,
) {
    clear(ui, state);
    let p = ui.global::<Players>();
    let tr = |text: &str| {
        crate::i18n::lookup(ui.global::<crate::Locale>().get_language().as_str(), text)
    };
    p.set_asks_name(matches!(&next, Pending::Profile { .. }));
    let has_settings = match &next {
        Pending::Archive {
            dst, whole: false, ..
        } => disk::is_file(&dst.join("option.sav")),
        Pending::Archive {
            dst, whole: true, ..
        } => disk::dirs(dst)
            .is_ok_and(|dirs| dirs.iter().any(|p| disk::is_file(&p.join("option.sav")))),
        _ => false,
    };
    p.set_asks_settings(has_settings);
    p.set_keep_settings(true);
    p.set_question(tr(question).into());
    p.set_choices(labels(choices.iter().map(|s| tr(s))));
    p.set_choice(0);
    p.set_prompt(tr(title).into());
    *state.borrow_mut() = Some(next);
}
fn folder(path: &Path) -> Result<()> {
    fs::create_dir_all(path)?;
    open::that_detached(path)?;
    Ok(())
}
/// Everything needed to present an operation or its confirmation. The filesystem
/// is read again when the player confirms an operation that could replace data.
struct Dialogs<'a> {
    ui: &'a MainWindow,
    home: &'a Path,
    pending: &'a State,
}
impl Dialogs<'_> {
    fn action(&self, action: &str, id: &str) -> Result<()> {
        let players = self.ui.global::<Players>();
        ensure!(
            !players.get_busy(),
            "Wait for the current transfer to finish."
        );
        players.set_message("".into());
        match action {
            "switch" | "profile-mods" | "profile-saves" | "profile-options" => {
                let dir = self.profile(id)?;
                profiles::read(&dir)?;
                home::patch_settings(self.home, &json!({"currentProfile": id}))?;
                refresh(self.ui, self.home)?;
                let page = match action {
                    "profile-mods" => crate::Page::Mods,
                    "profile-saves" => crate::Page::Saves,
                    "profile-options" => crate::Page::Settings,
                    _ => crate::Page::Home,
                };
                self.ui.invoke_navigate(page);
            }
            "new" | "duplicate" => self.name_profile(action, id)?,
            "colour" => self.next_colour(id)?,
            "delete-profile" => {
                self.profile(id)?;
                self.ask(
                    "Delete profile",
                    "Its saves and settings will be backed up before the profile is removed.",
                    vec![],
                    Pending::DeleteProfile(id.into()),
                );
            }
            "start" => start_slot(self.ui, self.home, id)?,
            "continue" => {
                start_slot(self.ui, self.home, id)?;
                self.ui.invoke_navigate(crate::Page::Home);
                self.ui.global::<crate::Game>().invoke_play();
            }
            "new-save" => {
                let profile = current_dir(self.home)?;
                let dir = saves::slots_dir(&profile)?;
                let slot = (0..100)
                    .map(|n| format!("slot_{n:02}"))
                    .find(|s| {
                        fs::symlink_metadata(dir.join(s))
                            .is_err_and(|e| e.kind() == std::io::ErrorKind::NotFound)
                    })
                    .context("All save slots are occupied.")?;
                // The game opens its file list when the requested slot has no progress yet.
                patch_profile(&profile, |p| {
                    p.insert("start_slot".into(), json!(slot));
                })?;
                self.ui.invoke_navigate(crate::Page::Home);
            }
            "transfer-slot" => self.transfer_slot(id)?,
            "open-saves" => {
                let slots = saves::slots_dir(&current_dir(self.home)?)?;
                folder(slots.parent().context("Invalid save folder.")?)?;
            }
            "import-menu" => self.browse_imports(imports::detected(self.home)?),
            "import-file" => self.import_file()?,
            "import-folder" => self.import_folder()?,
            "export-slot" => self.export_archive(id)?,
            "duplicate-slot" => {
                let row = players
                    .get_slots()
                    .iter()
                    .find(|row| row.id == id)
                    .context("Choose a save slot.")?;
                let profile = current_dir(self.home)?;
                let (slot, title) = (row.id.to_string(), row.name.to_string());
                background(self.ui, self.home, move || {
                    let free = imports::duplicate(&profile, &slot, &title)?;
                    Ok(format!("Created {}.", slot_label(&free)))
                });
            }
            "drop-zip" => self.review_archive(Path::new(id))?,
            "open-tags" => folder(&nfc::folder(self.home)?)?,
            "tag-files" | "tag-folder" => self.add_tags(action == "tag-folder")?,
            "delete-tag" => self.ask_delete_tags(vec![id.to_owned()])?,
            "delete-tags" => self.ask_delete_tags(
                players
                    .get_tags()
                    .iter()
                    .filter(|tag| tag.selected)
                    .map(|tag| tag.name.to_string())
                    .collect(),
            )?,
            _ => bail!("Unknown player action."),
        }
        Ok(())
    }

    fn next_colour(&self, id: &str) -> Result<()> {
        patch_profile(&self.profile(id)?, |record| {
            let colour = record.get("color").and_then(Value::as_str);
            let palette = profiles::COLORS;
            let index = palette
                .iter()
                .position(|(_, hex)| Some(*hex) == colour)
                .unwrap_or(0);
            record.insert(
                "color".into(),
                json!(palette[(index + 1) % palette.len()].1),
            );
        })?;
        refresh(self.ui, self.home)
    }

    fn ask_delete_tags(&self, names: Vec<String>) -> Result<()> {
        ensure!(!names.is_empty(), "Choose a tag first.");
        let dir = nfc::folder(self.home)?;
        let tags = names
            .iter()
            .map(|name| nfc::SelectedTag::capture(&dir, name))
            .collect::<Result<_>>()?;
        self.ask(
            "Delete NFC tags",
            &format!(
                "Remove these files from the NFC folder?\n{}",
                names.join("\n")
            ),
            vec![],
            Pending::DeleteTags { dir, tags },
        );
        Ok(())
    }

    fn profile(&self, id: &str) -> Result<PathBuf> {
        ensure!(profiles::valid_id(id), "Unknown profile.");
        Ok(crate::home::data_dir(self.home)?.join("profiles").join(id))
    }

    fn ask(&self, title: &str, detail: &str, options: Vec<String>, operation: Pending) {
        prompt(self.ui, self.pending, title, detail, options, operation);
    }

    fn name_profile(&self, mode: &str, id: &str) -> Result<()> {
        let (title, initial) = if mode == "new" {
            ("New profile", String::new())
        } else {
            let record = profiles::read(&self.profile(id)?)?;
            let name = record["name"].as_str().unwrap_or(id);
            ("Copy profile", format!("{name} (copy)"))
        };
        self.ask(
            title,
            "Give this profile a name.",
            vec![],
            Pending::Profile {
                mode: mode.into(),
                id: id.into(),
            },
        );
        self.ui.global::<Players>().set_entry(initial.into());
        Ok(())
    }

    fn transfer_slot(&self, name: &str) -> Result<()> {
        ensure!(saves::slot_name(name), "Invalid slot.");
        let from = current_dir(self.home)?;
        let mut options = vec![];
        let mut targets = vec![];
        for profile in profiles::list(self.home)? {
            let id = &profile.id;
            if id == profile_id(&from) || profile.problem.is_some() {
                continue;
            }
            for move_slot in [false, true] {
                let verb = if move_slot { "Move" } else { "Copy" };
                options.push(format!("{verb} to {}", profile.name()));
                targets.push((self.profile(id)?, move_slot));
            }
        }
        ensure!(!targets.is_empty(), "Create another profile first.");
        self.ask(
            "Copy or move a save",
            "If the same slot is in use, the first free slot will be used.",
            options,
            Pending::Slot {
                from,
                name: name.into(),
                targets,
            },
        );
        Ok(())
    }

    fn review_archive(&self, file: &Path) -> Result<()> {
        let zip = archive::open(file)?;
        let whole = archive::is_whole(&zip)?;
        let slots = saves::slots_dir(&current_dir(self.home)?)?;
        let dst = if whole {
            slots
                .parent()
                .context("The save folder has no parent.")?
                .to_owned()
        } else {
            slots
        };
        let contents = if whole { "whole save folder" } else { "slots" };
        let question = format!(
            "Import {} into {}? The existing {contents} will be backed up first.",
            crate::home::display(file),
            crate::home::display(&dst)
        );
        self.ask(
            "Transfer saves",
            &question,
            vec![],
            Pending::Archive { zip, dst, whole },
        );
        Ok(())
    }

    fn add_tags(&self, folder: bool) -> Result<()> {
        let files = if folder {
            rfd::FileDialog::new().pick_folder().into_iter().collect()
        } else {
            rfd::FileDialog::new()
                .add_filter("NFC tag dumps", &["bin", "nfc"])
                .pick_files()
                .unwrap_or_default()
        };
        add_tags(self.ui, self.home, files)
    }
}

pub(super) fn bind(ui: &MainWindow, home: &Path) {
    let players = ui.global::<Players>();
    let state = Rc::new(RefCell::new(None));
    let weak = ui.as_weak();
    let h = home.to_owned();
    let pending = state.clone();
    players.on_action(move |k, id| {
        if let Some(ui) = weak.upgrade()
            && let Err(e) = (Dialogs {
                ui: &ui,
                home: &h,
                pending: &pending,
            })
            .action(k.as_str(), id.as_str())
        {
            ui.global::<Players>().set_message(format!("{e:#}").into());
        }
    });
    let weak = ui.as_weak();
    let h = home.to_owned();
    let pending = state.clone();
    players.on_confirm(move || {
        if let Some(ui) = weak.upgrade()
            && let Err(e) = commit::confirm(&ui, &h, &pending)
        {
            ui.global::<Players>().set_message(format!("{e:#}").into());
        }
    });
    let weak = ui.as_weak();
    let h = home.to_owned();
    let pending = state.clone();
    players.on_import_slot(move |location, slot| {
        if let Some(ui) = weak.upgrade()
            && let Err(err) = (Dialogs {
                ui: &ui,
                home: &h,
                pending: &pending,
            })
            .choose_import_slot(location, slot)
        {
            ui.global::<Players>()
                .set_message(format!("{err:#}").into());
        }
    });
    let weak = ui.as_weak();
    players.on_cancel(move || {
        if let Some(ui) = weak.upgrade() {
            clear(&ui, &state);
        }
    });
}
