use super::{
    process::{Cancel, CancelWriter, Context, executable, run},
    versions::{installed, valid_name},
};
use crate::{home, update};
use anyhow::{Context as _, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
};

pub(super) const PLATFORM: &str = if cfg!(windows) {
    "windows-x64"
} else {
    "linux-x64"
};
const RELEASE_RECORD: &str = "longshot_release.json";
const PULLS: &str = "https://beanstalk.magicbean.dev/api/v1/public/pulls";

#[derive(Clone, Debug)]
pub(super) struct Source {
    pub sha: String,
    pub tag: String,
    pub warning: String,
    pub unapproved: Option<u64>,
}
#[derive(Clone, Debug)]
pub(super) enum Target {
    Release(String),
    Source(Source),
}
impl Target {
    pub fn tag(&self) -> &str {
        match self {
            Self::Release(t) => t,
            Self::Source(s) => &s.tag,
        }
    }
}

pub(super) fn latest(track: &str) -> Result<String> {
    ensure!(
        track != "stable" || super::STABLE_BUILDS_AVAILABLE,
        super::STABLE_UNAVAILABLE
    );
    let list = update::list_releases("Longshot")?;
    list.into_iter()
        .filter_map(|r| {
            let key = update::channel_order(&r.tag_name, track, 3)?;
            let manifest = format!("longshot-builder-{}-{PLATFORM}.json", r.tag_name);
            (!r.draft
                && (track == "nightly" || !r.prerelease)
                && r.assets.iter().any(|a| a.name == manifest))
            .then_some((key, r.tag_name))
        })
        .max()
        .map(|(_, tag)| tag)
        .with_context(|| format!("No {track} release has a Longshot builder for this system."))
}

fn json_url(url: &str) -> Result<Value> {
    let bytes = ureq::get(url)
        .header("Accept", "application/json")
        .call()?
        .body_mut()
        .with_config()
        .limit(4 << 20)
        .read_to_vec()?;
    Ok(serde_json::from_slice(&bytes)?)
}

pub(super) fn pulls() -> Result<Value> {
    json_url(PULLS)
}

fn sha(value: &Value) -> Result<String> {
    let s = value.as_str().unwrap_or_default();
    ensure!(
        s.len() == 40 && s.bytes().all(|c| c.is_ascii_hexdigit()),
        "The server did not provide a full commit ID."
    );
    Ok(s.to_ascii_lowercase())
}

pub(super) fn pr_number(raw: &str) -> Result<u64> {
    let raw = raw.trim().split(" · ").next().unwrap_or_default();
    let prefix = "git.magicbean.dev/MagicBeanDev/Longshot/pulls/";
    let number = if let Some(s) = raw.trim_start_matches("https://").strip_prefix(prefix) {
        s.split(['/', '#', '?']).next().unwrap_or_default()
    } else {
        raw.trim_start_matches('#')
    };
    ensure!(
        number.len() <= 9 && number.bytes().all(|n| n.is_ascii_digit()),
        "Enter a Longshot pull request number or its link."
    );
    let number: u64 = number.parse().context("Enter a pull request number.")?;
    ensure!(number > 0, "Enter a positive pull request number.");
    Ok(number)
}

pub(super) fn pull(rows: &Value, number: u64) -> Result<Source> {
    let row = rows["pulls"]
        .as_array()
        .context("Beanstalk did not return its list of pull requests.")?
        .iter()
        .find(|r| r["number"] == number)
        .context("This pull request is no longer open.")?;
    pull_row(row, number)
}

