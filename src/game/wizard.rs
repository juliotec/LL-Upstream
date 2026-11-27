use super::{
    CATALOG, LANGS, check_updates, find_files,
    jobs::{Prepared, Progress, SharedWork, Work, worker},
    process::{Cancel, Context, run, validated_source},
    releases::{self, Target},
    versions,
};
use crate::UiMutexExt as _;
use crate::{Game, MainWindow, home, players};
use anyhow::{Context as _, Result, bail, ensure};
use serde_json::{Value, json};
use slint::{ComponentHandle, SharedString};
use std::{
    path::{Path, PathBuf},
    thread,
};

pub(super) enum Install {
    New,
    Channel(String),
    Rebuild,
    Update { play_after: bool },
}

#[derive(Clone, Default)]
pub(super) enum BuildIntent {
    #[default]
    New,
    Rebuild(versions::Version),
    Update(versions::Version),
}

impl BuildIntent {
    pub fn instance(&self) -> Option<&versions::Version> {
        match self {
            Self::New => None,
            Self::Rebuild(instance) | Self::Update(instance) => Some(instance),
        }
    }

    pub fn relocate(&mut self, versions: &[versions::Version]) -> Result<()> {
        if let Self::Rebuild(instance) | Self::Update(instance) = self {
            *instance = versions
                .iter()
                .find(|v| v.id == instance.id && v.tag == instance.tag)
                .context("The selected installation changed. Open the build again.")?
                .clone();
        }
        Ok(())
    }

    fn updating(&self) -> bool {
        matches!(self, Self::Update(_))
    }

    fn validate(&self, home_dir: &Path, folder: &Path) -> Result<()> {
        let listed = versions::listed(home_dir)?;
        if let Some(instance) = self.instance() {
            ensure!(
                listed.iter().any(|v| v.folder == instance.folder
                    && v.tag == instance.tag
                    && v.id == instance.id),
                "The selected version changed. Open the build again."
            );
        }
        match self {
            Self::Rebuild(instance) => ensure!(
                folder == instance.folder,
                "The rebuild destination changed."
            ),
            Self::New | Self::Update(_) => ensure!(
                !listed.iter().any(|v| v.folder == folder)
                    && !super::library::build_output_exists(folder)?,
                "This build destination is already occupied. Choose the version again."
            ),
        }
        Ok(())
    }
}

