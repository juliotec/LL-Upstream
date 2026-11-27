#![cfg_attr(windows, windows_subsystem = "windows")]

/// UI state remains accessible after the worker's error has been surfaced.
/// Filesystem transactions take their own locks and validate state separately.
trait UiMutexExt<T> {
    fn guard(&self) -> std::sync::MutexGuard<'_, T>;
}
impl<T> UiMutexExt<T> for std::sync::Mutex<T> {
    fn guard(&self) -> std::sync::MutexGuard<'_, T> {
        self.lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

mod controller;
mod desktop;
mod diagnostics;
mod folders;
mod fsx;
use folders::mod_store;
mod game;
mod home;
mod i18n;
mod logbook;
mod mods;
mod players;
mod preferences;
mod shell;
mod storage;
mod update;
mod window_events;
use anyhow::{Context, Result, ensure};
use slint::{
    CloseRequestResponse, ComponentHandle, Model, Timer, TimerMode, winit_030::WinitWindowAccessor,
};
use std::{
    ffi::OsString,
    fs,
    path::{Path, PathBuf},
    time::Duration,
};

slint::include_modules!();

#[derive(Debug, PartialEq)]
enum Start {
    Window(Option<PathBuf>),
    Play {
        folder: Option<PathBuf>,
        profile: Option<PathBuf>,
        launcher_home: Option<PathBuf>,
    },
}

fn arguments(args: &[OsString]) -> Result<Start> {
    let mut play = false;
    let mut folder = None;
    let mut profile = None;
    let mut launcher_home = None;
    let mut args = args.iter().peekable();
    while let Some(arg) = args.next() {
        let mut path = || {
            args.next_if(|p| !p.to_string_lossy().starts_with("--"))
                .map(PathBuf::from)
        };
        if arg == "--play" {
            ensure!(
                !play && folder.is_none(),
                "--launch and --play go one at a time."
            );
            play = true;
            folder = path();
        } else if arg == "--launch" {
            ensure!(
                !play && folder.is_none(),
                "--launch and --play go one at a time."
            );
            folder = Some(path().context("--launch needs a game folder.")?);
        } else if arg == "--profile" {
            ensure!(profile.is_none(), "Specify only one profile.");
            profile = Some(path().context("--profile needs a profile folder.")?);
        } else if arg == "--launcher-home" {
            ensure!(launcher_home.is_none(), "Specify only one launcher home.");
            launcher_home = Some(std::path::absolute(
                path().context("--launcher-home needs a folder.")?,
            )?);
        }
    }
    if play {
        Ok(Start::Play {
            folder,
            profile,
            launcher_home,
        })
    } else {
        ensure!(
            profile.is_none() && launcher_home.is_none(),
            "Shortcut folder options require --play."
        );
        Ok(Start::Window(folder))
    }
}

fn busy(ui: &MainWindow) -> bool {
    ui.get_navigation_locked()
}

fn navigate(ui: &MainWindow, root: &Path, page: Page) {
    if busy(ui) {
        return;
    }
    if ui.get_page() == Page::Build && page != Page::Build {
        ui.global::<Game>().set_play_after_build(false);
    }
    if ui.get_page() != page || ui.global::<Updates>().get_show_notes() {
        ui.global::<Game>().set_show_log(false);
        ui.global::<Updates>().set_show_notes(false);
    }
    let result = match page {
        Page::Saves => players::refresh_saves(ui, root),
        Page::Home | Page::Profiles | Page::ChoosePlayer => players::refresh(ui, root),
        Page::Settings => preferences::refresh(ui, root),
        Page::Logs => logbook::refresh(ui, root),
        Page::Mods => {
            mods::refresh(ui);
            Ok(())
        }
        Page::Versions | Page::Build | Page::Launch => Ok(()),
    };
    if matches!(page, Page::Home | Page::Versions) {
        game::refresh(ui, root);
    }
    ui.global::<Shell>().set_popup(Popup::Closed);
    shell::refresh(ui);
    ui.set_page(page);
    if let Err(e) = result {
        ui.set_notice(format!("{e:#}").into());
    }
}

fn notice(ui: &MainWindow, result: Result<()>) {
    if let Err(error) = result {
        ui.set_notice(format!("{error:#}").into());
    }
}

macro_rules! with_window {
    ($handle:expr, |$window:ident $(, $argument:ident)*| $body:block) => {{
        let weak = $handle.as_weak();
        move |$($argument),*| {
            if let Some($window) = weak.upgrade() $body
        }
    }};
}

fn window(root: PathBuf, folder: Option<PathBuf>) -> Result<()> {
    let Some(instance) = home::acquire(&root)? else {
        let signal = root.join("launcher.show");
        if let Some(folder) = folder {
            // Resolve a relative argument in the caller's working directory.
            let folder = std::path::absolute(folder)?;
            home::write_json(&signal, &serde_json::to_value(folder)?)?;
        } else if let Err(error) = fs::File::create_new(&signal)
            && error.kind() != std::io::ErrorKind::AlreadyExists
        {
            return Err(error.into());
        }
        return Ok(());
    };
    diagnostics::start(&root, false);
    folders::resume_move(&root)?;
    let ui = MainWindow::new()?;
    slint::set_xdg_app_id("longshot-launcher")?;
    notice(&ui, update::recover_after_lock());
    notice(
        &ui,
        game::registry::prepare(&root, home::is_portable(&root)),
    );
    preferences::bind(&ui, &root)?;
    notice(&ui, players::bind(&ui, root.clone()));
    let choose_profile = ui.global::<Players>().get_profiles().row_count() != 1;
    let pending_folder = std::rc::Rc::new(std::cell::RefCell::new(folder));
    if choose_profile {
        ui.set_page(Page::ChoosePlayer);
    } else if let Some(folder) = pending_folder.take()
        && let Err(e) = game::select_folder(&root, &folder)
    {
        ui.set_notice(format!("{e:#}").into());
    }
    let downloads = game::bind(&ui, root.clone());
    update::bind(&ui, root.clone());
    window_events::bind(&ui, root.clone());
    mods::bind(&ui, &root);
    logbook::bind(&ui, &root);
    shell::bind(&ui);
    let poke = root.join("launcher.show");
    let weak = ui.as_weak();
    let nav_root = root.clone();
    let chooser_folder = pending_folder.clone();
    ui.on_navigate(move |page| {
        if let Some(ui) = weak.upgrade() {
            let leaving_chooser = ui.get_page() == Page::ChoosePlayer && page == Page::Home;
            if leaving_chooser
                && let Some(folder) = chooser_folder.take()
                && let Err(error) = game::select_folder(&nav_root, &folder)
            {
                ui.set_notice(format!("{error:#}").into());
            }
            navigate(&ui, &nav_root, page);
            if leaving_chooser && let Err(error) = game::first_run(&ui, &nav_root) {
                ui.set_notice(format!("{error:#}").into());
            }
        }
    });
    ui.global::<Preferences>()
        .on_navigate(with_window!(ui, |ui, page| {
            ui.invoke_navigate(page);
        }));
    let weak = ui.as_weak();
    ui.global::<Game>().on_show_storage(move || {
        if let Some(ui) = weak.upgrade()
            && !busy(&ui)
        {
            ui.global::<Preferences>()
                .set_section(SettingsGroup::Storage);
            ui.invoke_navigate(Page::Settings);
        }
    });
    if !choose_profile {
        game::first_run(&ui, &root)?;
    }
    ui.global::<Game>().on_show_home(with_window!(ui, |ui| {
        ui.invoke_navigate(Page::Home);
    }));
    ui.on_go_back(with_window!(ui, |ui| {
        if ui.global::<Shell>().get_popup() != Popup::Closed {
            ui.global::<Shell>().set_popup(Popup::Closed);
            return;
        }
        if ui.global::<Updates>().get_channel_prompt() {
            ui.global::<Updates>().set_channel_prompt(false);
            return;
        }
        let players = ui.global::<Players>();
        let game = ui.global::<Game>();
        if !players.get_prompt().is_empty() {
            players.invoke_cancel();
            return;
        }
        match ui.get_page() {
            Page::ChoosePlayer => {}
            Page::Mods if mods::back(&ui) => (),
            Page::Saves if players.get_save_details_open() => players.set_save_details_open(false),
            Page::Build if game.get_folders_open() => game.set_folders_open(false),
            Page::Build | Page::Launch if game.get_show_log() => game.set_show_log(false),
            Page::Build | Page::Versions if game.get_show_notes() => game.set_show_notes(false),
            Page::Build if game.get_step() == GameStep::Welcome => game.invoke_skip_setup(),
            Page::Build if game.get_busy() => {
                if game.get_step() == GameStep::Building {
                    game.invoke_confirm_stop();
                } else {
                    ui.set_notice("Use Cancel on the build screen to stop this operation.".into());
                }
            }
            Page::Build if !game.get_license().is_empty() => game.set_license("".into()),
            Page::Build if game.get_step() == GameStep::Settings => {
                game.set_step(GameStep::Files);
                game.set_status("".into());
            }
            Page::Settings | Page::Home if ui.global::<Updates>().get_show_notes() => {
                ui.global::<Updates>().set_show_notes(false);
            }
            Page::Logs => ui.invoke_navigate(Page::Settings),
            _ => ui.invoke_navigate(Page::Home),
        }
    }));
    let weak = ui.as_weak();
    ui.window().on_close_requested(move || {
        if let Some(ui) = weak.upgrade() && busy(&ui) {
            let game = ui.global::<Game>();
            if game.get_busy() && game.get_step() == GameStep::Building {
                ui.set_close_after_stop(true);
                ui.set_asking_to_stop(true);
            } else {
                ui.set_notice("An operation is in progress. Let it finish or cancel it before closing the launcher.".into());
            }
            CloseRequestResponse::KeepWindowShown
        } else {
            CloseRequestResponse::HideWindow
        }
    });
    ui.global::<Game>().on_confirm_stop(with_window!(ui, |ui| {
        ui.set_close_after_stop(false);
        ui.set_asking_to_stop(true);
    }));
    ui.on_stop_confirmed(with_window!(ui, |ui| {
        ui.set_asking_to_stop(false);
        ui.global::<Game>().invoke_cancel();
    }));
    ui.on_request_exit(with_window!(ui, |ui| {
        let _ = ui
            .window()
            .dispatch_event_with_result(slint::platform::WindowEvent::CloseRequested);
    }));
    let weak = ui.as_weak();
    let notifications = Timer::default();
    let claimed = root.join("launcher.show.claimed");
    let signal_root = root.clone();
    notifications.start(TimerMode::Repeated, Duration::from_millis(500), move || {
        if let Some(ui) = weak.upgrade()
            && !ui.global::<Game>().get_busy()
            && (ui.get_asking_to_stop() || ui.get_close_after_stop())
        {
            let close = ui.get_close_after_stop() && !ui.get_asking_to_stop();
            ui.set_asking_to_stop(false);
            ui.set_close_after_stop(false);
            if close {
                ui.invoke_request_exit();
            }
        }
        if let Some(ui) = weak.upgrade() {
            // Claim the complete request before reading so a later click stays queued.
            if fs::rename(&poke, &claimed).is_ok() {
                if let Ok(bytes) = fs::read(&claimed)
                    && let Ok(folder) = serde_json::from_slice::<PathBuf>(&bytes)
                {
                    pending_folder.replace(Some(folder));
                }
                let _ = fs::remove_file(&claimed);
                ui.window().set_minimized(false);
                ui.window().with_winit_window(|w| w.focus_window());
            }
            if ui.get_page() != Page::ChoosePlayer
                && !busy(&ui)
                && !ui.get_modal_open()
                && let Some(folder) = pending_folder.take()
            {
                match game::select_folder(&signal_root, &folder) {
                    Ok(()) => ui.invoke_navigate(Page::Home),
                    Err(error) => ui.set_notice(format!("{error:#}").into()),
                }
            }
        }
    });
    let _controller = controller::listen(&ui);
    let tv = controller::tv_mode();
    ui.set_tv_mode(tv);
    ui.window().set_fullscreen(tv);
    if ui.get_page() == Page::Home {
        let weak = ui.as_weak();
        Timer::single_shot(Duration::ZERO, move || {
            if let Some(ui) = weak.upgrade() {
                ui.invoke_focus_home();
            }
        });
    }
    let result = ui.run();
    drop(downloads);
    result?;
    drop(ui);
    diagnostics::event("Launcher closed");
    drop(instance);
    update::restart_if_needed()
}

fn review_shortcut_grants(root: &Path, grants: &[mods::trust::Grant]) -> Result<bool> {
    let window = ShortcutTrustWindow::new().with_context(|| {
        format!(
            "Open Longshot Launcher to review permissions for {} before playing.",
            grants
                .iter()
                .map(mods::trust::Grant::name)
                .collect::<Vec<_>>()
                .join(", ")
        )
    })?;
    let settings = home::settings(root)?;
    i18n::bind(&window.global::<Locale>(), &settings);
    window
        .global::<Mods>()
        .on_review_paragraphs(mods::review_paragraphs);
    window.set_details(mods::trust::descriptions(grants).into());
    let weak = window.as_weak();
    let mut cursor = None;
    window.window().on_winit_window_event(move |_, event| {
        if let Some(visible) = window_events::focus_visibility(event, &mut cursor)
            && let Some(window) = weak.upgrade()
        {
            window.global::<Ink>().set_focus_visible(visible);
        }
        slint::winit_030::EventResult::Propagate
    });
    let allowed = std::rc::Rc::new(std::cell::Cell::new(false));
    let response = allowed.clone();
    let weak = window.as_weak();
    window.on_answer(move |allow| {
        response.set(allow);
        if let Some(window) = weak.upgrade() {
            let _ = window.hide();
        }
    });
    window
        .run()
        .context("A graphical session is required to review mod permissions.")?;
    Ok(allowed.get())
}

/// Closing the first-run chooser leaves the downloaded launcher untouched.
fn choose_install(dest: &Path) -> Result<Option<bool>> {
    #[cfg(target_os = "linux")]
    {
        use slint::winit_030::winit::platform::x11::{WindowAttributesExtX11, WindowType};

        // Only the first window is the install chooser. The portable choice
        // opens the regular launcher in this same process.
        let first_window = std::cell::Cell::new(true);
        slint::BackendSelector::new()
            .with_winit_window_attributes_hook(move |attributes| {
                if first_window.replace(false) {
                    attributes.with_x11_window_type(vec![WindowType::Dialog])
                } else {
                    attributes
                }
            })
            .select()?;
        slint::set_xdg_app_id("longshot-launcher-setup")?;
    }
    let window = SetupWindow::new()?;
    let settings = home::resolve()
        .and_then(|root| home::settings(&root))
        .unwrap_or_default();
    let language = i18n::bind(&window.global::<Locale>(), &settings);
    let parent = dest.parent().context("Missing installation folder")?;
    window.set_description(
        i18n::lookup(
            language,
            "Install for your account in {0} and open Longshot from your applications menu.",
        )
        .replace("{0}", &home::display(parent))
        .into(),
    );
    let choice = std::rc::Rc::new(std::cell::Cell::new(None));
    let answer = |install| {
        let choice = choice.clone();
        let weak = window.as_weak();
        move || {
            choice.set(Some(install));
            if let Some(window) = weak.upgrade() {
                let _ = window.hide();
            }
        }
    };
    let weak = window.as_weak();
    window.on_cancel(move || {
        if let Some(window) = weak.upgrade() {
            let _ = window.hide();
        }
    });
    window.on_confirmed(answer(true));
    window.on_secondary(answer(false));
    window.run()?;
    Ok(choice.get())
}

fn run(show_error_dialog: &mut bool) -> Result<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    *show_error_dialog = args.is_empty();
    if game::handle_nvidia_cache_argument(&args)? || update::startup(&args)? {
        return Ok(());
    }
    *show_error_dialog = true;
    match arguments(&args)? {
        Start::Window(folder) => window(home::resolve()?, folder),
        Start::Play {
            folder,
            profile,
            launcher_home,
        } => {
            let root = match launcher_home {
                Some(root) => root,
                None => home::resolve()?,
            };
            diagnostics::start(&root, true);
            if let Some(_guard) = home::acquire(&root)? {
                folders::resume_move(&root)?;
                game::registry::prepare(&root, home::is_portable(&root))?;
                players::initialize(&root)?;
                players::migrate_stable(&root)?;
            }
            let session = game::play(&root, folder.as_deref(), profile.as_deref(), |grant| {
                review_shortcut_grants(&root, grant)
            })?;
            if let Some(session) = session {
                game::wait_shortcut(session)?;
            }
            Ok(())
        }
    }
}

