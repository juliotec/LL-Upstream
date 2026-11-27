use super::{Dialogs, Pending};
use crate::{
    ImportSource, Players, home,
    players::{
        archive, background, caption, current_dir, disk, imports, labels, model, saves, slot_number,
    },
};
use anyhow::{Context, Result, ensure};
use slint::ComponentHandle;
use std::path::{Path, PathBuf};

impl Dialogs<'_> {
    fn tr(&self, text: &str) -> String {
        crate::i18n::lookup(
            self.ui.global::<crate::Locale>().get_language().as_str(),
            text,
        )
    }
    fn slot_title(&self, id: &str) -> String {
        self.tr("Slot {0}")
            .replace("{0}", &slot_number(id).to_string())
    }

    pub(super) fn browse_imports(&self, sources: Vec<imports::Location>) {
        let rows = sources
            .iter()
            .map(|source| ImportSource {
                label: self.tr(&source.label).into(),
                path: home::display(&source.path).into(),
                slots: labels(source.slots.iter().map(|slot| {
                    let name = slot.file_name().unwrap_or_default().to_string_lossy();
                    let location = caption::read(&slot.join("caption.sav"), None)
                        .map(|c| c.location)
                        .unwrap_or_default();
                    if location.is_empty() {
                        self.slot_title(&name)
                    } else {
                        format!("{} · {location}", self.slot_title(&name))
                    }
                })),
            })
            .collect();
        self.ask(
            "Import saves",
            "Choose a slot from a detected location, or open a local save.",
            vec![],
            Pending::ImportBrowser(sources),
        );
        let p = self.ui.global::<Players>();
        p.set_import_sources(model(rows));
        p.set_browsing_saves(true);
    }

    pub(super) fn choose_import_slot(&self, location: i32, slot: i32) -> Result<()> {
        let source = {
            let state = self.pending.borrow();
            let Some(Pending::ImportBrowser(locations)) = state.as_ref() else {
                anyhow::bail!("Choose a save location first.")
            };
            locations
                .get(usize::try_from(location)?)
                .and_then(|location| location.slots.get(usize::try_from(slot).ok()?))
                .context("Choose a save slot.")?
                .clone()
        };
        self.review_slot(source)
    }

    pub(super) fn import_file(&self) -> Result<()> {
        let Some(file) = rfd::FileDialog::new()
            .set_title(self.tr("Open save file"))
            .add_filter("Save archive or progress.sav", &["zip", "sav"])
            .pick_file()
        else {
            return Ok(());
        };
        if file
            .extension()
            .is_some_and(|suffix| suffix.eq_ignore_ascii_case("zip"))
        {
            self.review_archive(&file)
        } else {
            ensure!(
                file.file_name().is_some_and(|name| name == "progress.sav"),
                "Choose progress.sav or a ZIP archive."
            );
            self.review_slot(file)
        }
    }

    pub(super) fn import_folder(&self) -> Result<()> {
        let Some(dir) = rfd::FileDialog::new()
            .set_title(self.tr("Open save folder"))
            .pick_folder()
        else {
            return Ok(());
        };
        if dir
            .file_name()
            .and_then(|n| n.to_str())
            .is_some_and(saves::slot_name)
        {
            return self.review_slot(dir);
        }
        let locations = saves::find(&dir)?
            .into_iter()
            .map(|path| imports::Location::from_folder("Selected folder", path))
            .collect::<Result<Vec<_>>>()?;
        ensure!(!locations.is_empty(), "This folder contains no save slots.");
        self.browse_imports(locations);
        Ok(())
    }

    fn review_slot(&self, source: PathBuf) -> Result<()> {
        saves::pending(&source)?;
        let profile = current_dir(self.home)?;
        let dst = saves::slots_dir(&profile)?;
        let name = if disk::is_file(&source) {
            saves::validate_file(&source)?;
            source.parent().and_then(Path::file_name)
        } else {
            saves::validate_slot(&source)?;
            source.file_name()
        };
        let id = match name
            .and_then(|n| n.to_str())
            .filter(|n| imports::playable_slot(n))
        {
            Some(name) => name.to_owned(),
            None => imports::free_slot(&dst)?,
        };
        disk::disjoint(&source, &dst.join(&id))?;
        let detail = format!(
            "Import {} into {} for {}? Any existing progress in this slot will be backed up before it is replaced.",
            home::display(&source),
            self.slot_title(&id),
            self.ui.global::<Players>().get_profile_name()
        );
        self.ask(
            "Import save slot",
            &detail,
            vec![],
            Pending::ImportSlot { source, dst, id },
        );
        Ok(())
    }

    pub(super) fn export_archive(&self, id: &str) -> Result<()> {
        ensure!(saves::slot_name(id), "Choose a save slot.");
        let root = saves::slots_dir(&current_dir(self.home)?)?;
        let slot = root.join(id);
        saves::validate_slot(&slot)?;
        let Some(file) = rfd::FileDialog::new()
            .set_title(self.tr("Export save slot"))
            .set_file_name(format!("Longshot {id}.zip"))
            .add_filter("ZIP archive", &["zip"])
            .save_file()
        else {
            return Ok(());
        };
        disk::disjoint(&root, &file)?;
        background(self.ui, self.home, move || {
            archive::export(&slot, &file)?;
            Ok(format!("Save exported to {}.", home::display(&file)))
        });
        Ok(())
    }
}
