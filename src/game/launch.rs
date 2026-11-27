//! Observe the builder's play command without owning the game's lifetime.
use super::{process::Cancel, versions::Session};
use crate::UiMutexExt as _;
use crate::{
    Game, MainWindow,
    logbook::{BuildLog, Transcript},
};
use serde_json::Value;
use slint::ComponentHandle;
use std::{
    fs::File,
    io::{self, BufRead, BufReader},
    path::Path,
    process::{Command, ExitStatus, Stdio},
    thread,
    time::{Duration, Instant},
};
use sysinfo::{Pid, ProcessRefreshKind, ProcessesToUpdate, System, UpdateKind};

pub(super) fn play(
    window: &MainWindow,
    home: &Path,
    work: &super::jobs::SharedWork,
    folder: &Path,
    profile: &Path,
) -> anyhow::Result<()> {
    let g = window.global::<Game>();
    if g.get_playing() {
        return Ok(());
    }
    g.set_show_log(false);
    g.set_log_rows(Default::default());
    g.set_log_tail(false);
    g.set_log_filter("".into());
    g.set_launch_log_available(false);
    g.set_launch_phase("launching".into());
    g.set_launch_can_cancel(false);
    g.set_launch_cancelling(false);
    g.set_launch_fraction(-1.);
    g.set_launch_eta("".into());
    g.set_launch_step("".into());
    let from_build_completion =
        window.get_page() == crate::Page::Build && g.get_step() == crate::GameStep::Ready;
    g.set_last_build_seconds(-1);
    g.set_last_rebuild_seconds(-1);
    window
        .global::<crate::Shell>()
        .set_popup(crate::Popup::Closed);
    window.set_page(crate::Page::Launch);
    let mut session =
        super::versions::start_for(home, folder, profile, super::versions::LaunchMode::Window)
            .inspect_err(|_| {
                g.invoke_show_home();
                crate::diagnostics::event("Game launch failed");
            })?;
    g.set_status("".into());
    window.global::<crate::Shell>().set_crash_message("".into());
    if let Some(elapsed) =
        work.guard()
            .take_build_time(from_build_completion, &session.profile, &session.folder)
    {
        g.set_last_build_seconds(elapsed.as_secs().min(i32::MAX as u64) as i32);
    }
    let cancel = Cancel::default();
    work.guard().launch_cancel = cancel.clone();
    g.set_playing(true);
    let weak = window.as_weak();
    let home = home.to_owned();
    thread::spawn(move || {
        let result = wait(&mut session, &weak, &home, &cancel);
        let _ = weak
            .upgrade_in_event_loop(move |ui| finished(&ui, &home, session.steam_opener, result));
    });
    Ok(())
}

pub(super) fn cancel_rebuild(
    ui: &MainWindow,
    _: &Path,
    work: &super::jobs::SharedWork,
) -> anyhow::Result<()> {
    let g = ui.global::<Game>();
    if g.get_playing() && g.get_launch_can_cancel() && !g.get_launch_cancelling() {
        g.set_launch_cancelling(true);
        work.guard().launch_cancel.request();
    }
    Ok(())
}

fn finished(
    ui: &MainWindow,
    home: &Path,
    steam_opener: bool,
    result: io::Result<Option<ExitStatus>>,
) {
    let g = ui.global::<Game>();
    g.set_playing(false);
    g.set_launch_can_cancel(false);
    g.set_launch_cancelling(false);
    if ui.get_page() == crate::Page::Launch {
        g.set_show_log(false);
        if steam_opener
            && result
                .as_ref()
                .is_ok_and(|s| s.is_some_and(|s| s.success()))
        {
            g.set_launch_phase("steam".into());
        } else {
            g.invoke_show_home();
        }
    }
    if !steam_opener {
        let _ = crate::players::refresh_saves(ui, home);
    }
    let note = match result {
        Ok(None) => "",
        Ok(Some(status)) if steam_opener => {
            if status.success() {
                ""
            } else {
                "Steam could not open the game shortcut."
            }
        }
        Ok(Some(status)) => match status.code() {
            Some(120) => "Choose your game files and build this version for the current profile.",
            Some(121) => "Longshot could not build or start the game.",
            Some(123) => "This profile is already playing or building.",
            Some(0 | 130 | 143 | -1073741510) => "",
            _ if !status.success() => {
                ui.global::<crate::Shell>()
                    .set_crash_message(format!("Longshot exited with {status}.").into());
                crate::diagnostics::event("Game process ended unexpectedly");
                ""
            }
            _ => "",
        },
        Err(_) => "Longshot's exit status could not be read.",
    };
    if !note.is_empty() {
        crate::diagnostics::event("Game ended with an error");
        g.set_status(note.into());
    }
}

