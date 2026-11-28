//! Check the metadata behind visible lists off the event loop. The bounded
//! request channel coalesces ticks if a library lives on a slow drive.
use crate::{
    Game, MainWindow, Mods, Page, Popup, Preferences, SettingsGroup, Shell, game, home, mods,
    players,
};
use slint::{ComponentHandle, Timer, TimerMode};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    time::{Duration, SystemTime},
};

#[derive(Default, PartialEq)]
struct Files(BTreeMap<PathBuf, (u64, Option<SystemTime>)>);

impl Files {
    fn record(&mut self, path: &Path) {
        if let Ok(m) = fs::symlink_metadata(path) {
            self.0.insert(path.into(), (m.len(), m.modified().ok()));
        }
    }
}

fn children(path: &Path) -> impl Iterator<Item = PathBuf> {
    fs::read_dir(path)
        .into_iter()
        .flatten()
        .filter_map(Result::ok)
        .map(|e| e.path())
}

fn mod_tree(files: &mut Files, dir: &Path, depth: usize) {
    if !players::is_dir(dir) {
        return;
    }
    files.record(dir);
    let manifest = dir.join("mod.json");
    if manifest.is_file() {
        files.record(&manifest);
        return;
    }
    if depth < 4 {
        for child in children(dir) {
            if child
                .file_name()
                .and_then(|s| s.to_str())
                .is_some_and(|n| !n.starts_with(['.', '_', '-']) && !matches!(n, "romfs" | "exefs"))
            {
                mod_tree(files, &child, depth + 1);
            }
        }
    }
}

#[derive(Default, PartialEq)]
struct Snapshot {
    players: Files,
    mods: Files,
    versions: Files,
    tags: Files,
}

impl Snapshot {
    fn read(root: &Path, mods_visible: bool, tags_visible: bool) -> anyhow::Result<Self> {
        let mut result = Self::default();
        let settings = home::settings(root)?;
        let profiles = home::data_folder(root, &settings).join("profiles");
        for profile in children(&profiles).filter(|p| {
            players::is_dir(p)
                && p.file_name()
                    .and_then(|s| s.to_str())
                    .is_some_and(players::valid_id)
        }) {
            result.players.record(&profile.join("profile.json"));
            let slots = profile.join("saves/save").join(players::SAVE_UID);
            for slot in children(&slots).filter(|p| players::is_dir(p)) {
                result.players.record(&slot.join("progress.sav"));
                result.players.record(&slot.join("caption.sav"));
            }
        }
        result.players.record(&root.join(home::SETTINGS_FILE));
        if mods_visible && let Some(active) = players::active_dir(root)? {
            result.mods.record(&active.join("mods.json"));
            result.mods.record(&active.join("profile.json"));
            mod_tree(&mut result.mods, &active.join("mods"), 0);
        }
        if tags_visible {
            let dir = players::nfc_folder(root)?;
            for tag in children(&dir).filter(|p| players::tag_candidate(p)) {
                result.tags.record(&tag);
            }
        }
        result.versions.record(&root.join(game::registry::OWN));
        let library = game::install_root(root, &settings);
        result.versions.record(&library);
        for folder in children(&library).filter(|p| players::is_dir(p)) {
            result
                .versions
                .record(&folder.join("longshot_release.json"));
            result
                .versions
                .record(&folder.join("builds/default/longshot_build.json"));
        }
        Ok(result)
    }
}

fn idle(ui: &MainWindow) -> bool {
    !ui.get_navigation_locked()
        && !ui.get_modal_open()
        && !ui.get_editing_text()
        && ui.global::<Shell>().get_popup() == Popup::Closed
}

