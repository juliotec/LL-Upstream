//! Observe the builder's play command without owning the game's lifetime.
use super::versions::Session;
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
    if let Some(elapsed) = work.lock().unwrap().take_build_time(
        from_build_completion,
        &session.profile,
        &session.folder,
    ) {
        g.set_last_build_seconds(elapsed.as_secs().min(i32::MAX as u64) as i32);
    }
    g.set_playing(true);
    let weak = window.as_weak();
    let home = home.to_owned();
    thread::spawn(move || {
        let result = wait(&mut session, &weak, &home);
        let _ = weak
            .upgrade_in_event_loop(move |ui| finished(&ui, &home, session.steam_opener, result));
    });
    Ok(())
}

fn finished(ui: &MainWindow, home: &Path, steam_opener: bool, result: io::Result<ExitStatus>) {
    let g = ui.global::<Game>();
    g.set_playing(false);
    if ui.get_page() == crate::Page::Launch {
        g.set_show_log(false);
        if steam_opener && result.as_ref().is_ok_and(|s| s.success()) {
            g.set_launch_phase("steam".into());
        } else {
            g.invoke_show_home();
        }
    }
    if !steam_opener {
        let _ = crate::players::refresh_saves(ui, home);
    }
    let note = match result {
        Ok(status) if steam_opener => {
            if status.success() {
                ""
            } else {
                "Steam could not open the game shortcut."
            }
        }
        Ok(status) => match status.code() {
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

#[derive(Default)]
struct Rebuild {
    started: Option<Instant>,
    elapsed: Option<Duration>,
    step: String,
    fraction: Option<f64>,
    eta: Option<Instant>,
}

impl Rebuild {
    fn observe(&mut self, event: &Value, now: Instant) {
        match event["type"].as_str() {
            Some("stage" | "progress") => {
                self.elapsed = None;
                let start = *self.started.get_or_insert(now);
                if let Some(text) = event["text"].as_str().filter(|s| !s.is_empty()) {
                    self.step = text.to_owned();
                } else if event["type"] == "stage" || self.step.is_empty() {
                    self.step = event["stage"].as_str().unwrap_or_default().to_owned();
                }
                if let Some(f) = event["overall"].as_f64().filter(|f| *f > 0. && *f < 1.) {
                    self.fraction = Some(f);
                    let elapsed = now.duration_since(start).as_secs_f64();
                    if elapsed >= 1. {
                        self.eta = deadline(now, elapsed * (1. - f) / f);
                    }
                }
            }
            Some("eta") if self.started.is_some() => {
                self.eta = event["seconds"].as_f64().and_then(|s| deadline(now, s));
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
        if !exe.starts_with(folder)
            || !exe
                .file_stem()
                .is_some_and(|s| s.eq_ignore_ascii_case("longshot"))
        {
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
    pub(super) fn attach(command: &mut Command) -> io::Result<Self> {
        let (stdout, out) = OutputFile::new()?;
        let (stderr, err) = OutputFile::new()?;
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
    fn new() -> io::Result<(Self, File)> {
        let file = tempfile::NamedTempFile::new()?;
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
) -> io::Result<ExitStatus> {
    let Some(mut output) = session.output.take() else {
        return session.child.wait();
    };
    let mut transcript = Transcript::start(home, BuildLog::Launch)
        .inspect_err(|e| eprintln!("Could not open the local launch log: {e}"))
        .ok();
    let available = transcript.is_some();
    let _ = window
        .upgrade_in_event_loop(move |ui| ui.global::<Game>().set_launch_log_available(available));
    let mut state = Rebuild::default();
    let mut reading_output = true;

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
                    if let Some(log) = &mut transcript
                        && let Err(error) = log.line(kind, text)
                    {
                        eprintln!("Could not write the local launch log: {error}");
                        transcript = None;
                    }
                    if kind == "stdout"
                        && text.starts_with('{')
                        && let Ok(event) = serde_json::from_str::<Value>(text)
                    {
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
            return Ok(exit);
        }
        let rebuilding = state.started.is_some();
        let fraction = state.fraction.map_or(-1., |f| f.min(0.99) as f32);
        let eta = state
            .eta
            .map(|until| until.saturating_duration_since(now).as_secs())
            .filter(|s| *s > 0);
        let step = state.step.clone();
        let rebuild_seconds = state
            .elapsed
            .map_or(-1, |elapsed| elapsed.as_secs().min(i32::MAX as u64) as i32);
        if !rebuilding && last_scan.is_none_or(|t| now.duration_since(t) >= Duration::from_secs(1))
        {
            last_scan = Some(now);
            if let Some(system) = &mut system
                && running(system, Pid::from_u32(session.child.id()), &builds)
            {
                let _ = window.upgrade_in_event_loop(move |ui| {
                    let game = ui.global::<Game>();
                    game.set_last_rebuild_seconds(rebuild_seconds);
                    game.set_launch_phase("running".into());
                });
                return session.child.wait();
            }
        }
        let view = (rebuilding, fraction, eta, step, rebuild_seconds);
        if previous.as_ref() != Some(&view) {
            let step = view.3.clone();
            previous = Some(view);
            let _ = window.upgrade_in_event_loop(move |ui| {
                let g = ui.global::<Game>();
                if rebuilding {
                    g.set_last_build_seconds(-1);
                }
                g.set_launch_phase(
                    if rebuilding {
                        "rebuilding"
                    } else {
                        "launching"
                    }
                    .into(),
                );
                g.set_last_rebuild_seconds(rebuild_seconds);
                g.set_launch_step(step.into());
                g.set_launch_fraction(fraction);
                g.set_launch_eta(
                    eta.map(|s| format!("About {} remaining", super::forecast::duration(s as f64)))
                        .unwrap_or_default()
                        .into(),
                );
            });
        }
        thread::sleep(Duration::from_millis(200));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

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
            let output = LaunchOutput::attach(&mut command)?;
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

        let (mut output, mut writer) = OutputFile::new()?;
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
}
