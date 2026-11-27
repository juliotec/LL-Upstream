use super::{Row, format_size};
use std::{
    collections::BTreeSet,
    fs,
    path::{Path, PathBuf},
};

pub(crate) fn volume(path: &Path) -> Option<PathBuf> {
    let path = fs::canonicalize(path).ok()?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        let dev = fs::metadata(&path).ok()?.dev();
        path.ancestors()
            .take_while(|p| fs::metadata(p).is_ok_and(|m| m.dev() == dev))
            .last()
            .map(Path::to_owned)
    }
    #[cfg(windows)]
    {
        use std::os::windows::ffi::{OsStrExt, OsStringExt};
        use windows_sys::Win32::Storage::FileSystem::GetVolumePathNameW;
        let input: Vec<_> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        let mut out = vec![0u16; 32_768];
        // Input and output are valid, NUL-terminated Windows paths.
        if unsafe { GetVolumePathNameW(input.as_ptr(), out.as_mut_ptr(), out.len() as u32) } == 0 {
            return None;
        }
        let len = out.iter().position(|c| *c == 0)?;
        Some(std::ffi::OsString::from_wide(&out[..len]).into())
    }
}

pub struct Drive {
    root: PathBuf,
    name: String,
    used: u64,
    free: Option<u64>,
}

pub struct Snapshot {
    pub(super) previous: super::reclaim::Plan,
    pub(super) rows: Vec<Row>,
    drives: Vec<Drive>,
}
impl Snapshot {
    pub(super) fn new(home: &Path, rows: Vec<Row>, previous: super::reclaim::Plan) -> Self {
        let mut paths: BTreeSet<_> = rows.iter().filter_map(|r| r.drive.clone()).collect();
        if let Some(system) = volume(home) {
            paths.insert(system);
        }
        let mut drives: Vec<_> = paths
            .into_iter()
            .map(|root| {
                let used = rows
                    .iter()
                    .filter(|r| r.drive.as_ref() == Some(&root))
                    .filter_map(|r| r.bytes)
                    .sum();
                let shown = root.to_string_lossy();
                let name = shown.strip_prefix(r"\\?\").unwrap_or(&shown).to_string();
                let free = crate::game::available_bytes(&root);
                Drive {
                    root,
                    name,
                    free,
                    used,
                }
            })
            .collect();
        drives.sort_by_key(|d| std::cmp::Reverse(d.used));
        Self {
            rows,
            drives,
            previous,
        }
    }
    pub fn has_previous_files(&self) -> bool {
        !self.previous.files.is_empty()
    }

    pub fn choices(&self) -> Vec<String> {
        if self.drives.len() <= 1 {
            return vec![];
        }
        std::iter::once("All drives".to_owned())
            .chain(self.drives.iter().map(|d| match d.free {
                Some(free) => format!(
                    "{} · {} used · {} free",
                    d.name,
                    format_size(d.used),
                    format_size(free)
                ),
                None => format!("{} · {} used", d.name, format_size(d.used)),
            }))
            .collect()
    }
    pub fn selected(&self, index: usize) -> Vec<Row> {
        let drive = index.checked_sub(1).and_then(|i| self.drives.get(i));
        self.rows
            .iter()
            .filter(|r| drive.is_none_or(|d| r.drive.as_ref() == Some(&d.root)))
            .cloned()
            .collect()
    }
    pub fn free_label(&self, index: usize) -> String {
        let drive = if self.drives.len() == 1 {
            self.drives.first()
        } else {
            index.checked_sub(1).and_then(|i| self.drives.get(i))
        };
        if let Some(drive) = drive {
            return drive
                .free
                .map_or_else(String::new, |n| format!("{} free", format_size(n)));
        }
        if self.drives.len() > 1 {
            format!("Across {} drives", self.drives.len())
        } else {
            String::new()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn filtering_a_drive_counts_only_its_rows() {
        let row = |root: &str, n| Row {
            path: PathBuf::from(root).join("build"),
            drive: Some(root.into()),
            bytes: Some(n),
            label: "Longshot".into(),
            group: "Installed games",
            cache: None,
        };
        let view = Snapshot {
            previous: Default::default(),
            rows: vec![row("first", 3), row("second", 9)],
            drives: vec![
                Drive {
                    root: "first".into(),
                    name: "first".into(),
                    used: 3,
                    free: Some(12),
                },
                Drive {
                    root: "second".into(),
                    name: "second".into(),
                    used: 9,
                    free: None,
                },
            ],
        };
        assert_eq!(view.selected(0).len(), 2);
        assert_eq!(view.selected(2)[0].bytes, Some(9));
        assert_eq!(view.free_label(1), "12 B free");
        assert_eq!(view.free_label(0), "Across 2 drives");
    }
}
