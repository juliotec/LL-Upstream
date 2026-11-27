use super::{disk, saves};
use crate::home;
use anyhow::{Context, Result, bail, ensure};
use serde_json::{Map, Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
    time::{Duration, Instant},
};
use tempfile::{Builder, NamedTempFile};

pub const COLORS: [(&str, &str); 12] = [
    ("Sage", "#7F9D53"),
    ("Russet", "#A9573C"),
    ("Mustard", "#D8A840"),
    ("Olive", "#56643A"),
    ("Sand", "#D8BC8C"),
    ("Teal", "#68A699"),
    ("Sky", "#80AEC9"),
    ("Lavender", "#AC96C5"),
    ("Plum", "#AB7D9E"),
    ("Rose", "#CD8584"),
    ("Apricot", "#DBA06C"),
    ("Slate", "#8399A5"),
];

pub fn valid_id(id: &str) -> bool {
    id == "default"
        || id.strip_prefix("p-").is_some_and(|s| {
            s.len() == 8
                && s.bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        })
}

pub(crate) use crate::home::system_data_folder as data_root;
pub fn backup_root(home: &Path) -> Result<PathBuf> {
    if home::is_portable(home) {
        return Ok(home.join("save-backups"));
    }
    Ok(data_root()?
        .join(if cfg!(windows) {
            "Longshot"
        } else {
            "longshot"
        })
        .join("save-backups"))
}

pub(crate) fn active_dir(home: &Path) -> Result<Option<PathBuf>> {
    let settings = home::settings(home)?;
    let root = home::data_folder(home, &settings).join("profiles");
    if !root.try_exists()? {
        return Ok(None);
    }
    if let Some(id) = settings["currentProfile"].as_str()
        && valid_id(id)
        && disk::is_dir(&root.join(id))
    {
        return Ok(Some(root.join(id)));
    }
    Ok(list(home)?.first().map(|row| root.join(&row.id)))
}

pub fn current_dir(home: &Path) -> Result<PathBuf> {
    active_dir(home)?.context("Create a profile first.")
}

pub fn read(dir: &Path) -> Result<Value> {
    disk::require_dir(dir)?;
    let path = dir.join("profile.json");
    disk::require_file(&path)?;
    let v = home::read_json(&path)?;
    ensure!(v.is_object(), "profile.json must contain an object.");
    ensure!(
        v.get("v").is_none_or(|v| v.as_u64() == Some(1)),
        "This profile needs a newer launcher or has an unsupported format."
    );
    Ok(v)
}

// This file is shared with the builder and the mod tools. Publish the complete
// "<pid> <epoch seconds>" record at once, including on removable drives.
struct EditLock {
    path: PathBuf,
    token: String,
}
impl EditLock {
    fn take(dir: &Path) -> Result<Self> {
        use std::io::Write;
        let path = dir.join(".longshot-edit.lock");
        let token = format!("{} {}", std::process::id(), chrono::Utc::now().timestamp());
        let mut tmp = NamedTempFile::new_in(dir)?;
        tmp.write_all(token.as_bytes())?;
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            match tmp.persist_noclobber(&path) {
                Ok(_) => return Ok(Self { path, token }),
                Err(e) if e.error.kind() == std::io::ErrorKind::AlreadyExists => tmp = e.file,
                Err(e) => return Err(e).context("Could not lock profile.json for editing."),
            }
            // The builder expires abandoned edit locks after 30 seconds. A live
            // edit is a single JSON read/replace, never a build or save transfer.
            if let Ok(held) = fs::read_to_string(&path)
                && let Some(stamp) = held
                    .split_whitespace()
                    .nth(1)
                    .and_then(|s| s.parse::<i64>().ok())
                && chrono::Utc::now().timestamp().saturating_sub(stamp) > 30
            {
                let aside = tempfile::tempdir_in(dir)?;
                if fs::rename(&path, aside.path().join("stale")).is_ok() {
                    continue;
                }
            }
            ensure!(
                Instant::now() < deadline,
                "Another editor is changing this profile. Try again in a moment."
            );
            std::thread::sleep(Duration::from_millis(15));
        }
    }
}
impl Drop for EditLock {
    fn drop(&mut self) {
        if fs::read_to_string(&self.path).is_ok_and(|s| s == self.token) {
            let _ = fs::remove_file(&self.path);
        }
    }
}
pub fn patch_profile(dir: &Path, edit: impl FnOnce(&mut Map<String, Value>)) -> Result<()> {
    edit_profile(dir, |object| {
        edit(object);
        Ok(())
    })
}

