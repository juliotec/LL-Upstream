//! The release builder owns the build, update and game processes. The launcher supplies the
//! selected profile, validates release downloads and presents the builder's v1 JSON events.
mod autocheck;
mod catalog;
mod channel;
mod chooser;
mod discover;
mod forecast;
mod licenses;
mod preload;
mod prune;
pub(crate) use requirements::disk as available_bytes;
mod process;
pub(crate) mod registry;
mod releases;
mod report;
mod requirements;
mod reuse;
mod romfs;
mod sharing;
mod source;
mod steam;
pub(crate) use steam::{ShortcutMove, shortcuts_to_move, shortcuts_to_move_data};
mod upscaler;
mod versions;
pub(crate) use versions::storage_versions;

use crate::{Game, MainWindow, home, players};
use anyhow::Result;
use serde_json::Value;
use slint::{ComponentHandle, Model, ModelRc, SharedString};
use std::{path::Path, thread};
mod actions;
mod awake;
mod info;
mod jobs;
mod launch;
mod library;
mod mod_review;
pub(crate) use library::FETCH_LOCK;
pub(crate) use library::{check_move_record, forget_moved_version, record_moved_version};
pub use library::{install_root, managed_folders, owned_folder};

/// Includes playable discovered installations as well as every registered instance.
pub(crate) fn playable_folders(root: &Path) -> anyhow::Result<Vec<std::path::PathBuf>> {
    Ok(versions::listed(root)?
        .into_iter()
        .map(|version| version.folder)
        .collect())
}

pub(crate) use prune::{is_retired, move_lease};
mod notes;
pub(crate) use notes::launcher_body;
mod nvidia;
pub use nvidia::handle_nvidia_cache_argument;
#[cfg(any(windows, test))]
mod windowed;
mod wizard;
pub use actions::bind;

fn version_title(tag: &str, nightly: &str) -> String {
    if let Some((build, revision)) = tag.strip_prefix("nightly-").and_then(|s| s.split_once('-')) {
        format!("{nightly} {build} · {revision}")
    } else {
        tag.to_owned()
    }
}

pub fn original_game(profile_dir: &Path, settings: &Value) -> Result<Option<std::path::PathBuf>> {
    let profile = home::read_json(&profile_dir.join("profile.json"))?;
    Ok(profile["game"]
        .as_str()
        .map(|p| profile_dir.join(p))
        .or_else(|| source::read(settings)))
}

pub fn drop_file(ui: &MainWindow, path: &Path) -> Result<()> {
    let game = ui.global::<Game>();
    anyhow::ensure!(
        !game.get_busy()
            && game.get_license().is_empty()
            && !game.get_folders_open()
            && matches!(
                game.get_step(),
                crate::GameStep::Files | crate::GameStep::Settings
            ),
        "Finish the current operation first."
    );
    let path = process::validated_source(path)?;
    if game.get_source_path().as_str() == path.to_string_lossy() {
        if game.get_step() == crate::GameStep::Files {
            game.invoke_retry_check();
        }
        return Ok(());
    }
    game.set_source_path(path.to_string_lossy().as_ref().into());
    game.set_step(crate::GameStep::Files);
    game.set_file_error(false);
    game.set_status("".into());
    Ok(())
}
pub(crate) use launch::wait_shortcut;
pub(crate) use process::ShortcutReported;
pub use versions::{play, select_folder};

/// Resolve only the selected builder's own output folder.
pub fn selected_build(home: &Path) -> Result<std::path::PathBuf> {
    let version = versions::selected(home, None)?;
    let builds = version.folder.join("builds");
    let output = info::folder(&version.folder, &players::current_dir(home)?);
    anyhow::ensure!(
        output.is_dir() && !versions::linked(&builds) && !versions::linked(&output),
        "The selected game has no local build folder."
    );
    Ok(output)
}

pub fn mod_command(home: &Path) -> Result<std::process::Command> {
    let version = versions::selected(home, None)?;
    let ctx = process::Context::load(home)?;
    ctx.mod_command(&version.folder.join(process::executable()))
}

/// Run a mod query against the exact builder that will compile or launch this profile.
/// A new installation in the build wizard may not appear in the Versions tab yet.
pub(crate) fn mod_command_for(
    home: &Path,
    exe: &Path,
    profile: &Path,
) -> Result<std::process::Command> {
    process::Context::for_profile(home, profile)?.mod_command(exe)
}

