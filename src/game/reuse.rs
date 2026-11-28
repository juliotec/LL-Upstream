//! A second instance may use the files an identical builder already produced.
//! Stage independent copies and ask the destination builder to verify both their
//! contents and its fast-play check before making them visible to the launcher.
use super::{
    jobs::Prepared,
    process::{Cancel, run},
    versions::{self, valid_name},
    wizard::{BuildIntent, Options},
};
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
};

const RECORD: &str = "longshot_build.json";
const SHADER_KEY: &str = "cache/longshot_prepared_key.txt";

fn info(prepared: &Prepared, exe: &Path, cancel: &Cancel) -> Result<Value> {
    let mut command = prepared.ctx.command(exe, false)?;
    command
        .arg("info")
        .arg("--profile")
        .arg(&prepared.ctx.profile)
        .args(["--output", "json"]);
    run(command, "info", cancel, &mut |_, _| {})?.require("Checking the existing build")
}

fn same_upscalers(value: &Value, wanted: &[&str]) -> bool {
    value.as_array().is_some_and(|list| {
        list.len() == wanted.len() && wanted.iter().all(|key| list.iter().any(|item| item == key))
    })
}

pub(super) fn existing(
    p: &Prepared,
    options: &Options,
    cancel: &Cancel,
    progress: &mut dyn FnMut(&str, Option<f32>),
) -> Result<Option<PathBuf>> {
    // The builder owns a player's choice to opt out of shader preparation.
    if !options.shaders || matches!(p.intent, BuildIntent::Rebuild(_)) || !p.game.is_file() {
        return Ok(None);
    }
    let versions: Vec<_> = versions::listed(&p.ctx.home)?
        .into_iter()
        .filter(|v| v.tag == p.tag)
        .collect();
    if versions.is_empty() {
        return Ok(None);
    }

    // The build command records newly accepted runtime licences in the profile.
    // Let it handle changes to the game, upscalers, or accepted vendors.
    let upscalers = options.upscalers();
    let profile = home::read_json(&p.ctx.profile.join("profile.json"))?;
    if profile["game"].as_str().map(Path::new) != Some(p.game.as_path())
        || upscalers.iter().any(|name| {
            let Some(vendor) = super::licenses::vendor(name) else {
                return false;
            };
            profile["licenses"][vendor]
                .as_str()
                .is_none_or(str::is_empty)
        })
    {
        return Ok(None);
    }
    let dest = p
        .exe
        .parent()
        .context("The builder has no installation folder")?;
    let target_info = info(p, &p.exe, cancel)?;
    if !same_upscalers(&target_info["upscalers"], &upscalers) {
        return Ok(None);
    }
    let folder = PathBuf::from(
        target_info["build"]["folder"]
            .as_str()
            .context("No profile build folder")?,
    );
    let name = folder
        .file_name()
        .and_then(|n| n.to_str())
        .filter(|n| valid_name(n))
        .context("Invalid profile build folder")?;
    ensure!(
        folder.parent() == Some(dest.join("builds").as_path()) && !folder.try_exists()?,
        "New build folder is occupied"
    );
    for version in versions {
        cancel.check()?;
        let source = version.folder.join("builds").join(name);
        if !players::is_dir(&version.folder.join("builds"))
            || !players::is_dir(&source)
            || !players::is_file(&source.join(RECORD))
        {
            continue;
        }
        match copy_candidate(p, &source, &folder, &target_info, options, cancel, progress) {
            Ok(()) => return Ok(Some(folder)),
            Err(error) => {
                cancel.check()?;
                eprintln!("Existing build could not be reused: {error:#}");
            }
        }
    }
    Ok(None)
}

// The builder records its own shipped files in the manifest. A tree entry represents
// a dump; its contents cannot be verified individually and go through the normal build.
fn copy_file(source: &Path, target: &Path, relative: &str) -> Result<()> {
    let mut parts = relative.split('/').peekable();
    let mut from = source.to_owned();
    while let Some(part) = parts.next() {
        ensure!(
            valid_name(part) && !part.ends_with('.'),
            "Invalid build file name"
        );
        from.push(part);
        if parts.peek().is_some() {
            ensure!(players::is_dir(&from), "Linked build directory");
        }
    }
    ensure!(players::is_file(&from), "Missing or linked build file");
    let to = target.join(relative);
    fs::create_dir_all(to.parent().context("Missing file directory")?)?;
    fs::copy(from, to)?;
    Ok(())
}

