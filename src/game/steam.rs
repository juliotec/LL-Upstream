mod library;
use super::process::{Cancel, Context, executable, run};
use crate::home;
use anyhow::{Context as _, Result, ensure};
use std::{
    fs,
    io::Write,
    path::Path,
    process::{Child, Command, Stdio},
    time::{Duration, SystemTime},
};

static LIBRARY_WRITE: std::sync::Mutex<()> = std::sync::Mutex::new(());

fn name(profile: &Path, data: &serde_json::Value) -> String {
    if profile.file_name().is_some_and(|n| n == "default") {
        return "Longshot".into();
    }
    let title = data["name"]
        .as_str()
        .filter(|name| !name.trim().is_empty())
        .or_else(|| profile.file_name()?.to_str())
        .unwrap_or("Profile");
    format!("Longshot ({title})")
}

pub(super) fn installed(
    home: &Path,
    profile: &Path,
    record: &serde_json::Value,
    builder: &Path,
) -> bool {
    library::library()
        .and_then(|p| library::Shortcuts::open(&p))
        .is_some_and(|library| {
            !library
                .owned_in(home, builder, profile, &name(profile, record))
                .is_empty()
        })
}

pub(super) fn shortcut(
    ctx: &Context,
    version: &Path,
    remove: bool,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<String> {
    let path = library::library().context("Sign in to Steam before managing its shortcuts.")?;
    let library = library::Shortcuts::open(&path)
        .context("Could not read Steam’s shortcuts. Its library was left untouched.")?;
    let base = name(
        &ctx.profile,
        &home::read_json(&ctx.profile.join("profile.json"))?,
    );
    let existing = library
        .owned_in(&ctx.home, &version.join(executable()), &ctx.profile, &base)
        .into_iter()
        .next();
    let title = if let Some((title, _)) = existing {
        title
    } else {
        ensure!(
            !remove,
            "This installation has no Steam shortcut for this profile."
        );
        let installed = super::versions::selected(&ctx.home, Some(version))?;
        let title = format!("{base} · {}", installed.id);
        library.available_title(&title)
    };
    write_shortcut(ctx, version, &title, remove, cancel, notify)
}

fn write_shortcut(
    ctx: &Context,
    version: &Path,
    title: &str,
    remove: bool,
    cancel: &Cancel,
    notify: &mut dyn FnMut(&str, Option<f32>),
) -> Result<String> {
    let _guard = LIBRARY_WRITE.lock().unwrap_or_else(|err| err.into_inner());
    let mut cmd = ctx.command(&version.join(executable()), false)?;
    cmd.arg("steam-shortcut").arg("--profile").arg(&ctx.profile);
    cmd.arg("--name").arg(title);
    if remove {
        cmd.arg("--remove");
    }
    cmd.args(["--output", "json"]);
    let result = run(cmd, "steam_shortcut", cancel, notify)?.require("Updating Steam")?;
    let action = result["action"].as_str().unwrap_or_default();
    if matches!(action, "added" | "updated" | "unchanged") {
        let id = u32::try_from(
            result["app_id"]
                .as_u64()
                .context("Steam shortcut has no app ID.")?,
        )?;
        let path = Path::new(
            result["library"]
                .as_str()
                .context("Steam shortcut has no library path.")?,
        );
        ensure!(
            library::library().and_then(|p| p.canonicalize().ok()) == Some(path.canonicalize()?),
            "The signed-in Steam account changed."
        );
        library::use_instance_executable(
            path,
            title,
            id,
            &version.join(executable()),
            &ctx.profile,
            &ctx.home,
        )?;
        artwork(path, id)?;
    }
    Ok(format!(
        "Steam shortcut {action}.{}",
        if result["steam_running"] == true {
            " Restart Steam to see the change."
        } else {
            ""
        }
    ))
}

/// Upgrade the signed-in user's previously registered shortcuts in place. Unknown targets stay intact.
pub(crate) fn migrate_shortcuts(home: &Path) -> Result<()> {
    let Some(path) = library::library().filter(|path| path.is_file()) else {
        return Ok(());
    };
    migrate_library(home, &path)
}

fn migrate_library(home: &Path, path: &Path) -> Result<()> {
    let _guard = LIBRARY_WRITE.lock().unwrap_or_else(|err| err.into_inner());
    let snapshot = library::Shortcuts::open(path).context("Could not read Steam’s shortcuts.")?;
    if snapshot.is_empty() {
        return Ok(());
    }
    let versions = super::versions::listed(home)?;
    let records = super::versions::registry(home)?;
    for profile in crate::players::list_profiles(home)? {
        let base = name(&profile.dir, &profile.file);
        for version in &versions {
            let row = records["builds"]
                .as_array()
                .unwrap()
                .iter()
                .find(|row| row["id"] == version.id);
            let records = records["builds"].as_array().unwrap();
            let historical_owner = |folder: &Path| {
                records
                    .iter()
                    .filter(|r| library::history(home, r).iter().any(|p| p == folder))
                    .count()
            };
            if row.is_none()
                && super::is_retired(&version.folder)
                && historical_owner(&version.folder) > 0
            {
                continue;
            }
            let mut folders = row.map(|r| library::history(home, r)).unwrap_or_default();
            folders.retain(|old| {
                (!old.exists() || super::is_retired(old))
                    && historical_owner(old) == 1
                    && !records.iter().any(|r| {
                        r["folder"]
                            .as_str()
                            .and_then(|p| Path::new(p).ancestors().nth(2))
                            == Some(old)
                    })
            });
            folders.push(version.folder.clone());
            let target = version.folder.join(executable());
            for folder in folders {
                let builder = folder.join(executable());
                for (name, id) in snapshot.owned_in(home, &builder, &profile.dir, &base) {
                    library::retarget_executable(
                        path,
                        &name,
                        id,
                        &builder,
                        &target,
                        &profile.dir,
                        home,
                    )?;
                }
            }
        }
    }
    Ok(())
}

fn artwork(library: &Path, app: u32) -> Result<()> {
    ensure!(
        library.file_name().is_some_and(|n| n == "shortcuts.vdf") && library.is_file(),
        "Steam did not name its shortcuts.vdf."
    );
    let grid = library.parent().unwrap().join("grid");
    fs::create_dir_all(&grid)?;
    let art: [(&str, &[u8]); 4] = [
        ("", include_bytes!("../../assets/steam/capsule_920x430.png")),
        (
            "p",
            include_bytes!("../../assets/steam/capsulep_600x900.png"),
        ),
        (
            "_hero",
            include_bytes!("../../assets/steam/hero_1920x620.png"),
        ),
        (
            "_logo",
            include_bytes!("../../assets/steam/logo_998x323.png"),
        ),
    ];
    for (suffix, bytes) in art {
        let stems = [
            format!("{app}{suffix}"),
            format!("{}{suffix}", game_id(app)),
        ];
        let held = fs::read_dir(&grid)?.flatten().any(|entry| {
            let path = entry.path();
            path.extension().is_some_and(|extension| extension != "tmp")
                && path
                    .file_stem()
                    .and_then(|s| s.to_str())
                    .is_some_and(|name| stems.iter().any(|s| s == name))
        });
        if held {
            continue;
        }
        for stem in stems {
            let mut temp = tempfile::NamedTempFile::new_in(&grid)?;
            temp.write_all(bytes)?;
            if let Err(err) = temp.persist_noclobber(grid.join(format!("{stem}.png")))
                && err.error.kind() != std::io::ErrorKind::AlreadyExists
            {
                return Err(err.error.into());
            }
        }
    }
    Ok(())
}

fn game_id(app: u32) -> u64 {
    (u64::from(app) << 32) | 0x02000000
}

pub(super) fn route(ctx: &Context, exe: &Path) -> Option<Child> {
    if !cfg!(target_os = "linux")
        || ["SteamGameId", "SteamAppId"]
            .iter()
            .any(|s| home::variable(s).is_some())
        || std::env::var("XDG_CURRENT_DESKTOP")
            .unwrap_or_default()
            .split(':')
            .any(|d| d.eq_ignore_ascii_case("gamescope"))
    {
        return None;
    }
    let library = library::library()?;
    let meta = library.metadata().ok()?;
    if meta.len() > 16 << 20 {
        return None;
    }
    let changed = meta.modified().ok()?;
    // The file's mtime must precede a currently running Steam client's start. An update
    // to a shortcut while Steam runs is visible only after the next Steam restart.
    let boot = fs::read_to_string("/proc/stat")
        .ok()?
        .lines()
        .find_map(|l| l.strip_prefix("btime ")?.parse::<u64>().ok())?;
    let mut command = Command::new("getconf");
    super::process::restore_host_environment(&mut command);
    let ticks = command.arg("CLK_TCK").output().ok()?;
    let ticks = std::str::from_utf8(&ticks.stdout)
        .ok()?
        .trim()
        .parse::<u64>()
        .ok()?
        .max(1);
    let active = fs::read_dir("/proc").ok()?.flatten().any(|e| {
        if e.file_name().to_string_lossy().parse::<u32>().is_err()
            || !fs::read_to_string(e.path().join("comm")).is_ok_and(|s| s.trim() == "steam")
        {
            return false;
        }
        let stat = fs::read_to_string(e.path().join("stat")).unwrap_or_default();
        let started = stat
            .rsplit_once(')')
            .and_then(|(_, fields)| fields.split_ascii_whitespace().nth(19))
            .and_then(|s| s.parse::<u64>().ok());
        started
            .and_then(|t| SystemTime::UNIX_EPOCH.checked_add(Duration::from_secs(boot + t / ticks)))
            .is_some_and(|t| t >= changed && t <= SystemTime::now())
    });
    if !active {
        return None;
    }
    let profile = home::read_json(&ctx.profile.join("profile.json")).ok()?;
    let (_, id) = library::Shortcuts::open(&library)?
        .owned_in(&ctx.home, exe, &ctx.profile, &name(&ctx.profile, &profile))
        .into_iter()
        .next()?;
    let mut command = Command::new("xdg-open");
    super::process::restore_host_environment(&mut command);
    let mut opener = command
        .arg(format!("steam://rungameid/{}", game_id(id)))
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .ok()?;
    for _ in 0..20 {
        match opener.try_wait().ok()? {
            Some(s) if s.success() => return Some(opener),
            Some(_) => return None,
            None => std::thread::sleep(Duration::from_millis(10)),
        }
    }
    Some(opener)
}

/// A launcher-owned shortcut in the signed-in Steam account that points at a version being moved.
#[derive(serde::Serialize, serde::Deserialize)]
pub(crate) struct ShortcutMove {
    profile: std::path::PathBuf,
    library: std::path::PathBuf,
    title: String,
    id: u32,
    from: std::path::PathBuf,
    to: std::path::PathBuf,
    new_profile: std::path::PathBuf,
}

impl ShortcutMove {
    pub(crate) fn retarget(&self, home: &Path, forward: bool) -> Result<()> {
        let target = if forward { &self.to } else { &self.from };
        let exe = target.join(executable());
        let (old_profile, new_profile) = if forward {
            (&self.profile, &self.new_profile)
        } else {
            (&self.new_profile, &self.profile)
        };
        let present = || {
            library::Shortcuts::open(&self.library).is_some_and(|s| {
                s.owned_in(home, &exe, new_profile, &self.title)
                    .contains(&(self.title.clone(), self.id))
            })
        };
        if present() {
            return Ok(());
        }
        ensure!(
            library::library().as_deref() == Some(self.library.as_path()),
            "The signed-in Steam account changed."
        );
        let original = if forward { &self.from } else { &self.to };
        library::prepare_retarget(
            home,
            &self.library,
            &self.title,
            &original.join(executable()),
            old_profile,
            self.id,
        )?;
        let mut ctx = Context::load(home)?;
        ctx.profile = new_profile.clone();
        write_shortcut(
            &ctx,
            target,
            &self.title,
            false,
            &Cancel::default(),
            &mut |_, _| {},
        )?;
        ensure!(
            present(),
            "Steam did not preserve the shortcut for {}.",
            self.title
        );
        Ok(())
    }
}

pub(crate) fn shortcuts_to_move(home: &Path, from: &Path, to: &Path) -> Result<Vec<ShortcutMove>> {
    shortcuts_to_move_data(home, from, to, &home::data_dir(home)?.join("profiles"))
}

pub(crate) fn shortcuts_to_move_data(
    home: &Path,
    from: &Path,
    to: &Path,
    new_profiles: &Path,
) -> Result<Vec<ShortcutMove>> {
    let Some(library) = library::library() else {
        return Ok(Vec::new());
    };
    if !library.is_file() || !home::data_dir(home)?.join("profiles").is_dir() {
        return Ok(Vec::new());
    }
    let snapshot =
        library::Shortcuts::open(&library).context("Could not read Steam's shortcuts.")?;
    let mut moves = Vec::new();
    for profile in crate::players::list_profiles(home)? {
        let dir = profile.dir;
        for (title, id) in snapshot.owned_in(
            home,
            &from.join(executable()),
            &dir,
            &name(&dir, &profile.file),
        ) {
            moves.push(ShortcutMove {
                profile: dir.clone(),
                library: library.clone(),
                title,
                id,
                from: from.into(),
                to: to.into(),
                new_profile: new_profiles.join(&profile.id),
            });
        }
    }
    Ok(moves)
}