pub fn first_run(ui: &MainWindow, root: &Path) -> Result<()> {
    let game = ui.global::<Game>();
    if game.get_version_rows().row_count() == 0 && home::settings(root)?["setupVisited"] != true {
        let ctx = process::Context::load(root)?;
        wizard::location(ui, &ctx, &wizard::BuildIntent::New);
        game.set_first_run(true);
        game.set_step(crate::GameStep::Welcome);
        ui.set_page(crate::Page::Build);
    }
    Ok(())
}

/// Remove a managed version selected from the storage screen.
pub fn remove_managed(home: &Path, folder: &Path) -> Result<()> {
    let version = versions::installed(home)?
        .into_iter()
        .find(|version| version.folder == folder)
        .ok_or_else(|| {
            anyhow::anyhow!("Only an installed Longshot version can be removed here.")
        })?;
    versions::delete(home, &version)
}
static CATALOG: std::sync::RwLock<Option<catalog::Catalog>> = std::sync::RwLock::new(None);

const LANGS: &[&str] = &[
    "en-US", "en-GB", "fr", "fr-CA", "de", "it", "es", "es-419", "pt-BR", "nl", "pl", "ru", "ja",
    "ko", "zh-Hans", "zh-Hant",
];

pub fn refresh(ui: &MainWindow, home_dir: &Path) {
    let result = (|| -> Result<()> {
        let versions = versions::listed(home_dir)?;
        let profile = players::current_dir(home_dir)?;
        let record = home::read_json(&profile.join("profile.json"))?;
        let selected = versions::current(&versions, &record);
        let index = selected.and_then(|s| versions.iter().position(|v| std::ptr::eq(v, s)));
        let global = ui.global::<Game>();
        global.set_stable_installations(
            registry::stable_only(home_dir).is_ok_and(|folders| !folders.is_empty()),
        );
        global.set_selected(index.map_or(-1, |i| i as i32));
        global.set_playable(selected.is_some());
        global.set_steam_shortcut_present(selected.is_some_and(|v| {
            steam::installed(&profile, &record, &v.folder.join(process::executable()))
        }));
        let catalog = CATALOG.read().unwrap();
        let sizes = SIZES.read().unwrap();
        let total: Option<u64> = versions.iter().map(|v| sizes.get(&v.folder).copied()).sum();
        let nightly = crate::i18n::lookup(
            ui.global::<crate::Locale>().get_language().as_str(),
            "Nightly",
        );
        let selected_modules = selected
            .map(versions::Version::modules)
            .transpose()?
            .unwrap_or_default();
        let rows = versions
            .iter()
            .map(|version| {
                let update = catalog.as_ref().and_then(|c| c.update_for(&version.tag));
                let status = if update.is_some() {
                    "Update available"
                } else if catalog.is_some() {
                    "Up to date"
                } else {
                    "Installed"
                };
                // Keep same-release copies distinguishable when the selected/default row moves.
                let siblings = versions.iter().filter(|v| v.tag == version.tag);
                let copy_suffix = if siblings.clone().nth(1).is_some() {
                    let number = 1 + siblings.filter(|v| v.folder < version.folder).count();
                    format!(" · #{number}").into()
                } else {
                    SharedString::default()
                };
                let selected = selected.is_some_and(|v| v.folder == version.folder);
                crate::VersionRow {
                    dlss: selected && selected_modules.contains(&"dlss"),
                    fsr: selected && selected_modules.contains(&"fsr4"),
                    xess: selected && selected_modules.contains(&"xess"),
                    copy_suffix,
                    title: version_title(&version.tag, &nightly).into(),
                    tag: version.tag.as_str().into(),
                    track: channel(&version.tag).into(),
                    status: status.into(),
                    size: sizes
                        .get(&version.folder)
                        .map(|n| crate::storage::format_size(*n))
                        .unwrap_or_else(|| "—".to_string())
                        .into(),
                    update: update
                        .as_deref()
                        .map(|tag| tag.strip_prefix("nightly-").unwrap_or(tag))
                        .unwrap_or_default()
                        .into(),
                    share: sizes
                        .get(&version.folder)
                        .zip(total)
                        .filter(|(_, total)| *total > 0)
                        .map_or(0., |(size, total)| *size as f32 / total as f32),
                }
            })
            .collect::<Vec<_>>();
        global.set_version_rows(ModelRc::new(slint::VecModel::from(rows)));
        global.set_library_size(
            total
                .map(crate::storage::format_size)
                .unwrap_or_default()
                .into(),
        );
        global.set_current_tag(selected.map_or("", |v| v.tag.as_str()).into());
        global.set_update_tag(
            catalog
                .as_ref()
                .and_then(|c| selected.and_then(|v| c.update_for(&v.tag)))
                .unwrap_or_default()
                .into(),
        );
        if let Some(v) = selected {
            info::refresh(ui, home_dir, &profile, v);
        } else {
            info::invalidate();
        }
        Ok(())
    })();
    if let Err(e) = result {
        ui.global::<Game>().set_status(format!("{e:#}").into());
    }
}

