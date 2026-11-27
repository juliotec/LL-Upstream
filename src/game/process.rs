#[cfg(windows)]
mod child_job;

use crate::{home, players};
use anyhow::{Context as _, Result, bail, ensure};
use serde_json::Value;
use std::{
    fs,
    io::{self, BufRead, BufReader, Read},
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::Duration,
};

pub(super) fn executable() -> &'static str {
    if cfg!(windows) {
        "Longshot.exe"
    } else {
        "Longshot.x86_64"
    }
}

#[derive(Clone)]
pub(super) struct Context {
    pub home: PathBuf,
    pub cache: PathBuf,
    pub profile: PathBuf,
    pub settings: Value,
}

impl Context {
    pub fn load(root: &Path) -> Result<Self> {
        Self::for_profile(root, &players::current_dir(root)?)
    }

    pub fn for_profile(root: &Path, profile: &Path) -> Result<Self> {
        let settings = home::settings(root)?;
        ensure!(
            profile.parent() == Some(&home::data_folder(root, &settings).join("profiles"))
                && profile
                    .file_name()
                    .and_then(|s| s.to_str())
                    .is_some_and(players::valid_id),
            "This profile no longer belongs to the launcher."
        );
        players::require_dir(profile)?;
        Ok(Self {
            home: root.to_owned(),
            cache: home::work(root, &settings).join("longshot-builder"),
            profile: profile.to_owned(),
            settings,
        })
    }

    // A user may clear the entire cache between runs. Mod queries can read the
    // physical store directly without recreating anything on disk.
    pub fn mod_cache(&self) -> Result<PathBuf> {
        let data = home::data_folder(&self.home, &self.settings);
        crate::mod_store::read_cache(&self.cache, &data)
    }

    pub(super) fn mod_command(&self, exe: &Path) -> Result<Command> {
        let mut command = self.command(exe, false)?;
        command
            .arg("mod")
            .arg("--profile")
            .arg(&self.profile)
            .arg("--cache")
            .arg(self.mod_cache()?)
            .args(["--output", "json"]);
        Ok(command)
    }

    pub fn prepare_mod_store(&self) -> Result<()> {
        let data = self.mod_cache()?;
        if data != self.cache {
            crate::mod_store::ensure(&self.cache, &data)?;
        }
        Ok(())
    }

