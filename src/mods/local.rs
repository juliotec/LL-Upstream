//! Basic profile folder mods for builders which do not ship the mod CLI.
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
};
use tempfile::NamedTempFile;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Import {
    profile: PathBuf,
    source: PathBuf,
    destination: PathBuf,
    name: String,
    id: String,
    parts: Vec<&'static str>,
}

pub(crate) fn review(profile: &Path, source: &Path) -> Result<Import> {
    players::require_dir(source)?;
    let mods = profile.join("mods");
    ensure!(
        source.is_absolute() && (!source.starts_with(&mods) || source.parent() == Some(&mods)),
        "Choose an extracted mod folder or a direct child of the profile’s mods folder."
    );
    ensure!(
        !source.join("mod.json").try_exists()?,
        "This folder already has a mod manifest."
    );
    let parts: Vec<_> = ["romfs", "exefs"]
        .into_iter()
        .filter(|part| players::is_dir(&source.join(part)))
        .collect();
    ensure!(
        !parts.is_empty(),
        "Choose a folder with romfs or exefs inside."
    );
    let name = source
        .file_name()
        .and_then(|s| s.to_str())
        .context("The mod needs a folder name.")?
        .to_owned();
    let slug = super::stable::slug(&name);
    let destination = if source.parent() == Some(&mods) {
        ensure!(
            !name.starts_with(['.', '-', '_']),
            "Choose a visible mod folder."
        );
        // In-place manifests make the original files live. Ignore sources that also contain hooks or code entry points.
        ensure!(
            !source.join("sites.toml").try_exists()?,
            "Use a builder with mod tools to review hook mods."
        );
        source.to_owned()
    } else {
        let target_parent = profile.canonicalize()?;
        for part in &parts {
            ensure!(
                !target_parent.starts_with(source.join(part).canonicalize()?),
                "Choose a source outside the destination data folder."
            );
        }
        let dest = mods.join(format!("local~{slug}"));
        ensure!(
            !dest.try_exists()?,
            "This profile already contains a folder for {name}."
        );
        dest
    };
    Ok(Import {
        profile: profile.into(),
        source: source.into(),
        destination,
        name,
        id: format!("local/{slug}"),
        parts,
    })
}

impl Import {
    pub(crate) fn has_code(&self) -> bool {
        self.parts.contains(&"exefs")
    }

    pub(crate) fn apply(&self, profile: &Path) -> Result<()> {
        ensure!(
            self.profile == profile && review(profile, &self.source)? == *self,
            "The mod or profile changed. Review it again."
        );
        let mods = profile.join("mods");
        fs::create_dir_all(&mods)?;
        players::require_dir(&mods)?;
        let manifest = self.manifest();
        let stage = if self.source != self.destination {
            let stage = tempfile::tempdir_in(&mods)?;
            for part in &self.parts {
                players::copy_tree(&self.source.join(part), &stage.path().join(part))?;
            }
            home::write_json(&stage.path().join("mod.json"), &manifest)?;
            Some(stage)
        } else {
            None
        };
        players::with_edit_lock(profile, || {
            ensure!(
                review(profile, &self.source)? == *self,
                "The mod changed. Review it again."
            );
            let prior = players::read_profile(profile)?;
            let mut updated = prior.clone();
            let pins = updated
                .as_object_mut()
                .context("Invalid profile")?
                .entry("mods")
                .or_insert_with(|| json!([]))
                .as_array_mut()
                .context("Invalid profile mod list.")?;
            let relative = relative_folder(profile, &self.destination)?;
            let pin_path = format!("mods/{relative}");
            let existing = pins
                .iter()
                .find(|v| v["id"] == self.id || v["path"] == pin_path);
            if let Some(pin) = existing {
                ensure!(
                    pin["id"] == self.id && pin["path"] == pin_path,
                    "Another mod already uses this ID or folder."
                );
            }
            let enabled = existing.is_none_or(|pin| pin["on"] != false);
            if existing.is_none() {
                pins.push(json!({"id":self.id,"path":pin_path,"on":true}));
            }
            for other in Inventory::read(profile)?.manifests {
                ensure!(
                    home::read_json(&other.join("mod.json"))
                        .ok()
                        .is_none_or(|v| v["id"] != self.id),
                    "A different mod uses this mod ID."
                );
            }
            if self.parts.contains(&"exefs") || !enabled {
                let mut switches = Switches::load(profile)?;
                switches.set(&relative, false, enabled)?;
                switches.save(profile)?;
            }
            let settings = profile.join("profile.json");
            home::write_json(&settings, &updated)?;
            let published = (|| -> Result<()> {
                if let Some(stage) = &stage {
                    crate::fsx::rename_new(stage.path(), &self.destination)?;
                } else {
                    let mut manifest_file = NamedTempFile::new_in(&self.source)?;
                    serde_json::to_writer_pretty(&mut manifest_file, &manifest)?;
                    manifest_file.flush()?;
                    manifest_file.as_file().sync_all()?;
                    manifest_file.persist_noclobber(self.source.join("mod.json"))?;
                }
                Ok(())
            })();
            if published.is_err() {
                home::write_json(&settings, &prior)?;
            }
            published
        })
    }

