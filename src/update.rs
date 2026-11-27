mod releases;
#[cfg(windows)]
mod shortcuts;
#[cfg(windows)]
pub use shortcuts::{game_shortcut, has_game_shortcut};
#[cfg(windows)]
mod windows;

use crate::{MainWindow, Updates};
use anyhow::{Context, Result, bail, ensure};
pub use releases::{Asset, channel_order, download, list_releases, signed_release, verified_bytes};
use slint::ComponentHandle;
use std::{
    env,
    ffi::OsString,
    fs::{self, File, OpenOptions},
    io::{self, Read, Seek, SeekFrom},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::{Arc, Mutex},
    thread,
    time::{Duration, Instant},
};
use tempfile::NamedTempFile;

pub fn own_tag() -> &'static str {
    option_env!("LONGSHOT_TAG").unwrap_or("dev")
}

pub fn current_file() -> Option<PathBuf> {
    if cfg!(windows) {
        return env::current_exe().ok();
    }
    let app = PathBuf::from(env::var_os("APPIMAGE")?);
    app.is_absolute().then(|| app.canonicalize().unwrap_or(app))
}

pub(crate) fn externally_managed() -> bool {
    env::args_os().any(|arg| arg == "--no-self-update")
        || env::var_os("LONGSHOT_LAUNCHER_NO_SELF_UPDATE")
            .is_some_and(|value| !value.is_empty() && value != "0")
        || crate::home::variable("FLATPAK_ID").is_some()
        || crate::home::variable("SNAP").is_some()
        || Path::new("/.flatpak-info").is_file()
}

fn installed_order(installed: &str, download: &str) -> Option<std::cmp::Ordering> {
    ["stable", "nightly"].into_iter().find_map(|channel| {
        let installed = channel_order(installed, channel, 1)?;
        let download = channel_order(download, channel, 1)?;
        Some(installed.cmp(&download))
    })
}

fn use_installed(installed: bool, order: Option<std::cmp::Ordering>, explicit: bool) -> bool {
    use std::cmp::Ordering;
    installed
        && if explicit {
            order == Some(Ordering::Greater)
        } else {
            order != Some(Ordering::Less)
        }
}

fn beside(path: &Path, suffix: &str) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(suffix);
    name.into()
}

#[derive(Clone)]
struct Offer {
    tag: String,
    file: Asset,
    portable_zip: bool,
}

#[derive(Default)]
struct CheckResult {
    status: String,
    offer: Option<Offer>,
    tag: String,
}

// Forgejo orders releases by publication date. Skip unrelated tags and unfinished releases.
fn launcher_notes(listed: &[releases::ListedRelease]) -> Vec<(String, String)> {
    listed
        .iter()
        .filter(|r| {
            !r.draft
                && (channel_order(&r.tag_name, "nightly", 1).is_some()
                    || !r.prerelease && channel_order(&r.tag_name, "stable", 1).is_some())
        })
        .map(|r| {
            (
                r.tag_name.clone(),
                crate::game::launcher_body(&r.body).to_owned(),
            )
        })
        .collect()
}

fn check_listed(
    own: &str,
    channel: &str,
    current: Option<&Path>,
    listed: Vec<releases::ListedRelease>,
) -> Result<CheckResult> {
    let own_track = if channel_order(own, "nightly", 1).is_some() {
        "nightly"
    } else {
        "stable"
    };
    let track = match channel {
        "stable" | "nightly" => channel,
        _ => own_track,
    };
    let Some((latest, release)) = listed
        .into_iter()
        .rev()
        .filter(|r| !r.draft && (track != "stable" || !r.prerelease))
        .filter_map(|r| Some((channel_order(&r.tag_name, track, 1)?, r)))
        .max_by_key(|(order, _)| *order)
    else {
        return Ok(CheckResult {
            status: format!("No {track} launcher releases have been published."),
            ..Default::default()
        });
    };
    let tag = release.tag_name.clone();
    let mut offer = None;
    let status = match (channel_order(own, own_track, 1), current) {
        (None, _) => {
            format!(
                "Latest {track} launcher: {tag}. To install it, open the packaged launcher and select {track} in Settings → Launcher."
            )
        }
        (Some(installed), _) if track == own_track && latest <= installed => {
            format!("No update found. You’re running {own} on the {track} channel.")
        }
        (Some(_), None) => {
            format!("Update found: {tag} ({track}). Open the packaged launcher to install it.")
        }
        (Some(_), Some(path)) => {
            offer = platform_offer(releases::open_release(release)?, path)?;
            if offer.is_some() {
                format!("Update found: {tag} ({track}).")
            } else {
                format!(
                    "Launcher {tag} predates this portable data folder. Open an installed copy of the launcher to switch to {track}."
                )
            }
        }
    };
    Ok(CheckResult { status, offer, tag })
}

