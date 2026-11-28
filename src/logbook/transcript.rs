//! Build output stays local. Stream the complete transcript to disk while the list shows a bounded tail.
use anyhow::{Result, ensure};
use std::{
    fs,
    io::{LineWriter, Write},
    path::Path,
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum BuildLog {
    Build,
    Launch,
}

impl BuildLog {
    pub fn filename(self) -> &'static str {
        match self {
            Self::Build => "last-build.log",
            Self::Launch => "last-launch.log",
        }
    }
}

pub(crate) struct Transcript {
    file: LineWriter<fs::File>,
    paths: Vec<(String, &'static str)>,
    usernames: Vec<String>,
}

impl Transcript {
    pub(crate) fn start(
        home: &Path,
        kind: BuildLog,
        locations: &[(&'static str, &Path)],
    ) -> Result<Self> {
        let dir = home.join("logs");
        ensure!(
            !dir.symlink_metadata()
                .is_ok_and(|m| crate::storage::is_link(&m)),
            "The logs folder points outside the launcher."
        );
        fs::create_dir_all(&dir)?;
        let path = dir.join(kind.filename());
        let file = tempfile::NamedTempFile::new_in(dir)?.persist(path)?;
        let mut log = Self {
            file: LineWriter::new(file),
            paths: Vec::new(),
            usernames: ["USER", "LOGNAME", "USERNAME"]
                .into_iter()
                .filter_map(|key| std::env::var(key).ok())
                .filter(|name| !name.trim().is_empty())
                .collect(),
        };
        for &(alias, path) in locations {
            log.add_path(path, alias);
        }
        log.add_path(home, "<launcher>");
        if let Some(account) = std::env::home_dir() {
            log.add_path(&account, "~");
            if let Some(name) = account.file_name().and_then(|name| name.to_str()) {
                log.usernames.push(name.to_owned());
            }
        }
        log.usernames.sort_unstable();
        log.usernames.dedup();
        // Replace more specific paths before their ancestors.
        log.paths
            .sort_by_key(|(path, _)| std::cmp::Reverse(path.len()));
        log.line(
            "launcher",
            &format!(
                "[INFO] Longshot Launcher {} · {} {}",
                crate::update::own_tag(),
                std::env::consts::OS,
                std::env::consts::ARCH,
            ),
        )?;
        Ok(log)
    }

    fn add_path(&mut self, path: &Path, alias: &'static str) {
        if !path.is_absolute() || path.parent().is_none() {
            return;
        }
        let resolved = path.canonicalize().ok();
        for path in std::iter::once(path).chain(resolved.as_deref()) {
            let plain = crate::fsx::plain(path);
            for variant in [path, &plain] {
                self.add_spelling(&variant.to_string_lossy(), alias);
            }
            #[cfg(windows)]
            self.add_spelling(&plain.to_string_lossy().replace('\\', "/"), alias);
        }
    }

    fn add_spelling(&mut self, path: &str, alias: &'static str) {
        // Child output includes both plain text and JSON protocol events.
        let json = serde_json::Value::String(path.to_owned()).to_string();
        for spelling in [path, &json[1..json.len() - 1]] {
            if !self.paths.iter().any(|(existing, _)| existing == spelling) {
                self.paths.push((spelling.to_owned(), alias));
            }
        }
    }

    pub fn flush(&mut self) -> std::io::Result<()> {
        self.file.flush()
    }

    pub(crate) fn line(&mut self, source: &str, line: &str) -> std::io::Result<()> {
        let mut scrubbed = std::borrow::Cow::Borrowed(line);
        for (path, alias) in &self.paths {
            if let std::borrow::Cow::Owned(replaced) =
                super::entries::replace_path(&scrubbed, path, alias)
            {
                scrubbed = replaced.into();
            }
        }
        let line = scrub_usernames(&scrubbed, &self.usernames);
        writeln!(
            self.file,
            "{}\t{source}\t{line}",
            chrono::Local::now().format("%H:%M:%S%.3f")
        )
    }
}

fn redact_names<'a>(text: &'a str, usernames: &[String]) -> std::borrow::Cow<'a, str> {
    let mut redacted = std::borrow::Cow::Borrowed(text);
    for name in usernames {
        if let std::borrow::Cow::Owned(replaced) =
            super::entries::replace_bounded(&redacted, name, "<user>", |before, after| {
                [before, after]
                    .into_iter()
                    .all(|c| c.is_none_or(|c| !c.is_alphanumeric() && c != '_'))
            })
        {
            redacted = replaced.into();
        }
    }
    redacted
}

fn scrub_usernames<'a>(line: &'a str, usernames: &[String]) -> std::borrow::Cow<'a, str> {
    let redacted = redact_names(line, usernames);
    if matches!(redacted, std::borrow::Cow::Borrowed(_)) && !line.contains('\\') {
        return redacted;
    }
    if line.starts_with('{')
        && let Ok(serde_json::Value::Object(mut fields)) = serde_json::from_str(line)
    {
        // Preserve fields used to interpret the builder protocol. Scrub payload strings.
        let protocol = fields.get("v").and_then(serde_json::Value::as_u64) == Some(1)
            && fields.contains_key("type");
        let mut changed = false;
        for (key, value) in &mut fields {
            if protocol && matches!(key.as_str(), "type" | "stage" | "code" | "level" | "result") {
                continue;
            }
            changed |= redact_payload(value, usernames);
        }
        return if changed {
            serde_json::Value::Object(fields).to_string().into()
        } else {
            line.into()
        };
    }
    redacted
}

