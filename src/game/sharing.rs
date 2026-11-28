//! Store the player's choices in the files the game and its crash helper read at start.
//! The version's builder owns the performance-report format and its sender.
use super::process::Context;
use anyhow::{Context as _, Result, ensure};
use std::{fs, io, io::Write, path::Path, process::Stdio, thread};

struct Choices {
    performance: bool,
    keep_copies: bool,
    crash: bool,
}

fn write_choice(path: &Path, text: &str) -> Result<()> {
    if fs::read_to_string(path).is_ok_and(|old| old == text) {
        return Ok(());
    }
    let mut tmp = tempfile::NamedTempFile::new_in(path.parent().unwrap())?;
    tmp.write_all(text.as_bytes())?;
    tmp.persist(path)?;
    Ok(())
}

fn reports(dir: &Path, clear: bool, backoff: bool) -> Result<bool> {
    ensure!(
        !crate::storage::linked_location(dir)?,
        "Linked report folder"
    );
    let files = match fs::read_dir(dir) {
        Ok(files) => files,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(e) => return Err(e.into()),
    };
    let mut pending = false;
    for file in files {
        let file = file?;
        if !file.file_type()?.is_file() {
            continue;
        }
        let name = file.file_name();
        let name = name.to_string_lossy();
        let json = name.ends_with(".json");
        if clear && (json || name.ends_with(".tmp") || backoff && name == ".next") {
            fs::remove_file(file.path())?;
        } else if json {
            pending = true;
        }
    }
    Ok(pending)
}

impl Choices {
    fn apply(&self, folder: &Path) -> Result<bool> {
        ensure!(
            !crate::storage::linked_location(folder)?,
            "Linked game folder"
        );
        if !folder.is_dir() {
            return Ok(false);
        }
        let organized = crate::players::is_file(&folder.join("data/image.bin"));
        let config = if organized {
            folder.join("config")
        } else {
            folder.to_owned()
        };
        let diagnostics = if organized {
            folder.join("diagnostics")
        } else {
            folder.to_owned()
        };
        let perf = config.join("longshot_perf.ini");
        let pending = diagnostics.join("perf_pending");
        let sent = diagnostics.join("perf_sent");
        let crash = config.join("longshot_crash.ini");
        // A game must never interpret preferences or report queues from a linked directory.
        for path in [&perf, &pending, &sent, &crash] {
            ensure!(
                !crate::storage::linked_location(path)?,
                "Report settings or queues in this build point outside their folder."
            );
        }
        if self.crash || crash.exists() {
            write_choice(&crash, if self.crash { "send=1\n" } else { "send=0\n" })?;
        }
        if self.performance {
            write_choice(
                &perf,
                if self.keep_copies {
                    "send=1\n"
                } else {
                    "send=1\nkeep_sent=0\n"
                },
            )?;
        } else if perf.try_exists()? {
            fs::remove_file(&perf)?;
        }
        if !self.performance || !self.keep_copies {
            reports(&sent, true, false)?;
        }
        let waiting = reports(&pending, !self.performance, true)?;
        Ok(self.performance && waiting)
    }
}

pub(super) fn before_play(ctx: &Context, exe: &Path, folder: &Path) -> Result<()> {
    ensure!(
        !crate::storage::linked_location(folder.parent().context("Missing builds folder")?)?,
        "Linked builds folder"
    );
    let answer = Choices {
        performance: ctx.statistics(),
        keep_copies: ctx.settings["keepSentPerformanceStatistics"] != false,
        crash: ctx.settings["sendCrashReportsAutomatically"] == true,
    };
    if !answer
        .apply(folder)
        .context("Could not apply your sharing settings before starting Longshot")?
    {
        return Ok(());
    }
    let mut cmd = ctx.command(exe, false)?;
    cmd.arg("perf-report")
        .arg(folder)
        .args(["--send", "--output", "json", "--cache"])
        .arg(&ctx.cache);
    cmd.stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    // The game can start while the builder sends its previous session's summaries.
    if let Ok(mut child) = cmd.spawn() {
        thread::spawn(move || {
            let _ = child.wait();
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn linked_report_queues_preserve_external_reports() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let game = temp.path().join("game");
        let external = temp.path().join("external");
        fs::create_dir(&game)?;
        fs::create_dir(&external)?;
        fs::write(external.join("report.json"), "{}")?;
        #[cfg(unix)]
        std::os::unix::fs::symlink(&external, game.join("perf_pending"))?;
        #[cfg(windows)]
        junction::create(&external, game.join("perf_pending"))?;
        let choices = Choices {
            performance: false,
            keep_copies: false,
            crash: false,
        };
        assert!(choices.apply(&game).is_err());
        assert!(external.join("report.json").exists());
        Ok(())
    }
    #[test]
    fn sharing_choices_only_affect_the_build_report_files() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        for dir in ["perf_sent", "perf_pending"] {
            fs::create_dir(root.join(dir))?;
            fs::write(root.join(dir).join("synthetic.json"), "{}")?;
            fs::write(root.join(dir).join("leave.txt"), "synthetic")?;
        }
        fs::write(root.join("perf_pending/.next"), "0")?;
        let opted_in = Choices {
            performance: true,
            keep_copies: false,
            crash: true,
        };
        assert!(opted_in.apply(root)?);
        assert_eq!(
            fs::read_to_string(root.join("longshot_perf.ini"))?,
            "send=1\nkeep_sent=0\n"
        );
        assert_eq!(
            fs::read_to_string(root.join("longshot_crash.ini"))?,
            "send=1\n"
        );
        assert!(!root.join("perf_sent/synthetic.json").exists());
        let opted_out = Choices {
            performance: false,
            keep_copies: true,
            crash: false,
        };
        assert!(!opted_out.apply(root)?);
        assert!(!root.join("longshot_perf.ini").exists());
        assert!(!root.join("perf_pending/synthetic.json").exists());
        assert!(!root.join("perf_pending/.next").exists());
        assert!(root.join("perf_pending/leave.txt").is_file());
        assert_eq!(
            fs::read_to_string(root.join("longshot_crash.ini"))?,
            "send=0\n"
        );
        Ok(())
    }
}
