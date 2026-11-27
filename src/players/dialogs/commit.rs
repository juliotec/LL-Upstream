use super::{Pending, State, clear};
use crate::{
    MainWindow, Players, home,
    players::{
        archive, background, disk, imports, nfc, profiles, refresh, refresh_tags, saves, slot_label,
    },
};
use anyhow::{Context, Result, ensure};
use serde_json::json;
use slint::ComponentHandle;
use std::{fs, path::Path};

pub(super) fn confirm(ui: &MainWindow, home: &Path, state: &State) -> Result<()> {
    let p = ui.global::<Players>();
    let choice = p.get_choice().max(0) as usize;
    let name = p.get_entry().to_string();
    let keep = p.get_asks_settings() && p.get_keep_settings();
    let next = state
        .borrow_mut()
        .take()
        .context("Choose an action first.")?;
    clear(ui, state);
    match next {
        Pending::Profile { mode, id } => {
            let onboarding = profiles::list(home)?.is_empty();
            profiles::new_or_rename(home, &mode, &id, &name)?;
            if onboarding {
                let profile = profiles::current_dir(home)?;
                home::patch_settings(
                    home,
                    &json!({"currentProfile":profile.file_name().and_then(|name| name.to_str())}),
                )?;
            }
            refresh(ui, home)?;
            if onboarding {
                ui.invoke_navigate(crate::Page::Home);
            }
        }
        Pending::ImportBrowser(_) => anyhow::bail!("Choose a save slot."),
        Pending::ImportSlot { source, dst, id } => {
            let root = home.to_owned();
            background(ui, home, move || {
                imports::import_slot(&source, &dst, &id, &root)?;
                Ok(format!("Imported {}.", slot_label(&id)))
            });
        }
        Pending::DeleteProfile(id) => {
            let (kept, remaining) = profiles::remove(home, &id)?;
            refresh(ui, home)?;
            ui.invoke_navigate(if remaining == 1 {
                crate::Page::Home
            } else {
                crate::Page::ChoosePlayer
            });
            p.set_message(
                format!(
                    "The profile was backed up in {}.",
                    crate::home::display(&kept)
                )
                .into(),
            );
        }
        Pending::Archive { zip, dst, whole } => {
            let home_dir = home.to_owned();
            background(ui, home, move || {
                let unpacked = archive::unpack(zip)?;
                ensure!(
                    whole == unpacked.whole,
                    "The archive changed. Choose it again."
                );
                if whole {
                    archive::install_whole(&unpacked.source, &dst, &home_dir, keep)?;
                } else {
                    saves::merge_slots(&unpacked.source, &dst, &home_dir, keep)?;
                }
                Ok(format!("Saves copied to {}.", crate::home::display(&dst)))
            });
        }
        Pending::Slot {
            from,
            name,
            targets,
        } => {
            let (to, mv) = targets
                .get(choice)
                .context("Choose a destination profile.")?;
            let source = saves::slots_dir(&from)?.join(&name);
            let dst = saves::slots_dir(to)?;
            crate::players::running::saves_idle(&[&source, &dst])?;
            let _source_lock = saves::SaveLock::acquire(&source)?;
            let _destination_edit = saves::SaveEdit::begin(&dst)?;
            fs::create_dir_all(&dst)?;
            let source_profile = profiles::read(&from)?;
            let alias = profiles::save_name(&source_profile, &name).unwrap_or_default();
            let free = imports::free_slot(&dst)?;
            let stage = tempfile::tempdir_in(&dst)?;
            disk::copy_tree(&source, stage.path())?;
            crate::players::running::saves_idle(&[&source, &dst])?;
            fs::rename(stage.path(), dst.join(&free))?;
            profiles::set_save_name(to, &free, alias)?;
            if *mv {
                crate::players::running::saves_idle(&[&source, &dst])?;
                disk::remove_after_copy(&source, &dst.join(&free))?;
                profiles::set_save_name(&from, &name, "")?;
            }
            refresh(ui, home)?;
            p.set_message(format!("Saved as {}.", slot_label(&free)).into());
        }
        Pending::DeleteTags { dir, tags } => {
            nfc::delete(&dir, &tags)?;
            refresh_tags(ui, home)?;
        }
    }
    Ok(())
}