fn platform_offer(mut release: releases::Release, current: &Path) -> Result<Option<Offer>> {
    let prefix = format!("LongshotLauncher-{}-", release.tag);
    let standard = if cfg!(windows) {
        "windows-x64.exe"
    } else {
        "linux-x64.AppImage"
    };
    let (file, portable_zip) =
        if let Some(file) = release.assets.remove(&format!("{prefix}{standard}")) {
            (file, false)
        } else {
            let old_portable = if cfg!(windows) {
                current.with_file_name("portable").is_file()
            } else {
                beside(current, ".home").is_dir()
            };
            if !old_portable && crate::home::portable_at(current) {
                return Ok(None);
            }
            let zipped = cfg!(windows) && old_portable;
            let legacy = if zipped {
                "windows-x64-portable.zip"
            } else if cfg!(windows) {
                "windows-x64-setup.exe"
            } else {
                "x86_64.AppImage"
            };
            let file = release
                .assets
                .remove(&format!("{prefix}{legacy}"))
                .with_context(|| format!("The {} release has no {legacy} package.", release.tag))?;
            (file, zipped)
        };
    ensure!(
        file.sha256.is_some(),
        "The available launcher is missing a signed checksum."
    );
    Ok(Some(Offer {
        tag: release.tag,
        file,
        portable_zip,
    }))
}

// The inode being replaced cannot guard two concurrent updates. The small lock file stays
// beside the launcher across swaps. The OS releases the lock if an updater dies.
fn update_lock(current: &Path) -> Result<crate::home::FileLease> {
    crate::home::FileLease::try_open(
        &beside(current, ".update-lock"),
        OpenOptions::new().create(true).truncate(false).write(true),
        crate::fsx::LockMode::Exclusive,
    )
    .context("Could not acquire the launcher update lock.")
}

fn executable(file: &File) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        file.set_permissions(fs::Permissions::from_mode(0o755))?;
    }
    file.sync_all()?;
    Ok(())
}

fn extract_program(zip: &mut File, output: &mut File) -> Result<()> {
    zip.seek(SeekFrom::Start(0))?;
    let mut archive = zip::ZipArchive::new(zip)?;
    let entry = archive
        .by_name("LongshotLauncher.exe")
        .context("The portable zip does not contain LongshotLauncher.exe.")?;
    let size = entry.size();
    ensure!(
        size > 0 && size <= 1 << 30,
        "Invalid portable executable size."
    );
    ensure!(
        io::copy(&mut entry.take(size + 1), output)? == size,
        "Incomplete portable executable."
    );
    Ok(())
}

fn launcher_command(path: &Path) -> Command {
    let mut cmd = Command::new(path);
    for key in ["APPIMAGE", "APPDIR", "TARGET_APPIMAGE"] {
        cmd.env_remove(key);
    }
    cmd
}

