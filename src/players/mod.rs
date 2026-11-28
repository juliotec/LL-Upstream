mod archive;
mod caption;
mod dialogs;
mod disk;
mod imports;
pub(crate) use disk::{copy_tree, is_dir, is_file, require_dir};
mod nfc;
pub(crate) use nfc::{candidate as tag_candidate, folder as nfc_folder};
pub(crate) use profiles::{backup_root, read as read_profile};
mod profiles;
#[cfg(any(windows, test))]
mod reader;
mod rows;
mod running;
mod saves;
pub(crate) use saves::UID as SAVE_UID;
mod stable;
pub(crate) use profiles::list as list_profiles;
pub(crate) use profiles::{active_dir, edit_profile, valid_id, with_edit_lock};
pub use profiles::{current_dir, initialize, patch_profile};
pub(crate) use running::{ensure_idle, ensure_paths_idle, ensure_shader_idle};
pub(crate) use stable::{
    empty_stable_registry, old_builds as old_stable_builds,
    profile_for_user as stable_profile_for_user, selected_profile as stable_saves_profile,
    user_folder as stable_user,
};

pub(crate) fn migrate_stable(home: &Path) -> Result<()> {
    stable::migrate(home)?;
    crate::mods::migrate_stable(home)
}

use crate::{MainWindow, PlayerRow, Players, SlotRow, TagRow};
use anyhow::{Result, ensure};
use serde_json::{Value, json};
use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};
use std::{
    fs,
    path::{Path, PathBuf},
};

fn model<T: Clone + 'static>(values: Vec<T>) -> ModelRc<T> {
    ModelRc::new(VecModel::from(values))
}
fn labels(values: impl IntoIterator<Item = String>) -> ModelRc<SharedString> {
    model(values.into_iter().map(Into::into).collect())
}
fn profile_id(dir: &Path) -> &str {
    dir.file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("default")
}
fn slot_number(name: &str) -> u16 {
    name.strip_prefix("slot_")
        .and_then(|n| n.parse::<u16>().ok())
        .map_or(0, |n| n + 1)
}

fn slot_label(name: &str) -> String {
    format!("Slot {}", slot_number(name))
}

fn selected_slot(value: &Value) -> Option<String> {
    let s = value
        .as_str()
        .map(str::to_owned)
        .or_else(|| value.as_u64().map(|n| n.to_string()))?;
    if saves::slot_name(&s) {
        Some(s)
    } else {
        s.parse::<u8>()
            .ok()
            .filter(|n| *n <= 99)
            .map(|n| format!("slot_{n:02}"))
    }
}

pub fn refresh(ui: &MainWindow, home: &Path) -> Result<()> {
    refresh_models(ui, home, ui.get_page() == crate::Page::Saves)
}