struct Startup {
    base: f64,
    fraction: Option<f64>,
    eta: Option<Instant>,
}

#[derive(Default)]
struct Rebuild {
    play_plan: bool,
    game_started: bool,
    build_finished: bool,
    startup: Option<Startup>,
    builder: super::forecast::BuilderProgress,
    started: Option<Instant>,
    shaders_started: Option<Instant>,
    elapsed: Option<Duration>,
    step: String,
    fraction: Option<f64>,
    eta: Option<Instant>,
}

impl Rebuild {
    fn can_cancel(&self) -> bool {
        self.started.is_some()
            && !self.build_finished
            && !self.game_started
            && self.startup.is_none()
    }

    fn observe(&mut self, event: &Value, now: Instant) {
        let kind = event["type"].as_str();
        let stage = event["stage"].as_str();
        let label = match stage {
            Some("shaders") => Some("Preparing shaders"),
            _ => event["text"].as_str().filter(|s| !s.is_empty()),
        };
        self.builder.observe(event, now);
        match kind {
            Some("play_plan") => {
                let rebuilding = event["rebuild"].is_string();
                *self = Self {
                    play_plan: true,
                    started: rebuilding.then_some(now),
                    elapsed: if rebuilding { None } else { self.elapsed },
                    ..Self::default()
                };
            }
            Some("game_starting") => {
                self.game_started = false;
                let base = self.builder.fraction.unwrap_or(0.0);
                self.startup = Some(Startup {
                    base,
                    fraction: self.builder.fraction,
                    eta: self
                        .builder
                        .seconds(now)
                        .and_then(|seconds| now.checked_add(Duration::from_secs(seconds))),
                });
                self.step = crate::i18n::key("Starting the game").into();
            }
            Some("startup_progress") => {
                if let Some(startup) = &mut self.startup {
                    self.step = match stage {
                        Some("shaders") => "Preparing shaders",
                        Some("pipelines") => crate::i18n::key("Loading pipelines"),
                        _ => return,
                    }
                    .into();
                    if let Some(permille) = event["permille"].as_u64().filter(|&n| n <= 1000) {
                        let fraction =
                            startup.base + (1.0 - startup.base) * (permille as f64 / 1000.0);
                        startup.fraction =
                            Some(fraction.max(startup.fraction.unwrap_or(0.0)).min(0.99));
                        if permille == 1000 {
                            self.step = crate::i18n::key("Starting the game").into();
                        }
                    }
                    startup.eta = event["seconds"]
                        .as_u64()
                        .and_then(|seconds| now.checked_add(Duration::from_secs(seconds)));
                }
            }
            Some("game_started") => {
                self.game_started = self.startup.is_none();
                self.finish_rebuild(now);
            }
            Some("game_ready") if self.startup.take().is_some() => {
                self.game_started = true;
                self.finish_rebuild(now);
            }
            Some("build_plan") => {
                self.started.get_or_insert(now);
                self.build_finished = false;
            }
            Some("stage" | "progress") if self.builder.planned() => {
                if kind == Some("stage")
                    && let Some(text) = label
                {
                    self.step = text.to_owned();
                }
            }
            Some("stage" | "progress") if !self.game_started => {
                self.elapsed = None;
                let start = *self.started.get_or_insert(now);
                if let Some(stage) = stage {
                    let shaders = stage == "shaders";
                    if shaders != self.shaders_started.is_some() {
                        self.shaders_started = shaders.then_some(now);
                        self.fraction = None;
                        self.eta = None;
                    }
                }
                if let Some(text) = label {
                    self.step = text.to_owned();
                } else if kind == Some("stage") || self.step.is_empty() {
                    self.step = stage.unwrap_or_default().to_owned();
                }
                // The builder assigns shaders no share of overall progress and reports them separately.
                let (fraction, since) = if let Some(shaders_started) = self.shaders_started {
                    (
                        event["fraction"]
                            .as_f64()
                            .filter(|f| (0. ..=1.).contains(f)),
                        shaders_started,
                    )
                } else {
                    (
                        event["overall"].as_f64().filter(|f| *f > 0. && *f < 1.),
                        start,
                    )
                };
                if let Some(f) = fraction {
                    self.fraction = Some(if self.shaders_started.is_some() {
                        f
                    } else {
                        f.min(0.99)
                    });
                    let elapsed = now.duration_since(since).as_secs_f64();
                    self.eta = if elapsed >= 1. && f > 0. && f < 1. {
                        deadline(now, elapsed * (1. - f) / f)
                    } else {
                        None
                    };
                }
            }
            Some("eta") if !self.builder.planned() && self.started.is_some() => {
                self.eta = event["seconds"].as_f64().and_then(|s| deadline(now, s));
            }
            Some("done") if self.play_plan && self.builder.planned() => {
                self.build_finished = true;
                self.step = "Game launching.".into();
            }
            Some("done") => {
                if let Some(started) = self.started {
                    *self = Self {
                        elapsed: Some(now.saturating_duration_since(started)),
                        ..Self::default()
                    };
                }
            }
            _ => {}
        }
    }