    pub fn statistics(&self) -> bool {
        self.settings["sendPerformanceStatistics"].as_bool() == Some(true)
    }
    pub fn command(&self, exe: &Path, play: bool) -> Result<Command> {
        let mut command = if cfg!(target_os = "linux") && play && self.settings["gamescope"] == true
        {
            let mut scope = Command::new("gamescope");
            let options = self.settings["gamescopeOptions"]
                .as_str()
                .unwrap_or_default();
            scope.args(
                shlex::split(options).context("The gamescope options have unmatched quotes.")?,
            );
            scope.arg("--").arg(exe);
            scope
        } else {
            Command::new(exe)
        };
        command.current_dir(exe.parent().context("Builder has no version folder")?);
        for (key, _) in std::env::vars_os() {
            let key_text = key.to_string_lossy().to_ascii_uppercase();
            let private = ["LONGSHOT_", "A64_", "RX_"]
                .iter()
                .any(|prefix| key_text.starts_with(prefix));
            let retained = matches!(
                key_text.as_str(),
                "LONGSHOT_SHADER_STORE" | "LONGSHOT_LAUNCHER_TRUST" | "LONGSHOT_LAUNCHER_NO_CDN"
            );
            let linux_game_switch = play
                && cfg!(unix)
                && (key_text.starts_with("LONGSHOT_")
                    && !matches!(
                        key_text.as_str(),
                        "LONGSHOT_NFC_DIR" | "LONGSHOT_SHADER_STORE"
                    )
                    || key_text.starts_with("PYTHON")
                    || key_text == "LD_LIBRARY_PATH");
            if linux_game_switch
                || !play && private && !retained
                || matches!(
                    key_text.as_str(),
                    "APPIMAGE"
                        | "APPDIR"
                        | "TARGET_APPIMAGE"
                        | "OWD"
                        | "ARGV0"
                        | "HOS_PROFILE"
                        | "HOS_SAVE_DIR"
                        | "HOS_START_SLOT"
                        | "HOS_OPTIONS_FILE"
                        | "HOS_MODS"
                        | "HOS_MODS_LIST"
                        | "LONGSHOT_ROOT"
                        | "LONGSHOT_OBJ_STORE"
                        | "LONGSHOT_LIFT_STORE"
                        | "LONGSHOT_TOOLS"
                        | "LONGSHOT_SYSROOT"
                        | "LONGSHOT_PROFILE"
                        | "LONGSHOT_STARTED_BY"
                )
            {
                command.env_remove(&key);
            }
        }
        if std::env::var_os("APPIMAGE").is_some() {
            for (name, old) in [
                ("LD_LIBRARY_PATH", "LD_LIBRARY_PATH_ORIG"),
                ("PATH", "PATH_ORIG"),
            ] {
                if let Some(value) = std::env::var_os(old) {
                    command.env(name, value);
                } else if name == "LD_LIBRARY_PATH" {
                    command.env_remove(name);
                }
            }
        }
        command
            .env("LONGSHOT_DATA", home::builder_data(&self.home))
            .env("LONGSHOT_CACHE", &self.cache)
            .env("LONGSHOT_STORE", self.cache.join("store"))
            .env("LONGSHOT_SHADER_STORE", home::shader_store(&self.cache)?);
        if home::is_portable(&self.home) && home::variable("LONGSHOT_NFC_DIR").is_none() {
            command.env("LONGSHOT_NFC_DIR", crate::players::nfc_folder(&self.home)?);
        }
        if play {
            command
                .env("LONGSHOT_NO_UPDATE_CHECK", "1")
                .env("HOS_MODS", self.profile.join("mods"));
        }
        if cfg!(windows) {
            let switch = match self.settings["intelCompiles"].as_u64() {
                Some(1) => Some("1"),
                Some(2) => Some("0"),
                _ => None,
            };
            if let Some(value) = switch {
                command.env("RX_VK_NO_INTEL_SERIAL", value);
            }
            if self.settings["intelLongerLimits"] == true {
                command
                    .env("RX_VK_PREPARE_STALL_S", "1800")
                    .env("RX_VK_PRECOMPILE_STALL_S", "180")
                    .env("RX_VK_PREDICT_STALL_S", "180");
            }
        }
        if let Some(gb) = self.settings["memoryGb"].as_u64().filter(|v| *v > 0) {
            command.env("LONGSHOT_MEM_LIMIT_GB", gb.to_string());
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000);
        }
        Ok(command)
    }
    pub fn configure(&self, command: &mut Command) -> Result<()> {
        let launcher = launcher_executable()?;
        command
            .args(["--output", "json", "--cache"])
            .arg(&self.cache)
            .arg("--builder-exe")
            .arg(launcher);
        Ok(())
    }
}

pub(super) fn launcher_executable() -> Result<PathBuf> {
    let path = std::env::var_os("LONGSHOT_LAUNCHER_EXE")
        .or_else(|| std::env::var_os("APPIMAGE"))
        .map(PathBuf::from)
        .map(Ok)
        .unwrap_or_else(std::env::current_exe)?;
    Ok(std::path::absolute(path)?)
}

#[derive(Clone, Default)]
pub(super) struct Cancel(Arc<AtomicBool>);
impl Cancel {
    pub fn request(&self) {
        self.0.store(true, Ordering::Release);
    }
    pub fn check(&self) -> Result<()> {
        ensure!(!self.0.load(Ordering::Acquire), "Cancelled.");
        Ok(())
    }
}

pub(super) struct Answer {
    pub status: ExitStatus,
    pub result: Option<Value>,
    pub error: Option<String>,
}
#[derive(Debug)]
pub(super) struct UnexpectedExit {
    pub status: ExitStatus,
}
impl std::fmt::Display for UnexpectedExit {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "The builder stopped with {} before reporting a result.",
            self.status
        )
    }
}
impl std::error::Error for UnexpectedExit {}

