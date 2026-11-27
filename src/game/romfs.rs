use super::{original_game, process, releases, versions};
use crate::{MainWindow, Preferences, i18n};
use anyhow::{Context as _, Result, bail, ensure};
use slint::ComponentHandle;
use std::{
    cell::RefCell,
    fs,
    path::{Path, PathBuf},
    rc::Rc,
    thread,
};

pub(super) fn bind(ui: &MainWindow, root: PathBuf) {
    let active = Rc::new(RefCell::new(None::<process::Cancel>));
    let request = active.clone();
    let weak = ui.as_weak();
    ui.global::<Preferences>().on_extract_romfs(move || {
        let Some(ui) = weak.upgrade() else {
            return;
        };
        if ui.get_navigation_locked() || ui.get_modal_open() {
            return;
        }
        let job = (|| -> Result<_> {
            let ctx = process::Context::load(&root)?;
            let game = original_game(&ctx.profile, &ctx.settings)?
                .context("Choose your original game in Settings first.")?;
            let source = process::validated_source(&game)?;
            Ok((ctx, source))
        })();
        let prefs = ui.global::<Preferences>();
        let (ctx, source) = match job {
            Ok(job) => job,
            Err(error) => {
                prefs.set_status(format!("{error:#}").into());
                return;
            }
        };
        let lang = ui.global::<crate::Locale>().get_language();
        let Some(out) = rfd::FileDialog::new()
            .set_title(i18n::lookup(&lang, "Choose a RomFS output folder"))
            .pick_folder()
        else {
            return;
        };

        prefs.set_status("".into());
        prefs.set_romfs_destination(out.join("romfs").to_string_lossy().as_ref().into());
        prefs.set_romfs_error("".into());
        prefs.set_romfs_progress(-1.0);
        prefs.set_romfs_cancelling(false);
        prefs.set_romfs_running(true);
        prefs.set_romfs_open(true);

        let cancel = process::Cancel::default();
        *request.borrow_mut() = Some(cancel.clone());
        let weak = ui.as_weak();
        thread::spawn(move || {
            let result = extract(&ctx, &source, &out, &cancel, &mut |fraction| {
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    ui.global::<Preferences>().set_romfs_progress(fraction);
                });
            });
            let _ = weak.upgrade_in_event_loop(move |ui| {
                let prefs = ui.global::<Preferences>();
                if let Err(error) = result {
                    if cancel.check().is_err() {
                        prefs.set_romfs_open(false);
                    } else {
                        prefs.set_romfs_error(format!("{error:#}").into());
                    }
                } else {
                    prefs.set_romfs_progress(1.0);
                }
                prefs.set_romfs_running(false);
            });
        });
    });
    ui.global::<Preferences>().on_cancel_romfs(move || {
        if let Some(cancel) = active.borrow().as_ref() {
            cancel.request();
        }
    });
}