pub(super) fn open(
    ui: &MainWindow,
    home_dir: &Path,
    work: &SharedWork,
    install: Install,
) -> Result<()> {
    let g = ui.global::<Game>();
    ensure!(!g.get_busy(), "Wait for the current operation to finish.");
    let ctx = Context::load(home_dir)?;

    let mut intent = BuildIntent::New;
    let mut pinned_update = SharedString::default();
    let mut track = super::default_track();
    let mut number = SharedString::default();
    let tag = match &install {
        Install::Channel(channel) => {
            ensure!(
                channel != "Stable" || super::STABLE_BUILDS_AVAILABLE,
                super::STABLE_UNAVAILABLE
            );
            track = channel;
            SharedString::default()
        }
        Install::New => SharedString::default(),
        Install::Rebuild => {
            let selected = versions::selected(home_dir, None)?;
            track = super::channel(&selected.tag);
            let tag = selected.tag.clone();
            intent = BuildIntent::Rebuild(selected);
            tag.into()
        }
        Install::Update { play_after } => {
            let target = g.get_update_tag();
            ensure!(!target.is_empty(), "Check for updates first.");
            let selected = versions::selected(home_dir, None)?;
            ensure!(
                selected.tag == g.get_current_tag().as_str(),
                "The selected version changed. Choose it again."
            );
            if *play_after {
                ensure!(
                    !g.get_playing(),
                    "Close the game before updating and playing."
                );
            }
            ensure!(
                super::channel(&target) == super::channel(&selected.tag),
                "The update channel changed. Check again."
            );
            track = super::channel(&target);
            pinned_update = target.clone();
            intent = BuildIntent::Update(selected);
            if let Some(pr) = super::channel::tag_pr(&target) {
                number = pr.to_string().into();
                SharedString::default()
            } else if track == "Main" {
                // Re-resolve and review the source commit before building it.
                SharedString::default()
            } else {
                target
            }
        }
    };
    g.set_install_tag(tag);
    g.set_update_target(pinned_update);
    location(ui, &ctx, &intent);
    g.set_track(track.into());
    let game = super::original_game(&ctx.profile, &ctx.settings)?;
    g.set_source_path(
        game.filter(|p| p.exists())
            .map_or_else(SharedString::default, |p| {
                p.to_string_lossy().as_ref().into()
            }),
    );
    let flag = |key: &str| ctx.settings[key] == true;
    let installed = intent
        .instance()
        .map(versions::Version::modules)
        .transpose()?;
    let enabled = |key: &str, name: &str| {
        installed
            .as_ref()
            .map_or_else(|| flag(key), |list| list.contains(&name))
    };
    let fsr_library = intent
        .instance()
        .and_then(|version| super::upscaler::library_for(&version.folder))
        .or_else(|| ctx.settings["fsrLibraryPath"].as_str().map(PathBuf::from));
    g.set_fsr_library(
        fsr_library
            .as_deref()
            .and_then(Path::to_str)
            .unwrap_or_default()
            .into(),
    );
    g.set_dlss(enabled("dlss", "dlss") && super::licenses::accepted("dlss", &ctx.settings));
    g.set_xess(enabled("xess", "xess") && super::licenses::accepted("xess", &ctx.settings));
    g.set_fsr(enabled("fsr", "fsr4") && super::licenses::accepted("fsr4", &ctx.settings));
    g.set_language(
        ctx.settings["language"]
            .as_str()
            .filter(|s| LANGS.contains(s))
            .unwrap_or("en-US")
            .into(),
    );
    g.set_shaders(true);
    g.set_file_error(false);
    g.set_mod_review_needed(false);
    g.set_source_approved(false);
    g.set_warning("".into());
    g.set_play_after_build(matches!(install, Install::Update { play_after: true }));
    g.set_pending_tag("".into());
    g.set_status("".into());
    g.set_step(crate::GameStep::Files);
    g.set_license("".into());
    g.set_folders_open(false);
    g.set_number(number);
    {
        let mut state = work.guard();
        state.preload.request();
        let generation = state.generation.wrapping_add(1);
        *state = Work {
            generation,
            downloads: std::mem::take(&mut state.downloads),
            intent,
            ..Work::default()
        };
    }
    g.set_requirements("Checking this PC…".into());
    g.set_requirements_detail("".into());
    let weak = ui.as_weak();
    thread::spawn(move || {
        let report = super::requirements::check();
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let g = ui.global::<Game>();
            g.set_requirements(report.title.into());
            g.set_requirements_detail(report.detail.into());
            g.set_requirements_colour(
                report
                    .colour
                    .unwrap_or_else(|| ui.global::<crate::Ink>().get_sage()),
            );
        });
    });
    ui.set_page(crate::Page::Build);
    g.invoke_prepare_tools();
    super::autocheck::schedule(ui, home_dir, work, false);
    find_files(ui);
    if CATALOG.read().unwrap().is_none()
        && (g.get_install_tag().is_empty() || ctx.settings["checkUpdates"] != false)
    {
        check_updates(ui, home_dir, work, false);
    }
    Ok(())
}

pub(super) fn location(ui: &MainWindow, ctx: &Context, intent: &BuildIntent) {
    let g = ui.global::<Game>();
    g.set_work_folder(ctx.cache.to_string_lossy().as_ref().into());
    let target = match intent {
        BuildIntent::Rebuild(instance) => instance.folder.clone(),
        BuildIntent::New | BuildIntent::Update(_) => super::install_root(&ctx.home, &ctx.settings),
    };
    let storage = super::requirements::BuildSpace::inspect(&ctx.cache, &target);
    g.set_build_space(storage.available().into());
    g.set_first_build_space(
        storage
            .recommendation(&ui.global::<crate::Locale>().get_language())
            .into(),
    );
    g.set_low_build_space(storage.below_estimate());
    g.set_space_measured(storage.measured());
}

