use super::{
    LANGS, check_updates, find_files,
    jobs::{Progress, Work, worker},
    process::Context,
    refresh, steam, versions, wizard,
};
use crate::UiMutexExt as _;
use crate::{Game, MainWindow, home};
use anyhow::{Context as _, Result, ensure};
use serde_json::json;
use slint::{ComponentHandle, Model, ModelExt, ModelRc, SharedString};
use std::{
    path::{Path, PathBuf},
    rc::Rc,
    sync::{Arc, Mutex},
    thread,
};
fn set_upscaler(g: &Game<'_>, key: &str, enabled: bool) {
    match key {
        "dlss" => g.set_dlss(enabled),
        "fsr4" => g.set_fsr(enabled),
        "xess" => g.set_xess(enabled),
        _ => (),
    }
}

struct Bindings {
    window: slint::Weak<MainWindow>,
    home: PathBuf,
    work: super::jobs::SharedWork,
}

impl Bindings {
    fn call(
        &self,
        action: impl FnOnce(&MainWindow, &Path, &super::jobs::SharedWork) -> Result<()>,
    ) {
        let Some(window) = self.window.upgrade() else {
            return;
        };
        if let Err(error) = action(&window, &self.home, &self.work) {
            window
                .global::<Game>()
                .set_status(format!("{error:#}").into());
        }
    }

    fn action(
        self: &Rc<Self>,
        action: impl Fn(&MainWindow, &Path, &super::jobs::SharedWork) -> Result<()> + 'static,
    ) -> impl Fn() + 'static {
        let session = self.clone();
        move || session.call(&action)
    }

    fn with_argument<T: 'static>(
        self: &Rc<Self>,
        action: impl Fn(&MainWindow, &Path, &super::jobs::SharedWork, T) -> Result<()> + 'static,
    ) -> impl Fn(T) + 'static {
        let session = self.clone();
        move |value| session.call(|window, home, work| action(window, home, work, value))
    }
}