fn self_check(path: &Path, tag: &str, timeout: Duration) -> Result<()> {
    // A private, seekable file also avoids a full pipe blocking the child's exit.
    let mut stdout = tempfile::tempfile()?;
    let mut child = launcher_command(path)
        .arg("--self-check")
        .stdout(stdout.try_clone()?)
        .stderr(Stdio::null())
        .stdin(Stdio::null())
        .spawn()
        .context("Could not start the downloaded launcher.")?;
    let start = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if start.elapsed() >= timeout {
            let _ = child.kill();
            let _ = child.wait();
            bail!("The downloaded launcher timed out during its self-check.");
        }
        thread::sleep(Duration::from_millis(50));
    };
    stdout.seek(SeekFrom::Start(0))?;
    let mut message = String::new();
    stdout.take(65537).read_to_string(&mut message)?;
    let expected = format!("LongshotLauncher {tag}");
    ensure!(
        status.success() && message.len() <= 65536 && message.lines().any(|l| l.trim() == expected),
        "The new launcher did not report version {tag}."
    );
    Ok(())
}

#[cfg(windows)]
fn rename_while_held(from: &Path, to: &Path) -> io::Result<()> {
    // Windows Defender can still hold an executable briefly after its self-check exits.
    let mut delays = [100, 250, 500, 1000, 2000, 2000, 2000, 2000].into_iter();
    loop {
        match fs::rename(from, to) {
            Err(e) if matches!(e.raw_os_error(), Some(5 | 32 | 33)) => {
                let Some(ms) = delays.next() else {
                    return Err(e);
                };
                thread::sleep(Duration::from_millis(ms));
            }
            result => return result,
        }
    }
}

fn replace(current: &Path, replacement: &Path, backup: &Path) -> Result<()> {
    #[cfg(not(windows))]
    {
        let dir = current.parent().context("Launcher path has no parent.")?;
        let directory = File::open(dir)?;
        let keep = NamedTempFile::new_in(dir)?;
        fs::copy(current, keep.path())?;
        keep.as_file().sync_all()?;
        keep.persist(backup)?;
        directory.sync_all()?;
        fs::rename(replacement, current)?;
        directory.sync_all()?;
    }
    #[cfg(windows)]
    {
        rename_while_held(current, backup)?;
        if let Err(error) = rename_while_held(replacement, current) {
            rename_while_held(backup, current)
                .context("Could not put the previous launcher back in place.")?;
            return Err(error.into());
        }
    }
    Ok(())
}

fn recover(current: &Path) -> Result<()> {
    let backup = beside(current, ".old");
    let partials = [".new", ".new.part", ".new.zip.part"];
    if current.exists() && !partials.iter().any(|s| beside(current, s).exists()) {
        return Ok(());
    }
    let _lock = update_lock(current)?;
    if !current.exists() && backup.exists() {
        fs::rename(backup, current)?;
    }
    for suffix in partials {
        match fs::remove_file(beside(current, suffix)) {
            Ok(()) => {}
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        }
    }
    Ok(())
}

fn rollback(current: &Path) -> Result<()> {
    let _lock = update_lock(current)?;
    let old = beside(current, ".old");
    ensure!(
        old.is_file(),
        "No earlier launcher was kept beside this one."
    );
    replace(current, &old, &beside(current, ".rolledback"))
}

fn apply(offer: &Offer, progress: impl FnMut(u64)) -> Result<PathBuf> {
    let current =
        current_file().context("Start the installed launcher or its AppImage to update it.")?;
    apply_to(&current, offer, progress)?;
    Ok(current)
}

fn apply_to(current: &Path, offer: &Offer, progress: impl FnMut(u64)) -> Result<()> {
    let _lock = update_lock(current)?;
    let dir = current.parent().context("Launcher path has no parent.")?;
    let mut payload = NamedTempFile::new_in(dir)?;
    download(&offer.file, payload.as_file_mut(), progress)?;
    let staged = if offer.portable_zip {
        let mut program = NamedTempFile::new_in(dir)?;
        extract_program(payload.as_file_mut(), program.as_file_mut())?;
        program
    } else {
        payload
    };
    executable(staged.as_file())?;
    let new_path = beside(current, ".new");
    let file = staged.persist(&new_path)?;
    drop(file); // close before executing (ETXTBSY on Linux)
    let op = self_check(&new_path, &offer.tag, Duration::from_secs(60))
        .and_then(|()| replace(current, &new_path, &beside(current, ".old")));
    if op.is_err() {
        let _ = fs::remove_file(&new_path);
    }
    op
}

