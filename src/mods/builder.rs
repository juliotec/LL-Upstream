//! The builder's versioned CLI is the sole interface to the mod store.
use crate::{game, players};
use anyhow::{Context, Result, bail, ensure};
use serde_json::Value;
use std::{
    ffi::OsString,
    io::Read,
    path::{Path, PathBuf},
    process::{Command, Output, Stdio},
};
pub(super) fn field<'a>(row: &'a Value, key: &str) -> &'a str {
    row[key].as_str().unwrap_or("")
}

pub(super) fn names(list: &Value) -> Vec<&str> {
    list.as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .collect()
}

pub(super) fn name(row: &Value) -> &str {
    ["name", "id", "url"]
        .iter()
        .find_map(|key| row[key].as_str().filter(|s| !s.is_empty()))
        .unwrap_or("")
}

#[derive(Debug)]
pub(super) struct MissingTools;

impl std::fmt::Display for MissingTools {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("This game version has no mod tools. Select a version with mod tools to import or manage mods.")
    }
}

impl std::error::Error for MissingTools {}

fn answer(output: Output) -> Result<(Value, String)> {
    let raw = String::from_utf8_lossy(&output.stdout);
    for line in raw.lines().rev() {
        let Ok(doc) = serde_json::from_str::<Value>(line) else {
            continue;
        };
        if doc["type"] == "error" {
            if doc["code"] == "mod_tooling_missing" && output.status.code() == Some(125) {
                return Err(MissingTools.into());
            }
            bail!(
                "{}",
                doc["text"].as_str().unwrap_or("The mod command failed.")
            );
        }
        match doc["ok"].as_bool() {
            Some(true) if output.status.success() => {
                return Ok((doc["result"].clone(), names(&doc["notes"]).join("\n")));
            }
            Some(false) => bail!(
                "{}",
                doc["error"]["text"]
                    .as_str()
                    .or(doc["error"]["code"].as_str())
                    .unwrap_or("The mod command failed.")
            ),
            _ => (),
        }
    }
    let error = String::from_utf8_lossy(&output.stderr);
    bail!(
        "{}",
        if error.trim().is_empty() {
            "This game version did not return a mod result."
        } else {
            error.trim()
        }
    )
}

pub(super) fn args(items: &[&str]) -> Vec<OsString> {
    items.iter().map(OsString::from).collect()
}

/// Bound both output streams. Drain stderr concurrently so progress messages cannot stall stdout.
pub(super) fn run(
    home: &Path,
    words: &[OsString],
    expected: Option<&(PathBuf, PathBuf)>,
    deadline: Option<std::time::Instant>,
) -> Result<(Value, String)> {
    let command = game::mod_command(home)?;
    if let Some((profile, program)) = expected {
        let mut common = command.get_args();
        let active = common
            .find(|v| *v == "--profile")
            .and_then(|_| common.next());
        ensure!(
            command.get_program() == program.as_os_str() && active == Some(profile.as_os_str()),
            "The active game or profile changed. Reopen Mods before continuing."
        );
    }
    let remaining = deadline.map_or(ACTION_TIMEOUT, |end| {
        end.saturating_duration_since(std::time::Instant::now())
    });
    ensure!(!remaining.is_zero(), "The mod operation timed out.");
    execute_with_timeout(command, words, remaining.min(command_timeout(words)))
}

pub(super) const ACTION_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(30 * 60);

fn command_timeout(words: &[OsString]) -> std::time::Duration {
    match words.first().and_then(|w| w.to_str()) {
        Some("installed" | "sources" | "plan") => std::time::Duration::from_secs(60),
        _ => ACTION_TIMEOUT,
    }
}

pub(super) fn execute(command: Command, words: &[OsString]) -> Result<(Value, String)> {
    execute_with_timeout(command, words, command_timeout(words))
}

pub(super) fn execute_with_timeout(
    mut command: Command,
    words: &[OsString],
    timeout: std::time::Duration,
) -> Result<(Value, String)> {
    use std::{
        io, thread,
        time::{Duration, Instant},
    };
    const MAX_RESULT: u64 = 16 * 1024 * 1024;
    let mut child = command
        .args(words)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("Could not start the mod tools")?;
    let stdout = child.stdout.take().context("Missing mod result stream")?;
    let mut stderr = child.stderr.take().context("Missing mod error stream")?;
    let read_stdout = thread::spawn(move || -> io::Result<Vec<u8>> {
        let mut bytes = Vec::new();
        stdout.take(MAX_RESULT + 1).read_to_end(&mut bytes)?;
        Ok(bytes)
    });
    let read_stderr = thread::spawn(move || -> io::Result<Vec<u8>> {
        let mut bytes = Vec::new();
        stderr.by_ref().take(16 * 1024).read_to_end(&mut bytes)?;
        io::copy(&mut stderr, &mut io::sink())?;
        Ok(bytes)
    });

    let read = |worker: thread::JoinHandle<io::Result<Vec<u8>>>| -> Result<Vec<u8>> {
        let output = worker
            .join()
            .map_err(|_| anyhow::anyhow!("Could not read the mod tools' output."))??;
        Ok(output)
    };
    let started = Instant::now();
    let result = (|| {
        let mut reader = Some(read_stdout);
        let mut stdout = None;
        loop {
            if let Some(task) = reader.take_if(|task| task.is_finished()) {
                let bytes = read(task)?;
                ensure!(
                    bytes.len() <= MAX_RESULT as usize,
                    "The mod tools returned more than 16 MiB in one response."
                );
                stdout = Some(bytes);
            }
            if read_stderr.is_finished()
                && let Some(status) = child.try_wait()?
                && let Some(bytes) = stdout.take()
            {
                return answer(Output {
                    status,
                    stdout: bytes,
                    stderr: read(read_stderr)?,
                });
            }
            ensure!(
                started.elapsed() < timeout,
                "The mod tools took too long to respond."
            );
            thread::sleep(Duration::from_millis(20));
        }
    })();
    if result.is_err() {
        let _ = child.kill();
        let _ = child.wait();
    }
    result
}

/// The builder computes reachability across all profiles before removing store entries.
pub fn store_gc(home: &Path, dry_run: bool) -> Result<Value> {
    if !dry_run {
        players::ensure_idle()?;
    }
    let words = if dry_run {
        args(&["gc", "--dry-run"])
    } else {
        args(&["gc"])
    };
    run(home, &words, None, None).map(|(result, _)| result)
}

pub(super) fn word(value: &str) -> Result<&str> {
    ensure!(
        !value.trim().is_empty() && !value.starts_with('-') && !value.contains('\0'),
        "The builder needs a valid name or address."
    );
    Ok(value)
}

pub(super) fn sourced(command: &str, row: &Value) -> Result<Vec<OsString>> {
    let mut words = args(&[command, word(field(row, "id"))?]);
    if let Some(source) = row["source"].as_str().filter(|u| !u.is_empty()) {
        words.extend(args(&["--source", word(source)?]));
    }
    Ok(words)
}
