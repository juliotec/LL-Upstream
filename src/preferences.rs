#[cfg(target_os = "linux")]
mod controller_rules;
mod folders;
use crate::{Locale, MainWindow, Preferences, SettingToggle, Updates, fsx, home, i18n};
use anyhow::Result;
pub(crate) use folders::bind as bind_folders;
use serde_json::{Value, json};
use slint::{ComponentHandle, Model, ModelRc, SharedString};
use std::path::{Path, PathBuf};

fn same_folder(current: &Path, default: &Path) -> bool {
    current == default
        || fsx::resolve_path(current)
            .ok()
            .zip(fsx::resolve_path(default).ok())
            .is_some_and(|(current, default)| current == default)
}

fn model<T: Clone + 'static>(items: Vec<T>) -> ModelRc<T> {
    ModelRc::new(slint::VecModel::from(items))
}

fn toggle(data: &Value, key: &str, label: &str, default: bool, enabled: bool) -> SettingToggle {
    SettingToggle {
        key: key.into(),
        label: label.into(),
        checked: data[key].as_bool().unwrap_or(default),
        enabled,
    }
}

// Keep the repeated checkbox controls alive when one setting changes. Replacing
// their model recreates the controls and drops the active keyboard focus.
fn refreshed_toggles(
    current: ModelRc<SettingToggle>,
    rows: Vec<SettingToggle>,
) -> ModelRc<SettingToggle> {
    if current.row_count() != rows.len() {
        return model(rows);
    }
    for (index, row) in rows.into_iter().enumerate() {
        if current.row_data(index).as_ref() != Some(&row) {
            current.set_row_data(index, row);
        }
    }
    current
}

fn show_storage(ui: &MainWindow, view: &crate::storage::Snapshot, index: usize) {
    let prefs = ui.global::<Preferences>();
    let rows = view.selected(index);
    let (total, groups) = crate::storage::groups(&rows);
    prefs.set_storage_total(total.into());
    prefs.set_previous_files_available(view.has_previous_files());
    prefs.set_drive_index(index as i32);
    prefs.set_drive_options(model(view.choices().into_iter().map(Into::into).collect()));
    prefs.set_drive_free(view.free_label(index).into());
    prefs.set_disk_groups(model(groups));
}

pub fn refresh(ui: &MainWindow, root: &Path) -> Result<()> {
    let data = home::settings(root)?;
    let p = ui.global::<Preferences>();
    let system = i18n::system();
    let locale = data["uiLanguage"].as_str().and_then(i18n::pick);
    ui.global::<Locale>()
        .set_language(locale.unwrap_or(system).into());
    let mut languages = vec![SharedString::from(i18n::lookup(
        locale.unwrap_or(system),
        "System language",
    ))];
    languages.extend(i18n::LANGUAGES.iter().map(|&(_, name)| name.into()));
    let profiles = ui.global::<crate::Players>().get_profiles();
    let mut options = vec![i18n::lookup(locale.unwrap_or(system), "None").into()];
    let mut selected = 0;
    for row in profiles.iter().filter(|row| !row.damaged) {
        if data["startupProfile"].as_str() == Some(row.id.as_str()) {
            selected = options.len() as i32;
        }
        options.push(row.name);
    }
    p.set_startup_profiles(model(options));
    p.set_startup_profile_index(selected);
    p.set_languages(model(languages));
    p.set_language_index(
        locale
            .and_then(|s| i18n::LANGUAGES.iter().position(|(c, _)| *c == s))
            .map_or(0, |i| i as i32 + 1),
    );
    let own = if crate::update::own_tag().starts_with("nightly-") {
        "nightly"
    } else {
        "stable"
    };
    p.set_launcher_track(data["launcherChannel"].as_str().unwrap_or(own).into());
    let check = data["checkUpdates"].as_bool().unwrap_or(true);
    p.set_update_options(refreshed_toggles(
        p.get_update_options(),
        vec![
            toggle(
                &data,
                "checkUpdates",
                "Check for updates at start",
                true,
                true,
            ),
            toggle(
                &data,
                "downloadUpdatesEarly",
                "Download updates early",
                false,
                check,
            ),
        ],
    ));
    let stats = data["sendPerformanceStatistics"].as_bool().unwrap_or(false);
    p.set_sharing_options(refreshed_toggles(
        p.get_sharing_options(),
        vec![
            toggle(
                &data,
                "sendPerformanceStatistics",
                "Send performance statistics",
                false,
                true,
            ),
            toggle(
                &data,
                "keepSentPerformanceStatistics",
                "Keep a copy of what was sent",
                true,
                stats,
            ),
            toggle(
                &data,
                "sendCrashReportsAutomatically",
                "Send crash reports automatically",
                false,
                true,
            ),
        ],
    ));
    let data_folder = home::data_folder(root, &data);
    p.set_custom_data_folder(
        !same_folder(&data_folder, &home::data_folder(root, &Value::Null))
            || !same_folder(
                &crate::game::install_root(root, &data),
                &crate::game::install_root(root, &Value::Null),
            ),
    );
    p.set_data_folder(data_folder.to_string_lossy().as_ref().into());
    let work = home::work(root, &data);
    p.set_custom_work_folder(!same_folder(&work, &home::work(root, &Value::Null)));
    p.set_work_folder(work.to_string_lossy().as_ref().into());
    let original_game = crate::players::active_dir(root)?
        .map(|profile| crate::game::original_game(&profile, &data))
        .transpose()?
        .flatten();
    p.set_original_game(
        original_game
            .as_deref()
            .unwrap_or(Path::new(""))
            .to_string_lossy()
            .as_ref()
            .into(),
    );
    #[cfg(windows)]
    {
        p.set_desktop_shortcut(crate::update::has_game_shortcut(true));
        p.set_start_shortcut(crate::update::has_game_shortcut(false));
    }
    p.set_linux(cfg!(target_os = "linux"));
    p.set_keep_build_files(data["keepBuildFiles"].as_bool().unwrap_or(true));
    let menu = crate::desktop::Menu::locate();
    let current = home::variable("APPIMAGE");
    let (available, owns_entry) = match (current.as_deref(), menu) {
        (Some(program), Some(menu)) => {
            let owns = menu.belongs_to(program);
            (owns || menu.installed_target().is_none(), owns)
        }
        _ => (false, false),
    };
    p.set_in_menu(owns_entry);
    p.set_menu_available(available);
    p.set_menu_label(
        if owns_entry {
            crate::i18n::key("Write the menu entry again")
        } else {
            crate::i18n::key("Add to applications menu")
        }
        .into(),
    );
    p.set_version(crate::update::own_tag().into());
    Ok(())
}