    fn manifest(&self) -> Value {
        let mut manifest = json!({"id":self.id,"name":self.name,"version":"1.0.0"});
        if self.parts.contains(&"exefs") {
            manifest["permissions"] = json!(["exefs"]);
        }
        manifest
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Approval {
    folder: PathBuf,
    id: String,
    name: String,
    hash: String,
}

impl Approval {
    pub(super) fn name(&self) -> &str {
        &self.name
    }

    pub(super) fn description(&self) -> String {
        let Self { name, id, hash, .. } = self;
        format!(
            "{name} ({id})\n\nThis mod changes executable game code. Approve its ExeFS patches only if you trust the author. Changing the code requires approval again.\n\nRequired permission\nexefs\n\nCode SHA-256\n{hash}"
        )
    }

    pub(super) fn apply(&self, profile: &Path) -> Result<()> {
        players::with_edit_lock(profile, || {
            let mut switches = Switches::load(profile)?;
            ensure!(
                scan(profile, &mut switches)?.contains(self),
                "This mod or its code changed. Review it again."
            );
            let relative = relative_folder(profile, &self.folder)?;
            let row = switches.set(&relative, true, false)?;
            row["launcherPatchHash"] = json!(self.hash);
            switches.save(profile)
        })
    }
}

// Nightlies without the mod CLI read mods.json by the physical folder's name. Keep
// their switch off while an ExeFS patch is awaiting review. The modern builder
// keeps its own grants in trust.json and reviews the mod under its own rules.
struct Switches {
    doc: Value,
    dirty: bool,
}

impl Switches {
    fn load(profile: &Path) -> Result<Self> {
        let path = profile.join("mods.json");
        let doc = if path.try_exists()? {
            ensure!(
                players::is_file(&path) && path.metadata()?.len() < 1 << 20,
                "Invalid local mod switches."
            );
            home::read_json(&path)?
        } else {
            json!({"mods":[]})
        };
        ensure!(doc["mods"].is_array(), "Invalid local mod switches.");
        Ok(Self { doc, dirty: false })
    }

    fn get(&self, relative: &str) -> Option<&Value> {
        let id = format!("local/{relative}");
        self.doc["mods"].as_array()?.iter().find(|r| r["id"] == id)
    }

    fn set(&mut self, relative: &str, on: bool, waiting: bool) -> Result<&mut Value> {
        let id = format!("local/{relative}");
        let rows = self.doc["mods"]
            .as_array_mut()
            .context("Invalid local mod switches.")?;
        let index = rows.iter().position(|r| r["id"] == id).unwrap_or_else(|| {
            rows.push(json!({"id":id}));
            rows.len() - 1
        });
        let row = &mut rows[index];
        row["enabled"] = json!(on);
        if waiting {
            row["launcherAwaitingApproval"] = json!(true);
        } else if let Some(r) = row.as_object_mut() {
            r.remove("launcherAwaitingApproval");
        }
        self.dirty = true;
        Ok(row)
    }

    fn save(self, profile: &Path) -> Result<()> {
        if self.dirty {
            ensure!(
                serde_json::to_vec_pretty(&self.doc)?.len() + 1 < 1 << 20,
                "Too many mod switches for this builder."
            );
            home::write_json(&profile.join("mods.json"), &self.doc)?;
        }
        Ok(())
    }
}

fn relative_folder(profile: &Path, folder: &Path) -> Result<String> {
    let relative = folder.strip_prefix(profile.join("mods"))?;
    ensure!(
        !relative.as_os_str().is_empty()
            && relative
                .components()
                .all(|c| matches!(c, std::path::Component::Normal(_))),
        "Invalid mod folder."
    );
    Ok(relative
        .to_str()
        .context("Invalid mod folder name.")?
        .replace(std::path::MAIN_SEPARATOR, "/"))
}

// Exactly one mod's contribution to nightly 836's play::patches_stamp: mod path,
// patch name, then the binary SHA-256 of the patch, in filename order.
fn exefs_hash(folder: &Path, relative: &str) -> Result<Option<String>> {
    let exefs = folder.join("exefs");
    if !exefs.try_exists()? {
        return Ok(None);
    }
    players::require_dir(&exefs)?;
    let mut patches = Vec::new();
    for entry in fs::read_dir(exefs)? {
        let entry = entry?;
        let name = entry
            .file_name()
            .into_string()
            .map_err(|_| anyhow::anyhow!("Invalid patch filename."))?;
        let lower = name.to_ascii_lowercase();
        if (lower.ends_with(".ips") || lower.ends_with(".pchtxt")) && entry.path().is_file() {
            ensure!(
                players::is_file(&entry.path()),
                "Linked patch files cannot be reviewed."
            );
            patches.push((name, entry.path()));
        }
    }
    if patches.is_empty() {
        return Ok(None);
    }
    patches.sort();
    let mut stamp = Sha256::new();
    for (name, path) in patches {
        let mut file = fs::File::open(path)?;
        let mut content = Sha256::new();
        std::io::copy(&mut file, &mut content)?;
        stamp.update(format!("{relative}\t{name}\t"));
        stamp.update(content.finalize());
        stamp.update(b"\n");
    }
    Ok(Some(format!("{:x}", stamp.finalize())))
}

#[derive(Default)]
struct Inventory {
    manifests: Vec<PathBuf>,
    loose: Vec<Import>,
}

impl Inventory {
    fn read(profile: &Path) -> Result<Self> {
        let root = profile.join("mods");
        let mut inventory = Self::default();
        if root.try_exists()? {
            players::require_dir(&root)?;
            inventory.walk(profile, &root, 0)?;
        }
        inventory.manifests.sort();
        inventory.loose.sort_by(|a, b| a.source.cmp(&b.source));
        Ok(inventory)
    }

    fn walk(&mut self, profile: &Path, dir: &Path, depth: u8) -> Result<()> {
        if depth > 0 && dir.join("mod.json").is_file() {
            self.manifests.push(dir.to_owned());
            return Ok(());
        }
        if depth == 1
            && let Ok(import) = review(profile, dir)
        {
            self.loose.push(import);
            return Ok(());
        }
        if depth == 4 {
            return Ok(());
        }
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            if entry
                .file_name()
                .to_string_lossy()
                .starts_with(['-', '_', '.'])
                || crate::fsx::is_link(&fs::symlink_metadata(entry.path())?)
                || !entry.path().is_dir()
            {
                continue;
            }
            players::require_dir(&entry.path())?;
            self.walk(profile, &entry.path(), depth + 1)?;
        }
        Ok(())
    }
}

fn pin<'a>(doc: &'a Value, relative: &str) -> Option<&'a Value> {
    let path = format!("mods/{relative}");
    doc["mods"].as_array()?.iter().find(|r| r["path"] == path)
}