    fn finish_rebuild(&mut self, now: Instant) {
        if let Some(started) = self.started.take() {
            self.elapsed = Some(now.saturating_duration_since(started));
        }
    }
}

fn deadline(now: Instant, seconds: f64) -> Option<Instant> {
    now.checked_add(Duration::try_from_secs_f64(seconds).ok()?)
}

fn running(system: &mut System, builder: Pid, folder: &Path) -> bool {
    system.refresh_processes_specifics(
        ProcessesToUpdate::All,
        true,
        ProcessRefreshKind::nothing()
            .without_tasks()
            .with_exe(UpdateKind::OnlyIfNotSet),
    );
    system.processes().values().any(|p| {
        let Some(exe) = p.exe() else { return false };
        if !exe
            .file_stem()
            .is_some_and(|s| s.eq_ignore_ascii_case("longshot"))
        {
            return false;
        }
        let in_build = exe.starts_with(folder)
            || (cfg!(windows)
                && exe
                    .canonicalize()
                    .is_ok_and(|path| path.starts_with(folder)));
        if !in_build {
            return false;
        }
        let mut parent = p.parent();
        while let Some(pid) = parent {
            if pid == builder {
                return true;
            }
            parent = system.process(pid).and_then(|p| p.parent());
        }
        false
    })
}

/// A game may inherit these handles from the builder. Unlike a pipe, they stay
/// writable after the launcher and its log readers exit.
pub(super) struct LaunchOutput {
    stdout: OutputFile,
    stderr: OutputFile,
}

impl LaunchOutput {
    pub(super) fn attach(command: &mut Command, dir: &std::path::Path) -> io::Result<Self> {
        std::fs::create_dir_all(dir)?;
        if crate::fsx::linked_location(dir)? {
            return Err(io::Error::other("The launcher log folder is linked."));
        }
        let (stdout, out) = OutputFile::new_in(dir)?;
        let (stderr, err) = OutputFile::new_in(dir)?;
        command.stdout(Stdio::from(out)).stderr(Stdio::from(err));
        Ok(Self { stdout, stderr })
    }
}