fn save(ui: &MainWindow, root: &Path, patch: Value) {
    let result = home::patch_settings(root, &patch).and_then(|_| refresh(ui, root));
    ui.global::<Preferences>().set_status(
        result
            .err()
            .map(|e| e.to_string())
            .unwrap_or_default()
            .into(),
    );
}

pub fn bind(ui: &MainWindow, root: &Path) -> Result<()> {
    // Reuse the launcher language, game language and a custom build-files
    // folder the previous launcher had chosen; the update channel, profile and
    // every consent stay the player's choices here. Best effort: a start
    // succeeds even when the old settings cannot be read.
    if let Err(error) = crate::home::link_from_stable(root) {
        eprintln!("Could not reuse the previous launcher's settings: {error:#}");
    }
    let p = ui.global::<Preferences>();
    #[cfg(target_os = "linux")]
    controller_rules::bind(ui);
    p.on_bundled_license(|index| {
        match index {
            0 => include_str!("../LICENSE"),
            1 => include_str!("../assets/fonts/Inter-LICENSE.txt"),
            2 => include_str!("../assets/fonts/Libertinus-OFL.txt"),
            _ => "",
        }
        .into()
    });
    ui.global::<Locale>()
        .on_display_path(|path| crate::home::display(Path::new(path.as_str())).into());
    crate::storage::cleanup::bind(ui, root);
    i18n::bind_messages(&ui.global::<Locale>());
    let (weak, path) = (ui.as_weak(), root.to_owned());
    p.on_choose_startup_profile(move |index| {
        let Some(ui) = weak.upgrade() else { return };
        let profiles = ui.global::<crate::Players>().get_profiles();
        let id = if index == 0 {
            None
        } else {
            let Some(row) = profiles
                .iter()
                .filter(|row| !row.damaged)
                .nth((index - 1) as usize)
            else {
                return;
            };
            Some(row.id.to_string())
        };
        save(&ui, &path, json!({"startupProfile":id}));
    });
    let (weak, path) = (ui.as_weak(), root.to_owned());
    p.on_language(move |index| {
        if let Some(ui) = weak.upgrade() {
            let code = usize::try_from(index - 1)
                .ok()
                .and_then(|n| i18n::LANGUAGES.get(n))
                .map(|&(c, _)| c);
            save(&ui, &path, json!({"uiLanguage":code}));
        }
    });
    let (weak, path) = (ui.as_weak(), root.to_owned());
    p.on_toggle(move |key, value| {
        if let Some(ui) = weak.upgrade() {
            save(&ui, &path, json!({key.as_str():value}));
        }
    });
    let (weak, path) = (ui.as_weak(), root.to_owned());
    p.on_choose_launcher_track(move |value| {
        if let Some(ui) = weak.upgrade()
            && matches!(value.as_str(), "stable" | "nightly")
            && value != ui.global::<Preferences>().get_launcher_track()
        {
            save(&ui, &path, json!({"launcherChannel":value.as_str()}));
            if ui.global::<Preferences>().get_launcher_track() == value {
                let updates = ui.global::<Updates>();
                updates.set_channel_prompt(true);
                updates.invoke_check();
            }
        }
    });
    let (weak, path) = (ui.as_weak(), root.to_owned());
    p.on_menu(move |remove| {
        if let Some(ui) = weak.upgrade()
            && let Err(e) = crate::desktop::change(remove).and_then(|_| refresh(&ui, &path))
        {
            ui.global::<Preferences>().set_status(e.to_string().into());
        }
    });
    #[cfg(windows)]
    {
        let (weak, path) = (ui.as_weak(), root.to_owned());
        p.on_shortcut(move |desktop, enabled| {
            if let Some(ui) = weak.upgrade() {
                let result = crate::update::game_shortcut(desktop, enabled);
                let refreshed = refresh(&ui, &path);
                if let Err(error) = result.and(refreshed) {
                    ui.global::<Preferences>()
                        .set_status(format!("{error:#}").into());
                }
            }
        });
    }
    let weak = ui.as_weak();
    p.on_save_diagnostics(move || {
        let Some(ui) = weak.upgrade() else { return };
        let Some(file) = rfd::FileDialog::new()
            .set_file_name("longshot-launcher-report.txt")
            .save_file()
        else {
            return;
        };
        let message = match crate::diagnostics::save(&file) {
            Ok(()) => "Diagnostic report saved.".to_owned(),
            Err(e) => e.to_string(),
        };
        ui.global::<Preferences>().set_status(message.into());
    });
    let weak = ui.as_weak();
    p.on_open(move |key| {
        let destination = match key.as_str() {
            "privacy" => "https://longshot.magicbean.dev/privacy/",
            path if Path::new(path).is_absolute() && Path::new(path).is_dir() => path,
            path if Path::new(path).is_absolute() && Path::new(path).is_file() => {
                let Some(parent) = Path::new(path).parent().and_then(Path::to_str) else {
                    return;
                };
                parent
            }
            _ => return,
        };
        if let Err(e) = open::that_detached(destination)
            && let Some(ui) = weak.upgrade()
        {
            ui.global::<Preferences>().set_status(e.to_string().into());
        }
    });
    let (weak, path) = (ui.as_weak(), root.to_owned());
    let snapshot = std::sync::Arc::new(std::sync::Mutex::new(None::<crate::storage::Snapshot>));
    crate::storage::bind_cleanup_review(ui, root, snapshot.clone());
    let cached = snapshot.clone();
    let drive_ui = ui.as_weak();
    p.on_choose_drive(move |index| {
        if let Some(ui) = drive_ui.upgrade()
            && let Ok(cache) = cached.lock()
            && let Some(view) = cache.as_ref()
        {
            show_storage(&ui, view, index.max(0) as usize);
        }
    });
    let measuring = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    p.on_measure(move || {
        let Some(ui) = weak.upgrade() else { return };
        if measuring.swap(true, std::sync::atomic::Ordering::AcqRel) {
            return;
        }
        let prefs = ui.global::<Preferences>();
        prefs.set_measuring_storage(true);
        prefs.set_status("Measuring folders…".into());
        let (weak, path, measuring) = (weak.clone(), path.clone(), measuring.clone());
        let snapshot = snapshot.clone();
        std::thread::spawn(move || {
            let result = crate::storage::measure(&path);
            let _ = weak.upgrade_in_event_loop(move |ui| {
                measuring.store(false, std::sync::atomic::Ordering::Release);
                let p = ui.global::<Preferences>();
                p.set_measuring_storage(false);
                match result {
                    Ok(view) => {
                        show_storage(&ui, &view, 0);
                        *snapshot.lock().unwrap_or_else(|e| e.into_inner()) = Some(view);
                        p.set_status("".into());
                    }
                    Err(error) => p.set_status(format!("{error:#}").into()),
                }
            });
        });
    });
    let (weak, path) = (ui.as_weak(), root.to_owned());
    p.on_clear_cache(move |cache, folder| {
        let Some(ui) = weak.upgrade() else { return };
        if crate::busy(&ui) || ui.global::<crate::Game>().get_playing() {
            return;
        }
        let prefs = ui.global::<Preferences>();
        prefs.set_cleaning(true);
        prefs.set_status("Clearing cache…".into());
        let (weak, path) = (ui.as_weak(), path.clone());
        let (cache, folder) = (cache.to_string(), PathBuf::from(folder.as_str()));
        std::thread::spawn(move || {
            let result = crate::storage::clear(&path, &cache, &folder);
            let _ = weak.upgrade_in_event_loop(move |ui| {
                let prefs = ui.global::<Preferences>();
                prefs.set_cleaning(false);
                match result {
                    Ok(()) => prefs.invoke_measure(),
                    Err(error) => prefs.set_status(format!("{error:#}").into()),
                }
            });
        });
    });
    refresh(ui, root)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn changing_updates_keeps_checkbox_rows_and_refreshes_the_dependent_setting() {
        let options = |check| {
            let data = json!({ "checkUpdates": check });
            vec![
                toggle(&data, "checkUpdates", "Check for updates", true, true),
                toggle(
                    &data,
                    "downloadUpdatesEarly",
                    "Download early",
                    false,
                    check,
                ),
            ]
        };
        let rows = refreshed_toggles(ModelRc::default(), options(false));
        for check in [true, false] {
            let refreshed = refreshed_toggles(rows.clone(), options(check));
            assert_eq!(refreshed, rows);
            assert_eq!(rows.row_data(0).unwrap().checked, check);
            assert_eq!(rows.row_data(1).unwrap().enabled, check);
        }
    }
}
