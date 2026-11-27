use std::{
    collections::{HashMap, HashSet},
    fs,
    path::{Path, PathBuf},
    time::SystemTime,
};

fn scan(roots: &[PathBuf]) -> Vec<PathBuf> {
    let mut found = HashMap::new();
    let mut seen = HashSet::new();
    for root in roots {
        let mut directories = vec![root.clone()];
        if let Ok(list) = fs::read_dir(root) {
            directories.extend(
                list.flatten()
                    .filter(|e| e.file_type().is_ok_and(|t| t.is_dir()))
                    .filter(|e| {
                        let n = e.file_name().to_string_lossy().to_ascii_lowercase();
                        !n.starts_with('.')
                            && !n.starts_with('$')
                            && !n.starts_with("program")
                            && n != "windows"
                    })
                    .take(200)
                    .map(|e| e.path()),
            );
        }
        for dir in directories {
            if !seen.insert(dir.clone()) {
                continue;
            }
            if dir.join("romfs").is_dir() && dir.join("exefs/main").is_file() {
                found.insert(dir.clone(), modified(&dir));
            }
            if let Ok(entries) = fs::read_dir(&dir) {
                for e in entries
                    .flatten()
                    .filter(|e| e.file_type().is_ok_and(|t| t.is_file()))
                {
                    let p = e.path();
                    if p.extension().is_some_and(|ext| {
                        ext.eq_ignore_ascii_case("nx") || ext.eq_ignore_ascii_case("nup")
                    }) {
                        found.insert(p.clone(), modified(&p));
                    }
                }
            }
        }
    }
    let mut found = found.into_iter().collect::<Vec<_>>();
    found.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
    found
        .into_iter()
        .map(|(p, _)| p)
        .filter(|p| p.to_str().is_some())
        .collect()
}
fn modified(p: &Path) -> Option<SystemTime> {
    p.metadata().and_then(|m| m.modified()).ok()
}

pub fn game_files() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(user) =
        std::env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" }).map(PathBuf::from)
    {
        roots.extend(["Downloads", "Desktop", "Documents", ""].map(|n| user.join(n)));
    }
    #[cfg(unix)]
    if let Ok(mounts) = fs::read_to_string("/proc/mounts") {
        roots.extend(
            mounts
                .lines()
                .filter_map(|l| l.split_whitespace().nth(1))
                .filter(|p| {
                    ["/mnt/", "/run/media/", "/media/"]
                        .iter()
                        .any(|pre| p.starts_with(pre))
                })
                .map(|p| PathBuf::from(p.replace("\\040", " "))),
        );
    }
    #[cfg(windows)]
    for c in b'C'..=b'Z' {
        let p = PathBuf::from(format!("{}:\\", c as char));
        if p.is_dir() {
            roots.push(p);
        }
    }
    scan(&roots)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    #[test]
    fn skips_paths_that_cannot_be_saved_and_rejects_them_when_selected() -> anyhow::Result<()> {
        use std::{ffi::OsStr, os::unix::ffi::OsStrExt};
        let dir = tempfile::tempdir()?;
        let invalid = dir.path().join(OsStr::from_bytes(b"game\xff.nup"));
        let valid = dir.path().join("game.nup");
        fs::write(&invalid, [])?;
        fs::write(&valid, [])?;
        assert_eq!(scan(&[dir.path().to_owned()]), vec![valid]);
        let error = super::super::process::validated_source(&invalid).unwrap_err();
        assert!(error.to_string().contains("Rename the file"));
        Ok(())
    }

    #[test]
    fn scans_only_the_top_and_one_level_of_each_place() -> std::io::Result<()> {
        let dir = tempfile::tempdir()?;
        fs::create_dir_all(dir.path().join("d/deep"))?;
        for p in ["a.nup", "d/b.NX", "d/deep/c.nup"] {
            fs::write(dir.path().join(p), [])?;
        }
        let paths = scan(&[dir.path().to_owned()]);
        assert_eq!(paths.len(), 2);
        assert!(paths.contains(&dir.path().join("d/b.NX")));
        Ok(())
    }
}