fn extract(
    ctx: &process::Context,
    source: &Path,
    out: &Path,
    cancel: &process::Cancel,
    progress: &mut dyn FnMut(f32),
) -> Result<()> {
    let out = out.canonicalize()?;
    let destination = out.join("romfs");
    ensure!(
        fs::symlink_metadata(&destination).is_err_and(|e| e.kind() == std::io::ErrorKind::NotFound),
        "Choose an output folder without an existing romfs folder."
    );
    if source.is_dir() {
        let romfs = source.join("romfs").canonicalize()?;
        ensure!(
            !out.starts_with(&romfs),
            "Choose an output folder outside the original RomFS."
        );
    }
    let stage = tempfile::Builder::new()
        .prefix(".export-romfs-")
        .tempdir_in(&out)?;
    let mut attempt = |exe: &Path| -> Result<process::Answer> {
        let mut cmd = ctx.command(exe, false)?;
        cmd.arg("extract-romfs")
            .arg("--game")
            .arg(source)
            .arg("--out")
            .arg(stage.path())
            .args(["--output", "json"]);
        process::run_with_stages(
            cmd,
            "done",
            cancel,
            &mut |_, _| {},
            &mut |event| {
                if let Some(event) = event
                    && event["type"] == "progress"
                    && let Some(fraction) = event["fraction"].as_f64()
                {
                    progress(fraction.clamp(0.0, 1.0) as f32);
                }
            },
            &mut |_, _| {},
        )
    };
    let selected = versions::selected(&ctx.home, None)
        .ok()
        .map(|version| version.folder.join(process::executable()));
    let exe = match selected {
        Some(exe) if supports_extraction(ctx, &exe, cancel)? => exe,
        _ => {
            cancel.check()?;
            let target = releases::resolve("Nightly", "")?;
            let exe = releases::ensure_builder(ctx, &target, cancel, &mut |_, _| {})?;
            if !supports_extraction(ctx, &exe, cancel)? {
                bail!("RomFS extraction requires a newer Longshot builder.");
            }
            exe
        }
    };
    attempt(&exe)?.require("Extracting RomFS")?;
    cancel.check()?;
    let romfs = stage.path().join("romfs");
    ensure!(
        crate::players::is_dir(&romfs),
        "The builder did not create a RomFS folder."
    );
    crate::fsx::rename_new(&romfs, &destination)
        .context("Could not publish the extracted RomFS")?;
    Ok(())
}

// An older builder under the installed game's filename treats an unknown
// command as a game launch. Probe a command it already understands first.
fn supports_extraction(
    ctx: &process::Context,
    exe: &Path,
    cancel: &process::Cancel,
) -> Result<bool> {
    let mut cmd = ctx.command(exe, false)?;
    cmd.arg("--help");
    let mut supported = false;
    let answer = process::run_with_stages(
        cmd,
        "",
        cancel,
        &mut |_, _| {},
        &mut |_| {},
        &mut |stream, line| {
            supported |= stream == "stdout"
                && line
                    .trim_start()
                    .starts_with("longshot-builder extract-romfs ");
        },
    )?;
    Ok(answer.status.success() && supported)
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use std::os::unix::fs::PermissionsExt;

    #[test]
    fn publishes_only_the_finished_romfs_and_preserves_an_existing_export() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let version = root.join("versions/nightly-1");
        crate::game::fake_version(&version)?;
        let exe = version.join(process::executable());
        fs::write(
            &exe,
            r#"#!/bin/sh
set -eu
case "$1" in
    --help)
        echo "       longshot-builder extract-romfs --game <file> --out <folder>"
        ;;
    extract-romfs)
        test "$2" = --game && test "$4" = --out
        mkdir "$5/romfs"
        cat "$3" > "$5/romfs/example.bin"
        printf '%s\n' '{"v":1,"type":"progress","fraction":0.5,"overall":0.075}'
        printf '%s\n' '{"v":1,"type":"done"}'
        ;;
    *) exit 1 ;;
esac
"#,
        )?;
        fs::set_permissions(&exe, fs::Permissions::from_mode(0o755))?;
        crate::players::initialize(root)?;
        let ctx = process::Context::load(root)?;
        let source = root.join("synthetic.nup");
        fs::write(&source, b"synthetic fixture")?;
        let dest = root.join("export");
        fs::create_dir(&dest)?;
        let mut fractions = Vec::new();
        let cancel = process::Cancel::default();
        extract(&ctx, &source, &dest, &cancel, &mut |fraction| {
            fractions.push(fraction)
        })?;
        assert_eq!(fractions, [0.5]);
        let file = dest.join("romfs/example.bin");
        assert_eq!(fs::read(&file)?, b"synthetic fixture");
        assert_eq!(fs::read_dir(&dest)?.count(), 1);
        fs::write(&file, b"keep")?;
        assert!(extract(&ctx, &source, &dest, &cancel, &mut |_| {}).is_err());
        assert_eq!(fs::read(file)?, b"keep");
        Ok(())
    }
}
