use super::disk;
use anyhow::{Context, Result, bail, ensure};
use std::{
    ffi::OsString,
    path::{Path, PathBuf},
};
use sysinfo::{
    Pid, Process, ProcessRefreshKind, ProcessStatus, ProcessesToUpdate, System, UpdateKind,
};

#[derive(Clone, Copy, PartialEq)]
pub(super) enum Program {
    Longshot,
    Emulator,
}

pub(super) fn game_process(path: &str) -> Option<Program> {
    let name = Path::new(path).file_name()?.to_str()?.to_ascii_lowercase();
    let name = name.strip_suffix(".exe").unwrap_or(&name);
    if name.starts_with("longshot-launcher") || name.starts_with("longshot-broker") {
        return None;
    }
    for known in [
        "longshot",
        "longshot-builder",
        "hookshot",
        "ocarina",
        "nx2navi",
        "lichii",
        "fritchii",
        "orchid",
        "eglantine",
        "eden",
    ] {
        let Some(suffix) = name.strip_prefix(known) else {
            continue;
        };
        if matches!(suffix, "" | ".x86_64")
            || (suffix.ends_with(".appimage") && suffix.starts_with(['-', '_', '.']))
        {
            return Some(if matches!(known, "longshot" | "longshot-builder") {
                Program::Longshot
            } else {
                Program::Emulator
            });
        }
    }
    None
}

enum Scope {
    All,
    Shaders,
    Saves(Vec<PathBuf>),
    Folders(Vec<PathBuf>),
}

impl Scope {
    fn paths(paths: &[&Path]) -> Result<Vec<PathBuf>> {
        ensure!(!paths.is_empty(), "Choose a folder first.");
        paths
            .iter()
            .map(|p| disk::resolve(&std::path::absolute(p)?))
            .collect()
    }

    fn check(&self, process: &ActiveProcess<'_>) -> Result<()> {
        let blocked = match self {
            Self::All => true,
            Self::Shaders => {
                process.program == Program::Longshot
                    && (!process.non_play_command()
                        || matches!(process.subcommand(), Some("build" | "from-source")))
            }
            Self::Saves(paths) => {
                if process.non_play_command() {
                    return Ok(());
                }
                // Only Longshot's --user and --profile identify an on-disk save root.
                // Emulator profile arguments can instead name an account in shared storage.
                process.program != Program::Longshot
                    || process.uses_options(paths, &["--user", "--profile"]) != Some(false)
            }
            Self::Folders(paths) => {
                let inside = |p: &Path| {
                    disk::resolve(p)
                        .map_or(true, |path| paths.iter().any(|root| path.starts_with(root)))
                };
                process.exe.is_none_or(inside)
                    || process
                        .script()
                        .is_some_and(|p| process.absolute(p).is_none_or(|p| inside(&p)))
                    || process.cwd.is_some_and(inside)
                    || process.uses_options(
                        paths,
                        &["--cache", "--game", "--profile", "--user", "--out"],
                    ) == Some(true)
                    || process.args.is_empty()
            }
        };
        if blocked {
            let target = match self {
                Self::Saves(_) => "before transferring these saves",
                Self::Folders(_) => "before moving these folders",
                Self::All => "before continuing",
                Self::Shaders => "before clearing shader caches",
            };
            bail!("Close {} (PID {}) {target}.", process.name, process.pid);
        }
        Ok(())
    }
}

fn overlaps_any(path: &Path, targets: &[PathBuf]) -> Result<bool> {
    let path = disk::resolve(path)?;
    Ok(targets
        .iter()
        .any(|p| p.starts_with(&path) || path.starts_with(p)))
}

struct ActiveProcess<'a> {
    pid: u32,
    name: &'a str,
    program: Program,
    exe: Option<&'a Path>,
    cwd: Option<&'a Path>,
    args: &'a [OsString],
}