fn requested(pin: Option<&Value>, switch: Option<&Value>) -> bool {
    pin.is_none_or(|p| p["on"] != false)
        && switch.is_none_or(|s| s["enabled"] != false || s["launcherAwaitingApproval"] == true)
}

pub(crate) fn installed_rows(profile: &Path) -> Result<Vec<Value>> {
    let inventory = Inventory::read(profile)?;
    if inventory.manifests.is_empty() && inventory.loose.is_empty() {
        return Ok(vec![]);
    }
    let switches = Switches::load(profile)?;
    let doc = home::read_json(&profile.join("profile.json"))?;
    let mut rows = Vec::new();
    for folder in inventory.manifests {
        let relative = relative_folder(profile, &folder)?;
        let manifest = home::read_json(&folder.join("mod.json")).unwrap_or(Value::Null);
        let id = manifest["id"]
            .as_str()
            .map(str::to_owned)
            .unwrap_or_else(|| format!("local/{relative}"));
        let title = manifest["name"].as_str().unwrap_or(&relative);
        rows.push(json!({"id":id,"name":title,"folder":folder,"version":manifest["version"],
          "path":format!("mods/{relative}"),"on":requested(pin(&doc,&relative),switches.get(&relative)),"kind":"Local mod"}));
    }
    for import in inventory.loose {
        rows.push(json!({"name":import.name,"folder":import.source,"loose":true,"on":false,"kind":"Local mod","summary":"Import this folder to use it in this profile."}));
    }
    Ok(rows)
}