fn refresh_models(ui: &MainWindow, home: &Path, with_thumbnails: bool) -> Result<()> {
    let players = ui.global::<Players>();
    let Some(dir) = active_dir(home)? else {
        players.set_profiles(Default::default());
        players.set_slots(Default::default());
        players.set_slot_index(-1);
        players.set_profile_id("".into());
        players.set_profile_name("".into());
        players.set_current_index(-1);
        return Ok(());
    };
    let id = profile_id(&dir);
    if players.get_profile_id() != id {
        // Never leave the previous player's saves visible if this store fails to load.
        players.set_slots(Default::default());
        players.set_slot_index(-1);
    }
    let locale = ui.global::<crate::Locale>().get_language();
    let rows = profiles::list(home)?
        .into_iter()
        .map(|profile| rows::profile_row(&profile, locale.as_str()))
        .collect::<Vec<_>>();
    if let Some((index, current)) = rows
        .iter()
        .enumerate()
        .find(|(_, profile)| profile.id == id)
    {
        players.set_current_index(index as i32);
        players.set_profile_name(current.name.clone());
        players.set_profile_id(current.id.clone());
        players.set_colour(current.colour);
        players.set_initial(current.avatar.clone());
    }
    players.set_profiles(model(rows));
    let data = profiles::read(&dir).unwrap_or(Value::Null);
    let prefer = selected_slot(&data["start_slot"]);
    let slots_dir = saves::slots_dir(&dir)?;
    let mut slots = vec![];
    for p in saves::slots(&slots_dir)? {
        let id = p.file_name().unwrap().to_string_lossy().into_owned();
        let time = fs::metadata(p.join("progress.sav"))?.modified()?;
        let caption = caption::read(
            &p.join("caption.sav"),
            with_thumbnails.then_some((560, 320)),
        )
        .unwrap_or_default();
        slots.push((
            time,
            SlotRow {
                id: id.clone().into(),
                starts: prefer.as_deref() == Some(id.as_str()),
                number: slot_number(&id).to_string().into(),
                name: profiles::save_name(&data, &id)
                    .map(str::to_owned)
                    .or_else(|| (!caption.location.is_empty()).then(|| caption.location.clone()))
                    .unwrap_or_else(|| crate::i18n::lookup(&locale, "Saved game"))
                    .into(),
                slot_label: crate::i18n::lookup(&locale, "Slot {0}")
                    .replace("{0}", &slot_number(&id).to_string())
                    .into(),
                location: caption.location.into(),
                scene: caption.scene.into(),
                health: caption
                    .health
                    .map(|(now, max)| format!("{}/{}", now as f32 / 4., max as f32 / 4.))
                    .unwrap_or_default()
                    .into(),
                health_fraction: caption
                    .health
                    .map_or(0., |(now, max)| now as f32 / max as f32),
                hearts: caption
                    .health
                    .map(|(now, max)| {
                        "♥".repeat((now / 4) as usize)
                            + if now % 4 == 0 { "" } else { "◐" }
                            + &"♡".repeat((max.div_ceil(4) - now.div_ceil(4)) as usize)
                    })
                    .unwrap_or_default()
                    .into(),
                medals_have: caption
                    .medals
                    .as_deref()
                    .unwrap_or_default()
                    .iter()
                    .filter(|b| **b)
                    .count() as i32,
                stones_have: caption
                    .stones
                    .as_deref()
                    .unwrap_or_default()
                    .iter()
                    .filter(|b| **b)
                    .count() as i32,
                medals: model(caption.medals.unwrap_or_default()),
                stones: model(caption.stones.unwrap_or_default()),
                thumbnail: caption.thumbnail,
                bytes: crate::storage::format_size(fs::metadata(p.join("progress.sav"))?.len())
                    .into(),
                saved: caption
                    .time
                    .map(|d| d.format("%b %-d · %H:%M").to_string())
                    .unwrap_or_else(|| rows::when(time, &locale))
                    .into(),
            },
        ));
    }
    slots.sort_by(|(_, left), (_, right)| left.id.cmp(&right.id));
    let newest = slots
        .iter()
        .enumerate()
        .max_by_key(|(_, (time, _))| time)
        .map(|(i, _)| i);
    let mut slots: Vec<_> = slots.into_iter().map(|(_, r)| r).collect();
    // A valid start_slot with no progress is an intentional new-save selection.
    let index = match prefer {
        Some(id) => slots.iter().position(|slot| slot.id == id),
        None => newest,
    };
    if !with_thumbnails && let Some(slot) = index.and_then(|i| slots.get_mut(i)) {
        load_small_picture(&slots_dir, slot);
    }
    players.set_slot_index(index.map_or(-1, |i| i as i32));
    players.set_slots(model(slots));
    Ok(())
}

fn load_small_picture(dir: &Path, slot: &mut SlotRow) -> bool {
    if slot.thumbnail.size().width > 0 {
        return false;
    }
    let path = dir.join(slot.id.as_str()).join("caption.sav");
    let Some(caption) = caption::read(&path, Some((128, 84))) else {
        return false;
    };
    if caption.thumbnail.size().width == 0 {
        return false;
    }
    slot.thumbnail = caption.thumbnail;
    true
}

fn load_slot_pictures(ui: &MainWindow, home: &Path) -> Result<()> {
    let dir = saves::slots_dir(&current_dir(home)?)?;
    let slots = ui.global::<Players>().get_slots();
    for (index, mut slot) in slots.iter().enumerate() {
        if load_small_picture(&dir, &mut slot) {
            slots.set_row_data(index, slot);
        }
    }
    Ok(())
}

pub fn refresh_saves(ui: &MainWindow, home: &Path) -> Result<()> {
    if !ui.global::<Players>().get_busy() {
        ui.global::<Players>().set_message(SharedString::default());
    }
    refresh_models(ui, home, true)?;
    if ui.get_page() != crate::Page::Saves {
        ui.global::<Players>().set_save_details_open(false);
    }
    Ok(())
}
pub fn refresh_tags(ui: &MainWindow, home: &Path) -> Result<()> {
    let folder = nfc::folder(home)?;
    let bound = ui.global::<Players>();
    let selected: std::collections::BTreeSet<_> = bound
        .get_tags()
        .iter()
        .filter(|r| r.selected)
        .map(|r| r.name.to_string())
        .collect();
    let tags: Vec<_> = nfc::files(&folder, false)?
        .into_iter()
        .map(|p| {
            let detail = nfc::bytes(&p)
                .and_then(|b| nfc::identify(&p, &b))
                .map(|s| format!("Model {s}"))
                .unwrap_or_else(|e| e.to_string());
            TagRow {
                name: p.file_name().unwrap().to_string_lossy().as_ref().into(),
                detail: detail.into(),
                format: p
                    .extension()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .to_uppercase()
                    .into(),
                selected: p
                    .file_name()
                    .and_then(|s| s.to_str())
                    .is_some_and(|s| selected.contains(s)),
            }
        })
        .collect();
    bound.set_selected_tags(tags.iter().filter(|r| r.selected).count() as i32);
    bound.set_tags(model(tags));
    bound.set_tag_folder(folder.to_string_lossy().as_ref().into());
    Ok(())
}