impl ActiveProcess<'_> {
    fn script(&self) -> Option<&Path> {
        // For a shebang entry point the OS reports the interpreter as the executable
        // and puts the script before its arguments.
        let image = self.exe?.file_name()?.to_str()?;
        if game_process(image).is_some() {
            return None;
        }
        let entry = Path::new(self.args.get(1)?);
        (game_process(entry.to_str()?) == Some(self.program)).then_some(entry)
    }

    fn absolute(&self, path: &Path) -> Option<PathBuf> {
        if path.is_absolute() {
            Some(path.to_owned())
        } else {
            Some(self.cwd?.join(path))
        }
    }

    fn subcommand(&self) -> Option<&str> {
        self.args
            .get(if self.script().is_some() { 2 } else { 1 })
            .and_then(|arg| arg.to_str())
    }

    fn non_play_command(&self) -> bool {
        self.program == Program::Longshot
            && matches!(
                self.subcommand(),
                Some(
                    "build"
                        | "fetch"
                        | "check"
                        | "info"
                        | "mod"
                        | "from-source"
                        | "steam-shortcut"
                        | "perf-report"
                        | "--help"
                        | "-h"
                        | "--version"
                        | "-V"
                )
            )
    }

    // None means no explicit location. Any ambiguous explicit option must block the operation.
    fn uses_options(&self, targets: &[PathBuf], flags: &[&str]) -> Option<bool> {
        let mut seen = false;
        let mut it = self.args.iter().skip(1);
        while let Some(arg) = it.next() {
            let Some(arg) = arg.to_str() else { continue };
            let (key, inline) = arg
                .split_once('=')
                .map_or((arg, None), |(k, v)| (k, Some(v)));
            if !flags.contains(&key) {
                continue;
            }
            seen = true;
            let value = inline.map(Path::new).or_else(|| it.next().map(Path::new));
            let Some(path) = value.filter(|p| {
                !p.as_os_str().is_empty() && !p.as_os_str().as_encoded_bytes().starts_with(b"-")
            }) else {
                return Some(true);
            };
            let Some(full) = self.absolute(path) else {
                return Some(true);
            };
            // A user directory or profile may join the same save tree through a junction.
            let saves = match key {
                "--user" => Some(full.join("save")),
                "--profile" => Some(full.join("saves/save")),
                _ => None,
            };
            if overlaps_any(&full, targets).unwrap_or(true)
                || saves
                    .as_deref()
                    .is_some_and(|p| overlaps_any(p, targets).unwrap_or(true))
            {
                return Some(true);
            }
        }
        seen.then_some(false)
    }
}

fn candidate(p: &Process) -> Option<(&str, Program)> {
    if p.pid().as_u32() == std::process::id()
        || matches!(p.status(), ProcessStatus::Zombie | ProcessStatus::Dead)
    {
        return None;
    }
    [p.exe().and_then(Path::file_name), Some(p.name())]
        .into_iter()
        .flatten()
        .filter_map(|name| name.to_str())
        .find_map(|name| Some((name, game_process(name)?)))
}

fn check(scope: Scope) -> Result<()> {
    #[cfg(test)]
    if let Some(list) = std::env::var_os("LONGSHOT_TEST_PLAYER_PROCESSES") {
        for name in list.to_string_lossy().lines() {
            if let Some(program) = game_process(name) {
                scope.check(&ActiveProcess {
                    pid: 1,
                    program,
                    name,
                    exe: None,
                    cwd: None,
                    args: &[],
                })?;
            }
        }
        return Ok(());
    }
    inspect(&scope, ProcessesToUpdate::All)
}

fn inspect(scope: &Scope, selected: ProcessesToUpdate) -> Result<()> {
    let mut system = System::new().context("Could not check for a running game.")?;
    let base = ProcessRefreshKind::nothing()
        .without_tasks()
        .with_exe(UpdateKind::OnlyIfNotSet);
    ensure!(
        system.refresh_processes_specifics(selected, true, base) > 0,
        "Could not check for a running game."
    );
    let games: Vec<_> = system
        .processes()
        .values()
        .filter(|p| candidate(p).is_some())
        .map(Process::pid)
        .collect();
    if games.is_empty() {
        return Ok(());
    }
    let details = base
        .with_exe(UpdateKind::Always)
        .with_cmd(UpdateKind::Always)
        .with_cwd(UpdateKind::Always);
    // Only fetch command lines for recognized games and emulators.
    system.refresh_processes_specifics(ProcessesToUpdate::Some(&games), true, details);
    for pid in games {
        let Some(process) = system.process(pid) else {
            continue;
        };
        let result = check_process(scope, process);
        if result.is_err() && (process.cmd().is_empty() || process.exe().is_none()) {
            // The kernel drops argv and executable information before marking an
            // exiting process dead. Verify ambiguous observations against a fresh
            // process table. A still-live game with inaccessible data stays protected.
            std::thread::sleep(std::time::Duration::from_millis(15));
            confirm_process(scope, pid, process.start_time(), details)?;
        } else {
            result?;
        }
    }
    Ok(())
}