/// An installed copy remembers the install choice even if the original download is opened again.
fn first_launch(
    current: &Path,
    destination: &Path,
    args: &[OsString],
    installed: bool,
    order: Option<std::cmp::Ordering>,
    install: impl FnOnce() -> Result<()>,
) -> Result<bool> {
    let explicit = args.iter().any(|a| a == "--install");
    if !explicit
        && (own_tag() == "dev"
            || args.iter().any(|a| a == "--play")
            || crate::home::variable("LONGSHOT_LAUNCHER_HOME").is_some()
            || crate::home::portable_at(current))
    {
        return Ok(false);
    }
    if !use_installed(installed, order, explicit) {
        if !explicit && !installed {
            match crate::choose_install(destination)? {
                Some(true) => {}
                Some(false) => {
                    crate::home::enable_portable(current)?;
                    return Ok(false);
                }
                None => return Ok(true),
            }
        }
        install()?;
    }
    launcher_command(destination)
        .args(args.iter().filter(|arg| *arg != "--install"))
        .spawn()
        .context("Could not start the installed launcher.")?;
    Ok(true)
}

#[cfg(not(windows))]
mod linux;

pub fn startup(args: &[OsString]) -> Result<bool> {
    if args.len() == 1 && args[0] == "--self-check" {
        println!("LongshotLauncher {}", own_tag());
        return Ok(true);
    }
    if externally_managed() {
        ensure!(
            !args
                .iter()
                .any(|arg| arg == "--install" || arg == "--rollback"),
            "Your package manager installs and updates this launcher."
        );
        return Ok(false);
    }
    let current = current_file();
    if args.len() == 1 && args[0] == "--rollback" {
        rollback(&current.context("Rollback requires the launcher executable or its AppImage.")?)?;
        println!("The earlier launcher is back in place. Start it again.");
        return Ok(true);
    }
    if let Some(path) = current.as_deref() {
        if let Some(name) = path.file_name().and_then(|n| n.to_str())
            && let Some(original) = name
                .strip_suffix(".old")
                .or_else(|| name.strip_suffix(".new"))
            && !original.is_empty()
        {
            let original = path.with_file_name(original);
            if !original.exists() && beside(&original, ".old").is_file() {
                // A running .new cannot be deleted on Windows. Restore the name first;
                // the restarted instance cleans up after this process has exited.
                let _lock = update_lock(&original)?;
                if !original.exists() {
                    fs::rename(beside(&original, ".old"), &original)?;
                }
                drop(_lock);
                launcher_command(&original).args(args).spawn()?;
                return Ok(true);
            }
        }
        #[cfg(windows)]
        {
            return windows::startup(path, args);
        }
        #[cfg(not(windows))]
        {
            return linux::startup(path, args);
        }
    }
    Ok(false)
}

/// Only the process holding the instance lock may clear another launcher's staged files.
pub fn recover_after_lock() -> Result<()> {
    if !externally_managed()
        && let Some(current) = current_file()
    {
        recover(&current)?;
    }
    Ok(())
}

static RESTART: Mutex<Option<PathBuf>> = Mutex::new(None);

/// Called after the event loop exits and the launcher's home lock has been released.
pub fn restart_if_needed() -> Result<()> {
    if let Some(path) = RESTART.lock().unwrap().take() {
        launcher_command(&path)
            .spawn()
            .context("The launcher was updated. Start it again to use the new version.")?;
    }
    Ok(())
}