fn labels(values: impl IntoIterator<Item = String>) -> ModelRc<SharedString> {
    ModelRc::new(slint::VecModel::from(
        values
            .into_iter()
            .map(SharedString::from)
            .collect::<Vec<_>>(),
    ))
}

fn check_updates(ui: &MainWindow, home_dir: &Path, work: &jobs::SharedWork, manual: bool) {
    let g = ui.global::<Game>();
    if g.get_checking() {
        return;
    }
    let installed: Vec<_> = g
        .get_version_rows()
        .iter()
        .map(|row| row.tag.to_string())
        .collect();
    g.set_checking(true);
    let weak = ui.as_weak();
    let dir = home_dir.to_owned();
    let work = work.clone();
    thread::spawn(move || {
        let result = catalog::Catalog::fetch(&installed);
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let g = ui.global::<Game>();
            g.set_checking(false);
            match result {
                Ok(catalog) => {
                    preload::likely(&dir, &catalog, &work);
                    let tags = catalog.versions();
                    let buildable: Vec<_> = tags
                        .iter()
                        .filter(|t| selectable_release(t))
                        .cloned()
                        .collect();
                    if !buildable
                        .iter()
                        .any(|tag| tag == g.get_release_choice().as_str())
                    {
                        g.set_release_choice(
                            buildable
                                .first()
                                .map(String::as_str)
                                .unwrap_or_default()
                                .into(),
                        );
                    }
                    g.set_build_releases(labels(buildable));
                    g.set_releases(ModelRc::from(
                        tags.into_iter()
                            .map(|tag| crate::ReleaseNote {
                                body: catalog.notes(&tag).into(),
                                tag: tag.into(),
                            })
                            .collect::<Vec<_>>()
                            .as_slice(),
                    ));
                    g.set_pulls(labels(catalog.trusted_pulls()));
                    *CATALOG.write().unwrap() = Some(catalog);
                    g.set_catalog_generation(g.get_catalog_generation().wrapping_add(1));
                    refresh(&ui, &dir);
                    if manual && !g.get_busy() {
                        let rows = g.get_version_rows();
                        let updates = rows.iter().filter(|row| !row.update.is_empty()).count();
                        let language = ui.global::<crate::Locale>().get_language();
                        let message =
                            crate::i18n::lookup(&language, "{0} checked · {1} with updates")
                                .replace("{0}", &rows.row_count().to_string())
                                .replace("{1}", &updates.to_string());
                        g.set_status(message.into());
                    }
                }
                Err(e) if manual => {
                    g.set_status(format!("Could not check for versions: {e:#}").into())
                }
                Err(_) => (),
            }
        });
    });
}
fn find_files(ui: &MainWindow) {
    let game = ui.global::<Game>();
    if game.get_searching() {
        return;
    }
    game.set_searching(true);
    let weak = ui.as_weak();
    thread::spawn(move || {
        let paths = discover::game_files();
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let g = ui.global::<Game>();
            g.set_searching(false);
            g.set_found(labels(
                paths.into_iter().map(|p| p.to_string_lossy().into_owned()),
            ));
        });
    });
}

/// Enable when a stable game builder is promoted for this launcher.
pub(crate) const STABLE_BUILDS_AVAILABLE: bool = false;
const STABLE_UNAVAILABLE: &str = "Stable game builds will be available after promotion.";

fn selectable_release(tag: &str) -> bool {
    crate::update::channel_order(tag, "nightly", 3).is_some()
        || (STABLE_BUILDS_AVAILABLE && crate::update::channel_order(tag, "stable", 3).is_some())
}

fn default_track() -> &'static str {
    if STABLE_BUILDS_AVAILABLE {
        "Stable"
    } else {
        "Nightly"
    }
}

fn channel(tag: &str) -> &'static str {
    channel::Channel::of(tag).label()
}

static SIZES: std::sync::RwLock<std::collections::BTreeMap<std::path::PathBuf, u64>> =
    std::sync::RwLock::new(std::collections::BTreeMap::new());

fn inspect_library(ui: &MainWindow, root: &Path) -> Result<()> {
    let versions = versions::listed(root)?;
    let root = root.to_owned();
    let weak = ui.as_weak();
    thread::spawn(move || {
        let sizes = versions
            .into_iter()
            .filter_map(|v| crate::storage::folder_bytes(&v.folder).map(|n| (v.folder, n)))
            .collect();
        let _ = weak.upgrade_in_event_loop(move |ui| {
            *SIZES.write().unwrap() = sizes;
            refresh(&ui, &root);
        });
    });
    Ok(())
}