pub fn bind(ui: &MainWindow, home_dir: PathBuf) -> super::preload::Downloads {
    super::chooser::bind(ui, &home_dir);
    super::romfs::bind(ui, home_dir.clone());
    let shortcut_home = home_dir.clone();
    thread::spawn(move || {
        if let Err(error) = super::steam::migrate_shortcuts(&shortcut_home) {
            eprintln!("Steam shortcut migration: {error}");
        }
        super::prune::retry_retired(&shortcut_home);
    });
    #[cfg(windows)]
    {
        let path = home_dir.clone();
        thread::spawn(move || {
            if let Err(error) = super::windowed::repair_versions(&path) {
                eprintln!("Could not inspect installed builders: {error}");
            }
        });
    }
    let state = Arc::new(Mutex::new(Work::default()));
    let g = ui.global::<Game>();
    g.set_languages(ModelRc::from(
        LANGS
            .iter()
            .map(|s| SharedString::from(*s))
            .collect::<Vec<_>>()
            .as_slice(),
    ));
    g.set_windows(cfg!(windows));
    let session = Rc::new(Bindings {
        window: ui.as_weak(),
        home: home_dir,
        work: state,
    });
    #[cfg(windows)]
    super::nvidia::windows::bind(ui);
    g.on_inspect_library(session.action(|ui, home, _| super::inspect_library(ui, home)));
    g.on_open_logs(session.action(|_, home, _| {
        open::that_detached(home.join("logs"))?;
        Ok(())
    }));
    g.on_file_name(|file| {
        Path::new(file.as_str())
            .file_name()
            .map_or_else(SharedString::default, |name| {
                name.to_string_lossy().as_ref().into()
            })
    });
    g.on_note_blocks(|markdown| {
        ModelRc::new(slint::VecModel::from(super::notes::blocks(&markdown)))
    });
    g.on_open_note_link(session.with_argument(|_, _, _, url: SharedString| {
        ensure!(
            super::notes::https(&url),
            "Only HTTPS links can be opened from release notes."
        );
        open::that_detached(url.as_str())?;
        Ok(())
    }));
    g.on_filter_notes(|entries, filter| {
        if filter == "All" {
            return entries;
        }
        let nightly = filter == "Nightly";
        ModelRc::new(entries.filter(move |entry| entry.tag.starts_with("nightly-") == nightly))
    });
    g.on_note_index(|entries, tag| {
        entries
            .iter()
            .position(|entry| entry.tag == tag)
            .map_or(if entries.row_count() == 0 { -1 } else { 0 }, |index| {
                index as i32
            })
    });
    g.on_notes_for(|tag, _generation| {
        super::CATALOG
            .read()
            .unwrap()
            .as_ref()
            .map_or_else(SharedString::default, |c| c.notes(&tag).into())
    });
    g.set_stable_available(super::STABLE_BUILDS_AVAILABLE);
    g.on_buildable_release(|tag| super::selectable_release(&tag));
    g.on_get_started(session.action(|ui, home, work| {
        home::patch_settings(home, &json!({"setupVisited":true}))?;
        wizard::open(ui, home, work, wizard::Install::New)
    }));
    g.on_skip_setup(session.action(|window, home, _| {
        home::patch_settings(home, &json!({"setupVisited":true}))?;
        let g = window.global::<Game>();
        g.set_first_run(false);
        g.invoke_show_home();
        Ok(())
    }));
    g.on_begin_track(
        session.with_argument(|window, home, work, track: SharedString| {
            ensure!(
                matches!(
                    track.as_str(),
                    "Stable" | "Nightly" | "Earlier release" | "Main" | "Pull request"
                ),
                "Unknown release channel."
            );
            wizard::open(
                window,
                home,
                work,
                wizard::Install::Channel(track.to_string()),
            )
        }),
    );
    let work = session.work.clone();
    crate::preferences::bind_folders(ui, &session.home, move |ui, home| {
        refresh(ui, home);
        if ui.get_page() == crate::Page::Build {
            let g = ui.global::<Game>();
            let ctx = Context::load(home)?;
            g.set_build_location("".into());
            g.set_mod_review_needed(false);
            {
                let mut state = work.guard();
                state.prepared = None;
                state.check = Default::default();
                state.intent.relocate(&versions::listed(home)?)?;
                wizard::location(ui, &ctx, &state.intent);
            }
            g.set_step(crate::GameStep::Files);
            g.invoke_prepare_tools();
            g.invoke_schedule_check();
        }
        Ok(())
    });
    bind_builder(&g, &session);
    bind_reports(&ui.global::<crate::BuildReport>(), &session);
    bind_library(&g, &session);
    bind_upscalers(&g, &session);
    refresh(ui, &session.home);
    if home::settings(&session.home).is_ok_and(|s| s["checkUpdates"] != false) {
        check_updates(ui, &session.home, &session.work, false);
    }
    super::preload::Downloads(session.work.clone())
}

fn bind_builder(game: &Game<'_>, session: &Rc<Bindings>) {
    game.on_prepare_tools(session.action(|window, home, work| {
        super::preload::start(window, home, work);
        Ok(())
    }));
    game.on_intro_words(|intro| super::labels(intro.split_whitespace().map(str::to_owned)));
    game.on_requirements_link(session.action(|_, _, _| {
        open::that_detached("https://longshot.magicbean.dev/requirements/")?;
        Ok(())
    }));
    game.on_begin_build(
        session.action(|window, home, work| wizard::open(window, home, work, wizard::Install::New)),
    );
    game.on_rebuild(
        session.action(|window, home, work| {
            wizard::open(window, home, work, wizard::Install::Rebuild)
        }),
    );
    game.on_schedule_check(session.action(|ui, home, work| {
        super::autocheck::schedule(ui, home, work, false);
        Ok(())
    }));
    game.on_retry_check(session.action(|ui, home, work| {
        super::autocheck::schedule(ui, home, work, true);
        Ok(())
    }));
    game.on_start(session.action(super::mod_review::build));
    game.on_review_mods(session.action(super::mod_review::review));
    game.on_approve_mod_trust(session.action(super::mod_review::approve));
    game.on_cancel_mod_trust(session.action(super::mod_review::cancel));
    game.on_cancel_rebuild(session.action(super::launch::cancel_rebuild));
    game.on_cancel(session.action(|window, _, work| {
        work.guard().cancel.request();
        window.global::<Game>().set_play_after_build(false);
        window.global::<Game>().set_status("Stopping…".into());
        Ok(())
    }));
    game.on_find_files(session.action(|window, _, _| {
        find_files(window);
        Ok(())
    }));
    game.on_update_version(session.with_argument(|window, home, work, play_after| {
        wizard::open(window, home, work, wizard::Install::Update { play_after })
    }));
}