fn scan(profile: &Path, switches: &mut Switches) -> Result<Vec<Approval>> {
    let inventory = Inventory::read(profile)?;
    let profile_doc = home::read_json(&profile.join("profile.json"))?;
    let mut approvals = Vec::new();
    for path in inventory.manifests {
        let relative = relative_folder(profile, &path)?;
        let pin = pin(&profile_doc, &relative);
        let switch = switches.get(&relative);
        let enabled = switch.is_none_or(|r| r["enabled"] != false);
        let waiting = switch.is_some_and(|r| r["launcherAwaitingApproval"] == true);
        if pin.is_some_and(|r| r["on"] == false) {
            if enabled || waiting {
                switches.set(&relative, false, false)?;
            }
            continue;
        }
        if !enabled && !waiting {
            continue;
        }
        let Some(hash) = exefs_hash(&path, &relative)? else {
            if waiting {
                switches.set(&relative, true, false)?;
            }
            continue;
        };
        if switch.is_some_and(|r| r["launcherPatchHash"] == hash) {
            if !enabled {
                switches.set(&relative, true, false)?;
            }
            continue;
        }
        if enabled || !waiting {
            switches.set(&relative, false, true)?;
        }
        let manifest = home::read_json(&path.join("mod.json")).unwrap_or(Value::Null);
        let name = manifest["name"].as_str().unwrap_or(&relative).to_owned();
        approvals.push(Approval {
            folder: path,
            id: format!("local/{relative}"),
            name,
            hash,
        });
    }
    Ok(approvals)
}

pub(super) fn pending(profile: &Path) -> Result<Vec<Approval>> {
    let mods = profile.join("mods");
    if !mods.try_exists()? {
        return Ok(Vec::new());
    }
    players::require_dir(&mods)?;
    // Register loose folders first. ExeFS stays off until the content hash is approved.
    for import in Inventory::read(profile)?.loose {
        import.apply(profile)?;
    }
    players::with_edit_lock(profile, || {
        let mut switches = Switches::load(profile)?;
        let approvals = scan(profile, &mut switches)?;
        switches.save(profile)?;
        Ok(approvals)
    })
}

