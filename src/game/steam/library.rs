//! Identify the active Steam account and its shortcut using the builder's KeyValues ID and relocation note.
use crate::home;
use anyhow::{Context, Result, ensure};
use std::{
    fs,
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

#[cfg(windows)]
fn homes() -> Vec<PathBuf> {
    use winreg::{RegKey, enums::HKEY_CURRENT_USER};
    let registry: Option<String> = RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Valve\Steam")
        .ok()
        .and_then(|key| key.get_value("SteamPath").ok());
    registry
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .into_iter()
        .chain(home::variable("ProgramFiles(x86)").map(|base| base.join("Steam")))
        .collect()
}
#[cfg(not(windows))]
fn homes() -> Vec<PathBuf> {
    let Some(user) = std::env::home_dir() else {
        return vec![];
    };
    let Ok(data) = home::system_data_folder() else {
        return vec![];
    };
    vec![
        data.join("Steam"),
        user.join(".steam/steam"),
        user.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"),
        user.join(".var/app/com.valvesoftware.Steam/data/Steam"),
        user.join("snap/steam/common/.local/share/Steam"),
    ]
}

pub(super) fn library() -> Option<PathBuf> {
    homes().iter().find_map(|root| account_library(root))
}

fn most_recent(root: &Path) -> Option<PathBuf> {
    let text = fs::read_to_string(root.join("config/loginusers.vdf")).ok()?;
    let mut account = None;
    for line in text.lines().map(str::trim) {
        if line.starts_with("\"7656") {
            account = line.trim_matches('"').parse::<u64>().ok();
        } else if line
            .strip_prefix("\"MostRecent\"")
            .is_some_and(|v| v.trim().trim_matches('"') == "1")
            && let Some(id) = account
        {
            let config = root
                .join("userdata")
                .join((id as u32).to_string())
                .join("config");
            if config.is_dir() {
                return Some(config);
            }
        }
    }
    None
}

fn account_library(root: &Path) -> Option<PathBuf> {
    let config = most_recent(root).or_else(|| {
        fs::read_dir(root.join("userdata"))
            .ok()?
            .flatten()
            .filter(|e| e.file_name().to_string_lossy().parse::<u64>().is_ok())
            .filter_map(|entry| {
                let dir = entry.path().join("config");
                Some((dir.metadata().ok()?.modified().ok()?, dir))
            })
            .max()
            .map(|(_, dir)| dir)
    });
    config.map(|path| path.join("shortcuts.vdf")).or_else(|| {
        let single = root.join("config/shortcuts.vdf");
        single.is_file().then_some(single)
    })
}

fn read(path: &Path, limit: u64) -> Option<Vec<u8>> {
    let file = fs::File::open(path).ok()?;
    let metadata = file.metadata().ok()?;
    if !metadata.is_file() || metadata.len() > limit {
        return None;
    }
    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    file.take(limit + 1).read_to_end(&mut bytes).ok()?;
    (bytes.len() as u64 <= limit).then_some(bytes)
}

fn app_id(exe: &str, name: &str) -> u32 {
    let mut crc = crc32fast::Hasher::new();
    crc.update(exe.as_bytes());
    crc.update(name.as_bytes());
    crc.finalize() | 0x8000_0000
}

#[derive(Default)]
struct Tile {
    name: String,
    exe: String,
    options: String,
    app: u32,
    exe_bytes: std::ops::Range<usize>,
    options_bytes: std::ops::Range<usize>,
}

struct Claim {
    name: String,
    exe: String,
    origin: String,
    app: u32,
}

pub(super) struct Shortcuts {
    tiles: Vec<Tile>,
    claims: Vec<Claim>,
}

impl Shortcuts {
    pub fn open(library: &Path) -> Option<Self> {
        if !library.try_exists().ok()? {
            return Some(Self {
                tiles: vec![],
                claims: vec![],
            });
        }
        let library = fs::canonicalize(library).ok()?;
        let tiles = tiles(&read(&library, 16 << 20)?)?;
        let mut sidecar = library.into_os_string();
        sidecar.push(".longshot-builder");
        let path = Path::new(&sidecar);
        let records = if path.try_exists().ok()? {
            String::from_utf8(read(path, 1 << 20)?).ok()?
        } else {
            String::new()
        };
        let claims = records
            .lines()
            .filter_map(|line| {
                let mut fields = line.split('\t');
                let (name, exe, id, origin) = (
                    fields.next()?,
                    fields.next()?,
                    fields.next()?,
                    fields.next()?,
                );
                let app = id.parse::<i32>().ok()? as u32;
                (fields.next().is_none()
                    && (app == app_id(exe, name) || app == app_id(origin, name)))
                .then(|| Claim {
                    name: name.into(),
                    exe: exe.into(),
                    origin: origin.into(),
                    app,
                })
            })
            .collect();
        Some(Self { tiles, claims })
    }

    pub fn is_empty(&self) -> bool {
        self.tiles.is_empty()
    }

    /// Identify tiles by executable, profile options and a builder-owned ID. A relocation
    /// keeps its original ID; the builder records its current executable in the sidecar.
    pub fn owned(&self, builder: &Path, profile: &Path, legacy_name: &str) -> Vec<(String, u32)> {
        let Ok(profile) = std::path::absolute(profile) else {
            return vec![];
        };
        let option_prefix = play_options(builder, &profile).ok();
        // Older shortcuts stored the verbatim spelling from the settings file.
        let old_prefix = if cfg!(windows) {
            profile
                .canonicalize()
                .ok()
                .and_then(|old| play_options(builder, &old).ok())
        } else {
            None
        };
        let mut found = Vec::new();
        for tile in &self.tiles {
            let exe = Path::new(
                tile.exe
                    .strip_prefix('"')
                    .and_then(|v| v.strip_suffix('"'))
                    .unwrap_or(&tile.exe),
            );
            if let Some(claim) = self
                .claims
                .iter()
                .find(|c| c.app == tile.app && c.exe == tile.exe)
                && [option_prefix.as_deref(), old_prefix.as_deref()]
                    .into_iter()
                    .flatten()
                    .any(|prefix| {
                        tile.options.strip_prefix(prefix).is_some_and(|home| {
                            home.starts_with(" --launcher-home \"") && home.ends_with('"')
                        })
                    })
            {
                found.push((claim.name.clone(), tile.app));
                continue;
            }
            let options = tile.options.strip_prefix("play ").unwrap_or(&tile.options);
            let Some(target_profile) = options
                .strip_prefix("--profile \"")
                .and_then(|v| v.strip_suffix('"'))
            else {
                continue;
            };
            if !same_path(exe, builder)
                || !same_path(Path::new(&target_profile.replace("\\\"", "\"")), &profile)
            {
                continue;
            }
            let claim = self
                .claims
                .iter()
                .find(|c| c.app == tile.app && (c.exe == tile.exe || c.origin == tile.exe));
            let name = if let Some(claim) = claim {
                &claim.name
            } else if tile.name == legacy_name && tile.app == app_id(&tile.exe, legacy_name) {
                &tile.name
            } else {
                continue;
            };
            found.push((name.clone(), tile.app));
        }
        found
    }

    pub fn launcher_id(&self, builder: &Path, profile: &Path, home: &Path) -> Option<u32> {
        let (exe, options) = launcher_command(builder, profile, home).ok()?;
        self.tiles.iter().find_map(|tile| {
            (tile.exe == exe
                && tile.options == options
                && self
                    .claims
                    .iter()
                    .any(|claim| claim.exe == exe && claim.app == tile.app))
            .then_some(tile.app)
        })
    }

    /// The builder relocates by title. A new installation must never borrow a
    /// title Steam already has or the builder still claims for another installation.
    pub fn available_title(&self, base: &str) -> String {
        (1..)
            .map(|n| {
                if n == 1 {
                    base.to_owned()
                } else {
                    format!("{base} ({n})")
                }
            })
            .find(|candidate| {
                !self.tiles.iter().any(|t| &t.name == candidate)
                    && !self.claims.iter().any(|c| &c.name == candidate)
            })
            .expect("finite shortcut collection")
    }
}

/// Early builder tiles can predate the relocation note. Record their original
/// identity only when both the app ID and the profile options still verify.
pub(super) fn prepare_retarget(
    library: &Path,
    title: &str,
    builder: &Path,
    profile: &Path,
    id: u32,
) -> Result<()> {
    let library_path = fs::canonicalize(library)?;
    let snapshot = Shortcuts::open(&library_path).context("Could not read Steam's shortcuts.")?;
    ensure!(
        snapshot
            .owned(builder, profile, title)
            .iter()
            .any(|(name, app)| name == title && *app == id),
        "Steam shortcut {title} changed. Its target was left untouched."
    );
    if let Some(claim) = snapshot.claims.iter().find(|c| c.name == title) {
        ensure!(
            claim.app == id
                && snapshot
                    .tiles
                    .iter()
                    .any(|tile| tile.app == id
                        && (tile.exe == claim.exe || tile.exe == claim.origin)),
            "Steam already uses this shortcut name for a different installation."
        );
        return Ok(());
    }
    let original = snapshot
        .tiles
        .iter()
        .find(|tile| {
            tile.app == id
                && tile.name == title
                && same_path(Path::new(tile.exe.trim_matches('"')), builder)
                && app_id(&tile.exe, title) == id
        })
        .context("Steam did not confirm the original shortcut ID.")?;
    ensure!(
        !title.contains(['\t', '\r', '\n']) && !original.exe.contains(['\t', '\r', '\n']),
        "The shortcut name or location cannot be recorded in Steam's relocation note."
    );
    let mut note = library_path.into_os_string();
    note.push(".longshot-builder");
    let note = Path::new(&note);
    if let Ok(meta) = fs::symlink_metadata(note) {
        ensure!(
            meta.is_file() && !crate::storage::is_link(&meta),
            "The Steam relocation note must be a regular file."
        );
    }
    let mut open = fs::OpenOptions::new();
    open.read(true).append(true).create(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        open.custom_flags(libc::O_NOFOLLOW);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        open.custom_flags(windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT);
    }
    let file = open.open(note)?;
    let meta = file.metadata()?;
    ensure!(
        meta.is_file() && !crate::storage::is_link(&meta),
        "The Steam relocation note must be a regular file."
    );
    file.try_lock()
        .context("Another launcher is changing the Steam relocation note.")?;
    let mut file = home::FileLease(file);
    ensure!(
        file.0.metadata()?.len() <= 1 << 20,
        "The Steam relocation note is too large."
    );
    let mut previous = String::new();
    file.0.seek(SeekFrom::Start(0))?;
    Read::by_ref(&mut file.0)
        .take((1 << 20) + 1)
        .read_to_string(&mut previous)?;
    ensure!(
        previous.len() <= 1 << 20
            && !previous
                .lines()
                .any(|line| line.split('\t').next() == Some(title)),
        "The Steam relocation note changed. Retry the update."
    );
    let separator = if previous.is_empty() || previous.ends_with('\n') {
        ""
    } else {
        "\n"
    };
    let claim = format!(
        "{separator}{title}\t{}\t{}\t{}\n",
        original.exe, id as i32, original.exe
    );
    file.0.write_all(claim.as_bytes())?;
    file.0.sync_all()?;
    Ok(())
}

// Steam passes launch options through a shell on Linux and a Windows command line on Windows.
fn argument(path: &Path) -> Result<String> {
    let path = path.to_str().context("Steam requires a Unicode path.")?;
    ensure!(
        !path.contains(['\0', '\r', '\n', '\t']),
        "Steam cannot use control characters in paths."
    );
    #[cfg(unix)]
    let path = path
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('$', "\\$")
        .replace('`', "\\`");
    #[cfg(windows)]
    let path = {
        ensure!(!path.contains('"'), "Invalid Windows shortcut path.");
        format!(
            "{path}{}",
            "\\".repeat(path.chars().rev().take_while(|&c| c == '\\').count())
        )
    };
    Ok(format!("\"{path}\""))
}

fn play_options(builder: &Path, profile: &Path) -> Result<String> {
    let version = builder
        .parent()
        .context("Builder has no installation folder.")?;
    Ok(format!(
        "--play {} --profile {}",
        argument(version)?,
        argument(profile)?
    ))
}

fn launcher_command(builder: &Path, profile: &Path, home: &Path) -> Result<(String, String)> {
    let launcher = super::super::process::launcher_executable()?;
    let exe = argument(&launcher)?;
    let options = format!(
        "{} --launcher-home {}",
        play_options(builder, profile)?,
        argument(home)?
    );
    Ok((exe, options))
}

pub(super) fn use_launcher(
    path: &Path,
    title: &str,
    id: u32,
    builder: &Path,
    profile: &Path,
    home: &Path,
) -> Result<()> {
    let (exe, options) = launcher_command(builder, profile, home)?;
    let path = fs::canonicalize(path)?;
    let before = read(&path, 16 << 20).context("Could not read Steam’s library.")?;
    let entries = Shortcuts::open(&path).context("Could not identify Steam’s shortcuts.")?;
    ensure!(
        entries
            .owned(builder, profile, title)
            .contains(&(title.to_owned(), id)),
        "The Steam shortcut changed; try again."
    );
    let mut matching = entries.tiles.iter().filter(|t| t.app == id);
    let tile = matching.next().context("Steam shortcut disappeared.")?;
    ensure!(matching.next().is_none(), "Steam shortcut ID is ambiguous.");
    if tile.exe == exe && tile.options == options {
        return Ok(());
    }
    prepare_retarget(&path, title, builder, profile, id)?;
    let mut note = path.as_os_str().to_owned();
    note.push(".longshot-builder");
    let note = Path::new(&note);
    let metadata = fs::symlink_metadata(note)?;
    ensure!(
        metadata.is_file() && !crate::storage::is_link(&metadata),
        "The Steam relocation note must be a regular file."
    );
    let prior =
        String::from_utf8(read(note, 1 << 20).context("Could not read Steam’s relocation note.")?)?;
    let mut claimed = false;
    let mut records = String::new();
    for line in prior.split_inclusive('\n') {
        let columns: Vec<_> = line.trim_end_matches(['\r', '\n']).split('\t').collect();
        if columns.len() == 4
            && columns[0] == title
            && columns[2].parse::<i32>().ok() == Some(id as i32)
        {
            ensure!(
                !claimed && (columns[1] == tile.exe || columns[1] == exe),
                "Steam shortcut ownership changed."
            );
            claimed = true;
            records.push_str(&format!("{title}\t{exe}\t{}\t{}\n", columns[2], columns[3]));
        } else {
            records.push_str(line);
        }
    }
    ensure!(
        claimed,
        "Could not confirm the Steam shortcut’s relocation note."
    );
    ensure!(
        before.get(tile.exe_bytes.clone()) == Some(tile.exe.as_bytes())
            && before.get(tile.options_bytes.clone()) == Some(tile.options.as_bytes()),
        "Steam changed its library. Please retry."
    );
    let mut patched = before.clone();
    let mut fields = [
        (tile.exe_bytes.clone(), exe.as_bytes()),
        (tile.options_bytes.clone(), options.as_bytes()),
    ];
    fields.sort_by_key(|(range, _)| std::cmp::Reverse(range.start));
    for (range, value) in fields {
        ensure!(
            !range.is_empty() && range.end <= patched.len(),
            "Incomplete Steam shortcut."
        );
        patched.splice(range, value.iter().copied());
    }
    replace_if_unchanged(note, prior.as_bytes(), records.as_bytes())?;
    if let Err(err) = replace_if_unchanged(&path, &before, &patched) {
        replace_if_unchanged(note, records.as_bytes(), prior.as_bytes())?;
        return Err(err);
    }
    Ok(())
}

fn replace_if_unchanged(path: &Path, before: &[u8], after: &[u8]) -> Result<()> {
    if before == after {
        return Ok(());
    }
    let directory = path
        .parent()
        .context("Missing Steam configuration folder.")?;
    let mut temp = tempfile::NamedTempFile::new_in(directory)?;
    temp.as_file()
        .set_permissions(fs::metadata(path)?.permissions())?;
    temp.write_all(after)?;
    temp.as_file().sync_all()?;
    ensure!(
        fs::read(path)? == before,
        "Steam changed its library. Please retry."
    );
    temp.persist(path)?;
    Ok(())
}

fn same_path(a: &Path, b: &Path) -> bool {
    if a == b {
        return true;
    }
    #[cfg(windows)]
    {
        // A relocated executable may already have left its original directory.
        // Match the normal and verbatim spellings without requiring it to exist.
        fn ordinary(path: &Path) -> String {
            let text = path.to_string_lossy();
            if let Some(unc) = text.strip_prefix(r"\\?\UNC\") {
                format!(r"\\{unc}")
            } else {
                text.strip_prefix(r"\\?\").unwrap_or(&text).to_owned()
            }
        }
        ordinary(a) == ordinary(b)
            || a.canonicalize()
                .is_ok_and(|p| b.canonicalize().is_ok_and(|q| q == p))
    }
    #[cfg(not(windows))]
    {
        false
    }
}

// Parse immediate shortcut fields only. A nested tag cannot impersonate the executable.
fn tiles(mut input: &[u8]) -> Option<Vec<Tile>> {
    fn take<'a>(input: &mut &'a [u8], n: usize) -> Option<&'a [u8]> {
        let (v, rest) = input.split_at_checked(n)?;
        *input = rest;
        Some(v)
    }
    fn string<'a>(input: &mut &'a [u8]) -> Option<&'a [u8]> {
        let n = input.iter().position(|&b| b == 0)?;
        let v = take(input, n)?;
        take(input, 1)?;
        Some(v)
    }
    let length = input.len();
    if input.is_empty() {
        return Some(vec![]);
    }
    if take(&mut input, 1)? != [0] || !string(&mut input)?.eq_ignore_ascii_case(b"shortcuts") {
        return None;
    }
    let mut result = Vec::new();
    let mut depth = 1;
    let mut tile = Tile::default();
    while !input.is_empty() {
        let kind = take(&mut input, 1)?[0];
        if kind == 8 {
            if depth == 2 {
                result.push(std::mem::take(&mut tile));
            }
            depth -= 1;
            if depth == 0 {
                return Some(result);
            }
            continue;
        }
        let key = string(&mut input)?;
        match kind {
            0 => {
                depth += 1;
                if depth > 32 {
                    return None;
                }
            }
            1 => {
                let start = length - input.len();
                let s = string(&mut input)?;
                if depth == 2 {
                    let dest = if key.eq_ignore_ascii_case(b"LaunchOptions") {
                        tile.options_bytes = start..start + s.len();
                        &mut tile.options
                    } else if key.eq_ignore_ascii_case(b"exe") {
                        tile.exe_bytes = start..start + s.len();
                        &mut tile.exe
                    } else if key.eq_ignore_ascii_case(b"AppName") {
                        &mut tile.name
                    } else {
                        continue;
                    };
                    *dest = String::from_utf8_lossy(s).into_owned();
                }
            }
            2..=4 | 6 => {
                let data = take(&mut input, 4)?;
                if depth == 2 && kind == 2 && key.eq_ignore_ascii_case(b"appid") {
                    tile.app = u32::from_le_bytes(data.try_into().ok()?);
                }
            }
            7 => {
                take(&mut input, 8)?;
            }
            _ => return None,
        }
    }
    None
}

