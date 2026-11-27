use super::{disk, profiles};
use anyhow::{Context, Result, bail, ensure};
use std::{
    env, fs,
    path::{Path, PathBuf},
};
pub const UID: &str = "5a3c9e1f0b7d24680000000000000001";
const TITLE: &str = "0400486024DEC000";
pub fn slot_name(s: &str) -> bool {
    s.strip_prefix("slot_")
        .is_some_and(|n| (2..=3).contains(&n.len()) && n.bytes().all(|c| c.is_ascii_digit()))
}
pub fn slots(root: &Path) -> Result<Vec<PathBuf>> {
    Ok(disk::dirs(root)?
        .into_iter()
        .filter(|p| {
            p.file_name()
                .and_then(|v| v.to_str())
                .is_some_and(slot_name)
                && disk::is_file(&p.join("progress.sav"))
        })
        .collect())
}
pub fn slots_dir(profile: &Path) -> Result<PathBuf> {
    disk::require_dir(profile)?;
    let mut p = profile.to_owned();
    for s in ["saves", "save", UID] {
        p.push(s);
        if s != "saves" {
            recover(&p)?;
        }
        if fs::symlink_metadata(&p).is_ok() {
            disk::require_dir(&p)?;
        }
    }
    Ok(p)
}
pub fn pending(p: &Path) -> Result<()> {
    for a in p.ancestors() {
        if a.file_name()
            .is_some_and(|s| s.eq_ignore_ascii_case("save"))
            && let Some(root) = a.parent()
        {
            let ready = disk::dirs(&root.join("save-tx"))?.into_iter().any(|x| {
                x.file_name()
                    .and_then(|s| s.to_str())
                    .and_then(|s| s.strip_prefix("ready-"))
                    .and_then(|s| s.parse::<u32>().ok())
                    .is_some_and(|v| v > 0)
            });
            ensure!(
                !ready && !root.join("save-import").is_dir(),
                "NX2navi has a pending save. Start it and quit from its menu before transferring saves."
            );
        }
    }
    Ok(())
}
pub fn find(root: &Path) -> Result<Vec<PathBuf>> {
    if !disk::is_dir(root) {
        return Ok(vec![]);
    }
    let layouts: &[&[&str]] = &[
        &["nand", "user", "save", "account", "*", TITLE, "0"],
        &["nand", "user", "save", "0000000000000000", "*", TITLE],
        &["profile", "save", "*"],
        &["user", "save", "*"],
        &["save", "*"],
    ];
    if !slots(root)?.is_empty() {
        return Ok(vec![root.into()]);
    }
    for layout in layouts {
        for offset in 0..layout.len() {
            if offset > 0
                && !root
                    .file_name()
                    .is_some_and(|name| name.eq_ignore_ascii_case(layout[offset - 1]))
            {
                continue;
            }
            let mut paths = vec![root.to_owned()];
            for &step in &layout[offset..] {
                let mut children = Vec::new();
                for p in paths {
                    children.extend(disk::dirs(&p)?.into_iter().filter(|d| {
                        step == "*" || d.file_name().is_some_and(|s| s.eq_ignore_ascii_case(step))
                    }));
                }
                paths = children;
            }
            let mut found = vec![];
            for p in paths {
                if !slots(&p)?.is_empty() {
                    found.push(p);
                }
            }
            if !found.is_empty() {
                return Ok(found);
            }
        }
    }
    Ok(vec![])
}
pub fn sources(group: &str) -> Result<Vec<PathBuf>> {
    let data = if cfg!(windows) {
        env::var_os("APPDATA")
            .map(PathBuf::from)
            .context("The roaming data folder is unavailable.")?
    } else {
        profiles::data_root()?
    };
    let user = env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" })
        .map(PathBuf::from)
        .context("The home folder is unavailable.")?;
    let mut roots = Vec::new();
    match group {
        "hookshot" if cfg!(windows) => {
            let base = user.join("Games/Hookshot");
            roots.extend(disk::dirs(&base)?);
            roots.push(base);
        }
        "hookshot" => roots.push(data.join("hookshot")),
        "lichii" | "eden" | "eglantine" => roots.push(data.join(group)),
        "nx2navi" if cfg!(windows) => roots.extend([
            profiles::data_root()?.join("Programs/NX2navi/game"),
            PathBuf::from("C:/Games/Ocarina"),
        ]),
        "nx2navi" => roots.push(user.join("Games/Ocarina")),
        _ => bail!("Unknown save source."),
    }
    let mut found = vec![];
    for p in roots {
        found.extend(find(&p)?);
    }
    found.sort();
    found.dedup();
    Ok(found)
}
pub fn backup(from: &Path, home: &Path, prefix: &str) -> Result<PathBuf> {
    let root = profiles::backup_root(home)?;
    disk::disjoint(from, &root)?;
    fs::create_dir_all(&root)?;
    let stage = tempfile::tempdir_in(&root)?;
    disk::copy_tree(from, stage.path())?;
    let now = chrono::Local::now().format("%Y%m%d-%H%M%S");
    for n in 0..10000 {
        let suffix = if n == 0 {
            String::new()
        } else {
            format!("-{n}")
        };
        let to = root.join(format!("{prefix}-{now}{suffix}"));
        if !to.try_exists()? {
            fs::rename(stage.path(), &to)?;
            return Ok(to);
        }
    }
    bail!("The backup folder has no free name.")
}
pub(super) fn validate_file(p: &Path) -> Result<()> {
    disk::require_file(p)?;
    ensure!(
        (1..=(4 << 20)).contains(&fs::metadata(p)?.len()),
        "{} is empty or too large to be a save.",
        crate::home::display(p)
    );
    Ok(())
}
pub(super) fn validate_slot(p: &Path) -> Result<()> {
    disk::require_dir(p)?;
    validate_file(&p.join("progress.sav"))?;
    for file in fs::read_dir(p)? {
        let file = file?;
        ensure!(
            matches!(
                file.file_name().to_str(),
                Some("caption.sav" | "progress.sav")
            ),
            "The source contains an unrecognized slot file."
        );
        validate_file(&file.path())?;
    }
    Ok(())
}
pub fn validate_input(p: &Path) -> Result<()> {
    ensure!(!slots(p)?.is_empty(), "The source contains no save slots.");
    for e in fs::read_dir(p)? {
        let e = e?;
        let n = e.file_name();
        let n = n.to_string_lossy();
        if slot_name(&n) {
            validate_slot(&e.path())?;
        } else {
            ensure!(
                matches!(n.as_ref(), "account.sav" | "option.sav"),
                "The source contains an unrecognized file: {n}."
            );
            validate_file(&e.path())?;
        }
    }
    Ok(())
}
fn aside_path(to: &Path) -> Result<PathBuf> {
    let name = to
        .file_name()
        .context("Invalid save folder.")?
        .to_string_lossy();
    Ok(to.with_file_name(format!(".{name}.longshot-old")))
}