fn copy_candidate(
    p: &Prepared,
    source: &Path,
    target: &Path,
    target_info: &Value,
    options: &Options,
    cancel: &Cancel,
    progress: &mut dyn FnMut(&str, Option<f32>),
) -> Result<()> {
    let lock = source.join(".longshot-builder.lock");
    ensure!(
        players::is_file(&lock),
        "The existing build has no regular builder lock"
    );
    let lock = home::FileLease(fs::File::open(lock)?);
    lock.0.try_lock_shared()?;
    let manifest = home::read_json(&source.join(RECORD))?;
    ensure!(
        manifest["format"] == 1
            && manifest["stamp"]["builder"].is_string()
            && manifest["stamp"]["builder"] == target_info["version"]["stamp"]
            && same_upscalers(&manifest["upscalers"], &options.upscalers())
            && manifest["game"]["nup"].as_str().map(Path::new) == Some(p.game.as_path()),
        "Different builder or game"
    );
    let files = manifest["files"]
        .as_object()
        .filter(|f| !f.is_empty())
        .context("Missing installed file manifest")?;
    let dest = p.exe.parent().unwrap();
    let temp = tempfile::Builder::new()
        .prefix(".instance-")
        .tempdir_in(dest)?;
    let staged = temp.path().join("builds").join(target.file_name().unwrap());
    fs::create_dir_all(&staged)?;
    progress("Copying the existing game files…", Some(0.));
    for (index, (name, entry)) in files.iter().enumerate() {
        cancel.check()?;
        ensure!(
            entry["sha256"]
                .as_str()
                .is_some_and(|s| s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit())),
            "Unverifiable file in the existing build"
        );
        let first = name
            .split('/')
            .next()
            .unwrap_or_default()
            .to_ascii_lowercase();
        ensure!(
            !matches!(
                first.as_str(),
                "user"
                    | "saves"
                    | "mods"
                    | "mods.json"
                    | "modcache"
                    | "cache"
                    | "config"
                    | "diagnostics"
                    | "logs"
                    | "bug-reports"
                    | "perf_pending"
                    | "perf_sent"
                    | "language.txt"
                    | "longshot_options.ini"
                    | "mouse_sens.txt"
                    | "longshot_env.txt"
                    | "longshot_perf.ini"
                    | "longshot_crash.ini"
                    | "saves-moved.txt"
            ) && !name.ends_with(".hosnew"),
            "User data is not an installed game file"
        );
        copy_file(source, &staged, name)?;
        progress(
            "Copying the existing game files…",
            Some((index + 1) as f32 / files.len() as f32),
        );
    }
    home::write_json(&staged.join(RECORD), &manifest)?;
    fs::File::create_new(staged.join(".longshot-builder.lock"))?;
    let settings = if manifest["features"]
        .as_array()
        .is_some_and(|features| features.iter().any(|feature| feature == "organized_build"))
    {
        let dir = staged.join("config");
        fs::create_dir_all(&dir)?;
        dir
    } else {
        staged.clone()
    };
    fs::write(
        settings.join("language.txt"),
        format!("{}\n", options.language),
    )?;
    copy_file(source, &staged, SHADER_KEY)?;
    let mut verify = p.ctx.command(&p.exe, false)?;
    verify
        .arg("verify")
        .arg("--out")
        .arg(&staged)
        .args(["--output", "json"]);
    progress("Verifying the copied game files…", None);
    let verified = run(verify, "verify", cancel, &mut |_, _| {})?
        .require("Verifying the copied game files")?;
    ensure!(
        verified["ok"] == true && verified["checked"] == json!(files.len()),
        "The copied build failed verification"
    );
    // Play's check resolves its build relative to the executable. Link our verified
    // builder into the staging area so its stamp, mod, and shader checks run there.
    let exe = temp.path().join(p.exe.file_name().unwrap());
    if fs::hard_link(&p.exe, &exe).is_err() {
        fs::copy(&p.exe, &exe)?;
    }
    let built = info(p, &exe, cancel)?;
    ensure!(
        built["build"]["present"] == true
            && built["build"]["up_to_date"] == true
            && built["build"]["folder"].as_str().map(Path::new) == Some(staged.as_path()),
        "The new instance still needs a build"
    );
    cancel.check()?;
    let builds = dest.join("builds");
    ensure!(!versions::linked(&builds), "Linked version builds folder");
    fs::create_dir_all(&builds)?;
    ensure!(!target.try_exists()?, "New build folder is occupied");
    fs::rename(&staged, target)?;
    Ok(())
}