#[cfg(test)]
fn fake_version(folder: &std::path::Path) -> std::io::Result<()> {
    std::fs::create_dir_all(folder.join("builds/default"))?;
    std::fs::write(folder.join(process::executable()), [])
}

#[cfg(test)]
mod channel_tests {
    use super::*;

    #[test]
    fn promotion_controls_stable_game_defaults_and_new_builds() {
        assert_eq!(
            default_track(),
            if STABLE_BUILDS_AVAILABLE {
                "Stable"
            } else {
                "Nightly"
            }
        );
        assert_eq!(selectable_release("v3.0.0"), STABLE_BUILDS_AVAILABLE);
        assert!(selectable_release("nightly-53-0123abcd"));
        assert!(!selectable_release("stable"));
        assert!(!selectable_release("v1.0.0"));
    }
}

#[cfg(test)]
mod tag_tests {
    use super::channel;
    use crate::update::channel_order;

    #[test]
    fn game_tags_classify_and_order_by_track() {
        for (tag, track) in [
            ("v3.0.0", "Stable"),
            ("v3.1.0", "Stable"),
            ("v10.0.0", "Stable"),
            ("v3.1.0-rc.1", "Stable"),
            ("v3.1.0-rc.12", "Stable"),
            ("nightly-2029-c544828f", "Nightly"),
            ("nightly-2029-c544828f-dirty", "Nightly"),
            ("nightly-1-00000000", "Nightly"),
            ("nightly-2029", "Stable"),
            ("nightly-2029-c544828", "Stable"),
            ("nightly-2029-C544828F", "Stable"),
            ("nightly-x-c544828f", "Stable"),
            ("nightly-1234567890-c544828f", "Stable"),
            ("v3.1.0-rc.1-3-g1a2b3c4", "Stable"),
            ("v2.1.0-rc.1", "Stable"),
            ("v2.0.7", "Stable"),
            ("pr-222-1a2b3c4d", "Pull request"),
            ("pr-1-00000000", "Pull request"),
            ("pr-0-1a2b3c4d", "Stable"),
            ("pr-222-1A2B3C4D", "Stable"),
            ("pr-222-1a2b3c4", "Stable"),
            ("pr-222", "Stable"),
        ] {
            assert_eq!(channel(tag), track, "{tag}");
        }
        for (new, old) in [
            ("v3.1.0", "v3.0.14"),
            ("v3.0.10", "v3.0.9"),
            ("v4.0.0", "v3.9.9"),
            ("v10.0.0", "v9.0.0"),
            ("nightly-2030-0a1b2c3d", "nightly-2029-c544828f"),
            ("nightly-10000-aaaaaaaa", "nightly-9999-bbbbbbbb"),
            ("nightly-2029-c544828f-dirty", "nightly-2029-c544828f"),
        ] {
            let track = if channel(new) == "Nightly" {
                "nightly"
            } else {
                "stable"
            };
            let new_key = channel_order(new, track, 3).unwrap();
            let old_key = channel_order(old, track, 3).unwrap();
            assert!(new_key > old_key, "{new} must follow {old}");
        }
        for (a, b) in [
            ("v3.1.0", "v3.1.0-rc.2"),
            ("v3.1.0-rc.2", "v3.1.0-rc.1"),
            ("v3.1.0-rc.1", "v3.0.14"),
            ("nightly-2029-c544828f", "v3.1.0"),
            ("nightly-2029-c544828f", "v3.1.0-rc.3"),
            ("nightly-1-aaaaaaaa", "v3.0.1"),
            ("v3.1.0", "v2.9.0"),
            ("v3.0.0", "v2.2.0-rc.1"),
            ("v3.1.0-rc.3-248-gc544828f", "v3.1.0"),
            ("nightly-2029-c544828f", "dev"),
            ("v3.1.0", "dev"),
            ("nightly-2029-c544828f", "nightly"),
            ("pr-222-bbbbbbbb", "pr-222-1a2b3c4d"),
            ("pr-223-1a2b3c4d", "pr-222-1a2b3c4d"),
            ("pr-222-1a2b3c4d", "nightly-2029-c544828f"),
            ("pr-222-1a2b3c4d", "v3.1.0"),
        ] {
            for track in ["stable", "nightly"] {
                assert!(
                    channel_order(a, track, 3).is_none() || channel_order(b, track, 3).is_none(),
                    "{a} and {b} must not be compared on {track}"
                );
            }
        }
    }
}