pub(super) fn check(ui: &MainWindow, home_dir: &Path, work: &SharedWork) -> Result<()> {
    let g = ui.global::<Game>();
    let source = validated_source(Path::new(g.get_source_path().as_str()))?;
    let ctx = Context::load(home_dir)?;
    let tag = g.get_install_tag().to_string();
    let track = g.get_track().to_string();
    let number = g.get_number().to_string();
    let requested_release = (track == "Earlier release")
        .then(|| g.get_release_choice().to_string())
        .filter(|t| !t.is_empty());
    if tag.is_empty() {
        ensure!(
            track != "Stable" || super::STABLE_BUILDS_AVAILABLE,
            super::STABLE_UNAVAILABLE
        );
    }
    if let Some(tag) = &requested_release {
        ensure!(
            super::selectable_release(tag),
            "This release is unavailable for new builds."
        );
    }
    if track == "Earlier release" && requested_release.is_none() {
        bail!("Choose a release from the list.");
    }
    let preloaded = if g.get_tools_ready() {
        g.get_tools_tag().to_string()
    } else {
        String::new()
    };
    let approved = g.get_source_approved() && g.get_confirm_number() == g.get_typed_number();
    let intent = work.guard().intent.clone();
    let update_target = g.get_update_target().to_string();
    let reviewed = work
        .guard()
        .review
        .clone()
        .filter(|t| t.tag() == g.get_pending_tag().as_str() && approved);
    g.set_status("".into());
    worker(ui, home_dir, work, move |cancel, progress| {
        let target = if !tag.is_empty() {
            Target::Release(tag)
        } else if let Some(tag) = requested_release {
            ensure!(versions::valid_name(&tag), "Invalid version.");
            Target::Release(tag)
        } else if let Some(t) = reviewed {
            t
        } else {
            let t = if matches!(track.as_str(), "Stable" | "Nightly") && !preloaded.is_empty() {
                Target::Release(preloaded)
            } else {
                releases::resolve(&track, &number)?
            };
            ensure!(
                update_target.is_empty() || update_target == t.tag(),
                "This update changed. Check for updates again."
            );
            if let Target::Source(source) = t {
                return Ok(Progress::Review(source));
            }
            t
        };
        ensure!(
            update_target.is_empty() || update_target == target.tag(),
            "This update changed. Check for updates again."
        );
        let exe = if let BuildIntent::Rebuild(instance) = &intent {
            ensure!(
                instance.tag == target.tag(),
                "The selected version changed."
            );
            instance.folder.join(super::process::executable())
        } else {
            releases::ensure_build_destination(&ctx, &target, cancel, progress)?
        };
        intent.validate(&ctx.home, exe.parent().context("Invalid builder folder")?)?;
        progress("Checking your game files…", None);
        let mut cmd = ctx.command(&exe, false)?;
        cmd.args(["check", "--game"]).arg(&source);
        ctx.configure(&mut cmd)?;
        let check = run(cmd, "check", cancel, progress)?.require("Checking game files")?;
        ensure!(
            check["ok"] == true,
            "{}",
            check["text"]
                .as_str()
                .unwrap_or("These game files are not supported.")
        );
        cancel.check()?;
        let mod_review = crate::mods::trust::pending(&ctx.home, &exe, &ctx.profile)
            .map(|grants| !grants.is_empty());
        cancel.check()?;
        Ok(Progress::Checked(
            Box::new(Prepared {
                ctx,
                exe,
                game: source,
                tag: target.tag().to_owned(),
                intent,
            }),
            mod_review,
        ))
    });
    Ok(())
}