pub(super) fn pull_row(row: &Value, number: u64) -> Result<Source> {
    ensure!(
        (1..=999_999_999).contains(&number),
        "Invalid pull request number."
    );
    let head = sha(&row["head"])?;
    let labels = row["labels"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .collect::<Vec<_>>();
    let linux = labels.iter().any(|l| {
        l.eq_ignore_ascii_case("platform/linux") || l.eq_ignore_ascii_case("platform/steam-deck")
    });
    let windows = labels
        .iter()
        .any(|l| l.eq_ignore_ascii_case("platform/windows"));
    ensure!(
        if cfg!(windows) {
            windows || !linux
        } else {
            linux || !windows
        },
        "This pull request is for another operating system."
    );
    let approved = row["approved"] == true || row["author_bigbean"] == true;
    let title = row["title"].as_str().unwrap_or_default();
    let who = row["author"].as_str().unwrap_or("unknown");
    let trust = if approved {
        "A BigBean wrote or approved this commit."
    } else {
        "No BigBean has approved this commit. Type its PR number to confirm."
    };
    Ok(Source {
        tag: format!("pr-{number}-{}", &head[..8]),
        warning: format!(
            "#{number} {title}\n{who} · {}\n{trust}\nThis runs code from this pull request on your PC. It has access to your files. Back up your saves before testing.",
            &head[..8]
        ),
        sha: head,
        unapproved: (!approved).then_some(number),
    })
}

pub(super) fn resolve(track: &str, number: &str) -> Result<Target> {
    match track {
        "Stable" | "Nightly" => Ok(Target::Release(latest(&track.to_ascii_lowercase())?)),
        "Pull request" => Ok(Target::Source(pull(&json_url(PULLS)?, pr_number(number)?)?)),
        "Main" => {
            let main = json_url(
                "https://git.magicbean.dev/api/v1/repos/MagicBeanDev/Longshot/branches/main",
            )?;
            let commit = sha(&main["commit"]["id"])?;
            Ok(Target::Source(Source {
                tag: format!("main-{}", &commit[..8]),
                warning: format!(
                    "Build commit {} from Longshot’s main branch on your PC. This installs a compiler and builds the tools from source.",
                    &commit[..8]
                ),
                sha: commit,
                unapproved: None,
            }))
        }
        _ => bail!("Select a version track."),
    }
}

#[derive(Debug, Deserialize)]
struct Manifest {
    v: u32,
    version: String,
    platform: String,
    channel: String,
    full: BuilderFile,
    #[serde(default)]
    stamps: std::collections::BTreeMap<String, String>,
}
#[derive(Debug, Serialize, Deserialize)]
struct BuilderFile {
    name: String,
    size: u64,
    sha256: String,
}

impl Manifest {
    fn parse(bytes: &[u8], tag: &str) -> Result<Self> {
        let manifest: Self = serde_json::from_slice(bytes).context("Invalid builder manifest.")?;
        let channel =
            if super::channel::Channel::from_prefix(tag) == super::channel::Channel::Nightly {
                "nightly"
            } else {
                "stable"
            };
        ensure!(
            manifest.v == 1
                && manifest.version == tag
                && manifest.channel == channel
                && manifest.platform == PLATFORM,
            "The signed builder manifest is for another version, channel, or system."
        );
        let file = &manifest.full;
        ensure!(valid_name(&file.name), "The builder file name is invalid.");
        ensure!(
            (1..=16_u64 << 30).contains(&file.size),
            "Invalid builder size."
        );
        ensure!(
            file.sha256.len() == 64 && file.sha256.bytes().all(|b| b.is_ascii_hexdigit()),
            "Invalid builder checksum."
        );
        Ok(manifest)
    }
}

fn bootstrap(
    tag: &str,
    folder: &Path,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<()> {
    notify("Checking the builder’s signed release…", None);
    let r = update::signed_release("Longshot", tag)?;
    let name = format!("longshot-builder-{tag}-{PLATFORM}.json");
    let manifest = r
        .assets
        .get(&name)
        .context("This release does not provide a compatible builder.")?;
    let body = update::verified_bytes(manifest, 1 << 20)?;
    let manifest = Manifest::parse(&body, tag)?;
    let BuilderFile { name, size, sha256 } = &manifest.full;
    let size = *size;
    let listed = r
        .assets
        .get(name)
        .context("The signed builder is missing from this release.")?;
    ensure!(
        listed.size == size,
        "The builder's size differs from its signed manifest."
    );
    let file = update::Asset {
        url: listed.url.clone(),
        size,
        sha256: Some(sha256.to_ascii_lowercase()),
    };
    let mut temp = tempfile::NamedTempFile::new_in(folder)?;
    let mut writer = CancelWriter {
        file: temp.as_file_mut(),
        cancel,
    };
    update::download(&file, &mut writer, |n| {
        notify("Downloading the builder…", Some(n as f32 / size as f32))
    })?;
    cancel.check()?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        temp.as_file()
            .set_permissions(fs::Permissions::from_mode(0o755))?;
    }
    #[cfg(windows)]
    super::windowed::mark_private_copy(temp.as_file_mut())?;
    temp.as_file().sync_all()?;
    let record = json!({"v":1,"version":tag,"platform":PLATFORM,"channel":manifest.channel,"full":manifest.full,"stamps":manifest.stamps,"program":executable()});
    home::write_json(&folder.join(RELEASE_RECORD), &record)?;
    temp.persist(folder.join(executable()))?;
    Ok(())
}

/// Re-verify a prefetched executable using the release record kept with it. Otherwise
/// let the signed fetch path restore it in the newly chosen library.
fn copy_prefetch(source: &Path, dest: &Path, tag: &str, cancel: &Cancel) -> Result<bool> {
    use sha2::{Digest, Sha256};
    use std::io::Seek;
    let record = home::read_json(&source.join(RELEASE_RECORD))?;
    let Some(expected) = record["full"]["sha256"].as_str() else {
        return Ok(false);
    };
    let size = record["full"]["size"].as_u64().unwrap_or_default();
    let from = source.join(executable());
    if record["version"] != tag
        || record["platform"] != PLATFORM
        || record["program"] != executable()
        || !(1..=16u64 << 30).contains(&size)
        || from.metadata()?.len() != size
    {
        return Ok(false);
    }
    cancel.check()?;
    let mut copy = tempfile::NamedTempFile::new_in(dest)?;
    fs::copy(&from, copy.path())?;
    let matches = |file: &mut std::fs::File| -> Result<bool> {
        file.rewind()?;
        let mut hash = Sha256::new();
        std::io::copy(file, &mut hash)?;
        Ok(format!("{:x}", hash.finalize()) == expected)
    };
    if !matches(copy.as_file_mut())? {
        #[cfg(not(windows))]
        return Ok(false);
        #[cfg(windows)]
        {
            super::windowed::restore_console_copy(copy.as_file_mut())?;
            if !matches(copy.as_file_mut())? {
                return Ok(false);
            }
        }
    }
    #[cfg(windows)]
    super::windowed::mark_private_copy(copy.as_file_mut())?;
    cancel.check()?;
    copy.as_file().sync_all()?;
    home::write_json(&dest.join(RELEASE_RECORD), &record)?;
    copy.persist(dest.join(executable()))?;
    Ok(true)
}

fn download_folder(ctx: &Context, tag: &str) -> Result<PathBuf> {
    ensure!(valid_name(tag), "Invalid release tag.");
    let folder = ctx.cache.join("downloads").join(tag);
    ensure!(
        folder.ancestors().all(|p| !super::versions::linked(p)),
        "The builder download folder is linked."
    );
    Ok(folder)
}

/// A release downloaded for a file check or future build is disposable. An
/// installed instance takes its own verified executable when its build starts.
fn release(
    ctx: &Context,
    tag: &str,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<PathBuf> {
    if let Some(v) = installed(&ctx.home)?
        .into_iter()
        .find(|v| v.tag == tag && v.folder.join(RELEASE_RECORD).is_file())
    {
        return Ok(v.folder.join(executable()));
    }
    release_into(ctx, tag, download_folder(ctx, tag)?, cancel, notify)
}

fn release_into(
    ctx: &Context,
    tag: &str,
    dest: PathBuf,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<PathBuf> {
    release_into_with(ctx, tag, dest, cancel, notify, bootstrap)
}

fn release_into_with(
    ctx: &Context,
    tag: &str,
    dest: PathBuf,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
    signed_download: impl FnOnce(&str, &Path, &Cancel, &mut dyn FnMut(&str, Option<f32>)) -> Result<()>,
) -> Result<PathBuf> {
    ensure!(valid_name(tag), "Invalid version.");
    ensure!(
        !super::versions::linked(&dest.join(executable())),
        "The builder executable is a link."
    );
    let exe = dest.join(executable());
    if exe.is_file() {
        return Ok(exe);
    }
    fs::create_dir_all(&dest)?;
    // The background prefetch, wizard and a second launcher may want this release together.
    let lock = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .write(true)
        .open(dest.join(super::library::FETCH_LOCK))?;
    let _lease = loop {
        cancel.check()?;
        match lock.try_lock() {
            Ok(()) => break home::FileLease(lock),
            Err(std::fs::TryLockError::WouldBlock) => {
                notify("Waiting for the builder download…", None);
                std::thread::sleep(std::time::Duration::from_millis(100));
            }
            Err(std::fs::TryLockError::Error(e)) => return Err(e.into()),
        }
    };
    if exe.is_file() {
        return Ok(exe);
    }
    let downloaded = download_folder(ctx, tag)?;
    if downloaded != dest
        && crate::players::is_file(&downloaded.join(executable()))
        && crate::players::is_file(&downloaded.join(RELEASE_RECORD))
        && copy_prefetch(&downloaded, &dest, tag, cancel)?
    {
        return Ok(exe);
    }
    let mut bases = installed(&ctx.home)?
        .into_iter()
        .filter(|v| v.folder != dest && v.folder.join(RELEASE_RECORD).is_file())
        .collect::<Vec<_>>();
    // A previous library may already contain this verified release's tools. The
    // next game build belongs in the chosen library even in an offline session.
    if let Some(cached) = bases.iter().find(|v| v.tag == tag)
        && copy_prefetch(&cached.folder, &dest, tag, cancel)?
    {
        return Ok(exe);
    }
    // A same-tag copy that failed validation cannot perform the fallback download.
    bases.retain(|base| base.tag != tag);
    cancel.check()?;
    let mut fetch_error = None;
    if let Some(base) = bases.first() {
        let fetch = (|| {
            // A failed or cancelled fetch must never leave an executable that
            // the next launch could mistake for a completed download.
            let staged = tempfile::tempdir_in(&dest)?;
            let mut fetch = ctx.command(&base.folder.join(executable()), false)?;
            fetch.arg("fetch").arg(tag).arg("--out").arg(staged.path());
            for b in &bases {
                fetch.arg("--from").arg(&b.folder);
            }
            fetch.args(["--output", "json"]);
            run(fetch, "fetched", cancel, notify)?.require("Fetching Longshot")?;
            ensure!(
                copy_prefetch(staged.path(), &dest, tag, cancel)?,
                "The builder did not return a valid executable for {tag}."
            );
            Ok(())
        })();
        match fetch {
            Ok(()) => return Ok(exe),
            Err(error) => fetch_error = Some(error),
        }
    }
    cancel.check()?;
    signed_download(tag, &dest, cancel, notify).with_context(|| match fetch_error {
        Some(error) => format!("Builder fetch failed ({error:#}) and the signed download failed"),
        None => format!("Could not download the signed builder for {tag}"),
    })?;
    Ok(exe)
}

pub(super) fn ensure_builder(
    ctx: &Context,
    target: &Target,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<PathBuf> {
    builder_for(ctx, target, false, cancel, notify)
}

pub(super) fn ensure_build_destination(
    ctx: &Context,
    target: &Target,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<PathBuf> {
    builder_for(ctx, target, true, cancel, notify)
}

fn builder_for(
    ctx: &Context,
    target: &Target,
    game_build: bool,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<PathBuf> {
    let _lease = super::prune::fetch_lease(&ctx.home, cancel)?;
    let current = crate::home::settings(&ctx.home)?;
    ensure!(
        crate::home::work(&ctx.home, &current).join("longshot-builder") == ctx.cache
            && super::install_root(&ctx.home, &current)
                == super::install_root(&ctx.home, &ctx.settings),
        "The selected folders changed. Retry with the new location."
    );
    if !game_build {
        ensure!(
            matches!(target, Target::Release(_)),
            "Source builds require confirmation."
        );
        return release(ctx, target.tag(), cancel, notify);
    }
    let folder = super::library::new_build(&ctx.home, &ctx.settings, target.tag())?;
    let result = install_builder(ctx, target, folder, cancel, notify);
    if let Ok(exe) = &result
        && let Some(folder) = exe.parent()
    {
        super::prune::remember(folder);
    }
    result
}

fn install_builder(
    ctx: &Context,
    target: &Target,
    folder: PathBuf,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<PathBuf> {
    match target {
        Target::Release(tag) => release_into(ctx, tag, folder, cancel, notify),
        Target::Source(s) => {
            let exe = folder.join(executable());
            ensure!(
                !super::versions::linked(&exe),
                "The source version folder is a symbolic link."
            );
            if exe.is_file() {
                return Ok(exe);
            }
            if let Some(number) = super::channel::tag_pr(&s.tag) {
                let current = pull(&json_url(PULLS)?, number)?;
                ensure!(
                    current.sha == s.sha && current.unapproved == s.unapproved,
                    "This pull request or its approval has changed. Pick it again to review the new commit."
                );
            }
            let nightly = latest("nightly")?;
            let builder = release(ctx, &nightly, cancel, notify)?;
            let mut cmd = ctx.command(&builder, false)?;
            cmd.args(["from-source", "--commit"])
                .arg(&s.sha)
                .arg("--out")
                .arg(&folder)
                .arg("--name")
                .arg(&s.tag)
                .arg("--cache")
                .arg(&ctx.cache)
                .args(["--output", "json"]);
            let _stay_awake = super::awake::BuildInhibitor::acquire();
            let done = run(cmd, "from_source", cancel, notify)?
                .require("Building the tools from source")?;
            ensure!(
                done["commit"] == s.sha && exe.is_file(),
                "The source build did not produce the selected version."
            );
            Ok(exe)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    #[test]
    fn failed_builder_fetch_leaves_the_signed_download_a_clean_destination() -> Result<()> {
        use std::os::unix::fs::PermissionsExt;

        let home = tempfile::tempdir()?;
        let ctx = Context {
            home: home.path().into(),
            settings: json!({}),
            profile: home.path().join("profiles/default"),
            cache: home.path().join("cache"),
        };
        let base = super::super::library::destination(&ctx.home, &ctx.settings, "v3.9.0")?;
        let old = base.join(executable());
        let script = format!(
            "#!/bin/sh\n[ \"$1\" = fetch ] || exit 9\nprintf ran > \"$0.ran\"\nprintf broken > \"$4/{}\"\nexit 1\n",
            executable()
        );
        fs::write(&old, script)?;
        fs::set_permissions(&old, fs::Permissions::from_mode(0o755))?;
        fs::write(base.join(RELEASE_RECORD), "{}")?;
        let tag = "v4.0.0";
        let dest = download_folder(&ctx, tag)?;
        let exe = release_into_with(
            &ctx,
            tag,
            dest.clone(),
            &Cancel::default(),
            &mut |_, _| {},
            |version, folder, _, _| {
                assert_eq!(version, tag);
                assert_eq!(folder, dest);
                assert_eq!(fs::read(old.with_extension("x86_64.ran"))?, b"ran");
                assert!(!folder.join(executable()).exists());
                assert!(fs::read_dir(folder)?.all(|entry| {
                    entry.is_ok_and(|entry| entry.file_name() == ".launcher-fetch.lock")
                }));
                fs::write(folder.join(executable()), b"verified download")?;
                Ok(())
            },
        )?;
        assert_eq!(fs::read(exe)?, b"verified download");
        Ok(())
    }

    #[test]
    fn moving_future_builds_reuses_prefetched_release_without_network() -> Result<()> {
        let home = tempfile::tempdir()?;
        let custom = tempfile::tempdir()?;
        let tag = "v3.9.0";
        let old = super::super::library::destination(home.path(), &json!({}), tag)?;
        fs::write(old.join(executable()), "fixture executable")?;
        home::write_json(
            &old.join(RELEASE_RECORD),
            &json!({"version":tag,"platform":PLATFORM,"program":executable(),"full":{"size":18,"sha256":"6f1af2dfc4d7f16dacf404b1f6c9fd4a65cfffb8edde6dcf957463a0e41fb1ed"}}),
        )?;
        let ctx = Context {
            home: home.path().into(),
            settings: json!({"buildsFolder":custom.path()}),
            profile: home.path().join("profiles/example"),
            cache: home.path().join("work"),
        };
        let dest = super::super::library::destination(&ctx.home, &ctx.settings, tag)?;
        let exe = release_into(&ctx, tag, dest, &Cancel::default(), &mut |_, _| {})?;
        assert_eq!(
            exe.parent(),
            Some(custom.path().canonicalize()?.join(tag).as_path())
        );
        assert_eq!(fs::read(&exe)?, b"fixture executable");
        assert!(old.join(executable()).is_file());
        fs::write(old.join(executable()), "fixture executablX")?;
        assert!(!copy_prefetch(
            &old,
            exe.parent().unwrap(),
            tag,
            &Cancel::default()
        )?);
        assert_eq!(fs::read(&exe)?, b"fixture executable");
        Ok(())
    }

    #[test]
    fn prefetched_builder_stays_in_cache_until_installation() -> Result<()> {
        let t = tempfile::tempdir()?;
        let ctx = Context {
            home: t.path().into(),
            settings: json!({}),
            profile: t.path().join("profiles/example"),
            cache: t.path().join("cache/longshot-builder"),
        };
        let tag = "nightly-20-abcd";
        let cached = download_folder(&ctx, tag)?;
        fs::create_dir_all(&cached)?;
        fs::write(cached.join(executable()), b"fixture executable")?;
        home::write_json(
            &cached.join(RELEASE_RECORD),
            &json!({"version":tag,"platform":PLATFORM,"program":executable(),"full":{"size":18,"sha256":"6f1af2dfc4d7f16dacf404b1f6c9fd4a65cfffb8edde6dcf957463a0e41fb1ed"}}),
        )?;
        assert_eq!(
            release(&ctx, tag, &Cancel::default(), &mut |_, _| {})?,
            cached.join(executable())
        );
        assert!(!ctx.home.join("versions").exists());
        let dest = super::super::library::new_build(&ctx.home, &ctx.settings, tag)?;
        let installed = release_into(&ctx, tag, dest.clone(), &Cancel::default(), &mut |_, _| {})?;
        fs::remove_file(cached.join(executable()))?;
        assert_eq!(installed, dest.join(executable()));
        assert_eq!(fs::read(installed)?, b"fixture executable");
        Ok(())
    }

    #[test]
    fn refuses_other_repo_for_named_pr() {
        assert_eq!(
            pr_number("https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/3/files").unwrap(),
            3
        );
        assert!(pr_number("https://git.magicbean.dev/other/Longshot/pulls/3").is_err());
        assert!(pr_number("3x").is_err());
    }
    #[test]
    fn bootstrap_checks_version_platform_and_signed_name() {
        let mut manifest = json!({"v":1,"version":"v3.1.0","channel":"stable","platform":PLATFORM,"full":{"name":"longshot-builder","size":10,"sha256":"a".repeat(64)}});
        assert!(Manifest::parse(&serde_json::to_vec(&manifest).unwrap(), "v3.1.0").is_ok());
        assert!(Manifest::parse(&serde_json::to_vec(&manifest).unwrap(), "v3.1.1").is_err());
        manifest["full"]["name"] = json!("../other");
        assert!(Manifest::parse(&serde_json::to_vec(&manifest).unwrap(), "v3.1.0").is_err());
    }
}
