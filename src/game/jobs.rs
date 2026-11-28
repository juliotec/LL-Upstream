use super::{
    process::{Cancel, Context},
    refresh,
    releases::{self, Target},
};
use crate::UiMutexExt as _;
use crate::{Game, MainWindow};
use anyhow::Result;
use slint::ComponentHandle;
use std::{
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
    thread,
};

#[derive(Clone)]
pub(super) struct Prepared {
    pub(super) ctx: Context,
    pub(super) game: PathBuf,
    pub(super) exe: PathBuf,
    pub(super) tag: String,
    pub(super) intent: super::wizard::BuildIntent,
}

impl Prepared {
    fn installed_folder(&self) -> Option<&Path> {
        self.intent
            .instance()
            .map(|v| v.folder.as_path())
            .or_else(|| self.exe.parent())
    }

    fn is_selected(&self, home_dir: &Path) -> bool {
        crate::players::current_dir(home_dir).is_ok_and(|profile| profile == self.ctx.profile)
            && super::versions::selected(home_dir, None).is_ok_and(|version| {
                version.tag == self.tag && self.installed_folder() == Some(version.folder.as_path())
            })
    }
}

#[derive(Default)]
pub(super) struct Work {
    pub(super) check: super::autocheck::State,
    pub(super) preload: Cancel,
    pub(super) downloads: Vec<(Cancel, thread::JoinHandle<()>)>,
    pub(super) generation: u64,
    pub(super) review: Option<Target>,
    pub(super) prepared: Option<Prepared>,
    pub(super) mod_review: Option<super::mod_review::Review>,
    pub(super) cancel: Cancel,
    pub(super) launch_cancel: Cancel,
    pub(super) report: Option<String>,
    pub(super) intent: super::wizard::BuildIntent,
    pub(super) finished_build: Option<(PathBuf, PathBuf, std::time::Duration)>,
}

impl Work {
    pub(super) fn take_build_time(
        &mut self,
        from_build_completion: bool,
        profile: &Path,
        folder: &Path,
    ) -> Option<std::time::Duration> {
        self.finished_build
            .take()
            .and_then(|(built_profile, built_folder, elapsed)| {
                (from_build_completion && built_profile == profile && built_folder == folder)
                    .then_some(elapsed)
            })
    }
}

pub(super) type SharedWork = Arc<Mutex<Work>>;
pub(super) enum Progress {
    ModTrust(
        super::mod_review::Request,
        Result<Vec<crate::mods::trust::Grant>>,
    ),
    Review(releases::Source),
    Checked(Box<Prepared>, Result<bool>),
    Built(std::time::Duration),
    Problem {
        why: String,
        report: String,
    },
    Message(String),
    Removed,
}

pub(super) fn worker(
    ui: &MainWindow,
    home_dir: &Path,
    work: &SharedWork,
    task: impl FnOnce(&Cancel, &mut dyn FnMut(&str, Option<f32>)) -> Result<Progress> + Send + 'static,
) {
    let global = ui.global::<Game>();
    if global.get_busy() {
        return;
    }
    let cancel = Cancel::default();
    work.guard().cancel = cancel.clone();
    global.set_busy(true);
    let weak = ui.as_weak();
    let state = work.clone();
    let check = state.guard().check.active;
    let home_dir = home_dir.to_owned();
    thread::spawn(move || {
        let mut last = std::time::Instant::now() - std::time::Duration::from_secs(1);
        let mut previous = String::new();
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            task(&cancel, &mut |msg, fraction| {
                if previous == msg && last.elapsed().as_millis() < 80 {
                    return;
                }
                last = std::time::Instant::now();
                previous = msg.to_owned();
                let msg = msg.to_owned();
                let state = state.clone();
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    if let Some(revision) = check
                        && !super::autocheck::current(&ui, &state.guard().check, revision)
                    {
                        return;
                    }
                    let g = ui.global::<Game>();
                    if check.is_some()
                        && let Some(value) = fraction
                    {
                        g.set_check_fraction(value);
                    }
                    g.set_status(msg.into());
                });
            })
        }))
        .unwrap_or_else(|_| {
            crate::diagnostics::event("Game worker panicked");
            Err(anyhow::anyhow!(
                "The game operation stopped unexpectedly. Try again."
            ))
        });
        let _ = weak.upgrade_in_event_loop(move |ui| {
            if let Some(revision) = check {
                let mut work = state.guard();
                let valid = super::autocheck::current(&ui, &work.check, revision);
                work.check.active = None;
                drop(work);
                if !valid {
                    ui.global::<Game>().set_busy(false);
                    super::autocheck::schedule(&ui, &home_dir, &state, false);
                    return;
                }
            }
            finish(&ui, &state, &home_dir, result);
        });
    });
}