impl Answer {
    /// A builder that emitted an error owns its statistics. The launcher can report a
    /// process that exited without one only after it genuinely started and wasn't cancelled.
    pub fn checked_build(self) -> Result<Value> {
        if self.error.is_none() && (!self.status.success() || self.result.is_none()) {
            return Err(UnexpectedExit {
                status: self.status,
            }
            .into());
        }
        self.require("Building Longshot")
    }
}
impl Answer {
    pub fn require(self, what: &str) -> Result<Value> {
        if let Some(message) = self.error {
            bail!("{message}")
        }
        ensure!(
            self.status.success(),
            "{what} stopped with {}.",
            self.status
        );
        self.result
            .with_context(|| format!("{what} finished without reporting a result."))
    }
}

#[cfg(not(windows))]
fn kill_tree(child: &mut Child) {
    #[cfg(unix)]
    {
        // Each build runs in a dedicated process group so compilers are stopped with the parent.
        let _ = Command::new("kill")
            .args(["-KILL", "--", &format!("-{}", child.id())])
            .status();
    }
    let _ = child.kill();
}

pub(super) enum Output {
    Line { text: String, stderr: bool },
    End,
}
fn read_lines(stream: impl Read, send: mpsc::SyncSender<Output>, stderr: bool) {
    let mut reader = BufReader::new(stream);
    let mut buffer = Vec::new();
    while reader.read_until(b'\n', &mut buffer).is_ok_and(|n| n > 0) {
        let text = String::from_utf8_lossy(&buffer)
            .trim_end_matches(['\r', '\n'])
            .to_owned();
        buffer.clear();
        if stderr {
            eprintln!("{text}");
        }
        if send.send(Output::Line { text, stderr }).is_err() {
            return;
        }
    }
    let _ = send.send(Output::End);
}

pub(super) fn builder_output(child: &mut Child) -> Option<mpsc::Receiver<Output>> {
    let stdout = child.stdout.take()?;
    let stderr = child.stderr.take()?;
    let (send, recv) = mpsc::sync_channel(128);
    let err_send = send.clone();
    thread::spawn(move || read_lines(stderr, err_send, true));
    thread::spawn(move || read_lines(stdout, send, false));
    Some(recv)
}

#[derive(Debug)]
pub(crate) struct ShortcutReported(pub(super) ExitStatus);

impl std::fmt::Display for ShortcutReported {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Longshot stopped with {}.", self.0)
    }
}

impl std::error::Error for ShortcutReported {}