pub(crate) fn edit_profile(
    dir: &Path,
    edit: impl FnOnce(&mut Map<String, Value>) -> Result<()>,
) -> Result<()> {
    with_edit_lock(dir, || {
        let mut value = read(dir)?;
        let object = value
            .as_object_mut()
            .context("profile.json must contain an object.")?;
        edit(object)?;
        home::write_json(&dir.join("profile.json"), &value)
    })
}

pub(crate) fn with_edit_lock<T>(dir: &Path, edit: impl FnOnce() -> Result<T>) -> Result<T> {
    disk::require_dir(dir)?;
    let _guard = EditLock::take(dir)?;
    edit()
}

pub(super) fn save_name<'a>(profile: &'a Value, slot: &str) -> Option<&'a str> {
    profile
        .get("save_names")?
        .get(slot)?
        .as_str()
        .filter(|name| !name.trim().is_empty())
}

pub(super) fn select_duplicate(dir: &Path, slot: &str, name: &str) -> Result<()> {
    edit_profile(dir, |profile| {
        let names = profile
            .entry("save_names")
            .or_insert_with(|| json!({}))
            .as_object_mut()
            .context("The save names in this profile cannot be read.")?;
        names.insert(slot.into(), json!(name));
        profile.insert("start_slot".into(), json!(slot));
        Ok(())
    })
}

pub(super) fn set_save_name(dir: &Path, slot: &str, name: &str) -> Result<()> {
    let name = name.trim();
    ensure!(saves::slot_name(slot), "Unknown save slot.");
    ensure!(
        name.chars().count() <= 64 && !name.chars().any(char::is_control),
        "Enter a save name of up to 64 characters."
    );
    edit_profile(dir, |profile| {
        if name.is_empty() && !profile.contains_key("save_names") {
            return Ok(());
        }
        let names = profile
            .entry("save_names")
            .or_insert_with(|| json!({}))
            .as_object_mut()
            .context("The save names in this profile cannot be read.")?;
        if name.is_empty() {
            names.remove(slot);
        } else {
            names.insert(slot.into(), json!(name));
        }
        Ok(())
    })
}

pub struct Profile {
    pub(crate) dir: PathBuf,
    pub id: String,
    pub file: Value,
    pub problem: Option<String>,
}

impl Profile {
    pub(crate) fn game_path(&self) -> Option<PathBuf> {
        let file = self.file["game"].as_str().filter(|s| !s.is_empty())?;
        Some(self.dir.join(file))
    }
    pub fn name(&self) -> &str {
        self.file["name"]
            .as_str()
            .filter(|s| !s.is_empty())
            .unwrap_or(&self.id)
    }
}

pub fn list(home: &Path) -> Result<Vec<Profile>> {
    let mut rows = Vec::new();
    for entry in fs::read_dir(home::data_dir(home)?.join("profiles"))? {
        let entry = entry?;
        let id = entry.file_name().to_string_lossy().into_owned();
        if !valid_id(&id) || !entry.file_type()?.is_dir() {
            continue;
        }
        let (value, problem) = match read(&entry.path()) {
            Ok(v) => (v, None),
            Err(e) => (json!({"name":id}), Some(e.to_string())),
        };
        rows.push(Profile {
            dir: entry.path(),
            id,
            file: value,
            problem,
        });
    }
    rows.sort_by(|a, b| (a.id != "default", &a.id).cmp(&(b.id != "default", &b.id)));
    Ok(rows)
}