pub(super) fn recover(target: &Path) -> Result<()> {
    if aside_path(target)?.try_exists()? {
        super::running::saves_idle(&[target])?;
        drop(SaveEdit::begin(target)?);
    }
    Ok(())
}

/// Keep the lock outside the tree being swapped. Whole-profile migration,
/// account imports and individual slots all share this file.
pub(super) struct SaveLock {
    _lease: crate::home::FileLease,
}

impl SaveLock {
    pub(super) fn recover_target(&self, target: &Path) -> Result<PathBuf> {
        let parent = target.parent().context("Missing save directory parent.")?;
        let aside = aside_path(target)?;
        if aside.try_exists()? {
            disk::require_dir(&aside)?;
            if target.try_exists()? {
                // Both directories survived an interrupted commit. Retain the previous
                // one under a different name so either version is still recoverable.
                let kept = tempfile::Builder::new()
                    .prefix(".previous-")
                    .tempdir_in(parent)?;
                fs::rename(&aside, kept.path().join("save"))?;
                let _ = kept.keep();
            } else {
                fs::rename(&aside, target)?;
            }
        }
        Ok(aside)
    }
    pub(super) fn acquire(target: &Path) -> Result<Self> {
        let parent = target.parent().context("The destination has no parent.")?;
        let owner = target
            .ancestors()
            .find(|path| path.file_name().is_some_and(|name| name == "saves"))
            .or_else(|| {
                target.ancestors().find(|path| {
                    path.file_name()
                        .is_some_and(|name| name == "save" || name == "save-work")
                })
            })
            .and_then(Path::parent)
            .unwrap_or(parent);
        fs::create_dir_all(owner)?;
        let lock = owner.join(".longshot-save-transfer.lock");
        if let Ok(meta) = fs::symlink_metadata(&lock) {
            ensure!(meta.is_file() && disk::is_file(&lock), "Invalid save lock.");
        }
        let mut options = fs::OpenOptions::new();
        options.read(true).write(true).create(true).truncate(false);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.share_mode(0);
        }
        let file = options
            .open(lock)
            .context("Another launcher may be changing these saves.")?;
        Ok(Self {
            _lease: crate::home::FileLease::try_acquire(file, crate::fsx::LockMode::Exclusive)
                .context("Another launcher is changing these saves.")?,
        })
    }
}

