//! Most builders can describe the game that would launch for this profile. Showing the window
//! only requires a disk lookup; the capability probe updates the picker when it finishes.
use super::{
    process::{Cancel, Context, executable, run},
    versions::Version,
};
use crate::{Game, MainWindow};
use slint::ComponentHandle;
use std::{
    cell::RefCell,
    path::{Path, PathBuf},
    thread,
    time::{Duration, Instant},
};

struct Probe {
    version: PathBuf,
    profile: PathBuf,
    started: Instant,
    built: Option<bool>,
    game_folder: Option<PathBuf>,
}
thread_local! {static PROBE:RefCell<Option<Probe>>=const { RefCell::new(None) };}

pub fn refresh(ui: &MainWindow, home_dir: &Path, profile: &Path, version: &Version) {
    let disk_build = version.folder.join("builds/default");
    let likely_built = disk_build.is_dir();
    let g = ui.global::<Game>();
    let should_probe = PROBE.with_borrow_mut(|last| {
        if let Some(p) = last
            && p.version == version.folder
            && p.profile == profile
            && p.started.elapsed() < Duration::from_secs(4)
        {
            g.set_playable(p.built.unwrap_or(likely_built));
            return false;
        }
        *last = Some(Probe {
            version: version.folder.clone(),
            profile: profile.to_owned(),
            started: Instant::now(),
            built: None,
            game_folder: None,
        });
        true
    });
    if !should_probe {
        return;
    }
    g.set_playable(likely_built);
    let weak = ui.as_weak();
    let dir = home_dir.to_owned();
    let version = version.clone();
    let profile = profile.to_owned();
    thread::spawn(move || {
        let result = Context::for_profile(&dir, &profile)
            .and_then(|context| probe(&context, &version.folder));
        let Ok((present, output)) = result else {
            return;
        };
        let _ = weak.upgrade_in_event_loop(move |ui| {
            PROBE.with_borrow_mut(|last| {
                if let Some(p) = last
                    && p.version == version.folder
                    && p.profile == profile
                {
                    p.built = Some(present);
                    p.game_folder = output.is_dir().then_some(output);
                    let g = ui.global::<Game>();
                    if g.get_current_tag() == version.tag {
                        g.set_playable(present);
                    }
                }
            });
        });
    });
}

fn probe(context: &Context, version: &Path) -> anyhow::Result<(bool, PathBuf)> {
    use anyhow::Context as _;
    let mut cmd = context.command(&version.join(executable()), false)?;
    cmd.arg("info")
        .arg("--profile")
        .arg(&context.profile)
        .args(["--output", "json"]);
    let cancel = Cancel::default();
    let runner = cancel.clone();
    let (done, watch) = std::sync::mpsc::channel::<()>();
    thread::spawn(move || {
        if watch.recv_timeout(Duration::from_secs(4))
            == Err(std::sync::mpsc::RecvTimeoutError::Timeout)
        {
            runner.request();
        }
    });
    let answer = run(cmd, "info", &cancel, &mut |_, _| {});
    let _ = done.send(());
    let info = answer?.require("Checking the build")?;
    anyhow::ensure!(
        info["capabilities"].is_object(),
        "No builder capability information."
    );
    let reported = Path::new(
        info["build"]["folder"]
            .as_str()
            .context("Missing build folder.")?,
    );
    let builds = version.join("builds");
    let parent = reported.parent().context("Missing builds folder.")?;
    anyhow::ensure!(
        parent == builds || (cfg!(windows) && parent.canonicalize()? == builds.canonicalize()?),
        "The builder reported a folder outside this version."
    );
    let output = builds.join(reported.file_name().context("Missing build folder name.")?);
    anyhow::ensure!(
        !super::versions::linked(&builds) && !super::versions::linked(&output),
        "Linked build folder"
    );
    Ok((info["build"]["present"] == true, output))
}

/// Launches from Steam or desktop shortcuts have no window probe. Resolve the
/// current profile again because enabling a code mod can change its build.
pub(super) fn launch_folder(context: &Context, version: &Path) -> anyhow::Result<PathBuf> {
    match probe(context, version) {
        Ok((_, folder)) => Ok(folder),
        Err(error) => {
            // Older builders only had builds/default and did not implement info.
            let builds = version.join("builds");
            if builds.is_dir() {
                for entry in std::fs::read_dir(&builds)? {
                    let entry = entry?;
                    if entry.file_name() != "default" && entry.path().is_dir() {
                        return Err(error.context("Could not identify this profile's build before applying sharing settings"));
                    }
                }
            }
            Ok(builds.join("default"))
        }
    }
}

/// The builder resolves code-mod profiles into their own build directory.
pub(super) fn folder(version: &Path, profile: &Path) -> PathBuf {
    PROBE
        .with_borrow(|last| {
            last.as_ref()
                .filter(|p| p.version == version && p.profile == profile)
                .and_then(|p| p.game_folder.clone())
        })
        .unwrap_or_else(|| version.join("builds/default"))
}

pub(super) fn invalidate() {
    PROBE.with_borrow_mut(|p| *p = None);
}
