//! Interpret the builder's versioned event stream and common plain session log prefixes.
use crate::LogEntry;
use serde_json::Value;

#[derive(Default)]
pub(super) struct Reader {
    stage: String,
    stage_text: String,
    session: &'static str,
}

fn entry(time: &str, level: &str, source: &str, message: &str) -> LogEntry {
    LogEntry {
        time: time.into(),
        level: level.into(),
        source: source.into(),
        message: display_message(message).as_ref().into(),
    }
}

// Keep the raw transcript intact for Copy and Save. A builder message can contain
// several paths interleaved with prose, unlike the launcher's path-valued labels.
fn display_message(message: &str) -> std::borrow::Cow<'_, str> {
    use std::sync::LazyLock;
    static HOME: LazyLock<Option<String>> =
        LazyLock::new(|| std::env::home_dir().and_then(|p| p.to_str().map(str::to_owned)));
    let Some(home) = HOME.as_deref() else {
        return message.into();
    };
    shorten_paths(message, home)
}

fn shorten_paths<'a>(message: &'a str, home: &str) -> std::borrow::Cow<'a, str> {
    if home.is_empty() || home == std::path::MAIN_SEPARATOR_STR {
        return message.into();
    }
    let mut output = String::new();
    let mut start = 0;
    for (at, _) in message.match_indices(home) {
        let before = message[..at].chars().next_back();
        let end = at + home.len();
        let after = message[end..].chars().next();
        if before.is_none_or(|c| c.is_whitespace() || "\"'`=:([".contains(c))
            && after.is_none_or(|c| {
                std::path::is_separator(c) || c.is_whitespace() || "\"'`),]".contains(c)
            })
        {
            output.push_str(&message[start..at]);
            output.push('~');
            start = end;
        }
    }
    if start == 0 {
        return message.into();
    }
    output.push_str(&message[start..]);
    output.into()
}

fn severity(word: &str) -> Option<&'static str> {
    match word.to_ascii_uppercase().as_str() {
        "TRACE" | "DEBUG" => Some("DEBUG"),
        "INFO" | "INFORMATION" => Some("INFO"),
        "WARN" | "WARNING" => Some("WARN"),
        "ERROR" | "FATAL" | "CRITICAL" => Some("ERROR"),
        _ => None,
    }
}

fn text_entry(time: &str, source: &str, text: &str) -> LogEntry {
    let word_end = text
        .trim_start_matches('[')
        .find(|c: char| c.is_whitespace() || matches!(c, ']' | ':'));
    if let Some((word, rest)) = word_end.map(|i| text.trim_start_matches('[').split_at(i))
        && let Some(level) = severity(word)
    {
        return entry(
            time,
            level,
            source,
            rest.trim_start_matches([']', ':', ' ']),
        );
    }
    // GCC and Clang prefix diagnostics with a location; keep it intact in the message.
    for (marker, level) in [
        (": fatal error: ", "ERROR"),
        (": error: ", "ERROR"),
        (": warning: ", "WARN"),
    ] {
        if text.contains(marker) {
            return entry(time, level, source, text);
        }
    }
    entry(time, "INFO", source, text)
}

// The builder also emits a detail line giving unit size, elapsed seconds and total compile time.
fn compile_detail(line: &str) -> Option<String> {
    let (name_size, timing) = line.trim().split_once(" MB ")?;
    let (name, mb) = name_size.trim().rsplit_once(char::is_whitespace)?;
    let mb = mb.parse::<u64>().ok()?;
    let (seconds, total) = timing.trim().split_once("s  (")?;
    let (total, result) = total.split_once("s)")?;
    let seconds = seconds.parse::<u64>().ok()?;
    let total = total.parse::<u64>().ok()?;
    let retry = if result.trim() == ", alone" {
        " · retried alone"
    } else {
        ""
    };
    Some(format!(
        "{} · {mb} MB · {seconds} s · {total} s into compile{retry}",
        name.trim()
    ))
}

/// Accept only a real HH:MM:SS[.fraction] or RFC3339 prefix. Untimed output has an empty time column.
fn timestamp(line: &str) -> (&str, &str) {
    let Some((prefix, rest)) = line.split_once(char::is_whitespace) else {
        return ("", line);
    };
    let prefix = prefix.trim_matches(['[', ']']);
    if line.starts_with('[')
        && prefix.contains('.')
        && prefix
            .parse::<f64>()
            .is_ok_and(|s| s.is_finite() && s >= 0.)
    {
        return (prefix, rest.trim_start());
    }
    if chrono::NaiveTime::parse_from_str(prefix, "%H:%M:%S%.f").is_ok() {
        return (prefix, rest.trim_start());
    }
    if chrono::DateTime::parse_from_rfc3339(prefix).is_ok()
        && let Some(clock) = prefix.split_once('T').map(|(_, s)| {
            s.trim_end_matches('Z')
                .split(['+', '-'])
                .next()
                .unwrap_or(s)
        })
    {
        return (clock, rest.trim_start());
    }
    ("", line)
}

