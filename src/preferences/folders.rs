//! One change-folder flow for the builder, Settings and Storage.
use super::refresh;
use crate::{FolderStage, Game, MainWindow, Preferences, folders::Change, i18n};
use slint::ComponentHandle;
use std::{
    cell::RefCell,
    path::Path,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
};

pub(super) fn bind(ui: &MainWindow, home: &Path) {
    let prefs = ui.global::<Preferences>();
    let pending = Rc::new(RefCell::new(None::<Change>));
    let cancel = Arc::new(AtomicBool::new(false));

    let (weak, root, prepared) = (ui.as_weak(), home.to_owned(), pending.clone());
    prefs.on_folder(move |key, reset| {
        let Some(ui) = weak.upgrade() else { return };
        let p = ui.global::<Preferences>();
        let game = ui.global::<Game>();
        if p.get_changing_folder()
            || p.get_cleaning()
            || game.get_busy()
            || game.get_playing()
            || game.get_tools_busy()
            || ui.global::<crate::Players>().get_busy()
            || ui.global::<crate::Mods>().get_busy()
            || ui.global::<crate::Updates>().get_applying()
        {
            ui.set_notice(
                "Wait for the current operation to finish before changing folders.".into(),
            );
            return;
        }

        let (label, current) = match key.as_str() {
            "work" => ("Choose the cache folder", p.get_work_folder()),
            "dataFolder" => ("Choose the Longshot data folder", p.get_data_folder()),
            _ => return,
        };
        let selected = if reset {
            None
        } else {
            let mut picker = rfd::FileDialog::new().set_title(i18n::lookup(
                ui.global::<crate::Locale>().get_language().as_str(),
                label,
            ));
            if let Some(dir) = Path::new(current.as_str()).ancestors().find(|d| d.is_dir()) {
                picker = picker.set_directory(dir);
            }
            let Some(path) = picker.pick_folder() else {
                return;
            };
            Some(path)
        };
        match Change::prepare(&root, key.as_str(), selected.as_deref()) {
            Ok(change) => {
                if change.from == change.to && !change.has_data() && !reset {
                    return;
                }
                p.set_moving_data_folder(key == "dataFolder");
                p.set_folder_from(change.from.to_string_lossy().as_ref().into());
                p.set_folder_to(change.to.to_string_lossy().as_ref().into());
                let ask = change.has_data();
                *prepared.borrow_mut() = Some(change);
                p.set_folder_stage(FolderStage::Review);
                if !ask {
                    p.invoke_apply_folder(false);
                }
            }
            Err(error) => ui.set_notice(format!("{error:#}").into()),
        }
    });

    let (weak, prepared, token) = (ui.as_weak(), pending.clone(), cancel.clone());
    prefs.on_cancel_folder(move || {
        let Some(ui) = weak.upgrade() else { return };
        let p = ui.global::<Preferences>();
        match p.get_folder_stage() {
            FolderStage::Review => {
                prepared.borrow_mut().take();
                p.set_folder_stage(FolderStage::Idle);
                ui.global::<Game>().invoke_schedule_check();
            }
            FolderStage::Applying => {
                token.store(true, Ordering::Release);
                p.set_folder_stage(FolderStage::Cancelling);
            }
            _ => {}
        }
    });

    let (weak, root) = (ui.as_weak(), home.to_owned());
    prefs.on_apply_folder(move |move_existing| {
        let Some(ui) = weak.upgrade() else { return };
        let Some(change) = pending.borrow_mut().take() else {
            return;
        };
        let p = ui.global::<Preferences>();
        p.set_folder_stage(FolderStage::Applying);
        cancel.store(false, Ordering::Release);
        let (weak, root, token) = (ui.as_weak(), root.clone(), cancel.clone());
        let moving_data = ui.global::<Preferences>().get_moving_data_folder();
        thread::spawn(move || {
            let result = change.apply(move_existing, &token);
            let _ = weak.upgrade_in_event_loop(move |ui| {
                let prefs = ui.global::<Preferences>();
                prefs.set_folder_stage(FolderStage::Idle);
                match result {
                    Ok(message) => {
                        let refresh_views = || {
                            refresh(&ui, &root)?;
                            if moving_data {
                                crate::players::refresh(&ui, &root)?;
                                crate::shell::refresh(&ui);
                            }
                            anyhow::Ok(())
                        };
                        if let Err(e) = refresh_views() {
                            ui.set_notice(format!("{e:#}").into());
                            return;
                        }
                        ui.global::<Game>().invoke_folders_changed();
                        prefs.invoke_measure();
                        if !message.is_empty() {
                            ui.set_notice(message.into());
                        }
                    }
                    Err(e) => {
                        ui.set_notice(format!("{e:#}").into());
                        ui.global::<Game>().invoke_schedule_check();
                    }
                }
            });
        });
    });
}
