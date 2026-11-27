//! One check per source, target and approval. Edits invalidate an in-flight
//! result immediately. A short timer lets the player finish typing a PR number.
use super::{jobs::SharedWork, wizard};
use crate::UiMutexExt as _;
use crate::{Game, GameStep, MainWindow, Page};
use slint::ComponentHandle;
use std::{path::Path, time::Duration};

#[derive(Clone, Debug, PartialEq, Eq)]
struct Target {
    track: String,
    tag: String,
    release: String,
    pull: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct Inputs {
    profile: String,
    source: String,
    target: Target,
    work: String,
    authorized: bool,
}

impl Inputs {
    fn read(ui: &MainWindow) -> Self {
        let g = ui.global::<Game>();
        let track = g.get_track().to_string();
        Self {
            profile: ui.global::<crate::Players>().get_profile_id().to_string(),
            source: g.get_source_path().to_string(),
            work: g.get_work_folder().to_string(),
            authorized: g.get_source_approved() && g.get_typed_number() == g.get_confirm_number(),
            target: Target {
                release: if track == "Earlier release" {
                    g.get_release_choice().to_string()
                } else {
                    String::new()
                },
                pull: if track == "Pull request" {
                    g.get_number().to_string()
                } else {
                    String::new()
                },
                track,
                tag: g.get_install_tag().to_string(),
            },
        }
    }
    fn ready(&self, g: &Game) -> bool {
        !self.source.is_empty()
            && !g.get_tools_busy()
            && !g.get_tools_failed()
            && (g.get_warning().is_empty() || self.authorized)
            && (!self.target.tag.is_empty()
                || match self.target.track.as_str() {
                    "Earlier release" => !self.target.release.is_empty(),
                    "Pull request" => !self.target.pull.trim().is_empty(),
                    "Stable" => super::STABLE_BUILDS_AVAILABLE,
                    "Nightly" | "Main" => true,
                    _ => false,
                })
    }
}

#[derive(Default)]
pub(super) struct State {
    inputs: Option<Inputs>,
    pub revision: u64,
    pub active: Option<u64>,
    attempted: bool,
}

impl State {
    fn replace(&mut self, inputs: Inputs) -> bool {
        if self.inputs.as_ref() == Some(&inputs) {
            return false;
        }
        self.revision = self.revision.wrapping_add(1);
        self.attempted = false;
        self.inputs = Some(inputs);
        true
    }
}

fn visible(ui: &MainWindow) -> bool {
    !ui.global::<crate::Preferences>().get_changing_folder()
        && ui.get_page() == Page::Build
        && matches!(
            ui.global::<Game>().get_step(),
            GameStep::Files | GameStep::Settings
        )
}

pub(super) fn schedule(ui: &MainWindow, home: &Path, work: &SharedWork, retry: bool) {
    if !visible(ui) {
        return;
    }
    let g = ui.global::<Game>();
    let mut input = Inputs::read(ui);
    let revision;
    {
        let mut state = work.guard();
        if state
            .check
            .inputs
            .as_ref()
            .is_some_and(|old| old.target != input.target)
        {
            state.review = None;
            g.set_pending_tag("".into());
            g.set_warning("".into());
            g.set_confirm_number("".into());
            g.set_typed_number("".into());
            g.set_source_approved(false);
            input.authorized = false;
        }
        if state.check.replace(input.clone()) {
            if state.check.active.is_some() {
                state.cancel.request();
            }
            state.prepared = None;
            g.set_mod_review_needed(false);
            g.set_step(GameStep::Files);
            g.set_file_error(false);
            g.set_status("".into());
        }
        if retry {
            g.set_mod_review_needed(false);
            state.check.attempted = false;
        }
        if g.get_busy() || state.check.attempted || !input.ready(&g) {
            return;
        }
        revision = state.check.revision;
    }
    let (weak, home, work) = (ui.as_weak(), home.to_owned(), work.clone());
    slint::Timer::single_shot(Duration::from_millis(275), move || {
        let Some(ui) = weak.upgrade() else { return };
        let g = ui.global::<Game>();
        if !visible(&ui) || g.get_busy() || Inputs::read(&ui) != input || !input.ready(&g) {
            return;
        }
        {
            let mut state = work.guard();
            if state.check.revision != revision || state.check.attempted {
                return;
            }
            state.check.attempted = true;
            state.check.active = Some(revision);
        }
        if let Err(e) = wizard::check(&ui, &home, &work) {
            work.guard().check.active = None;
            g.set_file_error(true);
            g.set_status(format!("{e:#}").into());
        }
    });
}

pub(super) fn current(ui: &MainWindow, state: &State, revision: u64) -> bool {
    visible(ui)
        && state.active == Some(revision)
        && state.revision == revision
        && state.inputs.as_ref() == Some(&Inputs::read(ui))
}

pub(super) fn matches_prepared(ui: &MainWindow, work: &SharedWork) -> bool {
    work.guard().check.inputs.as_ref() == Some(&Inputs::read(ui))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn repeated_inputs_preserve_attempt_and_source_target_and_consent_invalidate_it() {
        let mut state = State::default();
        let mut key = Inputs {
            profile: "default".into(),
            source: "example.nx".into(),
            work: "cache".into(),
            authorized: false,
            target: Target {
                track: "Pull request".into(),
                tag: String::new(),
                release: String::new(),
                pull: "3".into(),
            },
        };
        assert!(state.replace(key.clone()));
        state.attempted = true;
        assert!(!state.replace(key.clone()));
        assert!(state.attempted);
        let old = state.revision;
        key.authorized = true;
        assert!(state.replace(key.clone()));
        assert_ne!(old, state.revision);
        assert!(!state.attempted);
        key.source = "other.nup".into();
        assert!(state.replace(key.clone()));
        key.profile = "other".into();
        assert!(state.replace(key.clone()));
        key.target.pull = "4".into();
        assert!(state.replace(key));
    }
}