pub(crate) fn wait_shortcut(mut session: Session) -> anyhow::Result<()> {
    let status = session.child.wait()?;
    if cfg!(windows) && matches!(status.code(), Some(120 | 121 | 123)) {
        // These play failures have their own dialog in the builder's user mode.
        return Err(super::process::ShortcutReported(status).into());
    }
    if !status.success() {
        let mut message = None;
        let mut last_stderr = None;
        if let Some(mut output) = session.output {
            output.stdout.drain(true, |line| {
                if let Ok(event) = serde_json::from_str::<Value>(line)
                    && event["type"] == "error"
                {
                    message = event["text"].as_str().map(str::to_owned);
                }
            })?;
            output.stderr.drain(true, |line| {
                if !line.trim().is_empty() {
                    last_stderr = Some(line.to_owned());
                }
            })?;
        }
        anyhow::bail!(
            "{}",
            message
                .or(last_stderr)
                .unwrap_or_else(|| format!("Longshot stopped with {status}."))
        );
    }
    Ok(())
}

struct OutputFile {
    reader: BufReader<File>,
    pending: Vec<u8>,
}

impl OutputFile {
    fn new_in(dir: &std::path::Path) -> io::Result<(Self, File)> {
        let file = tempfile::NamedTempFile::new_in(dir)?;
        let reader = BufReader::new(file.reopen()?);
        Ok((
            Self {
                reader,
                pending: Vec::new(),
            },
            file.into_file(),
        ))
    }

    fn drain(&mut self, finished: bool, mut line: impl FnMut(&str)) -> io::Result<()> {
        loop {
            let read = self.reader.read_until(b'\n', &mut self.pending)?;
            if self.pending.ends_with(b"\n") || (finished && !self.pending.is_empty()) {
                line(String::from_utf8_lossy(&self.pending).trim_end_matches(['\r', '\n']));
                self.pending.clear();
            }
            if read == 0 {
                return Ok(());
            }
        }
    }
}