fn check_process(scope: &Scope, process: &Process) -> Result<()> {
    if let Some((name, program)) = candidate(process) {
        scope.check(&ActiveProcess {
            pid: process.pid().as_u32(),
            name,
            program,
            exe: process.exe(),
            cwd: process.cwd(),
            args: process.cmd(),
        })?;
    }
    Ok(())
}

fn confirm_process(scope: &Scope, pid: Pid, start: u64, details: ProcessRefreshKind) -> Result<()> {
    let mut fresh = System::new().context("Could not check for a running game.")?;
    fresh.refresh_processes_specifics(ProcessesToUpdate::Some(&[pid]), true, details);
    if let Some(current) = fresh.process(pid)
        && current.start_time() == start
    {
        check_process(scope, current)?;
    }
    Ok(())
}

pub(crate) fn ensure_shader_idle() -> Result<()> {
    check(Scope::Shaders)
}

pub(crate) fn ensure_idle() -> Result<()> {
    check(Scope::All)
}

pub(crate) fn ensure_paths_idle(paths: &[&Path]) -> Result<()> {
    check(Scope::Folders(Scope::paths(paths)?))
}

pub(super) fn saves_idle(paths: &[&Path]) -> Result<()> {
    check(Scope::Saves(Scope::paths(paths)?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn scopes_active_games_and_builder_commands_to_their_files() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let a = tmp.path().join("Ada");
        let b = tmp.path().join("Bea");
        let work = tmp.path().join("workspace");
        for root in [&a, &b, &work] {
            fs::create_dir(root)?;
        }
        let exe = tmp.path().join("longshot");
        let check = |scope: &Scope, name, argv: &[&str]| {
            let args: Vec<OsString> = argv.iter().map(OsString::from).collect();
            scope.check(&ActiveProcess {
                program: game_process(name).unwrap(),
                pid: 23,
                name,
                exe: Some(&exe),
                cwd: Some(tmp.path()),
                args: &args,
            })
        };
        assert!(check(&Scope::Shaders, "longshot", &["longshot"]).is_err());
        assert!(
            check(
                &Scope::Shaders,
                "longshot-builder",
                &["longshot-builder", "build"]
            )
            .is_err()
        );
        check(
            &Scope::Shaders,
            "longshot-builder",
            &["longshot-builder", "check"],
        )?;
        check(&Scope::Shaders, "eden", &["eden"])?;
        let ada = Scope::Saves(Scope::paths(&[&a.join("saves/save/slot_00")])?);
        let bea = Scope::Saves(Scope::paths(&[&b.join("saves")])?);
        let both = Scope::Saves(Scope::paths(&[&b, &a])?);
        for writer in [
            vec!["longshot", "--profile", "Ada"],
            vec!["longshot", "--user=Ada/saves"],
        ] {
            assert!(
                check(&ada, "longshot", &writer)
                    .unwrap_err()
                    .to_string()
                    .contains("longshot (PID 23)")
            );
            assert!(check(&both, "longshot", &writer).is_err());
            check(&bea, "longshot", &writer)?;
        }
        for action in [
            "build",
            "fetch",
            "check",
            "info",
            "mod",
            "from-source",
            "steam-shortcut",
            "perf-report",
        ] {
            check(
                &ada,
                "longshot-builder",
                &["longshot-builder", action, "--profile", "Ada"],
            )?;
        }
        for unknown in [&[][..], &["longshot", "--user"], &["longshot", "--user="]] {
            assert!(check(&ada, "longshot", unknown).is_err());
        }
        assert!(check(&ada, "eden", &["eden", "--profile", "Bea"]).is_err());
        assert!(game_process("longshot-broker").is_none());
        assert!(game_process("longshot-launcher-nightly.AppImage").is_none());
        #[cfg(unix)]
        {
            let shared = a.join("saves/save");
            fs::create_dir_all(&shared)?;
            std::os::unix::fs::symlink(&shared, b.join("save"))?;
            assert!(check(&ada, "longshot", &["longshot", "--user", "Bea"]).is_err());
        }
        let folders = Scope::Folders(Scope::paths(&[&work])?);
        check(&folders, "longshot", &["longshot", "--user", "Ada"])?;
        assert!(
            check(
                &folders,
                "longshot-builder",
                &["longshot-builder", "build", "--cache", "workspace"]
            )
            .is_err()
        );
        Ok(())
    }

    #[cfg(target_os = "linux")]
    fn start_ready(command: &mut std::process::Command) -> Result<std::process::Child> {
        use std::{
            io::{BufRead, BufReader, ErrorKind},
            process::Stdio,
            time::{Duration, Instant},
        };
        command.stdin(Stdio::piped()).stdout(Stdio::piped());
        // Concurrent test children can briefly inherit a file descriptor for a newly
        // created fixture script across fork. It is closed when they exec.
        let deadline = Instant::now() + Duration::from_secs(1);
        let mut child = loop {
            match command.spawn() {
                Ok(child) => break child,
                Err(err)
                    if err.kind() == ErrorKind::ExecutableFileBusy && Instant::now() < deadline =>
                {
                    std::thread::sleep(Duration::from_millis(2))
                }
                Err(err) => return Err(err.into()),
            }
        };
        let mut ready = String::new();
        BufReader::new(child.stdout.take().unwrap()).read_line(&mut ready)?;
        ensure!(ready == "ready\n", "Fixture did not start.");
        Ok(child)
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn reads_live_writer_and_tool_arguments_and_ignores_reaped_work() -> Result<()> {
        use std::{
            process::Command,
            time::{Duration, Instant},
        };
        use sysinfo::Pid;
        let tmp = tempfile::tempdir()?;
        let exe = tmp.path().join("longshot");
        std::os::unix::fs::symlink("/bin/sh", &exe)?;
        fs::write(tmp.path().join("build"), "echo ready; read line\n")?;
        let alice = tmp.path().join("alice");
        let bob = tmp.path().join("bob");
        fs::create_dir(&alice)?;
        fs::create_dir(&bob)?;
        let a = Scope::Saves(Scope::paths(&[&alice])?);
        let b = Scope::Saves(Scope::paths(&[&bob])?);
        for (args, writer) in [
            (
                vec!["-c", "echo ready; read line", "game", "--user", "alice"],
                true,
            ),
            (vec!["build", "--profile", "alice"], false),
        ] {
            let mut child = start_ready(Command::new(&exe).args(args).current_dir(tmp.path()))?;
            let pids = [Pid::from_u32(child.id())];
            let check = |scope| inspect(scope, ProcessesToUpdate::Some(&pids));
            // The child only reads from our pipe. Closing it also releases the fixture on failure.
            let results = (check(&a), check(&b));
            drop(child.stdin.take());
            let deadline = Instant::now() + Duration::from_secs(2);
            let state = format!("/proc/{}/stat", child.id());
            while !fs::read_to_string(&state)?.contains(") Z ") {
                ensure!(Instant::now() < deadline, "Fixture process did not exit.");
                std::thread::sleep(Duration::from_millis(2));
            }
            let zombie = check(&a);
            child.wait()?;
            confirm_process(
                &a,
                pids[0],
                0,
                ProcessRefreshKind::nothing().without_tasks(),
            )?;
            assert_eq!(results.0.is_err(), writer);
            if let Err(error) = results.0 {
                assert!(
                    error
                        .to_string()
                        .contains(&format!("longshot (PID {})", child.id()))
                );
            }
            results.1?;
            zombie?;
        }
        Ok(())
    }
    #[cfg(target_os = "linux")]
    #[test]
    fn script_builder_protects_its_install_but_leaves_profile_saves_available() -> Result<()> {
        use std::{os::unix::fs::PermissionsExt, process::Command};
        let tmp = tempfile::tempdir()?;
        let install = tmp.path().join("install");
        let profile = tmp.path().join("profile");
        let other = tmp.path().join("other");
        for dir in [&install, &profile, &other] {
            fs::create_dir(dir)?;
        }
        let script = install.join("Longshot.x86_64");
        fs::write(&script, "#!/bin/sh\necho ready; read input\n")?;
        fs::set_permissions(&script, fs::Permissions::from_mode(0o755))?;
        let mut child = start_ready(
            Command::new(&script)
                .args(["build", "--profile"])
                .arg(&profile)
                .current_dir(tmp.path()),
        )?;
        let pid = [Pid::from_u32(child.id())];
        let inspect_one = |scope| inspect(&scope, ProcessesToUpdate::Some(&pid));
        let protected = inspect_one(Scope::Folders(Scope::paths(&[&install])?));
        let separate = inspect_one(Scope::Folders(Scope::paths(&[&other])?));
        let saves = inspect_one(Scope::Saves(Scope::paths(&[&profile])?));
        drop(child.stdin.take());
        child.wait()?;
        assert!(protected.is_err());
        separate?;
        saves?;
        Ok(())
    }
}