pub(super) fn default_file(name: &str) -> Value {
    let (_, color) = COLORS[0];
    json!({"v":1,"name":name,"color":color})
}
/// Create the initial current-format profile. Existing profile directories belong to
/// the player, including any unreadable or newer metadata; the profile list can show them.
pub fn initialize(home: &Path) -> Result<()> {
    let root = home::data_dir(home)?.join("profiles");
    // An existing empty store is the onboarding state after the last profile was removed.
    if root.try_exists()? {
        return disk::require_dir(&root);
    }
    fs::create_dir_all(&root)?;
    disk::require_dir(&root)?;
    let default = root.join("default");
    match fs::symlink_metadata(&default) {
        Ok(_) => return disk::require_dir(&default),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => return Err(e.into()),
    }
    let stage = Builder::new().prefix(".creating-").tempdir_in(&root)?;
    fs::create_dir(stage.path().join("saves"))?;
    home::write_json(&stage.path().join("profile.json"), &default_file("Default"))?;
    match fs::rename(stage.path(), &default) {
        Ok(()) => Ok(()),
        Err(_) if disk::is_dir(&default) => Ok(()),
        Err(e) => Err(e.into()),
    }
}

pub fn new_or_rename(home: &Path, mode: &str, id: &str, name: &str) -> Result<()> {
    let name = name.trim();
    ensure!(
        !name.is_empty()
            && name.encode_utf16().count() <= 40
            && !name.chars().any(char::is_control),
        "Enter a profile name of 1–40 characters."
    );
    let rows = list(home)?;
    ensure!(
        !rows
            .iter()
            .any(|p| !(mode == "rename" && p.id == id)
                && p.name().to_lowercase() == name.to_lowercase()),
        "A profile already has that name."
    );
    if mode == "rename" {
        ensure!(valid_id(id), "Unknown profile.");
        return patch_profile(&home::data_dir(home)?.join("profiles").join(id), |o| {
            o.insert("name".into(), json!(name));
        });
    }
    ensure!(rows.len() < 16, "The launcher keeps up to 16 profiles.");
    let root = home::data_dir(home)?.join("profiles");
    let stage = Builder::new()
        .prefix(".creating-")
        .rand_bytes(12)
        .tempdir_in(&root)?;
    let mut file = default_file(name);
    if mode == "duplicate" {
        ensure!(valid_id(id), "Unknown profile.");
        let from = root.join(id);
        super::running::saves_idle(&[&from])?;
        file = read(&from)?;
        disk::copy_tree_filtered(&from, stage.path(), &|name| {
            name.to_str().is_none_or(|name| {
                name != ".longshot-save-transfer.lock" && !name.ends_with(".longshot-old")
            })
        })?;
        super::running::saves_idle(&[&from])?;
        file["name"] = json!(name);
        file["copy_of"] = json!(id);
        file.as_object_mut().unwrap().remove("last_played");
    } else if mode == "new" {
        // The builder's play::carry_saves treats an existing save directory
        // as an intentional profile choice, including when it has no slots.
        fs::create_dir_all(stage.path().join("saves/save"))?;
        if let Some(dir) = active_dir(home)?
            && let Some(game) = read(&dir)?.get("game")
        {
            file["game"] = game.clone();
        }
    } else {
        bail!("Unknown profile action.");
    }
    fs::create_dir_all(stage.path().join("saves"))?;
    home::write_json(&stage.path().join("profile.json"), &file)?;
    publish_new(&root, stage)?;
    Ok(())
}

pub(super) fn publish_new(root: &Path, stage: tempfile::TempDir) -> Result<String> {
    use sha2::{Digest, Sha256};
    let digest = Sha256::digest(stage.path().as_os_str().as_encoded_bytes());
    let id = format!(
        "p-{:02x}{:02x}{:02x}{:02x}",
        digest[0], digest[1], digest[2], digest[3]
    );
    ensure!(!root.join(&id).exists(), "Try creating this profile again.");
    fs::rename(stage.path(), root.join(&id))?;
    Ok(id)
}

/// Remove only the reviewed profile after verifying its complete local backup.
pub(super) fn remove(home: &Path, id: &str) -> Result<(PathBuf, usize)> {
    ensure!(valid_id(id), "Unknown profile.");
    let dir = home::data_dir(home)?.join("profiles").join(id);
    super::running::saves_idle(&[&dir])?;
    let kept = saves::backup(&dir, home, &format!("profile-{id}"))?;
    super::running::saves_idle(&[&dir])?;
    disk::remove_after_copy(&dir, &kept)?;
    let remaining = list(home)?;
    let selection = match remaining.as_slice() {
        [only] => Some(only.id.as_str()),
        _ => None,
    };
    home::patch_settings(home, &json!({"currentProfile":selection}))?;
    Ok((kept, remaining.len()))
}
