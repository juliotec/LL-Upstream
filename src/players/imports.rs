use super::{caption, disk, profiles, saves};
use anyhow::{Context, Result, bail, ensure};
use std::{
    fs,
    path::{Path, PathBuf},
};

pub(super) struct Location {
    pub label: String,
    pub path: PathBuf,
    pub slots: Vec<PathBuf>,
}

impl Location {
    pub fn from_folder(label: &str, path: PathBuf) -> Result<Self> {
        let slots = saves::slots(&path)?;
        Ok(Self {
            label: label.into(),
            path,
            slots,
        })
    }
}

pub(super) fn detected(home: &Path) -> Result<Vec<Location>> {
    let mut locations = Vec::new();
    for user in super::stable::sources(home)? {
        for path in saves::find(&user.join("save"))? {
            locations.push(Location::from_folder("Longshot stable", path)?);
        }
    }
    for (group, label) in [
        ("hookshot", "Hookshot"),
        ("nx2navi", "NX2navi"),
        ("lichii", "Lichii"),
        ("eden", "Eden"),
        ("eglantine", "Eglantine"),
    ] {
        for path in saves::sources(group)? {
            locations.push(Location::from_folder(label, path)?);
        }
    }
    Ok(locations)
}

const PLAYABLE_SLOTS: usize = 6;

pub(super) fn playable_slot(id: &str) -> bool {
    saves::slot_name(id) && id[5..].parse::<usize>().is_ok_and(|n| n < PLAYABLE_SLOTS)
}

pub(super) fn free_slot(root: &Path) -> Result<String> {
    for n in 0..PLAYABLE_SLOTS {
        let id = format!("slot_{n:02}");
        match fs::symlink_metadata(root.join(&id)) {
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => return Ok(id),
            Err(err) => return Err(err.into()),
            Ok(_) => (),
        }
    }
    bail!("This profile has six saves. Choose another profile or free a slot first.")
}

pub(super) fn import_slot(source: &Path, target: &Path, id: &str, home: &Path) -> Result<()> {
    ensure!(saves::slot_name(id), "Unknown save slot.");
    saves::pending(source)?;
    super::running::saves_idle(&[source, target])?;
    disk::disjoint(source, &target.join(id))?;
    let temp = tempfile::tempdir()?;
    let stage = temp.path().join(id);
    if source
        .file_name()
        .is_some_and(|name| name == "progress.sav")
    {
        saves::validate_file(source)?;
        fs::create_dir(&stage)?;
        disk::copy_new(source, &stage.join("progress.sav"))?;
    } else {
        saves::validate_slot(source)?;
        disk::copy_tree(source, &stage)?;
    }
    super::running::saves_idle(&[source, target])?;
    saves::merge_slots(temp.path(), target, home, true)
}

pub(super) fn duplicate(profile: &Path, id: &str, displayed: &str) -> Result<String> {
    ensure!(saves::slot_name(id), "Unknown save slot.");
    super::running::saves_idle(&[profile])?;
    let root = saves::slots_dir(profile)?;
    saves::pending(&root)?;
    let _lock = saves::SaveLock::acquire(&root)?;
    let source = root.join(id);
    disk::require_file(&source.join("progress.sav"))?;
    let free = free_slot(&root)?;
    let data = profiles::read(profile)?;
    let mut names = Vec::new();
    for path in saves::slots(&root)? {
        let name = path
            .file_name()
            .and_then(|s| s.to_str())
            .context("Invalid slot name.")?;
        if let Some(alias) = profiles::save_name(&data, name) {
            names.push(alias.to_lowercase());
        } else if let Some(caption) = caption::read(&path.join("caption.sav"), None) {
            names.push(caption.location.to_lowercase());
        }
    }
    let title = (1..)
        .map(|i| {
            let suffix = if i == 1 {
                " (copy)".into()
            } else {
                format!(" (copy {i})")
            };
            let base: String = displayed.chars().take(64 - suffix.len()).collect();
            format!("{}{suffix}", base.trim_end())
        })
        .find(|s| !names.contains(&s.to_lowercase()))
        .context("No free save name.")?;
    let stage = tempfile::tempdir_in(&root)?;
    let staged = stage.path().join(&free);
    disk::copy_tree(&source, &staged)?;
    super::running::saves_idle(&[profile])?;
    let destination = root.join(&free);
    ensure!(
        !destination.try_exists()?,
        "The destination is now occupied."
    );
    fs::rename(&staged, &destination)?;
    if let Err(err) = profiles::select_duplicate(profile, &free, &title) {
        fs::rename(&destination, &staged)
            .context("The save was copied, but its display name could not be saved.")?;
        return Err(err);
    }
    Ok(free)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_full_profile_does_not_allocate_a_hidden_game_slot() -> Result<()> {
        let root = tempfile::tempdir()?;
        for n in 0..6 {
            fs::create_dir(root.path().join(format!("slot_{n:02}")))?;
        }
        assert!(free_slot(root.path()).is_err());
        assert!(!root.path().join("slot_06").exists());
        fs::remove_dir(root.path().join("slot_03"))?;
        assert_eq!(free_slot(root.path())?, "slot_03");
        Ok(())
    }
}