pub(super) struct Options {
    dlss: bool,
    fsr: bool,
    xess: bool,
    pub(super) language: String,
    pub(super) shaders: bool,
}
impl Options {
    pub(super) fn upscalers(&self) -> Vec<&'static str> {
        [(self.dlss, "dlss"), (self.fsr, "fsr4"), (self.xess, "xess")]
            .into_iter()
            .filter_map(|(on, name)| on.then_some(name))
            .collect()
    }
}
pub(super) fn build(ui: &MainWindow, home_dir: &Path, work: &SharedWork) -> Result<()> {
    let g = ui.global::<Game>();
    ensure!(
        super::autocheck::matches_prepared(ui, work),
        "Wait for the selected game files to be checked."
    );
    let p = work
        .guard()
        .prepared
        .clone()
        .context("Check your game files first.")?;
    ensure!(
        players::current_dir(home_dir)? == p.ctx.profile,
        "The active profile changed. Choose the game files again."
    );
    let options = Options {
        dlss: g.get_dlss(),
        fsr: g.get_fsr(),
        xess: g.get_xess(),
        language: g.get_language().to_string(),
        shaders: g.get_shaders(),
    };
    let mut settings = home::settings(home_dir)?;
    if !cfg!(windows) && options.fsr {
        let source = g.get_fsr_library();
        super::upscaler::open_library(Path::new(source.as_str()))?;
        settings["fsrLibraryPath"] = json!(source.as_str());
    }
    ensure!(
        !options.dlss || super::licenses::accepted("dlss", &settings),
        "Accept NVIDIA's licence first."
    );
    ensure!(
        !options.xess || super::licenses::accepted("xess", &settings),
        "Accept Intel's licence first."
    );
    ensure!(
        !options.fsr || super::licenses::accepted("fsr4", &settings),
        "Review AMD's licence information first."
    );
    ensure!(
        LANGS.contains(&options.language.as_str()),
        "Choose a supported game language."
    );
    let report_home = home_dir.to_owned();
    let statistics = p.ctx.statistics();
    let summary = super::report::Summary::new(
        &p.tag,
        p.intent.updating(),
        !g.get_install_tag().is_empty(),
        options.upscalers(),
        options.shaders,
    );
    g.set_log_rows(slint::ModelRc::default());
    g.set_overall(-1.);
    g.set_stage_fractions(slint::ModelRc::default());
    g.set_show_log(false);
    g.set_log_filter("".into());
    g.set_eta("".into());
    g.set_stage_rows(slint::ModelRc::default());
    let weak = ui.as_weak();
    g.set_step(crate::GameStep::Building);
    g.set_status("Starting the build…".into());
    worker(ui, home_dir, work, move |cancel, progress| {
        let mut monitor = super::forecast::Monitor::new(
            Some(weak),
            &settings,
            options.shaders,
            p.intent.updating(),
        );
        let started = std::time::Instant::now();
        match execute_build(p, &options, &settings, cancel, progress, &mut monitor) {
            Ok(()) => Ok(Progress::Built(started.elapsed())),
            Err(e) if cancel.check().is_err() => Err(e),
            Err(e) => {
                let report = summary.finish(monitor.stages)?;
                if statistics
                    && let Some(died) = e.downcast_ref::<super::process::UnexpectedExit>()
                    && home::settings(&report_home)
                        .is_ok_and(|s| s["sendPerformanceStatistics"] == true)
                    && let Ok(payload) = super::report::fallback(&report, died.status)
                {
                    thread::spawn(move || {
                        let _ = super::report::send(&payload);
                    });
                }
                Ok(Progress::Problem {
                    why: format!("{e:#}"),
                    report,
                })
            }
        }
    });
    Ok(())
}

fn execute_build(
    p: Prepared,
    options: &Options,
    settings: &Value,
    cancel: &Cancel,
    progress: &mut dyn FnMut(&str, Option<f32>),
    monitor: &mut super::forecast::Monitor,
) -> Result<()> {
    p.ctx.prepare_mod_store()?;
    let mut transcript =
        crate::logbook::Transcript::start(&p.ctx.home, crate::logbook::BuildLog::Build)
            .inspect_err(|e| eprintln!("Could not open the local build log: {e}"))
            .ok();
    let mut record = |source: &str, line: &str| {
        if let Some(log) = &mut transcript
            && let Err(e) = log.line(source, line)
        {
            eprintln!("Could not write the local build log: {e}");
            transcript = None;
        }
    };
    record("launcher", "[INFO] Build started");
    build_and_register(p, options, settings, cancel, progress, monitor, &mut record)
        .inspect_err(|error| record("launcher", &format!("[ERROR] {error:#}")))
}