pub(crate) fn set_switch(profile: &Path, folder: &Path, on: bool) -> Result<()> {
    let relative = relative_folder(profile, folder)?;
    players::require_dir(folder)?;
    players::edit_profile(profile, |doc| {
        let current = Inventory::read(profile)?;
        ensure!(
            current.manifests.contains(&folder.to_owned()),
            "This mod is no longer in the profile."
        );
        let path = format!("mods/{relative}");
        let mut switches = Switches::load(profile)?;
        let pins = doc
            .entry("mods")
            .or_insert_with(|| json!([]))
            .as_array_mut()
            .context("Invalid profile mod list.")?;
        let pin = pins.iter_mut().find(|p| p["path"] == path);
        ensure!(
            requested(pin.as_deref(), switches.get(&relative)) != on,
            "This mod’s setting changed. Open Mods again."
        );
        let waiting = on
            && exefs_hash(folder, &relative)?.is_some_and(|h| {
                switches
                    .get(&relative)
                    .is_none_or(|r| r["launcherPatchHash"] != h)
            });
        switches.set(&relative, on && !waiting, waiting)?;
        switches.save(profile)?;
        if let Some(pin) = pin {
            pin["on"] = json!(on);
        } else if let Some(id) = home::read_json(&folder.join("mod.json"))
            .ok()
            .and_then(|m| m["id"].as_str().map(str::to_owned))
            && !pins.iter().any(|p| p["id"] == id)
        {
            pins.push(json!({"id":id,"path":path,"on":on}));
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profile(root: &Path) -> Result<PathBuf> {
        let profile = root.join("profiles/default");
        fs::create_dir_all(profile.join("mods"))?;
        home::write_json(&profile.join("profile.json"), &json!({"v":1,"mods":[]}))?;
        Ok(profile)
    }

    #[test]
    fn old_builder_import_is_off_until_reviewed_and_edits_need_fresh_approval() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let profile = profile(temp.path())?;
        let folder = profile.join("mods/Skull Kid");
        fs::create_dir_all(folder.join("exefs"))?;
        fs::create_dir_all(folder.join("romfs"))?;
        let patch = folder.join("exefs/demo.ips");
        fs::write(&patch, b"PATCHEOF")?;
        fs::write(folder.with_extension("zip"), b"an adjacent archive")?;
        let grants = pending(&profile)?;
        assert_eq!(
            home::read_json(&folder.join("mod.json"))?["id"],
            "local/skull-kid"
        );
        let on = || -> Result<Value> {
            Ok(home::read_json(&profile.join("mods.json"))?["mods"][0]["enabled"].clone())
        };
        assert_eq!(on()?, false);
        assert_eq!(grants.len(), 1);
        let expected = {
            let mut h = Sha256::new();
            h.update(b"Skull Kid\tdemo.ips\t");
            h.update(Sha256::digest(b"PATCHEOF"));
            h.update(b"\n");
            format!("{:x}", h.finalize())
        };
        assert!(grants[0].description().contains(&expected));
        fs::write(&patch, b"PATCHxxxxEOF")?;
        assert!(grants[0].apply(&profile).is_err());
        assert_eq!(on()?, false);
        pending(&profile)?[0].apply(&profile)?;
        assert_eq!(on()?, true);
        assert!(pending(&profile)?.is_empty());
        fs::write(&patch, b"another edit")?;
        assert_eq!(pending(&profile)?.len(), 1);
        assert_eq!(on()?, false);
        set_switch(&profile, &folder, false)?;
        assert!(pending(&profile)?.is_empty());
        set_switch(&profile, &folder, true)?;
        assert_eq!(on()?, false);
        assert_eq!(pending(&profile)?.len(), 1);
        assert_eq!(
            fs::read(folder.with_extension("zip"))?,
            b"an adjacent archive"
        );
        Ok(())
    }

    #[test]
    fn unpinned_nested_manifest_uses_the_builders_physical_switch() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let profile = profile(temp.path())?;
        let folder = profile.join("mods/collection/example");
        fs::create_dir_all(folder.join("exefs"))?;
        fs::write(folder.join("mod.json"), b"{}")?;
        fs::write(folder.join("exefs/example.ips"), b"PATCHEOF")?;
        let rows = installed_rows(&profile)?;
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0]["id"], "local/collection/example");
        assert_eq!(rows[0]["on"], true);
        let approvals = pending(&profile)?;
        assert_eq!(approvals.len(), 1);
        set_switch(&profile, &folder, false)?;
        assert!(pending(&profile)?.is_empty());
        assert!(approvals[0].apply(&profile).is_err());
        assert_eq!(installed_rows(&profile)?[0]["on"], false);
        set_switch(&profile, &folder, true)?;
        pending(&profile)?[0].apply(&profile)?;
        assert_eq!(installed_rows(&profile)?[0]["on"], true);
        assert_eq!(
            home::read_json(&profile.join("mods.json"))?["mods"][0]["id"],
            "local/collection/example"
        );
        Ok(())
    }

    #[test]
    fn external_romfs_folder_is_copied_only_after_review() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let profile = profile(temp.path())?;
        let folder = temp.path().join("Demo");
        fs::create_dir_all(folder.join("romfs"))?;
        fs::write(folder.join("romfs/asset"), b"synthetic asset")?;
        let review = review(&profile, &folder)?;
        let dest = profile.join("mods/local~demo");
        assert!(!dest.exists());
        review.apply(&profile)?;
        assert_eq!(fs::read(dest.join("romfs/asset"))?, b"synthetic asset");
        assert!(!folder.join("mod.json").exists());
        assert!(pending(&profile)?.is_empty());
        set_switch(&profile, &dest, false)?;
        assert_eq!(
            home::read_json(&profile.join("mods.json"))?["mods"][0],
            json!({"id":"local/local~demo","enabled":false})
        );
        Ok(())
    }
}