fn module_page(window: &MainWindow) -> Result<crate::Page> {
    let game = window.global::<Game>();
    let page = window.get_page();
    ensure!(
        matches!(page, crate::Page::Build | crate::Page::Versions)
            && !game.get_busy()
            && !game.get_playing()
            && !window.global::<crate::Preferences>().get_changing_folder(),
        "Wait until the game, build or folder change finishes before changing modules."
    );
    Ok(page)
}

fn bind_upscalers(game: &Game<'_>, session: &Rc<Bindings>) {
    let reviewed = Rc::new(std::cell::RefCell::new(None::<PathBuf>));
    let target = reviewed.clone();
    game.on_choose_library(session.action(|window, _, _| {
        module_page(window)?;
        let g = window.global::<Game>();
        ensure!(
            !cfg!(windows) && g.get_license() == "fsr4",
            "Open the FSR module first."
        );
        if let Some(path) = rfd::FileDialog::new()
            .set_title("Choose your Vulkan FSR 4 library")
            .add_filter("Vulkan FSR 4 library", &["so"])
            .pick_file()
        {
            super::upscaler::open_library(&path)?;
            g.set_license_library(path.to_string_lossy().as_ref().into());
        }
        Ok(())
    }));
    game.on_ask_license(
        session.with_argument(move |window, home, _, key: SharedString| {
            let page = module_page(window)?;
            let g = window.global::<Game>();
            let key = key.as_str();
            let license = super::licenses::for_module(key, cfg!(windows))?;
            let settings = home::settings(home)?;
            let (version, active, library) = if page == crate::Page::Versions {
                let v = versions::selected(home, None)?;
                let active = v.modules()?.contains(&key);
                let lib = super::upscaler::library_for(&v.folder).or_else(|| {
                    settings["fsrLibraryPath"]
                        .as_str()
                        .map(PathBuf::from)
                        .filter(|p| p.is_file())
                });
                (Some(v.folder), active, lib)
            } else {
                let active = match key {
                    "dlss" => g.get_dlss(),
                    "fsr4" => g.get_fsr(),
                    _ => g.get_xess(),
                };
                (
                    None,
                    active,
                    Some(PathBuf::from(g.get_fsr_library().as_str())),
                )
            };
            let expanded = license.first_view(home)?;
            *target.borrow_mut() = version;
            g.set_license_active(active);
            g.set_license_library(
                library
                    .as_deref()
                    .and_then(Path::to_str)
                    .unwrap_or_default()
                    .into(),
            );
            g.set_license_required(settings[license.setting] != true);
            g.set_license_text(license.summary.into());
            g.set_license_document(license.document.into());
            g.set_license_expanded(expanded);
            g.set_license(key.into());
            Ok(())
        }),
    );
    game.on_open_license(session.action(|window, _, _| {
        let key = window.global::<Game>().get_license();
        if let Some(url) = super::licenses::for_module(&key, cfg!(windows))?.source {
            open::that_detached(url)?;
        }
        Ok(())
    }));
    game.on_accept_license(session.action(move |window, home, work| {
        let page = module_page(window)?;
        let g = window.global::<Game>();
        let key = g.get_license();
        let license = super::licenses::for_module(&key, cfg!(windows))?;
        let enable = !g.get_license_active();
        let path = g.get_license_library();
        let source = Path::new(path.as_str());
        let fsr = key == "fsr4" && !cfg!(windows) && enable;
        if fsr {
            super::upscaler::open_library(source)?;
        }
        let selected = reviewed.borrow().clone();
        if let Some(folder) = selected {
            ensure!(
                page == crate::Page::Versions,
                "Open the module again for the selected installation."
            );
            let _lease = super::move_lease(home)?;
            let v = versions::selected(home, None)?;
            ensure!(
                v.folder == folder,
                "The selected installation changed. Open the module again."
            );
            crate::players::ensure_paths_idle(&[&folder])?;
            let mut modules = v.modules()?;
            ensure!(
                modules.contains(&key.as_str()) != enable,
                "The module settings changed. Open the module again."
            );
            if fsr {
                super::upscaler::pin(&folder, source)?;
            }
            modules.retain(|k| *k != key.as_str());
            if enable {
                modules.push(&key);
            }
            if enable {
                home::patch_settings(home, &json!({(license.setting):true}))?;
            }
            versions::set_modules(home, &v.folder, &modules)?;
            refresh(window, home);
        } else {
            ensure!(
                page == crate::Page::Build,
                "Open the module again for the current build."
            );
            if enable {
                home::patch_settings(home, &json!({(license.setting):true}))?;
            }
            if fsr {
                if work.guard().intent.instance().is_none() {
                    home::patch_settings(home, &json!({"fsrLibraryPath":source}))?;
                }
                g.set_fsr_library(path);
            }
            set_upscaler(&g, &key, enable);
        }
        g.set_license("".into());
        Ok(())
    }));
}