pub(crate) fn watch(ui: &MainWindow, root: &Path) -> Timer {
    let (requests, rx) = mpsc::sync_channel::<(bool, bool)>(1);
    let (updates, results) = mpsc::sync_channel(1);
    let root = root.to_owned();
    let scan_root = root.clone();
    let steam_dirty = Arc::new(AtomicBool::new(false));
    let sync_steam = steam_dirty.clone();
    std::thread::spawn(move || {
        while let Ok((mods_visible, tags_visible)) = rx.recv() {
            if sync_steam.swap(false, Ordering::AcqRel)
                && let Err(error) = game::migrate_shortcuts(&scan_root)
            {
                eprintln!("Steam shortcut migration: {error}");
            }
            if let Ok(snapshot) = Snapshot::read(&scan_root, mods_visible, tags_visible)
                && updates
                    .send((mods_visible, tags_visible, snapshot))
                    .is_err()
            {
                break;
            }
        }
    });
    let timer = Timer::default();
    let weak = ui.as_weak();
    let last = std::cell::RefCell::new(Snapshot::default());
    timer.start(TimerMode::Repeated, Duration::from_secs(2), move || {
        let Some(ui) = weak.upgrade() else { return };
        if !idle(&ui) {
            return;
        }
        let page = ui.get_page();
        if !matches!(
            page,
            Page::Home
                | Page::Versions
                | Page::Profiles
                | Page::ChoosePlayer
                | Page::Saves
                | Page::Mods
                | Page::Settings
        ) {
            return;
        }
        let mods_visible = page == Page::Mods
            && ui.global::<Mods>().get_section() == "installed"
            && !ui.global::<Mods>().get_showing_details();
        let tags_visible = page == Page::Settings
            && ui.global::<Preferences>().get_section() == SettingsGroup::Nfc;
        if let Ok((scanned_mods, scanned_tags, now)) = results.try_recv() {
            let mut previous = last.borrow_mut();
            let people_changed = previous.players != now.players;
            let settings = root.join(home::SETTINGS_FILE);
            let steam_changed = previous.versions != now.versions
                || previous.players.0.get(&settings) != now.players.0.get(&settings);
            if people_changed && players::refresh(&ui, &root).is_ok() {
                previous.players = now.players;
                crate::shell::refresh(&ui);
                if page == Page::Settings {
                    let _ = crate::preferences::refresh(&ui, &root);
                }
            }
            if (previous.versions != now.versions || people_changed)
                && game::reconnect_library(&root).is_ok()
            {
                if steam_changed {
                    steam_dirty.store(true, Ordering::Release);
                }
                game::refresh(&ui, &root);
                if page == Page::Versions {
                    ui.global::<Game>().invoke_inspect_library();
                }
                previous.versions = now.versions;
            }
            if scanned_tags
                && tags_visible
                && previous.tags != now.tags
                && players::refresh_tags(&ui, &root).is_ok()
            {
                previous.tags = now.tags;
            }
            if scanned_mods && mods_visible && previous.mods != now.mods {
                mods::reload_installed(&ui);
                previous.mods = now.mods;
            }
        }
        let _ = requests.try_send((mods_visible, tags_visible));
    });
    timer
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn observes_external_edits_to_visible_lists() -> anyhow::Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path();
        players::initialize(root)?;
        let profile = players::current_dir(root)?;
        let initial = Snapshot::read(root, true, false)?;
        let slot = profile
            .join("saves/save")
            .join(players::SAVE_UID)
            .join("slot_00");
        fs::create_dir_all(&slot)?;
        fs::write(slot.join("progress.sav"), b"synthetic")?;
        let changed = Snapshot::read(root, true, false)?;
        assert!(initial.players != changed.players && initial.mods == changed.mods);
        let dir = profile.join("mods/collection/example");
        fs::create_dir_all(&dir)?;
        fs::write(dir.join("mod.json"), b"{}")?;
        let added = Snapshot::read(root, true, false)?;
        assert!(changed.mods != added.mods);
        fs::write(dir.join("mod.json"), br#"{"name":"Changed"}"#)?;
        assert!(added.mods != Snapshot::read(root, true, false)?.mods);
        fs::remove_dir_all(&slot)?;
        assert!(initial.players == Snapshot::read(root, false, false)?.players);
        Ok(())
    }
}