fn start_check(ui: &MainWindow, home: PathBuf, next: Arc<Mutex<Option<Offer>>>) {
    let state = ui.global::<Updates>();
    if state.get_busy() {
        return;
    }
    *next.lock().unwrap() = None;
    state.set_notes_status("Loading launcher release notes…".into());
    state.set_notes_error(false);
    state.set_offer("".into());
    state.set_busy(true);
    state.set_progress(0.);
    state.set_status("Checking for launcher updates…".into());
    let weak = ui.as_weak();
    thread::spawn(move || {
        let settings = crate::home::settings(&home);
        let channel = settings
            .as_ref()
            .ok()
            .and_then(|s| s["launcherChannel"].as_str())
            .unwrap_or_default()
            .to_owned();
        let result = list_releases("LongshotLauncher").map(|listed| {
            let notes = launcher_notes(&listed);
            let checked = settings.and_then(|_| {
                if externally_managed() {
                    Ok(CheckResult {
                        status: "Your package manager updates this launcher.".to_owned(),
                        tag: notes
                            .first()
                            .map(|(tag, _)| tag.clone())
                            .unwrap_or_default(),
                        offer: None,
                    })
                } else {
                    check_listed(own_tag(), &channel, current_file().as_deref(), listed)
                }
            });
            (notes, checked)
        });
        let _ = weak.upgrade_in_event_loop(move |ui| {
            let state = ui.global::<Updates>();
            state.set_busy(false);
            // The player may have selected a different channel during the network request.
            if crate::home::settings(&home).is_ok_and(|settings| {
                settings["launcherChannel"].as_str().unwrap_or_default() != channel
            }) {
                start_check(&ui, home, next);
                return;
            }
            let checked = match result {
                Ok((notes, checked)) => {
                    let entries = notes
                        .into_iter()
                        .map(|(tag, body)| crate::ReleaseNote {
                            tag: tag.into(),
                            body: body.into(),
                        })
                        .collect::<Vec<_>>();
                    state.set_notes_status(if entries.is_empty() {
                        "No launcher releases have been published.".into()
                    } else {
                        "".into()
                    });
                    state.set_releases(slint::ModelRc::from(entries.as_slice()));
                    checked
                }
                Err(error) => {
                    state.set_notes_status(
                        format!("Could not load launcher release notes. {error:#}").into(),
                    );
                    state.set_notes_error(true);
                    Err(error)
                }
            };
            match checked {
                Ok(checked) => {
                    let tag = checked
                        .offer
                        .as_ref()
                        .map(|offer| offer.tag.as_str())
                        .unwrap_or_default();
                    state.set_offer(tag.into());
                    state.set_status(checked.status.into());
                    *next.lock().unwrap() = checked.offer;
                    state.set_notes_tag(checked.tag.into());
                }
                Err(e) => {
                    state.set_status(format!("Could not check for updates. {e:#}").into());
                }
            }
        });
    });
}

