//! Reclaim recognized generated files without deleting a directory tree or changing the game registries.
#[cfg(test)]
mod preservation;
mod scan;
#[cfg(test)]
mod tests;

use crate::{Game, MainWindow, Preferences, game, home, players};
use anyhow::{Context, Result, ensure};
use slint::{ComponentHandle, Model};
use std::{
    collections::{BTreeMap, BTreeSet, HashMap},
    fs,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
    time::SystemTime,
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct File {
    pub(super) category: &'static str,
    group: PathBuf,
    lock: Option<PathBuf>,
    identity: (u64, u64, u64),
    len: u64,
    modified: SystemTime,
}

#[derive(Clone, Default, Debug, PartialEq, Eq)]
enum Scope {
    #[default]
    All,
    Previous,
    Selected(BTreeSet<String>),
    Folder(PathBuf, String),
}

#[derive(Clone, Default, Debug, PartialEq, Eq)]
pub(super) struct Plan {
    pub(super) files: BTreeMap<PathBuf, File>,
    scope: Scope,
}

pub(super) use crate::fsx::direct;

impl Plan {
    fn keep_shared(&mut self) {
        let mut links = HashMap::new();
        for f in self.files.values().filter(|f| f.identity.2 > 1) {
            *links.entry(f.identity).or_insert(0) += 1;
        }
        self.files
            .retain(|_, f| f.identity.2 == 1 || links.get(&f.identity) == Some(&f.identity.2));
    }

    fn size(&self) -> u64 {
        let mut seen = BTreeSet::new();
        self.files
            .values()
            .filter(|f| seen.insert(f.identity))
            .map(|f| f.len)
            .sum()
    }

    fn categories(&self) -> Vec<crate::SettingToggle> {
        let mut groups = BTreeMap::new();
        for file in self.files.values() {
            *groups.entry(file.category).or_insert(0usize) += 1;
        }
        groups
            .into_iter()
            .map(|(name, count)| crate::SettingToggle {
                key: name.into(),
                label: format!(
                    "{name} · {count} {}",
                    if count == 1 { "file" } else { "files" }
                )
                .into(),
                checked: true,
                enabled: true,
            })
            .collect()
    }

    fn selected_size(&self, categories: &BTreeSet<String>) -> u64 {
        let mut links = HashMap::new();
        for file in self
            .files
            .values()
            .filter(|file| categories.contains(file.category))
        {
            links.entry(file.identity).or_insert((0, file.len)).0 += 1;
        }
        links
            .into_iter()
            .filter(|(id, (count, _))| id.2 == *count)
            .map(|(_, (_, bytes))| bytes)
            .sum()
    }

    fn select(&mut self, categories: BTreeSet<String>) {
        self.files
            .retain(|_, file| categories.contains(file.category));
        self.keep_shared();
        self.scope = Scope::Selected(categories);
    }

    fn review(&self) -> String {
        if let Scope::Folder(folder, _) = &self.scope
            && self.files.len() <= 100
        {
            return self
                .files
                .iter()
                .map(|(path, file)| {
                    format!(
                        "{}  ·  {}",
                        super::format_size(file.len),
                        path.strip_prefix(folder).unwrap_or(path).display()
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
        }
        let mut groups = BTreeMap::<_, (usize, u64)>::new();
        let mut counted = BTreeSet::new();
        for file in self.files.values() {
            let entry = groups.entry(&file.group).or_default();
            entry.0 += 1;
            if counted.insert(file.identity) {
                entry.1 += file.len;
            }
        }
        groups
            .into_iter()
            .map(|(path, (n, bytes))| {
                format!(
                    "{}  ·  {} {}  ·  {}",
                    super::format_size(bytes),
                    n,
                    if n == 1 { "file" } else { "files" },
                    home::display(path)
                )
            })
            .collect::<Vec<_>>()
            .join("\n")
    }

    #[cfg(test)]
    fn execute(&self, root: &Path) -> Result<()> {
        self.execute_with_progress(root, |_, _| {})
    }

    fn execute_with_progress(
        &self,
        root: &Path,
        mut progress: impl FnMut(usize, usize),
    ) -> Result<()> {
        let _lease = game::move_lease(root)?;
        let groups: BTreeSet<_> = self
            .files
            .values()
            .map(|file| file.group.as_path())
            .collect();
        players::ensure_paths_idle(&groups.into_iter().collect::<Vec<_>>())?;
        let paths: BTreeSet<_> = self
            .files
            .values()
            .filter_map(|f| f.lock.as_ref())
            .collect();
        let mut locks = Vec::new();
        for path in paths {
            ensure!(
                path.parent().is_some_and(direct)
                    && (direct(path)
                        || fs::symlink_metadata(path)
                            .is_err_and(|e| e.kind() == std::io::ErrorKind::NotFound)),
                "A build lock changed. Review these files again."
            );
            let file = fs::File::options()
                .read(true)
                .write(true)
                .create(true)
                .truncate(false)
                .open(path)?;
            locks.push(
                home::FileLease::try_acquire(file, crate::fsx::LockMode::Exclusive)
                    .with_context(|| format!("A build is using {}.", home::display(path)))?,
            );
        }
        ensure!(
            *self == scan::plan_for(root, &self.scope)?,
            "Build files changed. Review the cleanup again."
        );
        // Invalidate the builder's unpack stamp before removing even the first
        // extracted file. Keep the provenance so the other half can be cleaned later.
        for game in self
            .files
            .values()
            .filter(|file| matches!(file.category, "Extracted RomFS" | "Extracted ExeFS"))
            .map(|file| &file.group)
            .collect::<BTreeSet<_>>()
        {
            let stamp = game.join(scan::UNPACK_STAMP);
            if direct(&stamp) {
                let retired = game.join(scan::RETIRED_STAMP);
                if retired.try_exists()? {
                    anyhow::ensure!(
                        direct(&retired) && fs::read(&retired)? == fs::read(&stamp)?,
                        "Extraction changed. Review these files again."
                    );
                    fs::remove_file(stamp)?;
                } else {
                    fs::rename(stamp, retired)?;
                }
            }
        }
        let total = self.files.len();
        progress(0, total);
        for (index, (path, file)) in self.files.iter().enumerate() {
            let meta = fs::symlink_metadata(path)?;
            ensure!(
                direct(path)
                    && meta.is_file()
                    && meta.len() == file.len
                    && meta.modified()? == file.modified
                    && super::file_identity(path, &meta)
                        .is_some_and(|id| (id.0, id.1) == (file.identity.0, file.identity.1)),
                "A file changed. Review the cleanup again."
            );
            super::Removal::File.execute(path)?;
            progress(index + 1, total);
        }
        Ok(())
    }
}

pub(super) fn previous_plan(root: &Path) -> Result<Plan> {
    scan::plan_for(root, &Scope::Previous)
}

fn selected(prefs: &Preferences<'_>) -> BTreeSet<String> {
    prefs
        .get_reclaim_categories()
        .iter()
        .filter(|row| row.checked)
        .map(|row| row.key.to_string())
        .collect()
}

pub(crate) fn bind(ui: &MainWindow, root: &Path, snapshot: Arc<Mutex<Option<super::Snapshot>>>) {
    let reviewed = Arc::new(Mutex::new(None::<Plan>));
    let weak = ui.as_weak();
    let choices = reviewed.clone();
    ui.global::<Preferences>()
        .on_toggle_reclaim(move |index, checked| {
            let Some(ui) = weak.upgrade() else { return };
            let prefs = ui.global::<Preferences>();
            let model = prefs.get_reclaim_categories();
            let Ok(index) = usize::try_from(index) else {
                return;
            };
            let Some(mut row) = model.row_data(index) else {
                return;
            };
            row.checked = checked;
            model.set_row_data(index, row);
            if let Some(plan) = choices.lock().unwrap_or_else(|e| e.into_inner()).as_ref() {
                let categories = selected(&prefs);
                prefs.set_reclaim_selected(!categories.is_empty());
                prefs.set_reclaim_size(super::format_size(plan.selected_size(&categories)).into());
            }
        });
    let weak = ui.as_weak();
    let root = root.to_owned();
    ui.global::<Preferences>()
        .on_reclaim_build_files(move |confirm, previous, category| {
            let Some(ui) = weak.upgrade() else { return };
            let prefs = ui.global::<Preferences>();
            if crate::busy(&ui) || ui.global::<Game>().get_playing() {
                prefs.set_status("Finish running builds and close Longshot first.".into());
                return;
            }
            if prefs.get_cleaning() {
                return;
            }
            // Storage already inspected these files. Present that exact selection now.
            // execute() checks it against the filesystem again after confirmation.
            if !confirm && !previous.is_empty() {
                let view = snapshot.lock().unwrap_or_else(|e| e.into_inner());
                let Some(view) = view.as_ref() else { return };
                let mut plan = if previous == "previous" {
                    view.previous.clone()
                } else {
                    let folder = PathBuf::from(previous.as_str());
                    let files = view
                        .previous
                        .files
                        .range(folder.clone()..)
                        .take_while(|(path, _)| path.starts_with(&folder))
                        .filter(|(_, file)| file.category == category.as_str())
                        .map(|(path, file)| (path.clone(), file.clone()))
                        .collect();
                    Plan {
                        files,
                        scope: Scope::Folder(folder, category.to_string()),
                    }
                };
                plan.keep_shared();
                prefs.set_status("".into());
                prefs.set_reclaim_previous(true);
                prefs.set_reclaim_selected(!plan.files.is_empty());
                prefs.set_reclaim_categories(if plan.scope == Scope::Previous {
                    slint::ModelRc::new(slint::VecModel::from(plan.categories()))
                } else {
                    Default::default()
                });
                prefs.set_reclaim_folder(match &plan.scope {
                    Scope::Folder(folder, label) => {
                        format!("{label} · {}", home::display(folder)).into()
                    }
                    _ => Default::default(),
                });
                prefs.set_reclaim_size(super::format_size(plan.size()).into());
                prefs.set_reclaim_list(if plan.files.is_empty() {
                    "No files to clean up.".into()
                } else {
                    plan.review().into()
                });
                *reviewed.lock().unwrap_or_else(|e| e.into_inner()) = Some(plan);
                return;
            }
            let choices = selected(&prefs);
            prefs.set_cleaning(true);
            prefs.set_reclaim_running(confirm);
            prefs.set_reclaim_total(0);
            prefs.set_reclaim_removed(0);
            if !confirm {
                prefs.set_reclaim_categories(Default::default());
                prefs.set_reclaim_list("".into());
                prefs.set_reclaim_size("".into());
            }
            prefs.set_status("".into());
            let (root, weak, reviewed) = (root.clone(), ui.as_weak(), reviewed.clone());
            std::thread::spawn(move || {
                let result = (|| -> Result<Option<Plan>> {
                    let mut last = reviewed.lock().unwrap_or_else(|e| e.into_inner());
                    if confirm {
                        let mut plan = last
                            .take()
                            .context("Review these files before cleaning up.")?;
                        if plan.scope == Scope::Previous {
                            ensure!(!choices.is_empty(), "Select files to clean up.");
                            plan.select(choices);
                        }
                        let mut last_update = std::time::Instant::now();
                        plan.execute_with_progress(&root, |removed, total| {
                            if removed <= 1
                                || removed == total
                                || last_update.elapsed() >= std::time::Duration::from_millis(100)
                            {
                                last_update = std::time::Instant::now();
                                let _ = weak.upgrade_in_event_loop(move |ui| {
                                    let prefs = ui.global::<Preferences>();
                                    prefs.set_reclaim_total(total as i32);
                                    prefs.set_reclaim_removed(removed as i32);
                                });
                            }
                        })?;
                        return Ok(None);
                    }
                    *last = None;
                    Ok(Some(scan::plan_for(&root, &Scope::All)?))
                })();
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    let p = ui.global::<Preferences>();
                    p.set_cleaning(false);
                    p.set_reclaim_running(false);
                    if confirm {
                        p.set_reclaim_list(Default::default());
                        p.set_reclaim_categories(Default::default());
                    }
                    match result {
                        Ok(Some(plan)) if !plan.files.is_empty() => {
                            p.set_reclaim_selected(true);
                            p.set_reclaim_previous(false);
                            p.set_reclaim_folder(Default::default());
                            p.set_reclaim_size(super::format_size(plan.size()).into());
                            p.set_reclaim_list(plan.review().into());
                            *reviewed.lock().unwrap_or_else(|e| e.into_inner()) = Some(plan);
                        }
                        Ok(Some(_)) => p.set_status("No unused build files to clean up.".into()),
                        Ok(None) => {
                            p.invoke_measure();
                            p.set_status("Unused build files removed.".into());
                        }
                        Err(e) => {
                            p.set_reclaim_selected(false);
                            p.set_reclaim_list(format!("{e:#}").into());
                            p.invoke_measure();
                        }
                    }
                });
            });
        });
}