fn background(
    ui: &MainWindow,
    home: &Path,
    task: impl FnOnce() -> Result<String> + Send + 'static,
) {
    let p = ui.global::<Players>();
    p.set_busy(true);
    p.set_message("Working…".into());
    let weak = ui.as_weak();
    let home = home.to_owned();
    std::thread::spawn(move || {
        let result = task();
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let p = ui.global::<Players>();
            p.set_busy(false);
            p.set_scanning(false);
            let result = result.and_then(|msg| {
                if ui.get_page() == crate::Page::Saves {
                    refresh_saves(&ui, &home)?;
                } else {
                    refresh(&ui, &home)?;
                }
                Ok(msg)
            });
            if ui.get_page() == crate::Page::Settings
                && ui.global::<crate::Preferences>().get_section() == crate::SettingsGroup::Nfc
                && let Err(e) = refresh_tags(&ui, &home)
            {
                p.set_message(e.to_string().into());
            } else {
                p.set_message(result.unwrap_or_else(|e| format!("{e:#}")).into());
            }
        });
    });
}
fn start_slot(ui: &MainWindow, home: &Path, name: &str) -> Result<()> {
    ensure!(saves::slot_name(name), "Choose a save slot.");
    let dir = current_dir(home)?;
    ensure!(
        disk::is_file(&saves::slots_dir(&dir)?.join(name).join("progress.sav")),
        "This save is gone."
    );
    patch_profile(&dir, |o| {
        o.insert("start_slot".into(), json!(name));
    })?;
    let players = ui.global::<Players>();
    let slots = players.get_slots();
    let Some(selected) = slots.iter().position(|row| row.id == name) else {
        return refresh(ui, home);
    };
    let slots_dir = saves::slots_dir(&dir)?;
    for (index, mut row) in slots.iter().enumerate() {
        let current = index == selected;
        let picture_changed = current && load_small_picture(&slots_dir, &mut row);
        if row.starts != current || picture_changed {
            row.starts = current;
            slots.set_row_data(index, row);
        }
    }
    players.set_slot_index(selected as i32);
    Ok(())
}
pub fn add_tags(ui: &MainWindow, home: &Path, paths: Vec<PathBuf>) -> Result<()> {
    if paths.is_empty() {
        return Ok(());
    }
    let dest = nfc::folder(home)?;
    background(ui, home, move || {
        let mut added = 0;
        let mut problems = Vec::new();
        for path in paths {
            let files = if disk::is_dir(&path) {
                nfc::files(&path, true)?
            } else {
                vec![path]
            };
            for file in files {
                match nfc::add(&dest, &file) {
                    Ok(()) => added += 1,
                    Err(err) => problems.push(format!(
                        "{}: {err}",
                        file.file_name().unwrap_or_default().to_string_lossy()
                    )),
                }
            }
        }
        let mut summary = format!(
            "Processed {added} tag file{}.",
            if added == 1 { "" } else { "s" }
        );
        for problem in problems {
            summary.push('\n');
            summary.push_str(&problem);
        }
        Ok(summary)
    });
    Ok(())
}

fn rename_result(ui: &MainWindow, result: Result<()>) -> bool {
    match result {
        Ok(()) => {
            ui.global::<Players>().set_message("".into());
            true
        }
        Err(error) => {
            ui.global::<Players>()
                .set_message(format!("{error:#}").into());
            false
        }
    }
}