fn main() -> std::process::ExitCode {
    let mut show_error_dialog = false;
    match run(&mut show_error_dialog) {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            diagnostics::event("Launcher stopped after an error");
            eprintln!("Longshot Launcher: {e:#}");
            if show_error_dialog && e.downcast_ref::<game::ShortcutReported>().is_none() {
                let _ = rfd::MessageDialog::new()
                    .set_level(rfd::MessageLevel::Error)
                    .set_title("Longshot Launcher")
                    .set_description(format!("{e:#}"))
                    .show();
            }
            std::process::ExitCode::FAILURE
        }
    }
}

#[cfg(test)]
mod cli_tests {
    use super::*;
    fn parse(args: &[&str]) -> Result<Start> {
        arguments(&args.iter().map(OsString::from).collect::<Vec<_>>())
    }
    #[test]
    fn handles_shortcuts_and_unknown_options() {
        assert_eq!(
            parse(&["--extra", "--play", "A folder"]).unwrap(),
            Start::Play {
                folder: Some("A folder".into()),
                profile: None,
                launcher_home: None,
            }
        );
        assert_eq!(
            parse(&["--play"]).unwrap(),
            Start::Play {
                folder: None,
                profile: None,
                launcher_home: None,
            }
        );
        assert_eq!(
            parse(&["--profile", "Player Two", "--play", "Version One"]).unwrap(),
            Start::Play {
                folder: Some("Version One".into()),
                profile: Some("Player Two".into()),
                launcher_home: None,
            }
        );
        assert!(
            matches!(parse(&["--play", "--launcher-home", "Control"]), Ok(Start::Play { launcher_home: Some(path), .. }) if path.is_absolute())
        );
        assert!(parse(&["--launcher-home", "Control"]).is_err());
        assert!(parse(&["--play", "--profile"]).is_err());
        assert!(parse(&["--profile", "unused"]).is_err());
        assert!(parse(&["--launch", "--play"]).is_err());
        assert!(parse(&["--launch", "dir", "--play"]).is_err());
    }
}