fn bind_library(game: &Game<'_>, session: &Rc<Bindings>) {
    game.on_select_version(session.with_argument(|window, home, _, index| {
        let versions = versions::listed(home)?;
        let v = versions
            .get(usize::try_from(index)?)
            .context("Choose a version.")?;
        versions::remember(home, v, false)?;
        window.global::<Game>().set_status("".into());
        refresh(window, home);
        Ok(())
    }));
    game.on_open_folder(session.action(|_, home, _| {
        let v = versions::selected(home, None)?;
        open::that_detached(v.folder)?;
        Ok(())
    }));
    game.on_play(session.action(|ui, home, work| {
        work.guard().finished_build = None;
        super::mod_review::play(ui, home, work)
    }));
    game.on_play_completed_build(session.action(super::mod_review::play));
    game.on_check_updates(session.action(|window, home, work| {
        check_updates(window, home, work, true);
        Ok(())
    }));
    game.on_remove(session.action(|window, home, _| {
        let v = versions::selected(home, None)?;
        let g = window.global::<Game>();
        g.set_current_tag(v.tag.into());
        g.set_confirming_removal(true);
        g.set_status("".into());
        Ok(())
    }));
    game.on_confirm_remove(session.action(|window, home, work| {
        let v = versions::selected(home, None)?;
        ensure!(
            v.tag == window.global::<Game>().get_current_tag().as_str(),
            "The selected version changed."
        );
        let dir = home.to_owned();
        worker(window, home, work, move |_, _| {
            versions::delete(&dir, &v)?;
            Ok(Progress::Removed)
        });
        Ok(())
    }));
    game.on_steam_shortcut(session.with_argument(|window, home, work, remove| {
        let v = versions::selected(home, None)?;
        let ctx = Context::load(home)?;
        worker(window, home, work, move |cancel, progress| {
            Ok(Progress::Message(steam::shortcut(
                &ctx, &v.folder, remove, cancel, progress,
            )?))
        });
        Ok(())
    }));
}