pub fn bind(ui: &MainWindow, home: PathBuf) -> Result<bool> {
    let players = ui.global::<Players>();
    players.on_profile_position(|rows, id| {
        rows.iter()
            .position(|r| r.id == id)
            .map_or(-1, |i| i as i32)
    });
    players.on_save_position(|rows, id| {
        rows.iter()
            .position(|r| r.id == id)
            .map_or(-1, |i| i as i32)
    });
    players.set_colours(model(
        profiles::COLORS
            .iter()
            .map(|&(name, hex)| crate::ProfileSwatch {
                name: name.into(),
                key: hex.into(),
                tone: rows::profile_color(hex),
            })
            .collect(),
    ));
    let weak = ui.as_weak();
    let tag_home = home.clone();
    players.on_refresh_tags(move || {
        if let Some(ui) = weak.upgrade()
            && let Err(e) = refresh_tags(&ui, &tag_home)
        {
            ui.global::<Players>().set_message(format!("{e:#}").into());
        }
    });
    let weak = ui.as_weak();
    players.on_toggle_tag(move |index| {
        let Some(ui) = weak.upgrade() else {
            return;
        };
        let p = ui.global::<Players>();
        let tags = p.get_tags();
        let index = index as usize;
        if let Some(mut tag) = tags.row_data(index) {
            tag.selected = !tag.selected;
            tags.set_row_data(index, tag);
            p.set_selected_tags(tags.iter().filter(|t| t.selected).count() as i32);
        }
    });
    players.set_can_scan(cfg!(windows));
    #[cfg(windows)]
    {
        use std::sync::{
            Arc,
            atomic::{AtomicBool, Ordering},
        };
        let cancel = Arc::new(AtomicBool::new(false));
        let weak = ui.as_weak();
        let h = home.clone();
        let stop = cancel.clone();
        players.on_scan(move || {
            if let Some(ui) = weak.upgrade() {
                let p = ui.global::<Players>();
                if p.get_busy() {
                    return;
                }
                stop.store(false, Ordering::Relaxed);
                p.set_scanning(true);
                let token = stop.clone();
                let home = h.clone();
                background(&ui, &h, move || {
                    reader::scan_to(&nfc::folder(&home)?, &token)
                });
            }
        });
        players.on_cancel_scan(move || cancel.store(true, Ordering::Relaxed));
    }
    let weak = ui.as_weak();
    let path = home.clone();
    players.on_profile_colour(move |id, colour| {
        let Some(ui) = weak.upgrade() else { return };
        let apply = || -> Result<()> {
            ensure!(
                profiles::valid_id(id.as_str())
                    && profiles::COLORS
                        .iter()
                        .any(|(_, hex)| *hex == colour.as_str()),
                "Choose a profile colour."
            );
            patch_profile(
                &crate::home::data_dir(&path)?
                    .join("profiles")
                    .join(id.as_str()),
                |p| {
                    p.insert("color".into(), json!(colour.as_str()));
                },
            )?;
            refresh(&ui, &path)
        };
        if let Err(e) = apply() {
            ui.global::<Players>().set_message(format!("{e:#}").into());
        }
    });
    let weak = ui.as_weak();
    let root = home.clone();
    players.on_rename_profile(move |id, name| {
        let Some(ui) = weak.upgrade() else {
            return false;
        };
        let result = profiles::new_or_rename(&root, "rename", id.as_str(), name.as_str())
            .and_then(|_| refresh(&ui, &root));
        rename_result(&ui, result)
    });
    let weak = ui.as_weak();
    let root = home.clone();
    players.on_rename_save(move |id, name| {
        let Some(ui) = weak.upgrade() else {
            return false;
        };
        let result = (|| {
            let profile = current_dir(&root)?;
            ensure!(
                saves::slot_name(id.as_str())
                    && disk::is_file(
                        &saves::slots_dir(&profile)?
                            .join(id.as_str())
                            .join("progress.sav")
                    ),
                "This save is no longer available."
            );
            profiles::set_save_name(&profile, id.as_str(), name.as_str())?;
            refresh(&ui, &root)
        })();
        rename_result(&ui, result)
    });
    dialogs::bind(ui, &home);
    let weak = ui.as_weak();
    let picture_home = home.clone();
    ui.global::<Players>().on_load_slot_pictures(move || {
        if let Some(ui) = weak.upgrade()
            && let Err(e) = load_slot_pictures(&ui, &picture_home)
        {
            ui.set_notice(format!("{e:#}").into());
        }
    });
    let weak = ui.as_weak();
    let slot_home = home.clone();
    players.on_pick_slot(move |i| {
        if let Some(ui) = weak.upgrade() {
            let p = ui.global::<Players>();
            if let Some(slot) = p.get_slots().row_data(i as usize)
                && let Err(e) = start_slot(&ui, &slot_home, slot.id.as_str())
            {
                p.set_message(format!("{e:#}").into());
            }
        }
    });
    initialize(&home)?;
    // Reuse the game file the previous launcher had selected, so a player
    // upgrading does not have to find it again. Best effort: a start succeeds
    // even when the old path is gone (the wizard then asks for the file).
    let source_link = crate::game::link_stable_source(&home);
    let migration = migrate_stable(&home);
    let selected = profiles::apply_startup_profile(&home)?;
    refresh(ui, &home)?;
    if let Err(error) = source_link {
        eprintln!("Could not use the previous launcher's game file: {error:#}");
    }
    if let Err(error) = migration {
        ui.set_notice(
            format!("Could not import saves and mods from the stable launcher. {error:#}").into(),
        );
    }
    Ok(selected)
}

#[cfg(test)]
mod tests;
