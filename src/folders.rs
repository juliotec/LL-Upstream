//! Change the location used for new work or move the existing launcher-owned data first.
pub(crate) mod mod_store;
#[cfg(test)]
mod tests;
mod tree;
pub(crate) use tree::publish as publish_directory;

use crate::{game, home, players, storage};
use anyhow::{Context, Result, bail, ensure};
use serde_json::{Value, json};
use std::{
    collections::HashSet,
    fs,
    path::{Path, PathBuf},
    sync::atomic::AtomicBool,
};

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Workspace,
    Versions,
    Data,
}

impl Kind {
    fn key(self) -> &'static str {
        match self {
            Self::Workspace => "work",
            Self::Versions => "buildsFolder",
            Self::Data => "dataFolder",
        }
    }
    fn path(self, root: &Path, settings: &Value) -> PathBuf {
        match self {
            Self::Workspace => home::work(root, settings),
            Self::Versions => game::install_root(root, settings),
            Self::Data => home::data_folder(root, settings),
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
struct Entry {
    old: PathBuf,
    new: PathBuf,
    version: bool,
}

pub(crate) struct Change {
    pub(crate) from: PathBuf,
    pub(crate) to: PathBuf,
    root: PathBuf,
    kind: Kind,
    selection: Value,
    entries: Vec<Entry>,
}

impl Change {
    pub(crate) fn prepare(home: &Path, key: &str, selected: Option<&Path>) -> Result<Self> {
        let kind = match key {
            "work" => Kind::Workspace,
            "buildsFolder" => Kind::Versions,
            "dataFolder" => Kind::Data,
            _ => bail!("Unknown folder setting."),
        };
        let mut settings = home::settings(home)?;
        let from = tree::absolute(&kind.path(home, &settings))?;
        let selection = match selected {
            Some(path) => json!(tree::absolute(path)?),
            None => Value::Null,
        };
        settings[key] = selection.clone();
        let to = tree::absolute(&kind.path(home, &settings))?;
        if tree::exists(&to)? {
            ensure!(players::is_dir(&to), "Choose a regular folder.");
        }
        let mut plan = Self {
            from,
            to,
            root: home.into(),
            kind,
            selection,
            entries: vec![],
        };
        plan.validate_location(&settings)?;
        plan.entries = plan.current_entries()?;
        Ok(plan)
    }

    pub(crate) fn has_data(&self) -> bool {
        !self.entries.is_empty() && (self.from != self.to || self.kind == Kind::Data)
    }

    fn validate_location(&self, settings: &Value) -> Result<()> {
        if self.from == self.to {
            return Ok(());
        }
        let home = tree::absolute(&self.root)?;
        if self.kind == Kind::Data {
            return self.validate_data_target(&home, settings);
        }
        // A library may contain unrelated folders. The ledger and profiles, game inputs and
        // the other folder setting must never become storage for a version or workspace.
        let spaces = storage::protected_paths(&self.root)?;
        let other = match self.kind {
            Kind::Workspace => Kind::Versions.path(&self.root, settings),
            Kind::Versions | Kind::Data => Kind::Workspace
                .path(&self.root, settings)
                .join("longshot-builder"),
        };
        let actual = if self.kind == Kind::Workspace {
            self.to.join("longshot-builder")
        } else {
            self.to.clone()
        };
        ensure!(
            actual != home && !home.starts_with(&actual),
            "Choose a folder outside the launcher’s own files."
        );
        for private in [
            "profiles",
            "save-backups",
            "version-locations",
            "logs",
            "builder-data",
        ] {
            ensure!(
                !overlap(&actual, &home.join(private)),
                "Choose a folder outside the launcher’s private data."
            );
        }
        let data = tree::absolute(&home::data_folder(&self.root, settings))?;
        for private in ["profiles", "mods"] {
            ensure!(
                !overlap(&actual, &data.join(private)),
                "Choose a folder outside installed mods and profiles."
            );
        }
        ensure!(
            !overlap(&actual, &tree::absolute(&other)?),
            "Keep installed versions and the cache in separate folders."
        );
        for path in spaces
            .iter()
            .chain(game::managed_folders(&self.root)?.iter())
        {
            if let Ok(path) = tree::absolute(path) {
                // A pre-existing library can contain managed builds. Their parent is a valid new library.
                ensure!(
                    !actual.starts_with(&path)
                        && !(self.kind == Kind::Workspace && path.starts_with(&actual)),
                    "Choose a folder outside existing versions, profiles and game inputs."
                );
            }
        }
        Ok(())
    }

    fn validate_data_target(&self, control: &Path, settings: &Value) -> Result<()> {
        // Moving back to the original home is supported. Only the owned children
        // below it are destinations; settings, locks, logs and workspace stay put.
        ensure!(
            self.to == home::data_folder(control, &Value::Null)
                || self.to == control
                || !control.starts_with(&self.to),
            "Choose a folder outside the launcher’s parent folders."
        );
        let cache = tree::absolute(&home::work(control, settings))?;
        for dest in [
            self.to.join("profiles"),
            self.to.join("versions"),
            self.to.join("mods"),
        ] {
            ensure!(
                !overlap(&dest, &cache),
                "Keep the data folder and cache in separate folders."
            );
            ensure!(
                !tree::exists(&dest)? || tree::absolute(&dest)? == dest && players::is_dir(&dest),
                "The destination contains a linked or invalid data folder."
            );
        }
        ensure!(
            !self.to.starts_with(&cache),
            "Choose a data folder outside the cache."
        );
        if self.to != control {
            for path in [
                "profiles",
                "versions",
                "logs",
                "save-backups",
                "builder-data",
                "version-locations",
            ] {
                ensure!(
                    !self.to.starts_with(control.join(path)),
                    "Choose a separate data folder."
                );
            }
        }
        let target_profiles = self.to.join("profiles");
        ensure!(
            !tree::exists(&target_profiles)? && !tree::exists(&self.to.join("mods"))?,
            "This location already contains profiles or mods. Choose another folder to keep both sets of data."
        );
        for old in [
            self.from.join("profiles"),
            self.from.join("mods"),
            self.from.join("versions"),
        ] {
            ensure!(!self.to.starts_with(old), "Choose a separate data folder.");
        }
        for protected in storage::protected_paths(&self.root)?
            .into_iter()
            .chain(game::managed_folders(&self.root)?)
        {
            if let Ok(actual) = tree::absolute(&protected) {
                ensure!(
                    !self.to.starts_with(&actual),
                    "Choose a folder outside existing game files."
                );
            }
        }
        Ok(())
    }

    fn current_entries(&self) -> Result<Vec<Entry>> {
        if self.kind == Kind::Workspace {
            let folder = self.from.join("longshot-builder");
            if !tree::exists(&folder)? {
                return Ok(vec![]);
            }
            ensure!(
                players::is_dir(&folder),
                "The builder workspace is linked or is not a directory."
            );
            if !tree::has_workspace_data(&folder)? {
                return Ok(vec![]);
            }
            return Ok(vec![Entry {
                old: folder,
                new: self.to.join("longshot-builder"),
                version: false,
            }]);
        }
        let mut result = Vec::new();
        let mut folders = game::managed_folders(&self.root)?;
        let data_versions = self.to.join("versions");
        folders.sort_by_key(|path| (path.parent() != Some(data_versions.as_path()), path.clone()));
        let mut names = HashSet::new();
        for folder in folders {
            let old = tree::absolute(&folder)?;
            if self.kind == Kind::Versions && old.parent() != Some(self.from.as_path()) {
                continue;
            }
            let name = old.file_name().context("Invalid version directory.")?;
            let parent = if self.kind == Kind::Data {
                self.to.join("versions")
            } else {
                self.to.clone()
            };
            let mut new = parent.join(name);
            for suffix in 2.. {
                if names.insert(new.clone()) {
                    break;
                }
                new = parent.join(format!("{}.instance-{suffix}", name.to_string_lossy()));
            }
            if old != new {
                result.push(Entry {
                    old,
                    new,
                    version: true,
                });
            }
        }
        if self.kind == Kind::Data && self.from != self.to {
            let mods = self.from.join("mods");
            let builder_mods =
                home::work(&self.root, &home::settings(&self.root)?).join("longshot-builder/mods");
            if tree::exists(&mods)? || tree::exists(&builder_mods)? {
                result.push(Entry {
                    old: mods,
                    new: self.to.join("mods"),
                    version: false,
                });
            }
            let old = self.from.join("profiles");
            if tree::exists(&old)? {
                ensure!(
                    players::is_dir(&old) && tree::absolute(&old)? == old,
                    "The active profiles folder is linked or invalid."
                );
                result.push(Entry {
                    old,
                    new: self.to.join("profiles"),
                    version: false,
                });
            }
        }
        Ok(result)
    }

    fn moved_registry(&self, original: &Value) -> Result<Value> {
        let mut moved = original.clone();
        let root = tree::absolute(&self.root)?;
        let relocate = |value: &mut Value, portable: bool| {
            let Some(old) = value.as_str().filter(|s| !s.is_empty()) else {
                return;
            };
            let Ok(old) = tree::absolute(&root.join(old)) else {
                return;
            };
            let Some(new) = self
                .entries
                .iter()
                .filter(|entry| entry.version)
                .find_map(|entry| {
                    old.strip_prefix(&entry.old).ok().map(|tail| {
                        let mut path = entry.new.clone();
                        path.extend(tail.iter());
                        path
                    })
                })
            else {
                return;
            };
            *value = json!(if portable {
                new.strip_prefix(&root).unwrap_or(&new)
            } else {
                new.as_path()
            });
        };
        for row in moved["builds"]
            .as_array_mut()
            .context("Invalid versions registry.")?
        {
            if let Some(folder) = row.get_mut("folder") {
                relocate(folder, false);
            }
            if let Some(previous) = row.get_mut("previousFolder") {
                relocate(previous, true);
            }
        }
        if let Some(retired) = moved
            .get_mut("retiredFolders")
            .and_then(Value::as_array_mut)
        {
            for folder in retired {
                relocate(folder, true);
            }
        }
        Ok(moved)
    }

    fn validate_entries(&self) -> Result<()> {
        let protected = storage::protected_paths(&self.root)?;
        for Entry { old, new, .. } in &self.entries {
            ensure!(
                !overlap(old, new),
                "Choose separate source and destination folders."
            );
            ensure!(
                !tree::exists(new)?,
                "{} already exists. Select another location or keep the existing data.",
                home::display(new)
            );
            for path in &protected {
                if let Ok(path) = tree::absolute(path) {
                    ensure!(
                        !overlap(old, &path) && !overlap(new, &path),
                        "A version or game source at {} must stay where it is.",
                        home::display(&path)
                    );
                }
            }
        }
        Ok(())
    }

    fn mod_caches(&self, cache: &Path, data: &Path, settings: &Value) -> Result<Vec<PathBuf>> {
        let mut mod_caches = Vec::new();
        players::ensure_paths_idle(&[cache])?;
        mod_store::ensure(cache, data)?;
        mod_caches.push(cache.to_owned());
        // A previously used cache can still be selected again. Retarget its
        // existing alias only when it actually points into this data folder.
        let store = fs::canonicalize(data.join("mods"))?;
        let default_work = home::work(&self.root, &Value::Null);
        for path in settings["earlierWork"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
            .map(Path::new)
            .chain(std::iter::once(default_work.as_path()))
        {
            let earlier = path.join("longshot-builder");
            if earlier.is_absolute()
                && !mod_caches.contains(&earlier)
                && fs::symlink_metadata(earlier.join("mods")).is_ok_and(|m| storage::is_link(&m))
                && fs::canonicalize(earlier.join("mods")).is_ok_and(|path| path == store)
            {
                players::ensure_paths_idle(&[&earlier])?;
                mod_caches.push(earlier);
            }
        }
        Ok(mod_caches)
    }

    pub(crate) fn apply(&self, move_existing: bool, cancelled: &AtomicBool) -> Result<String> {
        tree::running(cancelled)?;
        let settings = home::settings(&self.root)?;
        ensure!(
            tree::absolute(&self.kind.path(&self.root, &settings))? == self.from,
            "The configured folder changed. Review the locations again."
        );
        self.validate_location(&settings)?;
        let save = || {
            let mut patch = json!({(self.kind.key()): self.selection});
            if self.kind == Kind::Data {
                patch["buildsFolder"] = Value::Null;
            }
            home::patch_settings(&self.root, &patch)
        };
        let cache = home::work(&self.root, &settings).join("longshot-builder");
        let data = home::data_folder(&self.root, &settings);
        let _lease = game::move_lease(&self.root)?;
        if self.kind == Kind::Workspace
            && self.from != self.to
            && tree::exists(&cache.join("mods"))?
        {
            players::ensure_paths_idle(&[&cache])?;
            mod_store::ensure(&cache, &data)?;
        }
        if self.kind != Kind::Data && (self.from == self.to || !move_existing) {
            if self.kind == Kind::Workspace
                && self.from != self.to
                && tree::exists(&data.join("mods"))?
            {
                mod_store::ensure(&self.to.join("longshot-builder"), &data)?;
            }
            save()?;
            return Ok("Folder updated.".into());
        }
        ensure!(
            self.current_entries()? == self.entries,
            "The contents changed. Review the move again."
        );
        ensure!(
            self.kind != Kind::Data || move_existing || self.entries.is_empty(),
            "Move existing launcher data or keep the current folder."
        );
        let move_mods = self.kind == Kind::Data
            && self
                .entries
                .iter()
                .any(|entry| entry.old == self.from.join("mods"));
        if self.entries.is_empty() {
            save()?;
            return Ok("Folder updated.".into());
        }
        let affected: Vec<_> = self
            .entries
            .iter()
            .flat_map(|entry| [entry.old.as_path(), entry.new.as_path()])
            .collect();
        players::ensure_paths_idle(&affected)?;
        self.validate_entries()?;
        let mod_caches = if move_mods {
            self.mod_caches(&cache, &data, &settings)?
        } else {
            Vec::new()
        };
        fs::create_dir_all(&self.to)?;
        ensure!(
            tree::absolute(&self.to)? == self.to,
            "The destination changed. Choose it again."
        );
        let stage = tempfile::Builder::new()
            .prefix(".longshot-move-")
            .tempdir_in(&self.to)?;
        let mut locks = Vec::new();
        let mut hardlinks = std::collections::HashMap::new();
        for (index, Entry { old, new, .. }) in self.entries.iter().enumerate() {
            let copied = stage.path().join(index.to_string());
            tree::copy(
                old,
                &copied,
                new,
                &self.entries,
                cancelled,
                &mut locks,
                &mut hardlinks,
            )
            .with_context(|| format!("Could not copy {}.", home::display(old)))?;
            ensure!(
                tree::same(old, &copied, old, new, &self.entries, false, cancelled)?,
                "Files changed during the move. The original folder has been kept."
            );
        }
        tree::running(cancelled)?;
        players::ensure_paths_idle(&affected)?;
        ensure!(
            self.current_entries()? == self.entries,
            "The contents changed. Try the move again."
        );
        self.validate_entries()?;
        let mut published = Vec::new();
        let mut records = Vec::new();
        let registry_path = self.root.join(game::registry::OWN);
        let original_registry = if self.kind != Kind::Workspace {
            Some(game::registry::read(&self.root, game::registry::OWN)?)
        } else {
            None
        };
        let mut shortcuts = Vec::new();
        for entry in self.entries.iter().filter(|e| e.version) {
            let moved = if self.kind == Kind::Data {
                game::shortcuts_to_move_data(
                    &self.root,
                    &entry.old,
                    &entry.new,
                    &self.to.join("profiles"),
                )?
            } else {
                game::shortcuts_to_move(&self.root, &entry.old, &entry.new)?
            };
            shortcuts.extend(moved);
        }
        tree::running(cancelled)?;
        let mut attempted = 0;
        let mut wrote_registry = false;
        let mut relinked_mods = Vec::new();
        // The originals stay intact through publication and both metadata changes. Cancel
        // before this short commit or finish the complete transaction.
        let commit = (|| -> Result<()> {
            for (index, entry) in self.entries.iter().enumerate() {
                let parent = entry.new.parent().context("Invalid destination folder.")?;
                fs::create_dir_all(parent)?;
                ensure!(
                    tree::absolute(parent)? == parent,
                    "The destination changed. Choose it again."
                );
                let staged = stage.path().join(index.to_string());
                tree::publish(&staged, &entry.new)?;
                published.push((&entry.new, staged));
                if entry.version
                    && let Some(record) = game::record_moved_version(&self.root, &entry.new)?
                {
                    records.push(record);
                }
            }
            for shortcut in &shortcuts {
                attempted += 1;
                shortcut.retarget(true)?;
            }
            if let Some(original) = &original_registry {
                let moved = self.moved_registry(original)?;
                if moved != *original {
                    home::write_json(&registry_path, &moved)?;
                    wrote_registry = true;
                }
            }
            for cache in &mod_caches {
                mod_store::repoint(cache, &data, &self.to)?;
                relinked_mods.push(cache);
            }
            save()?;
            Ok(())
        })();
        if let Err(err) = commit {
            for cache in relinked_mods.into_iter().rev() {
                mod_store::repoint(cache, &self.to, &data)
                    .context("Both folders were kept because the shared mod store link could not be restored.")?;
            }
            if wrote_registry {
                home::write_json(&registry_path, original_registry.as_ref().unwrap()).context(
                    "The original folders were kept. Restoring the version list failed.",
                )?;
            }
            for shortcut in shortcuts[..attempted].iter().rev() {
                shortcut.retarget(false).context("The Steam shortcut could not be restored. Both version folders have been kept.")?;
            }
            for record in records {
                fs::remove_file(record)?;
            }
            for (path, staged) in published.into_iter().rev() {
                tree::publish(path, &staged)?;
            }
            return Err(err);
        }
        // Keep the completed destination even if a third party changes or locks an original
        // after publication. Never delete the previous copy before verifying its contents.
        let mut retained = Vec::new();
        for Entry { old, new, version } in &self.entries {
            match retire(old, new, *version, &self.entries) {
                Ok(()) if *version => game::forget_moved_version(&self.root, old),
                Ok(()) => (),
                Err(e) => retained.push(format!("{} ({e:#})", home::display(old))),
            }
        }
        Ok(if retained.is_empty() {
            format!("Data moved to {}.", home::display(&self.to))
        } else {
            format!(
                "The new folder is active. Original data was kept: {}",
                retained.join(", ")
            )
        })
    }
}

use crate::fsx::overlaps as overlap;

fn retire(old: &Path, new: &Path, version: bool, entries: &[Entry]) -> Result<()> {
    let parent = old.parent().context("Missing source folder.")?;
    let hold = tempfile::Builder::new()
        .prefix(".longshot-previous-")
        .tempdir_in(parent)?;
    let parked = hold
        .path()
        .join(old.file_name().context("Invalid source folder.")?);
    tree::publish(old, &parked)?;
    // From now on, errors must leave the original recoverable, including an unexpected exit.
    let holding = hold.keep();
    let verified = tree::same(
        &parked,
        new,
        old,
        new,
        entries,
        version,
        &AtomicBool::new(false),
    );
    if !matches!(verified, Ok(true)) {
        tree::publish(&parked, old)
            .with_context(|| format!("The original was kept in {}.", home::display(&parked)))?;
        fs::remove_dir(holding)?;
        verified?;
        bail!("The source changed during the move.");
    }
    fs::remove_dir_all(&holding)
        .with_context(|| format!("The remaining original is in {}.", home::display(&holding)))
}
