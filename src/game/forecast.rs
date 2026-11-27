//! Display the builder's plan and total progress. Released builders without those events use the
//! stage-based forecast for compatibility.
use crate::{Game, GameStep, MainWindow};
use serde_json::Value;
use slint::ComponentHandle;
use std::time::{Duration, Instant};

const STAGES: [(&str, &str, f64); 5] = [
    ("read", crate::i18n::key("Read your game files"), 76.),
    (
        "lift",
        crate::i18n::key("Translate the game to PC code"),
        57.,
    ),
    ("compile", crate::i18n::key("Compile"), 305.),
    ("finish", crate::i18n::key("Put the game together"), 66.),
    ("shaders", crate::i18n::key("Prepare the shaders"), 311.),
];
const ACTIVE_LABELS: [&str; 5] = [
    crate::i18n::key("Reading your game files"),
    crate::i18n::key("Translating the game to PC code"),
    crate::i18n::key("Compiling"),
    crate::i18n::key("Putting the game together"),
    crate::i18n::key("Preparing shaders"),
];
const COMPILE: usize = 2;
const SHADERS: usize = 4;
#[derive(Default)]
pub(super) struct BuilderProgress {
    pub planned: bool,
    pub fraction: Option<f64>,
    until: Option<Instant>,
}

impl BuilderProgress {
    pub fn observe(&mut self, event: &Value, now: Instant) {
        match event["type"].as_str() {
            Some("build_plan") => {
                *self = Self {
                    planned: true,
                    ..Self::default()
                };
            }
            Some("build_progress") if self.planned => {
                self.fraction = event["fraction"]
                    .as_f64()
                    .filter(|f| (0. ..=1.).contains(f));
                self.until = event["seconds"]
                    .as_u64()
                    .and_then(|s| now.checked_add(Duration::from_secs(s)));
            }
            _ => {}
        }
    }

    pub fn seconds(&self, now: Instant) -> Option<u64> {
        self.until
            .map(|until| until.saturating_duration_since(now).as_secs())
            .filter(|s| *s > 0)
    }
}

pub(super) fn remaining(seconds: u64) -> String {
    format!("About {} remaining", duration(seconds as f64))
}

struct StageRow {
    marker: &'static str,
    title: &'static str,
    detail: String,
}

#[derive(Clone, Default)]
struct Sample {
    start: Option<f64>,
    duration: f64,
    fraction: f64,
    at: f64,
    still: f64,
    done: bool,
    kept: bool,
}