fn build_and_register(
    p: Prepared,
    options: &Options,
    settings: &Value,
    cancel: &Cancel,
    progress: &mut dyn FnMut(&str, Option<f32>),
    monitor: &mut super::forecast::Monitor,
    record: &mut dyn FnMut(&str, &str),
) -> Result<()> {
    let destination = p.exe.parent().context("Invalid builder folder")?;
    p.intent.validate(&p.ctx.home, destination)?;
    if !cfg!(windows) && options.fsr {
        let source = settings["fsrLibraryPath"]
            .as_str()
            .context("Choose your Vulkan FSR 4 library.")?;
        super::upscaler::pin(destination, Path::new(source))?;
    }
    let marker = destination.join(versions::INSTALLING);
    if !matches!(p.intent, BuildIntent::Rebuild(_)) && !marker.try_exists()? {
        std::fs::File::create_new(&marker)?;
    }
    let upscalers = options.upscalers();
    let _stay_awake = super::awake::BuildInhibitor::acquire();
    let reused = super::reuse::existing(&p, options, cancel, progress).unwrap_or_else(|error| {
        eprintln!("Could not reuse the existing game: {error:#}");
        None
    });
    cancel.check()?;
    let folder = if let Some(folder) = reused {
        record(
            "launcher",
            "[INFO] Reused verified files from an installed copy of this version.",
        );
        folder
    } else {
        let storage = super::requirements::BuildSpace::inspect(&p.ctx.cache, destination);
        if storage.below_estimate() {
            record(
                "launcher",
                &format!(
                    "[WARN] {}. {} Updates may use less.",
                    storage.available(),
                    storage.recommendation("en-US")
                ),
            );
        }
        run_builder(&p, options, settings, cancel, progress, monitor, record)?
    };
    let folder =
        built_in(&folder, destination).context("The game folder is missing from this version.")?;
    if !cfg!(windows) && options.fsr {
        super::upscaler::install_for_build(destination, &folder)?;
    }
    versions::register(
        &p.ctx.home,
        &p.tag,
        &p.game,
        &folder,
        &upscalers,
        p.intent.instance(),
    )?;
    if !matches!(p.intent, BuildIntent::Rebuild(_)) {
        let _ = std::fs::remove_file(marker);
    }
    if let BuildIntent::Update(old) = &p.intent {
        if let Some(version) = p.exe.parent()
            && let Err(error) =
                super::steam::retarget_updated_instance(&p.ctx.home, &old.folder, version)
        {
            let warning = format!(
                "Could not update the Steam shortcut. Use Add to Steam on the updated version to repair it: {error}"
            );
            record("launcher", &format!("[WARN] {warning}"));
            progress(&warning, None);
        }
        if old.default {
            let selected = versions::selected(&p.ctx.home, Some(&folder))?;
            versions::make_default(&p.ctx.home, &selected)?;
        }
    }
    let selected = versions::selected(&p.ctx.home, Some(&folder))?;
    let instance_id = if selected.id.is_empty() {
        &p.tag
    } else {
        &selected.id
    };
    players::patch_profile(&p.ctx.profile, |o| {
        o.insert("version".into(), json!(instance_id));
        o.insert("game".into(), json!(p.game));
        o.insert("upscalers".into(), json!(upscalers));
    })?;
    let mut patch = json!({"dlss": options.dlss, "fsr": options.fsr, "xess": options.xess, "language": options.language});
    for (key, value) in super::source::fields(&p.game)? {
        patch[key] = value;
    }
    home::patch_settings(&p.ctx.home, &patch)?;
    if p.intent.updating()
        && let Err(e) = super::prune::after_update(&p.ctx.home)
    {
        eprintln!("Version cleanup deferred: {e}");
    }
    let keep = home::settings(&p.ctx.home)?["keepBuildFiles"] != false;
    if let Err(error) = crate::storage::after_build(&p.ctx.home, keep) {
        crate::diagnostics::event("Build file cleanup did not complete");
        eprintln!("Build completed, but its temporary files could not be cleaned up: {error}");
    }
    Ok(())
}

/// `folder`, the game folder the builder reported, in the spelling of the version folder `version`, when it is a folder
/// in that version's `builds`. A Linux home symlink or a Windows verbatim path can give the builder a different
/// spelling for the same directory. Keep the launcher's spelling for registry lookups.
fn built_in(folder: &Path, version: &Path) -> Option<PathBuf> {
    let builds = version.join("builds");
    let registered = builds.join(folder.file_name()?);
    (versions::is_build_in(folder, &builds) && registered.is_dir()).then_some(registered)
}