pub fn bind(ui: &MainWindow, home: PathBuf) {
    let offer = Arc::new(Mutex::new(None));
    let (weak, check_home, check_offer) = (ui.as_weak(), home.clone(), offer.clone());
    ui.global::<Updates>().on_check(move || {
        if let Some(ui) = weak.upgrade() {
            start_check(&ui, check_home.clone(), check_offer.clone());
        }
    });
    let automatic =
        crate::home::settings(&home).is_ok_and(|s| s["checkUpdates"].as_bool().unwrap_or(true));
    let weak = ui.as_weak();
    ui.global::<Updates>().on_apply(move || {
        let Some(ui) = weak.upgrade() else { return };
        let state = ui.global::<Updates>();
        if state.get_busy() {
            return;
        }
        if state.get_blocked() || crate::busy(&ui) {
            state.set_status("Finish the current task and close the game before updating.".into());
            return;
        }
        let Some(offered) = offer.lock().unwrap().clone() else {
            return;
        };
        state.set_busy(true);
        state.set_progress(0.);
        state.set_applying(true);
        state.set_status("Downloading and checking the new launcher…".into());
        let weak = weak.clone();
        thread::spawn(move || {
            let mut last = 0;
            let result = apply(&offered, |n| {
                let percent = n.saturating_mul(100) / offered.file.size.max(1);
                if percent > last {
                    last = percent;
                    let _ = weak.upgrade_in_event_loop(move |ui| {
                        ui.global::<Updates>().set_progress(percent as f32 / 100.)
                    });
                }
            });
            let _ = weak.upgrade_in_event_loop(move |ui| {
                ui.global::<Updates>().set_busy(false);
                ui.global::<Updates>().set_applying(false);
                match result {
                    Ok(path) => {
                        *RESTART.lock().unwrap() = Some(path);
                        let _ = slint::quit_event_loop();
                    }
                    Err(e) => ui
                        .global::<Updates>()
                        .set_status(format!("Could not install the update. {e:#}").into()),
                }
            });
        });
    });
    if automatic && !externally_managed() && own_tag() != "dev" && current_file().is_some() {
        ui.global::<Updates>().invoke_check();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn newer_downloads_replace_an_installation_on_the_same_channel() {
        assert!(!use_installed(
            true,
            installed_order("v1.3.1", "v2.0.0"),
            false
        ));
        assert!(!use_installed(
            true,
            installed_order("nightly-11-abcdef12", "nightly-12-fedcba98"),
            false
        ));
        assert!(use_installed(
            true,
            installed_order("v2.0.0", "v1.3.1"),
            false
        ));
        assert!(use_installed(
            true,
            installed_order("v2.0.0", "v2.0.0"),
            false
        ));
        assert!(use_installed(
            true,
            installed_order("v2.0.0", "nightly-12-fedcba98"),
            false
        ));
        assert!(!use_installed(
            true,
            installed_order("v2.0.0", "nightly-12-fedcba98"),
            true
        ));
    }

    #[test]
    fn portable_updates_preserve_the_recognized_data_layout() -> Result<()> {
        let directory = tempfile::tempdir()?;
        let exe = directory.path().join("launcher");
        fs::write(&exe, b"launcher")?;
        let modern = if cfg!(windows) {
            "windows-x64.exe"
        } else {
            "linux-x64.AppImage"
        };
        let legacy = if cfg!(windows) {
            "windows-x64-setup.exe"
        } else {
            "x86_64.AppImage"
        };
        let release = |suffix: &str| releases::Release {
            tag: "v2.0.0".into(),
            assets: [(
                format!("LongshotLauncher-v2.0.0-{suffix}"),
                Asset {
                    url: "https://example.invalid/launcher".into(),
                    size: 1,
                    sha256: Some("ab".repeat(32)),
                },
            )]
            .into_iter()
            .collect(),
        };
        assert!(platform_offer(release(legacy), &exe)?.is_some());
        crate::home::enable_portable(&exe)?;
        assert!(platform_offer(release(legacy), &exe)?.is_none());
        assert!(!platform_offer(release(modern), &exe)?.unwrap().portable_zip);
        Ok(())
    }

    #[test]
    fn explicit_channel_switch_can_return_to_published_stable() {
        fn listed() -> Vec<releases::ListedRelease> {
            [
                ("nightly-900-0123abcd", false, true),
                ("v1.3.0", false, false),
                ("v1.2.0", false, false),
                ("v2.0.0", true, false),
                ("v3.0.0", false, true),
            ]
            .into_iter()
            .map(|(tag, draft, prerelease)| releases::ListedRelease {
                tag_name: tag.into(),
                draft,
                prerelease,
                body: format!("# ⬇️ Download Longshot Launcher\n\n[Download](https://example.invalid)\n\n---\n\nChanges for {tag}"),
                assets: vec![],
            })
            .collect()
        }
        let check = |own, channel, path| check_listed(own, channel, path, listed()).unwrap();
        let dev = check("dev", "nightly", Some(Path::new("LongshotLauncher")));
        assert_eq!(dev.tag, "nightly-900-0123abcd");
        assert!(dev.status.contains(&dev.tag));
        assert!(dev.offer.is_none());
        let current = check("v1.3.0", "stable", None);
        assert!(current.status.contains("No update found"));
        assert_eq!(current.tag, "v1.3.0");
        assert_eq!(
            launcher_notes(&listed()),
            vec![
                (
                    "nightly-900-0123abcd".into(),
                    "Changes for nightly-900-0123abcd".into()
                ),
                ("v1.3.0".into(), "Changes for v1.3.0".into()),
                ("v1.2.0".into(), "Changes for v1.2.0".into()),
            ]
        );
        let unpackaged = check("nightly-899-1122abcd", "nightly", None);
        assert!(unpackaged.status.contains("Update found"));
        assert!(unpackaged.offer.is_none());
        let downgrade = check("nightly-900-0123abcd", "stable", None);
        assert!(downgrade.status.contains("Update found"));
        assert_eq!(downgrade.tag, "v1.3.0");
        let empty = check_listed("dev", "nightly", None, vec![]).unwrap();
        assert!(empty.status.contains("No nightly launcher releases"));
    }

    /// Exercises the production signature, download, self-check, swap and rollback
    /// against the published .NET launcher. Only the temporary executable is replaced.
    #[test]
    #[ignore = "downloads signed v1.3.1 packages from Forgejo"]
    fn downgrade_nightly_to_published_stable() -> Result<()> {
        let release = || signed_release("LongshotLauncher", "v1.3.1");
        for portable in [false, true]
            .into_iter()
            .take(if cfg!(windows) { 2 } else { 1 })
        {
            let scratch = tempfile::tempdir()?;
            let current = scratch.path().join(if cfg!(windows) {
                "LongshotLauncher.exe"
            } else {
                "Renamed launcher.AppImage"
            });
            fs::write(&current, b"previous executable")?;
            fs::write(
                beside(&current, ".old"),
                b"backup from the previous upgrade",
            )?;
            let marker = current.with_file_name("portable");
            if portable {
                fs::write(&marker, b"portable")?;
            }
            let offered = platform_offer(release()?, &current)?
                .context("Expected a compatible stable package")?;
            assert_eq!(offered.portable_zip, portable);
            apply_to(&current, &offered, |_| {})?;
            self_check(&current, &offered.tag, Duration::from_secs(60))?;
            assert_eq!(fs::read(beside(&current, ".old"))?, b"previous executable");
            assert_eq!(marker.is_file(), portable);
            rollback(&current)?;
            assert_eq!(fs::read(current)?, b"previous executable");
        }
        Ok(())
    }

    #[test]
    #[cfg(unix)]
    fn staged_launcher_must_identify_the_expected_version() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let staged = temp.path().join("my launcher.new");
        fs::write(
            &staged,
            b"#!/bin/sh\n[ \"$1\" = --self-check ] && echo 'LongshotLauncher v2.0.0'\n",
        )?;
        executable(&File::open(&staged)?)?;
        self_check(&staged, "v2.0.0", Duration::from_secs(3))?;
        assert!(self_check(&staged, "v2.0.1", Duration::from_secs(3)).is_err());
        Ok(())
    }

    #[test]
    fn failed_replacement_keeps_the_original() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let current = temp.path().join("launcher");
        fs::write(&current, b"previous")?;
        assert!(
            replace(
                &current,
                &beside(&current, ".missing"),
                &beside(&current, ".old")
            )
            .is_err()
        );
        assert_eq!(fs::read(&current)?, b"previous");
        Ok(())
    }

    #[test]
    fn updates_only_hold_the_lock_while_the_swap_is_active() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let current = temp.path().join("launcher");
        let active = update_lock(&current)?;
        let _duplicate = active.0.try_clone()?;
        assert!(update_lock(&current).is_err());
        drop(active);
        let _next = update_lock(&current)?;
        Ok(())
    }

    #[test]
    fn swap_rollback_and_interrupted_stable_update() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let current = temp.path().join("My Launcher");
        let next = beside(&current, ".new");
        fs::write(&current, b"stable")?;
        fs::write(&next, b"rust")?;
        replace(&current, &next, &beside(&current, ".old"))?;
        assert_eq!(fs::read(&current)?, b"rust");
        rollback(&current)?;
        assert_eq!(fs::read(&current)?, b"stable");
        assert_eq!(fs::read(beside(&current, ".rolledback"))?, b"rust");
        fs::rename(&current, beside(&current, ".old"))?;
        fs::write(&next, b"incomplete")?;
        recover(&current)?;
        assert_eq!(fs::read(&current)?, b"stable");
        assert!(!next.exists());
        Ok(())
    }
}