pub(super) fn run(
    command: Command,
    terminal: &str,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<Answer> {
    run_with_stages(
        command,
        terminal,
        cancel,
        notify,
        &mut |_| {},
        &mut |_, _| {},
    )
}

/// A newly installed executable can briefly remain open for writing on Linux.
pub(super) fn spawn_builder(command: &mut Command) -> io::Result<Child> {
    let deadline = std::time::Instant::now() + Duration::from_secs(1);
    loop {
        match command.spawn() {
            Err(e)
                if e.kind() == io::ErrorKind::ExecutableFileBusy
                    && std::time::Instant::now() < deadline =>
            {
                thread::sleep(Duration::from_millis(20));
            }
            result => return result,
        }
    }
}

pub(super) fn run_with_stages(
    mut command: Command,
    terminal: &str,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
    observe: &mut dyn FnMut(Option<&Value>),
    transcript: &mut dyn FnMut(&str, &str),
) -> Result<Answer> {
    cancel.check()?;
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        command.process_group(0);
    }
    command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    #[cfg(windows)]
    let (mut job, mut child) = {
        let (job, child) = child_job::ChildJob::spawn(&mut command)
            .context("Could not start longshot-builder in its Windows job")?;
        (Some(job), child)
    };
    #[cfg(not(windows))]
    let mut child = spawn_builder(&mut command).context("Could not start longshot-builder")?;
    let recv = builder_output(&mut child).context("The builder output was not piped")?;
    let mut result = None;
    let mut error = None;
    let mut ends = 0;
    let mut stage = "Preparing…".to_string();
    #[cfg(windows)]
    let mut last_process_poll = std::time::Instant::now();
    loop {
        if let Err(e) = cancel.check() {
            #[cfg(windows)]
            drop(job.take());
            #[cfg(not(windows))]
            kill_tree(&mut child);
            let _ = child.wait();
            return Err(e);
        }
        #[cfg(windows)]
        if job.is_some() && last_process_poll.elapsed() >= Duration::from_millis(100) {
            last_process_poll = std::time::Instant::now();
            if child.try_wait()?.is_some() {
                // A surviving compiler may still own a pipe after the builder
                // crashes. Close the job before waiting for those pipes to drain.
                drop(job.take());
            }
        }
        if ends == 2 {
            if let Some(status) = child.try_wait()? {
                return Ok(Answer {
                    status,
                    result,
                    error,
                });
            }
            thread::sleep(Duration::from_millis(50));
            continue;
        }
        match recv.recv_timeout(Duration::from_millis(100)) {
            Ok(Output::End) => ends += 1,
            Err(mpsc::RecvTimeoutError::Timeout) => observe(None),
            Err(mpsc::RecvTimeoutError::Disconnected) => ends = 2,
            Ok(Output::Line { text: line, stderr }) => {
                transcript(if stderr { "stderr" } else { "stdout" }, &line);
                if stderr {
                    continue;
                }
                let Ok(e) = serde_json::from_str::<Value>(&line) else {
                    continue;
                };
                if e["v"] != 1 {
                    continue;
                }
                observe(Some(&e));
                let text = e["text"].as_str().unwrap_or_default();
                match e["type"].as_str() {
                    Some("error") => {
                        if !text.is_empty() {
                            notify(text, None);
                        }
                        error = Some(if text.is_empty() {
                            e["code"].as_str().unwrap_or("Builder failed").to_string()
                        } else {
                            text.to_owned()
                        })
                    }
                    Some("stage") => {
                        stage = text.to_owned();
                        notify(&stage, None)
                    }
                    Some("progress") => notify(
                        &stage,
                        e["overall"].as_f64().map(|f| f.clamp(0., 1.) as f32),
                    ),
                    Some("log" | "warning") if !text.is_empty() => notify(text, None),
                    Some("fetch_progress") => {
                        let total = e["total"].as_u64().unwrap_or(0);
                        let fraction = (total > 0).then(|| {
                            (e["bytes"].as_u64().unwrap_or(0) as f32 / total as f32).clamp(0., 1.)
                        });
                        notify("Fetching the builder…", fraction)
                    }
                    Some(t) if t == terminal => result = Some(e),
                    _ => (),
                }
            }
        }
    }
}

pub(super) fn validated_source(path: &Path) -> Result<PathBuf> {
    let path = fs::canonicalize(path).context("The game files could not be opened.")?;
    ensure!(
        path.to_str().is_some(),
        "The game path contains characters that cannot be saved. Rename the file or its parent folder and try again."
    );
    if path.is_file()
        && path
            .extension()
            .is_some_and(|ext| ext.eq_ignore_ascii_case("nup") || ext.eq_ignore_ascii_case("nx"))
    {
        return Ok(path);
    }
    if path.is_dir() {
        let dump = if path
            .file_name()
            .is_some_and(|s| s == "romfs" || s == "exefs")
        {
            path.parent().context("Missing dump folder")?
        } else {
            &path
        };
        if dump.join("romfs").is_dir() && dump.join("exefs/main").is_file() {
            return Ok(dump.to_owned());
        }
    }
    bail!("Choose a .nup or .nx file, or a dump containing romfs and exefs/main.")
}

