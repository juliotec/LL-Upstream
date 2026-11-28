use super::*;
use std::fs;
fn setting(build: &Path, name: &str) -> PathBuf {
    if versions::organized_build(build) {
        build
            .join("config")
            .join(if name == "longshot_options.ini" {
                "settings.ini"
            } else {
                name
            })
    } else {
        build.join(name)
    }
}

pub(super) fn occupied(path: &Path) -> std::io::Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(_) => Ok(true),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(e),
    }
}

pub(super) fn collision_destination(target: &Path, original: &Path) -> Result<PathBuf> {
    for index in 0u32.. {
        let suffix = if index == 0 {
            ".pre-upgrade".to_owned()
        } else {
            format!(".pre-upgrade-{index}")
        };
        let sibling = |path: &Path| {
            let mut name = path.as_os_str().to_owned();
            name.push(&suffix);
            PathBuf::from(name)
        };
        let candidate = sibling(target);
        if !occupied(&candidate)? && !occupied(&sibling(original))? {
            return Ok(candidate);
        }
    }
    bail!("No available filename for a locally changed build file.")
}

/// Move runtime state using the upgrade's rename journal. Conflicting log or
/// report files remain available to the normal residual-file preservation pass.
pub(super) fn carry_runtime_files(
    old: &Path,
    new: &Path,
    scratch: &Path,
    relocate: &mut impl FnMut(PathBuf, PathBuf) -> Result<()>,
) -> Result<()> {
    let old_builds = old.join("builds");
    let new_builds = new.join("builds");
    if !players::is_dir(&old_builds) || !players::is_dir(&new_builds) {
        return Ok(());
    }
    for entry in fs::read_dir(old_builds)? {
        let entry = entry?;
        let from = entry.path();
        let to = new_builds.join(entry.file_name());
        if !players::is_dir(&from) {
            continue;
        }
        let backup = scratch.join(entry.file_name());
        fs::create_dir(&backup)?;
        if occupied(&to)? && !players::is_dir(&to) {
            bail!("Build path is occupied: {}", to.display());
        }
        for name in [
            "longshot_options.ini",
            "language.txt",
            "mouse_sens.txt",
            "longshot_env.txt",
            "longshot_perf.ini",
            "longshot_crash.ini",
        ] {
            let source = setting(&from, name);
            let target = setting(&to, name);
            if !players::is_file(&source) {
                continue;
            }
            ensure_parent(&target, &new_builds, scratch, relocate)?;
            if players::is_file(&target) {
                relocate(target.clone(), backup.join(name))?;
            }
            relocate(source, target)?;
        }
        carry_local_changes(&from, &to, &new_builds, scratch, relocate)?;
    }
    Ok(())
}

fn ensure_parent(
    target: &Path,
    root: &Path,
    scratch: &Path,
    relocate: &mut impl FnMut(PathBuf, PathBuf) -> Result<()>,
) -> Result<()> {
    let mut dir = root.to_owned();
    for part in target
        .parent()
        .context("Missing build directory")?
        .strip_prefix(root)?
        .components()
    {
        dir.push(part);
        if !occupied(&dir)? {
            let empty = tempfile::tempdir_in(scratch)?.keep();
            relocate(empty, dir.clone())?;
        }
        ensure!(
            players::is_dir(&dir),
            "Unsafe upgrade directory: {}",
            dir.display()
        );
    }
    Ok(())
}

/// Move unrecorded and locally edited files with the installation. A collision
/// keeps the freshly built file in service and the local copy alongside it.
fn carry_local_changes(
    old: &Path,
    new: &Path,
    root: &Path,
    scratch: &Path,
    relocate: &mut impl FnMut(PathBuf, PathBuf) -> Result<()>,
) -> Result<()> {
    use sha2::{Digest, Sha256};
    let manifest = old.join("longshot_build.json");
    let record = if players::is_file(&manifest) {
        home::read_json(&manifest)?
    } else {
        Value::Null
    };
    let mut pending = vec![(old.to_owned(), new.to_owned())];
    while let Some((from, to)) = pending.pop() {
        for entry in fs::read_dir(&from)? {
            let entry = entry?;
            let name = entry.file_name();
            if from == old
                && matches!(
                    name.to_str(),
                    Some(".longshot-builder.lock" | "longshot_build.json")
                )
            {
                continue;
            }
            let source = entry.path();
            let mut target =
                if from == old && versions::organized_build(new) && !versions::organized_build(old)
                {
                    match name.to_str() {
                        Some("logs" | "bug-reports" | "perf_pending" | "perf_sent") => {
                            new.join("diagnostics").join(&name)
                        }
                        Some(
                            "image.bin"
                            | "longshot_romfs.idx"
                            | ".longshot_romfs.json"
                            | "longshot_shaders.bin"
                            | "longshot_pipeline_states_shipped.bin"
                            | "longshot.png"
                            | "licenses"
                            | "prompts"
                            | "texts"
                            | "longshot_title.txt"
                            | "longshot_damaged_files.txt",
                        ) => new.join("data").join(&name),
                        _ => to.join(&name),
                    }
                } else {
                    to.join(&name)
                };
            let relative = source.strip_prefix(old)?;
            if super::super::prune::generated_runtime_cache(relative) {
                continue;
            }
            let is_dir = players::is_dir(&source);
            if !is_dir && !players::is_file(&source) {
                continue;
            }
            if !is_dir {
                let rel = source.strip_prefix(old)?.to_str().map(|rel| {
                    if cfg!(windows) {
                        rel.replace('\\', "/")
                    } else {
                        rel.into()
                    }
                });
                if let Some(digest) = rel
                    .as_ref()
                    .and_then(|rel| record["files"][rel]["sha256"].as_str())
                {
                    let mut actual = Sha256::new();
                    std::io::copy(&mut fs::File::open(&source)?, &mut actual)?;
                    if format!("{:x}", actual.finalize()) == digest {
                        continue;
                    }
                }
            }
            if is_dir && players::is_dir(&target) {
                pending.push((source, target));
                continue;
            }
            if fs::symlink_metadata(&target).is_ok() {
                target = collision_destination(&target, &source)?;
            }
            if is_dir {
                // An old-only directory can contain both generated and personal files.

                pending.push((source, target));
            } else {
                ensure_parent(&target, root, scratch, relocate)?;
                relocate(source, target)?;
            }
        }
    }
    Ok(())
}