fn run_builder(
    p: &Prepared,
    options: &Options,
    settings: &Value,
    cancel: &Cancel,
    progress: &mut dyn FnMut(&str, Option<f32>),
    monitor: &mut super::forecast::Monitor,
    record: &mut dyn FnMut(&str, &str),
) -> Result<PathBuf> {
    let upscalers = options.upscalers();
    let mut cmd = p.ctx.command(&p.exe, false)?;
    cmd.arg("build")
        .arg("--profile")
        .arg(&p.ctx.profile)
        .arg("--game")
        .arg(&p.game)
        .arg("--language")
        .arg(&options.language);
    p.ctx.configure(&mut cmd)?;
    cmd.arg("--upscaler").arg(if upscalers.is_empty() {
        "none".to_string()
    } else {
        upscalers.join(",")
    });
    let licenses: Vec<_> = upscalers
        .iter()
        .filter_map(|module| super::licenses::vendor(module))
        .collect();
    if !licenses.is_empty() {
        cmd.arg("--accept-licenses").arg(licenses.join(","));
    }
    if !options.shaders {
        cmd.arg("--no-prepare-shaders");
    }
    let available = thread::available_parallelism().map_or(1, usize::from) as u32;
    let jobs = super::forecast::workers(settings["buildWorkers"].as_u64(), available);
    cmd.arg("--jobs").arg(jobs.to_string());
    if p.ctx.statistics() {
        cmd.arg("--send-build-statistics");
    }
    #[cfg(windows)]
    if options.shaders && super::nvidia::windows::adjust().unwrap_or(false) {
        progress(
            "NVIDIA's shader cache was raised to 10 GB for shader preparation.",
            None,
        );
    }
    let result = super::process::run_with_stages(
        cmd,
        "done",
        cancel,
        progress,
        &mut |e| monitor.observe(e),
        record,
    )?
    .checked_build()?;
    Ok(PathBuf::from(
        result["folder"]
            .as_str()
            .context("The build finished without a game folder.")?,
    ))
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use std::{fs, os::unix::fs::PermissionsExt};

    #[test]
    fn a_build_reported_under_the_real_home_registers_under_the_linked_home() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let real_home = temp.path().join("var/home");
        let home_link = temp.path().join("home");
        let version = real_home.join("player/versions/nightly-845-388c9d4b");
        let game = version.join("builds/default");
        fs::create_dir_all(&game)?;
        std::os::unix::fs::symlink(&real_home, &home_link)?;
        let linked_version = home_link.join("player/versions/nightly-845-388c9d4b");
        let linked_game = linked_version.join("builds/default");

        assert_eq!(built_in(&game, &linked_version), Some(linked_game.clone()));
        assert_eq!(built_in(&linked_game, &version), Some(game));
        assert_eq!(
            built_in(&version.join("builds/missing"), &linked_version),
            None
        );
        let other = real_home.join("player/versions/nightly-846/builds/default");
        fs::create_dir_all(&other)?;
        assert_eq!(built_in(&other, &linked_version), None);
        Ok(())
    }

    #[test]
    fn updating_an_instance_keeps_an_existing_copy_of_its_target_intact() -> Result<()> {
        use sha2::{Digest, Sha256};
        let temp = tempfile::tempdir()?;
        let home_dir = temp.path();
        let source = home_dir.join("copy.nup");
        fs::write(&source, b"synthetic")?;
        let profile = home_dir.join("profiles/default");
        fs::create_dir_all(&profile)?;
        home::write_json(
            &profile.join("profile.json"),
            &json!({"v":1,"name":"Player","version":"updating"}),
        )?;
        let ctx = Context::load(home_dir)?;
        let name = super::super::process::executable();
        let target = "v3.2.1";
        let mut rows = Vec::new();
        let script = b"#!/bin/sh
set -eu
mkdir -p builds/default
echo made > builds/default/example
printf '{\"v\":1,\"type\":\"done\",\"folder\":\"%s/builds/default\"}\n' \"$PWD\"
";
        for (id, tag) in [("updating", "v3.2.0"), ("keep-me", target)] {
            let version = home_dir.join("versions").join(tag);
            let folder = version.join("builds/default");
            fs::create_dir_all(&folder)?;
            fs::write(folder.join("example"), id)?;
            fs::write(version.join(name), script)?;
            fs::set_permissions(version.join(name), fs::Permissions::from_mode(0o755))?;
            home::write_json(
                &version.join("longshot_release.json"),
                &json!({"v":1,"version":tag,"platform":releases::PLATFORM,"program":name,"full":{"size":script.len(),"sha256":format!("{:x}",Sha256::digest(script))}}),
            )?;
            rows.push(json!({"id":id,"version":tag,"folder":folder,"default":id=="updating"}));
        }
        home::write_json(
            &home_dir.join(crate::game::registry::OWN),
            &json!({"format":1,"builds":rows}),
        )?;
        let selected = versions::selected(home_dir, None)?;
        let target_folder = home_dir.join("versions").join(target);
        let original = target_folder.join("builds/default/example");
        let intent = BuildIntent::Update(selected);
        assert!(intent.validate(home_dir, &target_folder).is_err());
        let other = versions::selected(home_dir, Some(&target_folder))?;
        assert!(
            BuildIntent::Rebuild(other.clone())
                .validate(home_dir, &other.folder)
                .is_ok()
        );
        assert!(
            BuildIntent::Rebuild(other)
                .validate(home_dir, &home_dir.join("versions/v3.2.0"))
                .is_err()
        );
        let cancel = Cancel::default();
        let exe = releases::ensure_build_destination(
            &ctx,
            &Target::Release(target.into()),
            &cancel,
            &mut |_, _| {},
        )?;
        let dest = exe.parent().unwrap().to_owned();
        assert_ne!(dest, target_folder);
        let options = Options {
            dlss: false,
            fsr: false,
            xess: false,
            language: "en-US".into(),
            shaders: false,
        };
        execute_build(
            Prepared {
                ctx,
                exe,
                game: source,
                tag: target.into(),
                intent,
            },
            &options,
            &json!({}),
            &cancel,
            &mut |_, _| {},
            &mut super::super::forecast::Monitor::new(None, &json!({}), false, true),
        )?;
        assert_eq!(fs::read_to_string(original)?, "keep-me");
        assert_eq!(
            fs::read_to_string(dest.join("builds/default/example"))?,
            "made\n"
        );
        let installed = versions::listed(home_dir)?;
        assert_eq!(installed.len(), 2);
        assert!(installed.iter().all(|v| v.tag == target));
        assert!(
            installed
                .iter()
                .any(|v| v.id == "updating" && v.folder == dest)
        );
        assert!(
            installed
                .iter()
                .any(|v| v.id == "keep-me" && v.folder == target_folder)
        );
        assert_eq!(versions::selected(home_dir, None)?.id, "updating");
        Ok(())
    }

    #[test]
    fn successful_build_and_play_reuse_the_profile_and_registered_version() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let version = root.join("versions/v3.2.0");
        let profile = root.join("profiles/default");
        fs::create_dir_all(&version)?;
        fs::create_dir_all(&profile)?;
        home::write_json(
            &profile.join("profile.json"),
            &json!({"v":1,"name":"Default","unknown":[{"ok":true}]}),
        )?;
        let exe = version.join(crate::game::process::executable());
        // A real child process with the supported NDJSON ABI. The fixture never reads game content.
        fs::write(
            &exe,
            r#"#!/bin/sh
set -eu
if [ "$1" = mod ] && [ "$8" = installed ]; then
    echo '{"ok":true,"result":{"mods":[]}}'
elif [ "$1" = info ]; then
    test "$2" = --profile
    test -f "$3/profile.json"
    printf '{"v":1,"type":"info","capabilities":{},"build":{"present":true,"folder":"%s/builds/code-mod"}}\n' "$PWD"
elif [ "$1" = build ]; then
    printf '%s\n' "$@" >build.args
    test -d "$3"
    echo 'unstructured fixture stdout'
    echo 'fixture stderr' >&2
    for i in 1 2; do
        echo '{"v":1,"type":"stage","stage":"compile","text":"Compiling the game"}'
        echo '{"v":1,"type":"progress","stage":"compile","fraction":0.5}'
    done
    mkdir -p builds/default
    printf '{"v":1,"type":"done","folder":"%s/builds/default"}\n' "$PWD"
else
    printf '%s\n' "$@" >play.args
    printf '%s' "${RX_VK_FSR_DIR:-}" >play.fsr
fi
"#,
        )?;
        fs::set_permissions(&exe, fs::Permissions::from_mode(0o755))?;
        let game = root.join("my copy.nup");
        fs::write(&game, [])?;
        let prepared = Prepared {
            ctx: Context::load(root)?,
            game: game.clone(),
            exe,
            tag: "v3.2.0".into(),
            intent: BuildIntent::New,
        };
        let options = Options {
            dlss: false,
            fsr: false,
            xess: false,
            language: "en-US".into(),
            shaders: false,
        };
        execute_build(
            prepared,
            &options,
            &json!({"buildWorkers":2}),
            &Cancel::default(),
            &mut |_, _| {},
            &mut super::super::forecast::Monitor::new(None, &json!({}), false, false),
        )?;
        let transcript = fs::read_to_string(root.join("logs/last-build.log"))?;
        assert_eq!(transcript.matches("Compiling the game").count(), 2);
        assert_eq!(transcript.matches(r#""type":"progress""#).count(), 2);
        assert!(transcript.contains("stdout\tunstructured fixture stdout"));
        assert!(transcript.contains("stderr\tfixture stderr"));
        let args = fs::read_to_string(version.join("build.args"))?;
        let args: Vec<_> = args.lines().collect();
        assert!(args.windows(2).any(|w| w == ["--jobs", "2"]));
        assert!(
            args.windows(2)
                .any(|w| w == ["--game", game.to_str().unwrap()])
        );
        assert!(args.contains(&"--no-prepare-shaders"));
        assert!(!args.contains(&"--accept-licenses"));
        assert!(!args.contains(&"--send-build-statistics"));
        let catalog = versions::installed(root)?;
        assert_eq!(catalog.len(), 1);
        let state = home::read_json(&profile.join("profile.json"))?;
        assert_eq!(state["version"], "v3.2.0");
        assert_eq!(state["unknown"][0]["ok"], true);
        // Editing the instance stores the choice without starting a build.
        versions::set_modules(root, &version, &["dlss"])?;
        home::patch_settings(root, &json!({"dlssLicense":true}))?;
        assert_eq!(home::read_json(&profile.join("profile.json"))?, state);
        assert!(!version.join("play.args").exists());
        // A shortcut starts without the GUI capability cache. A reporting opt-out
        // must reach the profile-specific build that will actually run.
        let mod_build = version.join("builds/code-mod");
        fs::create_dir_all(&mod_build)?;
        fs::write(mod_build.join("longshot_crash.ini"), "send=1\n")?;
        fs::write(
            version.join("builds/default/longshot_crash.ini"),
            "send=1\n",
        )?;
        let child = versions::play(root, Some(&version.join("builds/default")), None, |_| {
            anyhow::bail!("A profile without mods should start directly.")
        })?
        .expect("the fixture requested no approvals");
        crate::game::wait_shortcut(child)?;
        assert_eq!(
            fs::read_to_string(mod_build.join("longshot_crash.ini"))?,
            "send=0\n"
        );
        assert_eq!(
            fs::read_to_string(version.join("builds/default/longshot_crash.ini"))?,
            "send=1\n"
        );
        let play = fs::read_to_string(version.join("play.args"))?;
        assert!(play.lines().any(|arg| arg == profile.to_str().unwrap()));
        assert!(play.lines().any(|arg| arg == "--no-self-update"));
        assert!(play.contains("--upscaler\ndlss\n"));
        assert!(!play.contains("--accept-licenses"));
        let receipt = home::read_json(&profile.join("profile.json"))?;
        assert_eq!(
            receipt["licenses"]["nvidia"],
            "9981c6350dc58c0ce21f024fd65e31e851c31689"
        );
        versions::set_modules(root, &version, &[])?;
        let pinned = versions::play(root, Some(&version), Some(&profile), |_| Ok(false))?
            .expect("Steam entry with no mod approvals");
        crate::game::wait_shortcut(pinned)?;
        assert!(fs::read_to_string(version.join("play.args"))?.contains("--upscaler\nnone\n"));
        assert!(fs::read_to_string(version.join("play.fsr"))?.is_empty());
        fs::write(
            version.join("amd_fidelityfx_upscaler_vk.so"),
            b"\x7fELF\x02fixture",
        )?;
        versions::set_modules(root, &version, &["fsr4"])?;
        let pinned = versions::play(root, Some(&version), Some(&profile), |_| Ok(false))?
            .expect("Steam entry with no mod approvals");
        crate::game::wait_shortcut(pinned)?;
        assert!(fs::read_to_string(version.join("play.args"))?.contains("--upscaler\nfsr4\n"));
        assert_eq!(
            fs::read_to_string(version.join("play.fsr"))?,
            version.to_string_lossy()
        );
        Ok(())
    }
}