pub(super) fn installed_id(
    library: &Path,
    name: &str,
    builder: &Path,
    profile: &Path,
) -> Option<u32> {
    Shortcuts::open(library)?
        .owned(builder, profile, name)
        .into_iter()
        .find_map(|(title, id)| (title == name).then_some(id))
}
#[cfg(test)]
mod tests {
    use super::*;
    use anyhow::Result;

    fn shortcut(exe: &str, id: u32, options: &str) -> Vec<u8> {
        shortcuts(&[("Longshot", exe, id, options)])
    }

    fn shortcuts(entries: &[(&str, &str, u32, &str)]) -> Vec<u8> {
        let mut data = b"\x00shortcuts\x00".to_vec();
        for (index, (title, exe, id, options)) in entries.iter().enumerate() {
            data.push(0);
            data.extend(index.to_string().bytes());
            data.extend(b"\x00\x02appid\x00");
            data.extend(id.to_le_bytes());
            for (key, value) in [
                ("AppName", *title),
                ("Exe", *exe),
                ("LaunchOptions", *options),
            ] {
                data.push(1);
                data.extend(key.bytes());
                data.push(0);
                data.extend(value.bytes());
                data.push(0);
            }
            data.push(8);
        }
        data.push(8);
        data
    }

    #[test]
    fn instances_in_the_same_profile_own_distinct_steam_shortcuts() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let old = tmp.path().join("old/Longshot");
        let other = tmp.path().join("other/Longshot");
        let destination = tmp.path().join("current/Longshot");
        let profile = tmp.path().join("profiles/default");
        let different = tmp.path().join("profiles/other");
        let opts = format!("--profile \"{}\"", profile.display());
        let other_opts = format!("--profile \"{}\"", different.display());
        let old_exe = old.to_str().unwrap();
        let other_exe = other.to_str().unwrap();
        let dest_exe = destination.to_str().unwrap();
        let title = "Longshot · my-install";
        let sibling = "Longshot · other-install";
        let first_id = app_id(old_exe, title);
        let sibling_id = app_id(other_exe, sibling);
        let other_profile = "Longshot (Alice) · my-install";
        let profile_id = app_id(old_exe, other_profile);
        let vdf = tmp.path().join("shortcuts.vdf");
        fs::write(
            &vdf,
            shortcuts(&[
                (title, dest_exe, first_id, &opts),
                (sibling, other_exe, sibling_id, &opts),
                (other_profile, old_exe, profile_id, &other_opts),
            ]),
        )?;
        fs::write(
            tmp.path().join("shortcuts.vdf.longshot-builder"),
            format!(
                "{title}\t{dest_exe}\t{}\t{old_exe}\n{sibling}\t{other_exe}\t{}\t{other_exe}\n{other_profile}\t{old_exe}\t{}\t{old_exe}\n",
                first_id as i32, sibling_id as i32, profile_id as i32
            ),
        )?;
        let shortcuts = Shortcuts::open(&vdf).unwrap();
        assert_eq!(
            shortcuts.owned(&destination, &profile, "Longshot"),
            [(title.into(), first_id)]
        );
        assert_eq!(
            shortcuts.owned(&other, &profile, "Longshot"),
            [(sibling.into(), sibling_id)]
        );
        assert!(shortcuts.owned(&old, &profile, "Longshot").is_empty());
        assert_eq!(
            shortcuts.owned(&old, &different, "Longshot (Renamed)"),
            [(other_profile.into(), profile_id)]
        );
        assert_eq!(shortcuts.available_title(title), format!("{title} (2)"));
        Ok(())
    }

    #[test]
    fn library_play_uses_the_launcher_and_preserves_the_steam_identity() -> Result<()> {
        let root = tempfile::tempdir()?;
        let builder = root.path().join("Version A/game");
        let profile = root.path().join("profiles/My Player");
        let game = format!("\"{}\"", builder.display());
        let opts = format!("--profile \"{}\"", profile.display());
        let id = app_id(&game, "Longshot");
        let path = root.path().join("shortcuts.vdf");
        let external = ("Other", "/custom/game", 42, "custom --option");
        fs::write(
            &path,
            shortcuts(&[("Longshot", &game, id, &opts), external]),
        )?;
        use_launcher(&path, "Longshot", id, &builder, &profile, root.path())?;
        let exe = argument(&super::super::super::process::launcher_executable()?)?;
        let launch = format!(
            "{} --launcher-home {}",
            play_options(&builder, &profile)?,
            argument(root.path())?
        );
        let expected = shortcuts(&[("Longshot", &exe, id, &launch), external]);
        let entries = Shortcuts::open(&path).unwrap();
        assert_eq!(
            entries.launcher_id(&builder, &profile, root.path()),
            Some(id)
        );
        assert_eq!(
            entries.launcher_id(&builder, &profile, &root.path().join("other")),
            None
        );
        assert_eq!(fs::read(&path)?, expected);
        assert_eq!(
            installed_id(&path, "Longshot", &builder, &profile),
            Some(id)
        );
        assert_eq!(
            installed_id(
                &path,
                "Longshot",
                &builder,
                &profile.with_file_name("other")
            ),
            None
        );
        use_launcher(&path, "Longshot", id, &builder, &profile, root.path())?;
        assert_eq!(fs::read(&path)?, expected);
        assert!(use_launcher(&path, "Other", 42, &builder, &profile, root.path()).is_err());
        assert_eq!(fs::read(&path)?, expected);
        Ok(())
    }

    #[test]
    fn adopts_only_a_proven_legacy_shortcut_before_retargeting() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let builder = tmp.path().join("old/Longshot");
        let profile = tmp.path().join("profiles/default");
        let other = tmp.path().join("profiles/other");
        let program = builder.to_str().unwrap();
        let id = app_id(program, "Longshot");
        let options = format!("--profile \"{}\"", profile.display());
        let vdf = tmp.path().join("shortcuts.vdf");
        let original = shortcut(program, id, &options);
        fs::write(&vdf, &original)?;
        let note = tmp.path().join("shortcuts.vdf.longshot-builder");
        fs::write(&note, "preserve this unknown record")?;
        assert!(prepare_retarget(&vdf, "Longshot", &builder, &other, id).is_err());
        assert_eq!(fs::read_to_string(&note)?, "preserve this unknown record");
        prepare_retarget(&vdf, "Longshot", &builder, &profile, id)?;
        let content = fs::read_to_string(&note)?;
        assert_eq!(
            content,
            format!(
                "preserve this unknown record\nLongshot\t{program}\t{}\t{program}\n",
                id as i32
            )
        );
        prepare_retarget(&vdf, "Longshot", &builder, &profile, id)?;
        assert_eq!(fs::read_to_string(&note)?, content);
        assert_eq!(fs::read(&vdf)?, original);
        Ok(())
    }

    #[test]
    fn shortcut_presence_follows_the_account_version_and_profile_on_disk() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let profile = root.join("profiles/default");
        let build = root.join("Version A").join("Longshot.x86_64");
        let other_build = root.join("Version B").join("Longshot.x86_64");
        let steam_a = root.join("userdata/1/config");
        let steam_b = root.join("userdata/2/config");
        fs::create_dir_all(&steam_a)?;
        fs::create_dir_all(&steam_b)?;
        fs::create_dir_all(root.join("config"))?;
        fs::write(
            root.join("config/loginusers.vdf"),
            b"\"76561197960265729\"\n{\n\"MostRecent\"  \"1\"\n}\n",
        )?;
        let library = account_library(root).unwrap();
        assert_eq!(library, steam_a.join("shortcuts.vdf"));
        let exe = format!("\"{}\"", build.display());
        let opts = format!("--profile \"{}\"", profile.display());
        let app = app_id(&exe, "Longshot");
        let tile = shortcut(&exe, app, &opts);
        fs::write(steam_b.join("shortcuts.vdf"), &tile)?;
        assert_eq!(installed_id(&library, "Longshot", &build, &profile), None);
        fs::write(&library, &tile)?;
        assert_eq!(
            installed_id(&library, "Longshot", &build, &profile),
            Some(app)
        );
        assert_eq!(
            installed_id(&library, "Longshot", &other_build, &profile),
            None
        );
        assert_eq!(
            installed_id(&library, "Longshot", &build, &root.join("profiles/other")),
            None
        );
        let previous = "\"/old folder/Longshot.x86_64\"";
        let old_app = app_id(previous, "Longshot");
        fs::write(&library, shortcut(&exe, old_app, &format!("play {opts}")))?;
        assert_eq!(installed_id(&library, "Longshot", &build, &profile), None);
        fs::write(
            steam_a.join("shortcuts.vdf.longshot-builder"),
            format!("Longshot\t{exe}\t{}\t{previous}\n", old_app as i32),
        )?;
        assert_eq!(
            installed_id(&library, "Longshot", &build, &profile),
            Some(old_app)
        );
        let moved_profile = root.join("new-data/profiles/default");
        let options = format!("play --profile \"{}\"", moved_profile.display());
        fs::write(&library, shortcut(&exe, old_app, &options))?;
        assert_eq!(
            installed_id(&library, "Longshot", &build, &moved_profile),
            Some(old_app)
        );
        assert_eq!(installed_id(&library, "Longshot", &build, &profile), None);
        fs::write(&library, b"\0shortcuts\0\x08")?;
        assert_eq!(installed_id(&library, "Longshot", &build, &profile), None);
        Ok(())
    }
}