fn bind_reports(game: &crate::BuildReport<'_>, session: &Rc<Bindings>) {
    game.on_preview_report(session.action(|window, _, work| {
        let report = work
            .guard()
            .report
            .clone()
            .context("No failed build is selected.")?;
        window
            .global::<crate::BuildReport>()
            .set_report_preview(report.into());
        Ok(())
    }));
    game.on_save_diagnostic(session.action(|window, _, _| {
        if let Some(file) = rfd::FileDialog::new()
            .set_title("Save local launcher diagnostics")
            .set_file_name("longshot-launcher-diagnostics.txt")
            .save_file()
        {
            crate::diagnostics::save(&file)?;
            window
                .global::<crate::BuildReport>()
                .set_report_status("Diagnostics saved.".into());
        }
        Ok(())
    }));
    game.on_send_report(session.action(|window, _, work| {
        let g = window.global::<crate::BuildReport>();
        ensure!(
            window.global::<Game>().get_step() == crate::GameStep::Problem
                && !g.get_reporting()
                && !g.get_report_sent(),
            "No report is waiting to be sent."
        );
        let report = work
            .guard()
            .report
            .clone()
            .context("No failed build is selected.")?;
        ensure!(
            g.get_report_preview().as_str() == report,
            "Preview the report before sending."
        );
        g.set_reporting(true);
        g.set_report_status("Sending…".into());
        let weak = window.as_weak();
        thread::spawn(move || {
            let result = super::report::send(&report);
            let _ = weak.upgrade_in_event_loop(move |window| {
                let g = window.global::<crate::BuildReport>();
                if g.get_report_preview().as_str() != report {
                    return;
                }
                g.set_reporting(false);
                match result {
                    Ok(()) => {
                        g.set_report_sent(true);
                        g.set_report_status("Sent to the Longshot team.".into());
                    }
                    Err(e) => g.set_report_status(format!("Could not send the report: {e}").into()),
                }
            });
        });
        Ok(())
    }));
}

#[cfg(test)]
mod module_tests {
    use super::*;
    use slint::platform::{
        Platform, WindowAdapter,
        software_renderer::{MinimalSoftwareWindow, RepaintBufferType},
    };

    #[test]
    fn version_module_dialog_preserves_the_build_wizard_and_handles_busy_state() -> Result<()> {
        struct Display;
        impl Platform for Display {
            fn create_window_adapter(&self) -> Result<Rc<dyn WindowAdapter>, slint::PlatformError> {
                Ok(MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer))
            }
        }
        slint::platform::set_platform(Box::new(Display)).unwrap();
        let scratch = tempfile::tempdir()?;
        let home = scratch.path();
        let tag = "nightly-32-12345678";
        let instance = home.join("versions").join(tag);
        crate::game::fake_version(&instance)?;
        let profile = home.join("profiles/default");
        std::fs::create_dir_all(&profile)?;
        home::write_json(
            &home.join("settings.json"),
            &json!({"format":2,"currentProfile":"default"}),
        )?;
        home::write_json(
            &profile.join("profile.json"),
            &json!({"v":1,"name":"Player","version":tag}),
        )?;

        let ui = MainWindow::new()?;
        let game = ui.global::<Game>();
        bind_upscalers(
            &game,
            &Rc::new(Bindings {
                window: ui.as_weak(),
                home: home.into(),
                work: Default::default(),
            }),
        );
        ui.set_page(crate::Page::Versions);
        game.set_busy(true);
        game.invoke_ask_license("dlss".into());
        assert!(game.get_license().is_empty());
        game.set_busy(false);
        game.invoke_ask_license("dlss".into());
        assert_eq!(game.get_license(), "dlss");
        assert!(!game.get_license_active());
        game.set_license("".into()); // Dismiss before consent.
        assert!(versions::selected(home, None)?.modules()?.is_empty());
        game.invoke_ask_license("dlss".into());
        assert!(!game.get_license_expanded());
        game.invoke_accept_license();
        assert!(game.get_license().is_empty(), "{}", game.get_status());
        assert_eq!(versions::selected(home, None)?.modules()?, ["dlss"]);
        assert!(!game.get_dlss());
        game.invoke_ask_license("dlss".into());
        assert!(game.get_license_active());
        game.set_playing(true);
        game.invoke_accept_license();
        assert_eq!(versions::selected(home, None)?.modules()?, ["dlss"]);
        game.set_playing(false);
        game.invoke_accept_license();
        assert!(versions::selected(home, None)?.modules()?.is_empty());
        Ok(())
    }
}