struct Clock {
    samples: [Sample; 5],
    current: Option<usize>,
    begun: Option<f64>,
    threads: u32,
    plan: Option<f64>,
    compiler_eta: Option<(f64, f64)>,
    settled: bool,
    shaders: bool,
}
impl Clock {
    fn new(threads: u32, shaders: bool, update: bool) -> Self {
        let mut samples: [Sample; 5] = Default::default();
        samples[0].kept = update;
        Self {
            samples,
            current: None,
            begun: None,
            threads,
            plan: None,
            compiler_eta: None,
            settled: false,
            shaders,
        }
    }
    fn observe(&mut self, e: &Value, now: f64) {
        let id = e["stage"]
            .as_str()
            .and_then(|id| STAGES.iter().position(|s| s.0 == id));
        match (e["type"].as_str(), id) {
            (Some("stage"), Some(idx)) if self.current != id => {
                if let Some(previous) = self.current {
                    let p = &mut self.samples[previous];
                    p.duration += now - p.start.take().unwrap_or(now);
                    p.done = true;
                }
                for prior in &mut self.samples[..idx] {
                    prior.done = true;
                }
                self.current = id;
                self.begun.get_or_insert(now);
                self.compiler_eta = None;
                let s = &mut self.samples[idx];
                s.start = Some(now);
                s.still = now;
                s.at = now;
                s.fraction = 0.;
                s.done = false;
            }
            (Some("progress"), Some(idx)) if id == self.current => {
                let s = &mut self.samples[idx];
                let f = e["fraction"].as_f64().unwrap_or(0.).clamp(0., 1.);
                if f == 0. {
                    s.still = now;
                } else if f > s.fraction {
                    s.at = now;
                    s.fraction = f;
                }
            }
            (Some("compile_plan"), _) => {
                if let (Some(n), Some(total)) = (e["changed"].as_u64(), e["total"].as_u64())
                    && total > 0
                {
                    self.plan = Some((n as f64 / total as f64).clamp(0.03, 1.));
                    self.samples[COMPILE].kept = n < total;
                }
            }
            (Some("eta"), _) if self.current == Some(COMPILE) => {
                self.compiler_eta = e["seconds"].as_f64().map(|s| (s.max(0.), now));
            }
            (Some("done"), _) => {
                if let Some(index) = self.current.take() {
                    let stage = &mut self.samples[index];
                    stage.duration += now - stage.start.take().unwrap_or(now);
                    stage.done = true;
                }
            }
            (Some("not_prepared"), _) => {
                self.shaders = false;
            }
            _ => (),
        }
    }
    fn base(&self, i: usize) -> f64 {
        let parallel = matches!(i, 1 | 2);
        STAGES[i].2
            * if parallel {
                (24. / f64::from(self.threads.clamp(1, 256))).powf(0.85)
            } else {
                1.
            }
    }
    fn moving(&self, i: usize) -> Option<f64> {
        let s = &self.samples[i];
        if s.fraction < 0.1 {
            return None;
        }
        if s.fraction == 1. {
            return Some(0.);
        }
        let movement = s.at - if i == COMPILE { s.start? } else { s.still };
        (movement >= 10.).then(|| movement / s.fraction * (1. - s.fraction))
    }
    fn pace(&self, cpu: bool) -> Option<f64> {
        let (mut actual, mut expected) = (0., 0.);
        for (i, s) in self.samples[..SHADERS].iter().enumerate() {
            if matches!(i, 1 | 2) != cpu || s.kept {
                continue;
            }
            if s.done && s.duration > 0. {
                actual += s.duration;
                expected += self.base(i);
            } else if Some(i) == self.current
                && let Some(left) = self.moving(i)
            {
                actual += (s.at - s.start.unwrap_or(s.at) + left) * s.fraction;
                expected += self.base(i) * s.fraction;
            }
        }
        (expected >= 30.).then(|| (actual / expected).clamp(0.2, 5.))
    }
    fn expected(&self, i: usize) -> f64 {
        let share = if i == COMPILE {
            self.plan.unwrap_or(1.)
        } else if self.samples[i].kept {
            0.1
        } else {
            1.
        };
        let pace = if i == SHADERS {
            1.
        } else {
            self.pace(matches!(i, 1 | 2)).unwrap_or(1.)
        };
        self.base(i) * pace * share
    }
    fn left(&self, i: usize, now: f64) -> Option<f64> {
        let s = &self.samples[i];
        if s.done {
            return Some(0.);
        }
        if Some(i) != self.current {
            return Some(self.expected(i));
        }
        let countdown = |seconds: f64, elapsed: f64| {
            (elapsed <= seconds + (seconds * 0.25).max(30.)).then(|| (seconds - elapsed).max(3.))
        };
        if i == COMPILE
            && let Some((eta, at)) = self.compiler_eta
        {
            let link = self.base(i) * 0.03 * self.pace(true).unwrap_or(1.);
            if now - at <= eta {
                return Some(eta - (now - at) + link);
            }
            return if let Some(rate) = self.moving(i) {
                countdown(rate + link, now - s.at)
            } else {
                countdown(link, now - at - eta)
            };
        }
        if let Some(rate) = self.moving(i) {
            countdown(rate, now - s.at)
        } else {
            countdown(self.expected(i), now - s.start?)
        }
    }
    fn view(&mut self, now: f64, estimate: bool) -> (Vec<StageRow>, String) {
        let ran = now - self.begun.unwrap_or(now);
        if !self.settled && ran >= 20. {
            self.settled = ran >= 120.
                || self.compiler_eta.is_some()
                || self.pace(true).is_some()
                || self.pace(false).is_some()
                || self.current.and_then(|i| self.moving(i)).is_some();
        }
        let mut rows = Vec::new();
        let mut total = Some(0.);
        let mut before = false;
        for (i, s) in self
            .samples
            .iter()
            .enumerate()
            .filter(|(i, _)| *i != SHADERS || self.shaders)
        {
            let left = self.left(i, now);
            let showing = estimate
                && self.settled
                && (i != SHADERS || self.current == Some(i) && self.moving(i).is_some());
            let marker = if s.done {
                "✓"
            } else if self.current == Some(i) {
                "●"
            } else {
                "○"
            };
            let active = self.current == Some(i) && !s.done;
            let title = if active {
                ACTIVE_LABELS[i]
            } else {
                STAGES[i].1
            };
            let detail = if s.done {
                format!("done in {}", duration(s.duration))
            } else if active {
                let percent =
                    (s.fraction > 0.).then(|| format!("{:.0}%", (s.fraction * 100.).min(99.)));
                let estimate = left
                    .filter(|_| showing)
                    .map(|t| format!("about {}", duration(t)));
                percent
                    .into_iter()
                    .chain(estimate)
                    .collect::<Vec<_>>()
                    .join(" · ")
            } else {
                String::new()
            };
            rows.push(StageRow {
                marker,
                title,
                detail,
            });
            if i == SHADERS && !s.done && !showing {
                before = true;
            } else {
                total = total.zip(left).map(|(a, b)| a + b);
            }
        }
        let eta = if estimate
            && self.settled
            && let Some(seconds) = total.filter(|n| *n > 0.)
        {
            format!(
                "About {} {}",
                duration(seconds),
                if before {
                    "before shader preparation"
                } else {
                    "remaining"
                }
            )
        } else {
            String::new()
        };
        (rows, eta)
    }
}
pub(super) fn duration(seconds: f64) -> String {
    let n = seconds.round().max(0.) as u64;
    if n < 60 {
        format!("{n}s")
    } else {
        format!("{}m {:02}s", n / 60, n % 60)
    }
}