fn finish(ui: &MainWindow, state: &SharedWork, home_dir: &Path, result: Result<Progress>) {
    let g = ui.global::<Game>();
    let report_ui = ui.global::<crate::BuildReport>();
    g.set_busy(false);
    match result {
        Ok(Progress::ModTrust(request, grant)) => {
            if let Err(error) = super::mod_review::ready(ui, home_dir, state, request, grant) {
                ui.set_notice(format!("{error:#}").into());
            }
        }
        Ok(Progress::Review(s)) => {
            g.set_warning(s.warning.clone().into());
            g.set_confirm_number(
                s.unapproved
                    .map(|n| n.to_string())
                    .unwrap_or_default()
                    .into(),
            );
            g.set_source_approved(false);
            g.set_typed_number("".into());
            g.set_status("".into());
            g.set_pending_tag(s.tag.clone().into());
            state.guard().review = Some(Target::Source(s));
        }
        Ok(Progress::Checked(p, mod_review)) => {
            crate::diagnostics::event("Game file check passed");
            g.set_pending_tag(p.tag.clone().into());
            if let Some(dir) = p.installed_folder() {
                g.set_build_location(dir.join("builds/default").to_string_lossy().as_ref().into());
            }
            // File verification succeeds independently of the permissions query.
            // Build always checks the selected builder and profile again.
            match mod_review {
                Ok(needed) => g.set_mod_review_needed(needed),
                Err(error) => {
                    g.set_mod_review_needed(false);
                    ui.set_notice(format!("Could not check mod permissions. {error:#}").into());
                }
            }
            state.guard().prepared = Some(*p);
            g.set_step(crate::GameStep::Settings);
            g.set_status("".into());
        }
        Ok(Progress::Removed) => {
            g.set_confirming_removal(false);
            g.set_status("Version removed.".into());
            g.invoke_show_home();
            refresh(ui, home_dir);
        }
        Ok(Progress::Message(msg)) => {
            refresh(ui, home_dir);
            g.set_status(msg.into());
        }
        Ok(Progress::Problem { why, report }) => {
            g.set_play_after_build(false);
            crate::diagnostics::event("Game build did not complete");
            state.guard().report = Some(report);
            report_ui.set_can_report(true);
            report_ui.set_report_preview("".into());
            report_ui.set_report_sent(false);
            report_ui.set_reporting(false);
            report_ui.set_report_status("".into());
            g.set_status(why.into());
            g.set_step(crate::GameStep::Problem);
        }
        Ok(Progress::Built(duration)) => {
            let mut work = state.guard();
            work.finished_build = work.prepared.as_ref().and_then(|p| {
                Some((
                    p.ctx.profile.clone(),
                    p.installed_folder()?.to_owned(),
                    duration,
                ))
            });
            let play = g.get_play_after_build()
                && work.cancel.check().is_ok()
                && work
                    .prepared
                    .as_ref()
                    .is_some_and(|p| p.is_selected(home_dir));
            g.set_play_after_build(false);
            drop(work);
            crate::diagnostics::event("Game build completed");
            g.set_first_run(false);
            super::info::invalidate();
            g.set_step(crate::GameStep::Ready);
            g.set_status(
                "Longshot is ready. Your profile's saves and settings follow you when you play."
                    .into(),
            );
            refresh(ui, home_dir);
            if play && ui.get_page() == crate::Page::Build && !g.get_playing() {
                g.invoke_play_completed_build();
            }
        }
        Err(error) => {
            g.set_play_after_build(false);
            g.set_file_error(
                g.get_step() == crate::GameStep::Files
                    && error
                        .downcast_ref::<super::process::BuilderFailure>()
                        .is_none_or(|failure| failure.is_file_error()),
            );
            if g.get_step() == crate::GameStep::Building {
                crate::diagnostics::event("Game build did not complete");
                state.guard().report = None;
                report_ui.set_can_report(false);
                report_ui.set_report_preview("".into());
                g.set_step(crate::GameStep::Problem);
            } else {
                crate::diagnostics::event("Game operation did not complete");
                refresh(ui, home_dir);
            }
            g.set_status(format!("{error:#}").into());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use anyhow::Result;
    use slint::platform::{
        Platform, WindowAdapter,
        software_renderer::{MinimalSoftwareWindow, RepaintBufferType},
    };
    use std::{cell::Cell, fs, rc::Rc};

    #[test]
    fn play_after_leaving_build_completion_does_not_reuse_its_duration() {
        let profile = Path::new("profiles/default");
        let folder = Path::new("versions/nightly");
        let duration = std::time::Duration::from_secs(495);
        let completed = || Some((profile.into(), folder.into(), duration));
        let mut work = Work {
            finished_build: completed(),
            ..Work::default()
        };
        assert_eq!(work.take_build_time(false, profile, folder), None);
        assert_eq!(work.take_build_time(true, profile, folder), None);
        work.finished_build = completed();
        assert_eq!(work.take_build_time(true, profile, folder), Some(duration));
        assert_eq!(work.take_build_time(true, profile, folder), None);
    }

    // Isolate Slint's platform, profile paths and Steam discovery from other tests and the desktop.
    #[test]
    fn update_plays_only_its_completed_version_and_profile_once() -> Result<()> {
        let Some(root) = std::env::var_os("LONGSHOT_TEST_UPDATE_PLAY") else {
            let scratch = tempfile::tempdir()?;
            let status = std::process::Command::new(std::env::current_exe()?)
                .args([
                    "--exact",
                    "game::jobs::tests::update_plays_only_its_completed_version_and_profile_once",
                    "--nocapture",
                ])
                .env("LONGSHOT_TEST_UPDATE_PLAY", scratch.path())
                .env("HOME", scratch.path())
                .env("XDG_DATA_HOME", scratch.path())
                .env("APPDATA", scratch.path())
                .env("LOCALAPPDATA", scratch.path())
                .status()?;
            anyhow::ensure!(
                status.success(),
                "The isolated update-and-play check failed."
            );
            return Ok(());
        };
        struct Display;
        impl Platform for Display {
            fn create_window_adapter(&self) -> Result<Rc<dyn WindowAdapter>, slint::PlatformError> {
                Ok(MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer))
            }
        }
        slint::platform::set_platform(Box::new(Display)).unwrap();
        let ui = MainWindow::new()?;
        let home = Path::new(&root);
        let tag = "nightly-15-abcdef12";
        let folder = home.join("versions").join(tag);
        crate::game::fake_version(&folder)?;
        let profile = home.join("profiles/default");
        fs::create_dir_all(&profile)?;
        crate::home::write_json(
            &profile.join("profile.json"),
            &serde_json::json!({"v":1,"name":"Player","version":tag}),
        )?;
        crate::home::write_json(
            &home.join("settings.json"),
            &serde_json::json!({"format":2,"currentProfile":"default","checkUpdates":false}),
        )?;

        let calls = Rc::new(Cell::new(0));
        let plays = calls.clone();
        ui.global::<Game>()
            .on_play_completed_build(move || plays.set(plays.get() + 1));
        ui.set_page(crate::Page::Build);
        let state = SharedWork::default();
        let requested = |profile: &Path, tag: &str| {
            let mut ctx = Context::load(home).unwrap();
            ctx.profile = profile.into();
            state.guard().prepared = Some(Prepared {
                ctx,
                game: home.join("sample.nup"),
                exe: folder.join(super::super::process::executable()),
                intent: Default::default(),
                tag: tag.into(),
            });
            ui.global::<Game>().set_play_after_build(true);
        };
        let built = || {
            finish(
                &ui,
                &state,
                home,
                Ok(Progress::Built(std::time::Duration::from_secs(12))),
            )
        };
        requested(&profile, tag);
        finish(&ui, &state, home, Err(anyhow::anyhow!("Update cancelled")));
        built();
        assert_eq!(calls.get(), 0);
        requested(&profile, "nightly-14-abcdef01");
        built();
        requested(&home.join("profiles/other"), tag);
        built();
        assert_eq!(calls.get(), 0);
        requested(&profile, tag);
        state.guard().prepared.as_mut().unwrap().exe = home
            .join("other-copy")
            .join(super::super::process::executable());
        built();
        requested(&profile, tag);
        ui.global::<Game>().set_play_after_build(false);
        built();
        requested(&profile, tag);
        state.guard().cancel.request();
        built();
        state.guard().cancel = Cancel::default();
        assert_eq!(calls.get(), 0);
        requested(&profile, tag);
        built();
        built();
        assert_eq!(calls.get(), 1);
        Ok(())
    }
}