/// Hold the destination lock throughout a staged save transfer and its directory swap.
pub(super) struct SaveEdit {
    target: PathBuf,
    aside: PathBuf,
    _lock: SaveLock,
}

impl SaveEdit {
    pub(super) fn begin(target: &Path) -> Result<Self> {
        let lock = SaveLock::acquire(target)?;
        let parent = target.parent().context("The destination has no parent.")?;
        fs::create_dir_all(parent)?;
        let aside = lock.recover_target(target)?;
        Ok(Self {
            target: target.to_owned(),
            aside,
            _lock: lock,
        })
    }
    pub(super) fn transact(self, fill: impl FnOnce(&Path) -> Result<()>) -> Result<()> {
        let parent = self
            .target
            .parent()
            .context("The save path has no parent.")?;
        let stage = tempfile::tempdir_in(parent)?;
        fill(stage.path())?;
        self.install(stage.path())
    }

    pub(super) fn install(self, stage: &Path) -> Result<()> {
        let had = self.target.try_exists()?;
        if had {
            disk::require_dir(&self.target)?;
            fs::rename(&self.target, &self.aside)?;
        }
        if let Err(e) = fs::rename(stage, &self.target) {
            if had && fs::rename(&self.aside, &self.target).is_err() {
                bail!(
                    "Could not install the save ({e}). The original is in {}.",
                    crate::home::display(&self.aside)
                );
            }
            return Err(e.into());
        }
        if had {
            fs::remove_dir_all(self.aside)?;
        }
        Ok(())
    }
}
pub(super) fn merge_slots(src: &Path, dst: &Path, home: &Path, keep: bool) -> Result<()> {
    super::running::saves_idle(&[src, dst])?;
    pending(src)?;
    pending(dst)?;
    disk::disjoint(src, dst)?;
    validate_input(src)?;
    let selected = slots(src)?;
    ensure!(!selected.is_empty(), "There are no save slots to copy.");
    SaveEdit::begin(dst)?.transact(|stage| {
        if dst.try_exists()? {
            disk::require_dir(dst)?;
            backup(dst, home, "import-slot")?;
            disk::copy_tree(dst, stage)?;
        }
        for p in selected {
            let target = stage.join(p.file_name().context("Invalid slot name.")?);
            if target.try_exists()? {
                disk::require_dir(&target)?;
                fs::remove_dir_all(&target)?;
            }
            disk::copy_tree(&p, &target)?;
        }
        for name in ["account.sav", "option.sav"] {
            let de = stage.join(name);
            if src.join(name).try_exists()? && !(keep && name == "option.sav") {
                if de.exists() {
                    fs::remove_file(&de)?;
                }
                disk::copy_new(&src.join(name), &de)?;
            }
        }
        super::running::saves_idle(&[src, dst])?;
        Ok(())
    })
}
