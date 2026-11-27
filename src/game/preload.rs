//! Start downloading a release's builder while the player looks for their game dump.
//! Source builds always go through the confirmation step first.
use super::{
    CATALOG,
    jobs::SharedWork,
    process::{Cancel, Context},
    releases::{self, Target},
};
use crate::UiMutexExt as _;
use crate::{Game, MainWindow};
use slint::ComponentHandle;
use std::{path::Path, thread};

pub struct Downloads(pub(super) SharedWork);

impl Drop for Downloads {
    fn drop(&mut self) {
        let workers = std::mem::take(&mut self.0.guard().downloads);
        for (cancel, _) in &workers {
            cancel.request();
        }
        for (_, worker) in workers {
            let _ = worker.join();
        }
    }
}

fn spawn(work: &SharedWork, cancel: Cancel, task: impl FnOnce(Cancel) + Send + 'static) {
    let token = cancel.clone();
    let worker = thread::spawn(move || task(token));
    let mut state = work.guard();
    state.downloads.retain(|(_, worker)| !worker.is_finished());
    state.downloads.push((cancel, worker));
}

pub(super) fn start(ui: &MainWindow, home: &Path, work: &SharedWork) {
    if ui.global::<crate::Preferences>().get_changing_folder() {
        return;
    }
    let global = ui.global::<Game>();
    let generation;
    let token = Cancel::default();
    {
        let mut state = work.guard();
        state.preload.request();
        state.preload = token.clone();
        state.generation = state.generation.wrapping_add(1);
        generation = state.generation;
    }
    let track = global.get_track().to_string();
    let explicit = global.get_install_tag().to_string();
    let cached = CATALOG
        .read()
        .unwrap()
        .as_ref()
        .and_then(|c| match track.as_str() {
            "Stable" if super::STABLE_BUILDS_AVAILABLE => c.stable.first().cloned(),
            "Nightly" => c.nightly.first().cloned(),
            _ => None,
        });
    let selected = if !explicit.is_empty() {
        Some(explicit)
    } else if track == "Earlier release" {
        Some(global.get_release_choice().to_string()).filter(|tag| super::selectable_release(tag))
    } else {
        cached
    };
    global.set_tools_ready(false);
    global.set_tools_fraction(-1.);
    global.set_tools_failed(false);
    if selected.is_none() && !matches!(track.as_str(), "Stable" | "Nightly") {
        global.set_tools_busy(false);
        global.set_tools_tag("".into());
        global.set_tools_note("Review the selected version before downloading its tools.".into());
        return;
    }
    let dir = home.to_owned();
    let weak = ui.as_weak();
    let state = work.clone();
    global.set_tools_tag(selected.as_deref().unwrap_or(&track).into());
    global.set_tools_busy(true);
    global.set_tools_note("Looking for this version’s builder…".into());
    spawn(work, token, move |token| {
        let work = state;
        let result = (|| {
            let tag = match selected {
                Some(t) => t,
                None => releases::latest(&track.to_ascii_lowercase())?,
            };
            let ctx = Context::load(&dir)?;
            let mut last = std::time::Instant::now() - std::time::Duration::from_secs(1);
            releases::ensure_builder(
                &ctx,
                &Target::Release(tag.clone()),
                &token,
                &mut |msg, n| {
                    if last.elapsed().as_millis() < 80 {
                        return;
                    }
                    last = std::time::Instant::now();
                    let work = work.clone();
                    let msg = msg.to_owned();
                    let tag = tag.clone();
                    let _ = weak.upgrade_in_event_loop(move |ui| {
                        if work.guard().generation != generation {
                            return;
                        }
                        let g = ui.global::<Game>();
                        g.set_tools_tag(tag.into());
                        g.set_tools_note(msg.into());
                        g.set_tools_fraction(n.unwrap_or(-1.));
                    });
                },
            )?;
            anyhow::Ok(tag)
        })();
        let _ = weak.upgrade_in_event_loop(move |ui| {
            if work.guard().generation != generation {
                return;
            }
            let g = ui.global::<Game>();
            g.set_tools_busy(false);
            match result {
                Ok(tag) => {
                    g.set_tools_tag(tag.into());
                    g.set_tools_ready(true);
                    g.set_tools_note("Installed on this PC".into());
                    g.set_tools_fraction(1.);
                }
                Err(e) => {
                    g.set_tools_failed(true);
                    g.set_tools_note(format!("{e:#}").into());
                }
            }
        });
    });
}

/// Prepare a first build on the default channel, or the default version's next update
/// when the player has enabled early downloads. Source builds always need explicit review.
pub(super) fn likely(home: &Path, catalog: &super::catalog::Catalog, work: &SharedWork) {
    if std::env::var("LONGSHOT_LAUNCHER_NO_PREFETCH").as_deref() == Ok("1") {
        return;
    }
    let Ok(settings) = crate::home::settings(home) else {
        return;
    };
    let Ok(versions) = super::versions::listed(home) else {
        return;
    };
    let chosen = match versions.first() {
        None => match super::default_track() {
            "Stable" => catalog.stable.first().cloned(),
            "Nightly" => catalog.nightly.first().cloned(),
            _ => None,
        },
        Some(version)
            if settings["downloadUpdatesEarly"] == true
                && !super::channel::Channel::from_prefix(&version.tag).is_source() =>
        {
            catalog.update_for(&version.tag)
        }
        _ => None,
    };
    let Some(tag) = chosen else { return };
    if super::library::existing(home, &settings, &tag).is_ok_and(|folder| folder.is_some()) {
        return;
    }
    let home = home.to_owned();
    spawn(work, Cancel::default(), move |cancel| {
        if let Ok(ctx) = Context::load(&home) {
            let _ = releases::ensure_builder(&ctx, &Target::Release(tag), &cancel, &mut |_, _| {});
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shutdown_waits_until_cancelled_prefetch_has_exited() {
        let work = SharedWork::default();
        let downloads = Downloads(work.clone());
        let (done, receipt) = std::sync::mpsc::channel();
        spawn(&work, Cancel::default(), move |cancel| {
            while cancel.check().is_ok() {
                thread::sleep(std::time::Duration::from_millis(1));
            }
            done.send(()).unwrap();
        });
        drop(downloads);
        assert!(receipt.try_recv().is_ok());
    }
}