impl Reader {
    pub(super) fn new(session: &'static str) -> Self {
        Self {
            session,
            ..Self::default()
        }
    }
    fn stage(&mut self, time: &str, stage: &str, text: &str) -> Option<LogEntry> {
        if self.stage == stage && self.stage_text == text {
            return None;
        }
        self.stage = stage.to_owned();
        self.stage_text = text.to_owned();
        Some(entry(
            time,
            "INFO",
            stage,
            if text.is_empty() { stage } else { text },
        ))
    }
    pub(super) fn line(&mut self, line: &str) -> Option<LogEntry> {
        let (time, rest) = timestamp(line);
        let (source, text) = rest
            .split_once('\t')
            .filter(|(stream, _)| matches!(*stream, "stdout" | "stderr" | "launcher"))
            .unwrap_or(("", rest));
        if text.is_empty() {
            return None;
        }
        if source != "stderr"
            && text.starts_with('{')
            && let Ok(event) = serde_json::from_str::<Value>(text)
            && event["v"] == 1
            && let Some(kind) = event["type"].as_str()
        {
            let body = event["text"].as_str().unwrap_or_default();
            let stage = event["stage"].as_str().unwrap_or_default();
            match kind {
                "stage" => return self.stage(time, stage, body),
                // Fine-grained progress and estimates already have live gauges. Keep every event on disk.
                "progress" | "build_progress" | "eta" | "fetch_progress" => return None,
                "startup_progress" => {
                    return match stage {
                        "shaders" => self.stage(time, stage, "Preparing shaders"),
                        "pipelines" => self.stage(time, stage, "Loading pipelines"),
                        _ => None,
                    };
                }
                "build_plan" => {
                    let stages = event["stages"]
                        .as_array()
                        .into_iter()
                        .flatten()
                        .filter_map(Value::as_str)
                        .collect::<Vec<_>>()
                        .join(" → ");
                    let message = if stages.is_empty() {
                        "Build planned".into()
                    } else {
                        format!("Build plan · {stages}")
                    };
                    return Some(entry(time, "INFO", "build", &message));
                }
                "play_plan" => {
                    let message = event["rebuild"].as_str().map_or_else(
                        || "Game is up to date".into(),
                        |why| format!("Rebuild needed · {why}"),
                    );
                    return Some(entry(time, "INFO", "launch", &message));
                }
                "game_starting" => return self.stage(time, "launch", "Starting the game"),
                "game_started" => return self.stage(time, "launch", "Game process started"),
                "game_ready" => return self.stage(time, "launch", "Game ready"),
                "unit" => {
                    let name = event["name"].as_str().unwrap_or("Unnamed unit");
                    let result = event["result"].as_str().unwrap_or("processed");
                    let elapsed = event["seconds"]
                        .as_f64()
                        .filter(|s| s.is_finite() && *s >= 0.)
                        .map(|s| format!(" · {s:.2} s"))
                        .unwrap_or_default();
                    let level = match result {
                        "failed" => "ERROR",
                        "retried" => "WARN",
                        _ => "DEBUG",
                    };
                    let action = match result {
                        "compiled" => "Compiled",
                        "cached" => "Cache hit",
                        "retried" => "Retried",
                        "failed" => "Failed",
                        other => other,
                    };
                    return Some(entry(
                        time,
                        level,
                        "compile",
                        &format!("{action} · {name}{elapsed}"),
                    ));
                }
                "compile_plan" => {
                    if let (Some(changed), Some(total)) =
                        (event["changed"].as_u64(), event["total"].as_u64())
                    {
                        return Some(entry(
                            time,
                            "INFO",
                            "compile",
                            &format!(
                                "Compile {changed} of {total} units · {} reusable",
                                total.saturating_sub(changed)
                            ),
                        ));
                    }
                }
                "done" => {
                    return Some(entry(
                        time,
                        "INFO",
                        "build",
                        if body.is_empty() {
                            "Build completed"
                        } else {
                            body
                        },
                    ));
                }
                "warning" | "error" | "log" => {
                    if kind == "log"
                        && self.stage == "compile"
                        && let Some(detail) = compile_detail(body)
                    {
                        return Some(entry(time, "DEBUG", "compile", &detail));
                    }
                    let level = match kind {
                        "warning" => "WARN",
                        "error" => "ERROR",
                        _ => event["level"].as_str().and_then(severity).unwrap_or("INFO"),
                    };
                    return Some(entry(
                        time,
                        level,
                        event["code"]
                            .as_str()
                            .or(event["source"].as_str())
                            .unwrap_or(&self.stage),
                        body,
                    ));
                }
                _ if !body.is_empty() => return Some(entry(time, "INFO", kind, body)),
                _ => {}
            }
            return Some(entry(time, "DEBUG", kind, text));
        }
        if self.session == "build" && source.is_empty() {
            // Previously saved launcher transcripts contain human-readable stage and unit lines.
            // Recognize documented builder phrases so these sessions remain readable too.
            let stage = match text {
                "Reading your game files" | "Indexing the game's romfs" => Some("read"),
                "Translating the game to PC code" => Some("lift"),
                "Compiling the game" => Some("compile"),
                "Putting the game folder together" => Some("finish"),
                "Preparing the shaders" => Some("shaders"),
                _ => None,
            };
            if let Some(stage) = stage {
                return self.stage(time, stage, text);
            }
        }
        if self.stage == "compile"
            && let Some(detail) = compile_detail(text)
        {
            return Some(entry(time, "DEBUG", "compile", &detail));
        }
        Some(text_entry(
            time,
            if source.is_empty() {
                self.session
            } else {
                source
            },
            text,
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn progress_updates_render_once_per_startup_stage() {
        let mut reader = Reader::new("build");
        assert!(
            reader
                .line(
                    "15:30:00.012\tstdout\t{\"v\":1,\"type\":\"build_progress\",\"fraction\":0.1}"
                )
                .is_none()
        );
        let sample = r#"{"v":1,"type":"startup_progress","stage":"pipelines","permille":20}"#;
        assert_eq!(reader.line(sample).unwrap().message, "Loading pipelines");
        assert!(reader.line(sample).is_none());
        assert_eq!(
            reader
                .line(r#"{"v":1,"type":"game_ready"}"#)
                .unwrap()
                .message,
            "Game ready"
        );
    }

    #[test]
    fn alternating_compile_events_keep_units_and_real_warnings() {
        let mut read = Reader::new("build");
        assert_eq!(
            read.line("09:13:22   Compiling the game").unwrap().source,
            "compile"
        );
        let row = read
            .line("09:13:23   sample.cpp      4 MB       2s  (13s)")
            .unwrap();
        assert_eq!(row.message, "sample.cpp · 4 MB · 2 s · 13 s into compile");
        assert!(read.line("09:13:24   Compiling the game").is_none());
        let mut read = Reader::new("build");
        let stage = r#"{"v":1,"type":"stage","stage":"compile","text":"Compiling the game"}"#;
        assert_eq!(read.line(stage).unwrap().source, "compile");
        assert!(
            read.line(r#"{"v":1,"type":"progress","fraction":0.42}"#)
                .is_none()
        );
        let unit = read
            .line(r#"{"v":1,"type":"unit","name":"sample.cpp","seconds":1.27,"result":"retried"}"#)
            .unwrap();
        assert_eq!(unit.level, "WARN");
        assert_eq!(unit.message, "Retried · sample.cpp · 1.27 s");
        assert!(read.line(stage).is_none());
        let warning = read
            .line("18:02:04.882\tstderr\t[warning] sample warning")
            .unwrap();
        assert_eq!(
            (
                warning.time.as_str(),
                warning.level.as_str(),
                warning.message.as_str()
            ),
            ("18:02:04.882", "WARN", "sample warning")
        );
        assert_eq!(read.line("plain stdout").unwrap().level, "INFO");
        assert_eq!(
            read.line("demo.c:42:7: error: sample error").unwrap().level,
            "ERROR"
        );
        let game = Reader::new("game").line("[0.042] a game line").unwrap();
        assert_eq!(
            (game.time.as_str(), game.source.as_str()),
            ("0.042", "game")
        );
    }
}

#[cfg(test)]
mod path_tests {
    #[test]
    fn shorten_local_paths_without_rewriting_other_paths_or_urls() {
        let message = "Copy /home/ada/run to '/home/ada/two games' via /mnt/home/ada/file. Keep /home/adam and https://example/home/ada/test.";
        assert_eq!(
            super::shorten_paths(message, "/home/ada"),
            "Copy ~/run to '~/two games' via /mnt/home/ada/file. Keep /home/adam and https://example/home/ada/test."
        );
    }
}