#[cfg(all(test, unix))]
mod reuse_tests;

#[cfg(all(test, windows))]
mod windows_tests {
    use super::built_in;
    use anyhow::Result;
    use std::fs;

    /// Longshot issue 2661: a version folder in a data folder of its own is reserved by its verbatim spelling
    /// (`\\?\C:\...`) and the builder started from it, but the builder names its game folder from its own path, which
    /// Windows gives plain.
    #[test]
    fn a_game_folder_the_builder_names_plainly_is_in_a_verbatim_version_folder() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let plain = temp.path().join("versions").join("nightly-842-86b3b9e4");
        let game = plain.join("builds").join("default");
        fs::create_dir_all(&game)?;
        let verbatim = fs::canonicalize(&plain)?;
        assert!(
            verbatim.to_string_lossy().starts_with(r"\\?\"),
            "{}",
            verbatim.display()
        );
        // (accepted, and carried on in the version folder's own spelling, the one the registry's lookups use)
        let in_verbatim = verbatim.join("builds").join("default");
        assert_eq!(
            built_in(&game, &verbatim),
            Some(in_verbatim.clone()),
            "{} not in {}",
            game.display(),
            verbatim.display()
        );
        assert_eq!(built_in(&in_verbatim, &plain), Some(game.clone()));
        assert_eq!(built_in(&game, &plain), Some(game.clone()));
        // (still refused: a folder that isn't there, and one outside the version's builds)
        assert_eq!(
            built_in(&plain.join("builds").join("missing"), &verbatim),
            None
        );
        let other = temp.path().join("elsewhere").join("default");
        fs::create_dir_all(&other)?;
        assert_eq!(built_in(&other, &verbatim), None);
        Ok(())
    }
}