pub(super) struct CancelWriter<'a, W> {
    pub file: &'a mut W,
    pub cancel: &'a Cancel,
}
impl<W: io::Write> io::Write for CancelWriter<'_, W> {
    fn write(&mut self, data: &[u8]) -> io::Result<usize> {
        self.cancel.check().map_err(io::Error::other)?;
        self.file.write(data)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.file.flush()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn preparing_and_playing_use_the_same_managed_cache_and_store() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let cache = root.join("other drive/longshot-builder");
        let ctx = Context {
            home: root.into(),
            cache: cache.clone(),
            profile: root.join("profiles/example"),
            settings: Value::Null,
        };
        for play in [false, true] {
            let command = ctx.command(&root.join("versions/game"), play)?;
            let env: std::collections::HashMap<_, _> = command.get_envs().collect();
            assert_eq!(
                env.get(std::ffi::OsStr::new("HOS_MODS")).copied().flatten(),
                play.then(|| ctx.profile.join("mods"))
                    .as_deref()
                    .map(Path::as_os_str)
            );
            for (key, expected) in [
                ("LONGSHOT_STORE", cache.join("store")),
                ("LONGSHOT_CACHE", cache.clone()),
                ("LONGSHOT_SHADER_STORE", home::shader_store(&cache)?),
                ("LONGSHOT_DATA", root.join("builder-data")),
            ] {
                assert_eq!(env[std::ffi::OsStr::new(key)], Some(expected.as_os_str()));
            }
        }
        Ok(())
    }
    #[test]
    fn accepts_archive_and_valid_dump_from_a_child_folder() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let archive = temp.path().join("Game.NX");
        fs::write(&archive, [])?;
        assert_eq!(validated_source(&archive)?, archive.canonicalize()?);
        fs::create_dir(temp.path().join("romfs"))?;
        fs::create_dir(temp.path().join("exefs"))?;
        assert!(validated_source(temp.path()).is_err());
        fs::write(temp.path().join("exefs/main"), [])?;
        assert_eq!(
            validated_source(&temp.path().join("romfs"))?,
            temp.path().canonicalize()?
        );
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn shortcut_waits_and_reports_the_builders_error() -> Result<()> {
        let mut command = Command::new("sh");
        command.args(["-c", r#"printf '%s\n' '{"v":1,"type":"error","text":"The install drive is offline"}'; exit 1"#]);
        command.stdout(Stdio::piped()).stderr(Stdio::piped());
        let output = super::super::launch::LaunchOutput::attach(&mut command)?;
        let error = super::super::launch::wait_shortcut(super::super::versions::Session {
            child: command.spawn()?,
            output: Some(output),
            steam_opener: false,
            folder: Default::default(),
            profile: Default::default(),
        })
        .unwrap_err();
        assert_eq!(error.to_string(), "The install drive is offline");
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn checked_builder_events_require_v1_result_and_successful_exit() -> Result<()> {
        let mut command = Command::new("sh");
        command.args(["-c", r#"printf '%s\n' '{"type":"check","v":2,"ok":false}' '{"type":"check","v":1,"ok":true}'"#]);
        let result = run(command, "check", &Cancel::default(), &mut |_, _| {})?.require("Check")?;
        assert_eq!(result["ok"], true);
        let mut command = Command::new("sh");
        command.args([
            "-c",
            r#"printf '%s\n' '{"type":"error","v":1,"text":"Invalid input"}'; exit 1"#,
        ]);
        let error = run(command, "done", &Cancel::default(), &mut |_, _| {})?
            .require("Build")
            .unwrap_err();
        assert_eq!(error.to_string(), "Invalid input");
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn cancelling_a_process_group_unblocks_inherited_output_pipes() -> Result<()> {
        let mut command = Command::new("sh");
        command.args(["-c", "sleep 45 & wait"]);
        let cancel = Cancel::default();
        let token = cancel.clone();
        let start = std::time::Instant::now();
        let handle = thread::spawn(move || {
            thread::sleep(Duration::from_millis(100));
            token.request();
        });
        assert!(run(command, "done", &cancel, &mut |_, _| {}).is_err());
        handle.join().unwrap();
        assert!(start.elapsed() < Duration::from_secs(10));
        Ok(())
    }
}
