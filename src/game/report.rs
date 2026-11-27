//! A failed-build report contains only published version tags, selected options, standard
//! stage identifiers and counts. Local diagnostics and builder output never enter the payload.
use anyhow::{Result, bail};
use serde::Serialize;
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    time::{Duration, Instant},
};

const ENDPOINT: &str = "https://data-services.magicbean.dev/telemetry/build";
const STAGES: [&str; 6] = ["download", "read", "lift", "compile", "finish", "shaders"];

#[derive(Default, Serialize)]
struct Stage {
    progress: f32,
    seconds: f64,
    kept: bool,
}

#[derive(Default)]
pub(super) struct Stages {
    entries: BTreeMap<&'static str, Stage>,
    active: Option<(&'static str, Instant)>,
}

impl Stages {
    pub fn observe(&mut self, name: &str, fraction: Option<f64>) {
        let Some(&name) = STAGES.iter().find(|&&stage| stage == name) else {
            return;
        };
        if self.active.is_none_or(|(last, _)| last != name) {
            self.end(true);
            self.active = Some((name, Instant::now()));
        }
        let entry = self.entries.entry(name).or_default();
        if let Some(fraction) = fraction.filter(|f| f.is_finite()) {
            entry.progress = entry.progress.max(fraction.clamp(0., 1.) as f32);
        }
    }

    fn end(&mut self, complete: bool) {
        if let Some((name, start)) = self.active.take() {
            let entry = self.entries.entry(name).or_default();
            entry.seconds += start.elapsed().as_secs_f64();
            if complete {
                entry.progress = 1.;
            }
        }
    }
}

/// Reject free-form local build names, including file names and player-supplied target paths.
fn channel(tag: &str) -> Option<&'static str> {
    for name in ["stable", "nightly"] {
        if crate::update::channel_order(tag, name, 1).is_some() {
            return Some(name);
        }
    }
    let commit = |s: &str| s.len() == 8 && s.bytes().all(|c| c.is_ascii_hexdigit());
    if tag.strip_prefix("main-").is_some_and(commit) {
        return Some("main");
    }
    let (n, sha) = tag.strip_prefix("pr-")?.split_once('-')?;
    (n.parse::<u64>().is_ok() && commit(sha)).then_some("pr")
}

pub(super) struct Summary {
    version: String,
    kind: &'static str,
    upscalers: Vec<&'static str>,
    shaders: bool,
}
impl Summary {
    pub fn new(
        tag: &str,
        update: bool,
        rebuild: bool,
        upscalers: Vec<&'static str>,
        shaders: bool,
    ) -> Self {
        Self {
            version: tag.to_owned(),
            kind: if update {
                "update"
            } else if rebuild {
                "rebuild"
            } else {
                "first build"
            },
            upscalers,
            shaders,
        }
    }

    pub fn finish(self, mut stages: Stages) -> Result<String> {
        stages.end(false);
        let launcher = crate::update::own_tag();
        let channel_name = channel(&self.version);
        let summary = json!({
            "v":1, "client":"Longshot",
            "launcher": if channel(launcher).is_some() {launcher} else {"dev"},
            "channel":channel_name.unwrap_or("unknown"),
            "version": if channel_name.is_some() {self.version.as_str()} else {"unknown"},
            "kind":self.kind,"outcome":"failed","auto":false,
            "os":std::env::consts::OS,"cpu":std::env::consts::ARCH,
            "pc":format!("{} logical processors", std::thread::available_parallelism().map_or(1,usize::from)),
            "settings":format!("Upscalers: {}; prepare shaders: {}", self.upscalers.join(","), self.shaders),
            "why":"The builder did not complete the build.","error":"","stages":stages.entries,"log": Vec::<Value>::new()
        });
        Ok(serde_json::to_string_pretty(&summary)?)
    }
}

/// Convert exactly the same allowlisted metadata shown on the Problem page into the
/// server's statistics-only shape. The server does not create an issue for auto reports.
pub(super) fn fallback(preview: &str, status: std::process::ExitStatus) -> Result<String> {
    let mut data: Value = serde_json::from_str(preview)?;
    #[cfg(unix)]
    let term = {
        use std::os::unix::process::ExitStatusExt;
        status.signal() == Some(15) || status.code() == Some(143)
    };
    #[cfg(not(unix))]
    let term = status.code() == Some(143);
    data["auto"] = json!(true);
    data["outcome"] = json!(if term { "terminated" } else { "failed" });
    data["why"] = json!(if term {
        "The builder was stopped by a signal.".to_string()
    } else if let Some(code) = status.code() {
        format!("The builder exited with code {code} before reporting a result.")
    } else {
        "The builder exited before reporting a result.".to_owned()
    });
    Ok(serde_json::to_string(&data)?)
}

pub(super) fn send(preview: &str) -> Result<()> {
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .max_redirects(0)
        .http_status_as_error(false)
        .timeout_global(Some(Duration::from_secs(20)))
        .build()
        .into();
    let answer = agent
        .post(ENDPOINT)
        .header("Content-Type", "application/json")
        .send(preview.as_bytes())?;
    match answer.status().as_u16() {
        200 | 201 => Ok(()),
        429 => bail!("The server has had many new reports this hour. Try again later."),
        n => bail!("The report was not accepted (HTTP {n})."),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    #[test]
    fn only_unreported_process_exits_qualify_for_automatic_statistics() -> Result<()> {
        use std::os::unix::process::ExitStatusExt;
        let answer = |status, message: Option<&str>| super::super::process::Answer {
            status: std::process::ExitStatus::from_raw(status),
            result: None,
            error: message.map(|m| super::super::process::BuilderFailure {
                code: "io".into(),
                message: m.into(),
            }),
        };
        assert!(
            answer(1 << 8, Some("private game path"))
                .checked_build()
                .unwrap_err()
                .downcast_ref::<super::super::process::UnexpectedExit>()
                .is_none()
        );
        let error = answer(15, None).checked_build().unwrap_err();
        let crash = error
            .downcast_ref::<super::super::process::UnexpectedExit>()
            .unwrap();
        let preview =
            Summary::new("v3.0.0", true, false, vec![], false).finish(Stages::default())?;
        let payload: Value = serde_json::from_str(&fallback(&preview, crash.status)?)?;
        assert_eq!(payload["auto"], true);
        assert_eq!(payload["outcome"], "terminated");
        assert_eq!(payload["log"], json!([]));
        assert_eq!(payload["error"], "");
        Ok(())
    }

    #[test]
    fn report_preview_has_only_allowlisted_metadata() -> Result<()> {
        let mut stages = Stages::default();
        stages.observe("/private/game.nup", Some(0.91));
        stages.observe("compile", Some(0.25));
        let preview =
            Summary::new("private-name.nup", false, false, vec!["dlss"], true).finish(stages)?;
        let payload: Value = serde_json::from_str(&preview)?;
        assert_eq!(payload["v"], 1);
        assert_eq!(payload["version"], "unknown");
        assert_eq!(payload["auto"], false);
        assert_eq!(payload["log"], json!([]));
        assert_eq!(payload["stages"]["compile"]["progress"], 0.25);
        assert!(!preview.contains("private"));
        for tag in [
            "v3.0.0",
            "nightly-829-8ef4be4f",
            "pr-1-a123bcde",
            "main-a123bcde",
        ] {
            assert!(channel(tag).is_some());
        }
        Ok(())
    }
}