fn redact_payload(value: &mut serde_json::Value, usernames: &[String]) -> bool {
    match value {
        serde_json::Value::String(text) => {
            if let std::borrow::Cow::Owned(redacted) = redact_names(text, usernames) {
                *text = redacted;
                return true;
            }
        }
        serde_json::Value::Array(items) => {
            return items.iter_mut().fold(false, |changed, item| {
                redact_payload(item, usernames) | changed
            });
        }
        serde_json::Value::Object(fields) => {
            return fields.values_mut().fold(false, |changed, item| {
                redact_payload(item, usernames) | changed
            });
        }
        _ => {}
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn latest_entry_can_be_read_while_builder_is_quiet() -> Result<()> {
        let home = tempfile::tempdir()?;
        let mut transcript = Transcript::start(home.path(), BuildLog::Build, &[])?;
        transcript.line("stdout", "Preparing shaders")?;
        let preview = fs::read_to_string(home.path().join("logs/last-build.log"))?;
        assert!(preview.ends_with("\tstdout\tPreparing shaders\n"));
        Ok(())
    }

    #[test]
    fn scrubs_paths_before_persisting_plain_and_json_output() -> Result<()> {
        let home = tempfile::tempdir()?;
        let profile = home.path().join("private-profile");
        let game = home.path().join("private-source.nup");
        let mut transcript = Transcript::start(
            home.path(),
            BuildLog::Build,
            &[("<profile>", &profile), ("<game source>", &game)],
        )?;
        transcript
            .usernames
            .extend(["done".into(), "player-example".into()]);
        transcript.line(
            "stderr",
            &format!("Reading {} as player-example", game.display()),
        )?;
        transcript.line(
            "stdout",
            &serde_json::json!({
                "v": 1, "type": "done",
                "text": format!("Using {} for player-example", profile.join("settings.json").display())
            }).to_string(),
        )?;
        let log = fs::read_to_string(home.path().join("logs/last-build.log"))?;
        assert!(
            log.lines()
                .next()
                .unwrap()
                .contains(crate::update::own_tag())
        );
        assert!(log.contains("Reading <game source> as <user>"));
        assert!(!log.contains("private-"));
        assert!(!log.contains("player-example"));
        let event: serde_json::Value =
            serde_json::from_str(log.lines().last().unwrap().splitn(3, '\t').nth(2).unwrap())?;
        assert_eq!(
            event["text"],
            format!(
                "Using <profile>{}settings.json for <user>",
                std::path::MAIN_SEPARATOR
            )
        );
        assert_eq!(event["type"], "done");
        Ok(())
    }
}