pub(super) fn workers(setting: Option<u64>, available: u32) -> u32 {
    let threads = available.max(1);
    match setting {
        Some(0) => threads,
        Some(n) => n.clamp(1, threads as u64) as u32,
        None => {
            threads
                - match threads {
                    1 => 0,
                    2..=4 => 1,
                    5..=8 => 2,
                    _ => threads / 4,
                }
        }
    }
}

pub(super) struct Monitor {
    clock: Clock,
    builder: BuilderProgress,
    pub stages: super::report::Stages,
    window: Option<slint::Weak<MainWindow>>,
    start: Instant,
    last: Option<Instant>,
}
impl Monitor {
    pub fn new(
        window: Option<slint::Weak<MainWindow>>,
        settings: &Value,
        shaders: bool,
        update: bool,
    ) -> Self {
        let available = std::thread::available_parallelism().map_or(1, usize::from) as u32;
        let workers = workers(settings["buildWorkers"].as_u64(), available);
        Self {
            clock: Clock::new(workers, shaders, update),
            builder: BuilderProgress::default(),
            stages: Default::default(),
            window,
            start: Instant::now(),
            last: None,
        }
    }
    pub fn observe(&mut self, event: Option<&Value>) {
        let at = Instant::now();
        let now = at.duration_since(self.start).as_secs_f64();
        if let Some(e) = event {
            self.builder.observe(e, at);
            if e["type"] == "build_plan"
                && let Some(stages) = e["stages"].as_array()
            {
                self.clock.shaders = stages.iter().any(|stage| stage == "shaders");
            }
            self.clock.observe(e, now);
            if matches!(e["type"].as_str(), Some("stage" | "progress"))
                && let Some(id) = e["stage"].as_str()
            {
                self.stages.observe(id, e["fraction"].as_f64());
            }
        }
        let Some(window) = &self.window else { return };
        if event.is_none_or(|e| !matches!(e["type"].as_str(), Some("done" | "build_plan")))
            && self
                .last
                .is_some_and(|last| last.elapsed() < Duration::from_millis(500))
        {
            return;
        }
        self.last = Some(Instant::now());
        let (rows, mut eta) = self.clock.view(now, !self.builder.planned);
        let fractions: Vec<f32> = self
            .clock
            .samples
            .iter()
            .take(rows.len())
            .map(|s| if s.done { 1. } else { s.fraction as f32 })
            .collect();
        let overall = if self.builder.planned {
            eta = self.builder.seconds(at).map(remaining).unwrap_or_default();
            self.builder
                .fraction
                .map_or(-1., |fraction| fraction as f32)
        } else {
            let expected: Vec<f64> = (0..fractions.len())
                .map(|i| self.clock.expected(i))
                .collect();
            let total = expected.iter().sum::<f64>();
            fractions
                .iter()
                .zip(expected)
                .map(|(fraction, weight)| f64::from(*fraction) * weight / total)
                .sum::<f64>()
                .min(0.99) as f32
        };
        let _ = window.upgrade_in_event_loop(move |window| {
            let g = window.global::<Game>();
            if g.get_step() == GameStep::Building {
                let rows = rows
                    .into_iter()
                    .map(|row| crate::BuildStageRow {
                        marker: row.marker.into(),
                        title: row.title.into(),
                        detail: row.detail.into(),
                    })
                    .collect::<Vec<_>>();
                g.set_stage_rows(slint::ModelRc::new(slint::VecModel::from(rows)));
                g.set_stage_fractions(slint::ModelRc::new(slint::VecModel::from(fractions)));
                g.set_overall(overall);
                g.set_eta(eta.into());
            }
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn builder_estimate_counts_down_and_expires_until_the_builder_updates_it() {
        let now = Instant::now();
        let mut builder = BuilderProgress::default();
        builder.observe(
            &json!({"type":"build_plan","stages":["compile","shaders"]}),
            now,
        );
        assert!(builder.planned);
        assert_eq!(builder.fraction, None);
        builder.observe(
            &json!({"type":"build_progress","fraction":0.36,"seconds":60}),
            now,
        );
        builder.observe(&json!({"type":"eta","seconds":5}), now);
        assert_eq!(builder.fraction, Some(0.36));
        assert_eq!(builder.seconds(now + Duration::from_secs(30)), Some(30));
        assert_eq!(builder.seconds(now + Duration::from_secs(60)), None);
        builder.observe(
            &json!({"type":"build_progress","fraction":1.0,"seconds":0}),
            now,
        );
        assert_eq!(builder.fraction, Some(1.0));
        assert_eq!(builder.seconds(now), None);
    }

    #[test]
    fn forecast_waits_for_evidence_keeps_shader_estimates_separate_and_expires_stale_eta() {
        let mut c = Clock::new(24, true, false);
        c.observe(&json!({"type":"stage","stage":"compile"}), 0.);
        c.observe(&json!({"type":"eta","seconds":22.}), 5.);
        assert!(c.view(10., true).1.is_empty());
        let (rows, eta) = c.view(20., true);
        assert!(eta.contains("before shader preparation"));
        assert!(
            rows.iter()
                .filter(|row| row.marker == "○")
                .all(|row| row.detail.is_empty())
        );
        assert!(c.view(150., true).1.is_empty());
        c.observe(&json!({"type":"stage","stage":"shaders"}), 200.);
        c.observe(
            &json!({"type":"progress","stage":"shaders","fraction":0.5}),
            220.,
        );
        let (rows, eta) = c.view(220., true);
        assert!(eta.contains("remaining"));
        assert_eq!(rows[COMPILE].title, "Compile");
        assert_eq!(rows[COMPILE].detail, "done in 3m 20s");
        assert_eq!(rows[SHADERS].title, "Preparing shaders");
        assert!(rows[SHADERS].detail.starts_with("50% · about"));
    }
}