fn wait(
    session: &mut Session,
    window: &slint::Weak<MainWindow>,
    home: &Path,
    cancel: &Cancel,
) -> io::Result<Option<ExitStatus>> {
    let Some(mut output) = session.output.take() else {
        return session.child.wait().map(Some);
    };
    let mut transcript = Transcript::start(home, BuildLog::Launch)
        .inspect_err(|e| eprintln!("Could not open the local launch log: {e}"))
        .ok();
    let available = transcript.is_some();
    let _ = window
        .upgrade_in_event_loop(move |ui| ui.global::<Game>().set_launch_log_available(available));
    let mut state = Rebuild::default();
    let mut reading_output = true;
    let mut inhibitor = None;

    let mut system = System::new().ok();
    let mut last_scan = None;
    let mut previous = None;
    let builds = session.folder.join("builds");
    loop {
        let exit = session.child.try_wait()?;
        let now = Instant::now();
        if reading_output {
            for (kind, file) in [
                ("stdout", &mut output.stdout),
                ("stderr", &mut output.stderr),
            ] {
                if let Err(error) = file.drain(exit.is_some(), |text| {
                    let event = (kind == "stdout" && text.starts_with('{'))
                        .then(|| serde_json::from_str::<Value>(text).ok())
                        .flatten()
                        .filter(|event| event["v"] == 1);
                    // While playing, keep listening for reloads without recording ordinary game output.
                    if (!state.game_started || event.is_some())
                        && let Some(log) = &mut transcript
                        && let Err(error) = log.line(kind, text)
                    {
                        eprintln!("Could not write the local launch log: {error}");
                        transcript = None;
                    }
                    if let Some(event) = event {
                        state.observe(&event, now);
                        if event["type"] == "warning" && event["code"] == "mod_needs_import" {
                            let _ = window.upgrade_in_event_loop(|ui| {
                                ui.set_notice("Some mods still need importing. After closing the game, open Mods and choose the extracted mod folder.".into());
                            });
                        }
                    }
                }) {
                    eprintln!("Could not read game launch output: {error}");
                    reading_output = false;
                }
            }
        }
        if let Some(log) = &mut transcript
            && let Err(error) = log.flush()
        {
            eprintln!("Could not flush the local launch log: {error}");
            transcript = None;
        }
        if let Some(exit) = exit {
            return Ok(Some(exit));
        }
        if cancel.take_request() {
            // Process the latest events first so a click at handoff leaves a starting game alone.
            if state.can_cancel() {
                match session.cancel_rebuild() {
                    Ok(()) => return session.child.wait().map(|_| None),
                    Err(error) => eprintln!("Could not cancel Longshot's rebuild: {error}"),
                }
            }
            let _ =
                window.upgrade_in_event_loop(|ui| ui.global::<Game>().set_launch_cancelling(false));
        }
        let rebuilding = state.started.is_some();
        let loading = state.startup.is_some();
        if (rebuilding || loading) && inhibitor.is_none() {
            inhibitor = Some(super::awake::BuildInhibitor::acquire());
        } else if !rebuilding && !loading {
            inhibitor.take();
        }
        let seconds_left = |until: Option<Instant>| {
            until
                .map(|until| until.saturating_duration_since(now).as_secs())
                .filter(|s| *s > 0)
        };
        let (fraction, eta) = if let Some(startup) = &state.startup {
            (startup.fraction, seconds_left(startup.eta))
        } else if state.builder.planned() {
            (state.builder.fraction, state.builder.seconds(now))
        } else {
            let eta = state
                .started
                .filter(|started| now.duration_since(*started) >= super::forecast::ETA_WARMUP)
                .and_then(|_| seconds_left(state.eta));
            (state.fraction, eta)
        };
        let fraction = fraction.map_or(-1., |f| f as f32);
        let step = state.step.clone();
        let rebuild_seconds = state
            .elapsed
            .map_or(-1, |elapsed| elapsed.as_secs().min(i32::MAX as u64) as i32);
        if !state.play_plan
            && !state.game_started
            && !rebuilding
            && !loading
            && last_scan.is_none_or(|t| now.duration_since(t) >= Duration::from_secs(1))
        {
            last_scan = Some(now);
            if let Some(system) = &mut system
                && running(system, Pid::from_u32(session.child.id()), &builds)
            {
                state.game_started = true;
            }
        }
        let phase = if state.game_started {
            "running"
        } else if loading {
            "starting"
        } else if rebuilding {
            "rebuilding"
        } else {
            "launching"
        };
        let can_cancel = state.can_cancel();
        let view = (phase, fraction, eta, step, rebuild_seconds, can_cancel);
        if previous.as_ref() != Some(&view) {
            let step = view.3.clone();
            previous = Some(view);
            let _ = window.upgrade_in_event_loop(move |ui| {
                let g = ui.global::<Game>();
                if rebuilding {
                    g.set_last_build_seconds(-1);
                }
                g.set_launch_phase(phase.into());
                g.set_launch_can_cancel(can_cancel);
                g.set_last_rebuild_seconds(rebuild_seconds);
                g.set_launch_step(step.into());
                g.set_launch_fraction(fraction);
                g.set_launch_eta(
                    eta.map(super::forecast::remaining)
                        .unwrap_or_default()
                        .into(),
                );
            });
        }
        thread::sleep(Duration::from_millis(if state.game_started {
            500
        } else {
            200
        }));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[cfg(unix)]
    #[test]
    fn cancelling_a_launch_rebuild_stops_its_builder() -> anyhow::Result<()> {
        let temp = tempfile::tempdir()?;
        let mut command = super::super::process::launch_program(Path::new("sh"), true);
        command
            .args([
                "-c",
                r#"printf '%s\n' '{"v":1,"type":"stage","stage":"compile"}'; touch ready; sleep 8"#,
            ])
            .current_dir(temp.path());
        let output = LaunchOutput::attach(&mut command, temp.path())?;
        let mut session = Session {
            child: command.spawn()?,
            output: Some(output),
            steam_opener: false,
            folder: temp.path().into(),
            profile: temp.path().into(),
        };
        let deadline = Instant::now() + Duration::from_secs(4);
        while !temp.path().join("ready").is_file() {
            anyhow::ensure!(
                Instant::now() < deadline,
                "The fixture did not start building"
            );
            thread::sleep(Duration::from_millis(10));
        }
        let cancel = Cancel::default();
        cancel.request();
        assert!(wait(&mut session, &slint::Weak::default(), temp.path(), &cancel)?.is_none());
        assert!(!session.child.wait()?.success());
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn game_can_keep_writing_after_the_launcher_process_exits() -> anyhow::Result<()> {
        const FIXTURE: &str = "LONGSHOT_TEST_ORPHANED_LAUNCH_OUTPUT";
        if let Some(path) = std::env::var_os(FIXTURE) {
            let mut command = Command::new("sh");
            command.current_dir(path).args([
                "-ec",
                "while [ ! -e launcher-exited ]; do sleep .02; done; \
                 printf 'still running\\n'; printf 'still running\\n' >&2; touch game-finished",
            ]);
            let output = LaunchOutput::attach(&mut command, &std::env::temp_dir())?;
            command.spawn()?;
            drop(output);
            return Ok(());
        }
        let temp = tempfile::tempdir()?;
        let status = Command::new(std::env::current_exe()?)
            .args([
                "--exact",
                "game::launch::tests::game_can_keep_writing_after_the_launcher_process_exits",
            ])
            .env(FIXTURE, temp.path())
            .status()?;
        assert!(status.success());
        std::fs::write(temp.path().join("launcher-exited"), [])?;
        let deadline = Instant::now() + Duration::from_secs(5);
        while !temp.path().join("game-finished").is_file() {
            anyhow::ensure!(
                Instant::now() < deadline,
                "The game stopped after the launcher exited"
            );
            thread::sleep(Duration::from_millis(20));
        }
        Ok(())
    }

    #[test]
    fn launch_output_keeps_incomplete_lines_until_the_builder_finishes_them() -> io::Result<()> {
        use std::io::Write;

        let (mut output, mut writer) = OutputFile::new_in(&std::env::temp_dir())?;
        let mut lines = Vec::new();
        writer.write_all(b"compil")?;
        output.drain(false, |line| lines.push(line.to_owned()))?;
        assert!(lines.is_empty());
        writer.write_all(b"ing\nfinal message")?;
        output.drain(false, |line| lines.push(line.to_owned()))?;
        assert_eq!(lines, ["compiling"]);
        output.drain(true, |line| lines.push(line.to_owned()))?;
        assert_eq!(lines, ["compiling", "final message"]);
        Ok(())
    }

    #[test]
    fn rebuild_time_follows_builder_events() {
        let now = Instant::now();
        let mut state = Rebuild::default();
        state.observe(
            &json!({"type":"stage","stage":"compile","text":"Compiling"}),
            now,
        );
        state.observe(
            &json!({"type":"progress","overall":0.25}),
            now + Duration::from_secs(2),
        );
        assert_eq!(state.step, "Compiling");
        assert_eq!(state.fraction, Some(0.25));
        assert_eq!(state.eta, Some(now + Duration::from_secs(8)));
        state.observe(
            &json!({"type":"stage","stage":"shaders","text":"Preparing shaders"}),
            now + Duration::from_secs(5),
        );
        assert_eq!(state.step, "Preparing shaders");
        assert!(state.fraction.is_none());
        assert!(state.eta.is_none());
        state.observe(
            &json!({"type":"progress","stage":"shaders","fraction":0.25,"overall":1.0}),
            now + Duration::from_secs(6),
        );
        assert_eq!(state.fraction, Some(0.25));
        assert_eq!(state.eta, Some(now + Duration::from_secs(9)));
        state.observe(
            &json!({"type":"progress","stage":"shaders","fraction":1.0,"overall":1.0}),
            now + Duration::from_secs(7),
        );
        assert_eq!(state.fraction, Some(1.0));
        assert!(state.started.is_some());
        assert!(state.eta.is_none());
        state.observe(&json!({"type":"done"}), now + Duration::from_secs(7));
        assert!(state.step.is_empty());
        assert!(state.started.is_none());
        assert!(state.eta.is_none());
        assert_eq!(state.elapsed, Some(Duration::from_secs(7)));
        state.observe(&json!({"type":"done"}), now + Duration::from_secs(9));
        assert_eq!(state.elapsed, Some(Duration::from_secs(7)));
        let mut unchanged = Rebuild::default();
        unchanged.observe(&json!({"type":"done"}), now);
        assert!(unchanged.elapsed.is_none());
    }
    #[test]
    fn builder_progress_spans_the_shader_stage_and_game_handoff_on_each_reload() {
        let start = Instant::now();
        let mut state = Rebuild::default();
        let planned = json!({"type":"play_plan","rebuild":"the game asked to build again"});
        let plan = json!({"type":"build_plan","stages":["read","shaders","start"]});
        let progress = json!({"type":"build_progress","fraction":0.64,"seconds":40});
        for offset in [0, 60] {
            let now = start + Duration::from_secs(offset);
            state.observe(&planned, now);
            assert!(!state.game_started);
            assert_eq!(state.started, Some(now));
            assert!(state.can_cancel());
            assert!(state.elapsed.is_none());
            state.observe(&plan, now);
            state.observe(
                &json!({"type":"stage","stage":"shaders","text":"Preparing the shaders"}),
                now,
            );
            state.observe(
                &json!({"type":"progress","stage":"shaders","fraction":0.25,"overall":1.0}),
                now,
            );
            state.observe(&progress, now);
            assert_eq!(state.step, "Preparing shaders");
            assert_eq!(state.builder.fraction, Some(0.64));
            assert_eq!(
                state.builder.seconds(now + Duration::from_secs(10)),
                Some(30)
            );
            state.observe(&json!({"type":"done"}), now + Duration::from_secs(20));
            assert_eq!(state.started, Some(now));
            assert!(!state.can_cancel());
            assert!(!state.game_started);
            state.observe(
                &json!({"type":"game_started"}),
                now + Duration::from_secs(22),
            );
            assert!(state.game_started);
            assert!(state.started.is_none());
            assert_eq!(state.elapsed, Some(Duration::from_secs(22)));
        }
    }
    #[test]
    fn managed_startup_waits_for_window_readiness_and_preserves_total_progress() {
        let now = Instant::now();
        let mut state = Rebuild::default();
        let start = json!({"type":"game_starting"});
        let spawned = json!({"type":"game_started"});
        let pipeline =
            json!({"type":"startup_progress","stage":"pipelines","permille":500,"seconds":12});
        let ready = json!({"type":"game_ready"});

        // A warm launch still has renderer startup work and can receive progress before the spawn notification.
        state.observe(&start, now);
        assert!(state.startup.as_ref().unwrap().fraction.is_none());
        state.observe(&pipeline, now);
        state.observe(&spawned, now);
        assert!(!state.game_started);
        let startup = state.startup.as_ref().unwrap();
        assert_eq!(startup.fraction, Some(0.5));
        assert_eq!(startup.eta, Some(now + Duration::from_secs(12)));
        assert_eq!(state.step, "Loading pipelines");
        state.observe(&ready, now);
        assert!(state.game_started);
        assert!(state.startup.is_none());

        // When Reload rebuilds, startup consumes the remaining share of the builder's overall estimate.
        state.observe(
            &json!({"type":"play_plan","rebuild":"the game asked for a build"}),
            now,
        );
        state.observe(
            &json!({"type":"build_plan","stages":["compile","start"]}),
            now,
        );
        state.observe(
            &json!({"type":"build_progress","fraction":0.8,"seconds":15}),
            now,
        );
        state.observe(&json!({"type":"done"}), now);
        state.observe(&start, now);
        state.observe(&spawned, now);
        state.observe(&pipeline, now);
        assert_eq!(state.startup.as_ref().unwrap().fraction, Some(0.9));
        state.observe(
            &json!({"type":"startup_progress","stage":"pipelines","permille":1000}),
            now,
        );
        assert_eq!(state.startup.as_ref().unwrap().fraction, Some(0.99));
        assert!(!state.game_started);
        state.observe(&ready, now);
        assert!(state.game_started);
    }
}
