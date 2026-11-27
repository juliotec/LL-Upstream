use super::{File, Plan, Scope, direct};
use crate::{game, home, players, storage};
use anyhow::{Context, Result};
use serde_json::Value;
use std::{
    collections::BTreeSet,
    fs, io,
    path::{Component, Path, PathBuf},
};

use crate::fsx::overlaps as overlap;

fn children(path: &Path) -> Result<Vec<PathBuf>> {
    if !direct(path) || !players::is_dir(path) {
        return Ok(vec![]);
    }
    fs::read_dir(path)?
        .map(|e| e.map(|e| e.path()))
        .collect::<io::Result<_>>()
        .map_err(Into::into)
}

fn text_file(path: &Path) -> Option<String> {
    (direct(path) && players::is_file(path) && fs::metadata(path).ok()?.len() <= 64 * 1024)
        .then(|| fs::read_to_string(path).ok())
        .flatten()
}

fn generated_object(path: &Path) -> bool {
    // make_game.py and the modern compile stage keep ordinary object files here.
    path.extension().is_some_and(|e| e == "o")
        && path.file_stem().and_then(|s| s.to_str()).is_some_and(|s| {
            s.len() == 40
                && s.bytes()
                    .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
        })
}

pub(super) const UNPACK_STAMP: &str = ".longshot_romfs";
pub(super) const RETIRED_STAMP: &str = ".longshot_romfs.cleanup";

fn legacy_shader_cache(name: &str) -> bool {
    storage::shaders::GAME_FILES.contains(&name)
        || matches!(name, "longshot_pipelines.bin" | "mkw_vk_pipelines.bin")
        || ["longshot_vk_pipelines_play_", "mkw_vk_pipelines_play_"]
            .iter()
            .any(|prefix| {
                name.strip_prefix(prefix)
                    .and_then(|s| s.strip_suffix(".bin"))
                    .is_some_and(|n| !n.is_empty() && n.bytes().all(|b| b.is_ascii_digit()))
            })
}

struct Scan {
    category: &'static str,
    plan: Plan,
    protected: Vec<PathBuf>,
    stable: Vec<PathBuf>,
    unused: BTreeSet<PathBuf>,
}

impl Scan {
    fn add(
        &mut self,
        path: &Path,
        group: &Path,
        lock: Option<&Path>,
        stable_game: Option<&Path>,
    ) -> Result<()> {
        if !direct(path) || !players::is_file(path) {
            return Ok(());
        }
        let actual = path.canonicalize()?;
        let meta = fs::symlink_metadata(path)?;
        self.record(actual, &meta, group, lock, stable_game)
    }

    fn record(
        &mut self,
        actual: PathBuf,
        meta: &fs::Metadata,
        group: &Path,
        lock: Option<&Path>,
        stable_game: Option<&Path>,
    ) -> Result<()> {
        if self.protected.iter().any(|p| overlap(p, &actual))
            || self
                .stable
                .iter()
                .any(|p| stable_game != Some(p.as_path()) && overlap(p, &actual))
        {
            return Ok(());
        }
        let Some(identity) = storage::file_identity(&actual, meta) else {
            return Ok(());
        };
        self.plan.files.insert(
            actual,
            File {
                category: self.category,
                group: group.to_owned(),
                lock: lock.map(Path::to_path_buf),
                identity,
                len: meta.len(),
                modified: meta.modified()?,
            },
        );
        Ok(())
    }

    fn game_outputs(
        &mut self,
        game: &Path,
        group: &Path,
        lock: Option<&Path>,
        stable: Option<&Path>,
    ) -> Result<()> {
        for name in [
            "bin/longshot",
            "bin/longshot.exe",
            "bin/longshot_vk.so",
            "bin/longshot_vk.dll",
            "longshot_vk.so",
            "longshot_vk.dll",
            "longshot_shaders.bin",
            "longshot_pipeline_states_shipped.bin",
            "bin/longshot_shaders.bin",
            "bin/longshot_pipeline_states_shipped.bin",
        ] {
            self.add(&game.join(name), group, lock, stable)?;
        }
        for (exe, renderer) in [
            ("longshot", "longshot_vk.so"),
            ("longshot.exe", "longshot_vk.dll"),
        ] {
            if players::is_file(&game.join(renderer)) {
                self.add(&game.join(exe), group, lock, stable)?;
            }
        }
        Ok(())
    }

    fn unused_version(&mut self, version: &Path) -> Result<()> {
        let lock = version.join(".launcher-fetch.lock");
        let lock = Some(lock.as_path());
        for name in ["Longshot.x86_64", "Longshot.exe"] {
            self.add(&version.join(name), version, lock, None)?;
        }
        for game in children(&version.join("builds"))? {
            self.game_outputs(&game, version, lock, None)?;
        }
        Ok(())
    }

    fn extraction(&mut self, game: &Path) -> Result<()> {
        let Some(stamp) =
            text_file(&game.join(UNPACK_STAMP)).or_else(|| text_file(&game.join(RETIRED_STAMP)))
        else {
            return Ok(());
        };
        let Some((name, size)) = stamp
            .trim_end()
            .strip_prefix("unpacked by this builder: ")
            .and_then(|s| s.rsplit_once(' '))
        else {
            return Ok(());
        };
        let size = size.parse::<u64>().ok();
        let mut originals = self.protected.clone();
        if Path::new(name).file_name().is_some_and(|n| n == name)
            && let Some(home) = std::env::home_dir()
        {
            originals.push(home.join("Downloads").join(name));
        }
        if !originals.iter().any(|p| {
            p.file_name().is_some_and(|n| n == name)
                && !p.starts_with(game)
                && p.metadata()
                    .is_ok_and(|m| m.is_file() && m.len() > 0 && Some(m.len()) == size)
        }) {
            return Ok(());
        }
        for (subdir, category) in [
            ("romfs", "Extracted RomFS"),
            (".longshot_exefs", "Extracted ExeFS"),
        ] {
            let dir = game.join(subdir);
            if self.protected.iter().any(|p| overlap(p, &dir)) {
                continue;
            }
            self.category = category;
            self.generated_tree(&dir, game, Some(game))?;
        }
        Ok(())
    }

    fn generated_tree(&mut self, dir: &Path, group: &Path, stable: Option<&Path>) -> Result<()> {
        let mut folders = vec![dir.to_owned()];
        while let Some(folder) = folders.pop() {
            if !direct(&folder) || !players::is_dir(&folder) {
                continue;
            }
            // Each entry is one child of this verified directory. Resolve the parent
            // once per directory and reuse the entry metadata for the review record.
            let actual = folder.canonicalize()?;
            for entry in fs::read_dir(&actual)? {
                let entry = entry?;
                let path = entry.path();
                let meta = entry.metadata()?;
                if storage::is_link(&meta) {
                    continue;
                }
                let name = path
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .to_ascii_lowercase();
                if matches!(
                    name.as_str(),
                    "mods" | "profiles" | "user" | "save" | "saves" | "backups"
                ) || [".nup", ".nx", ".sav"]
                    .iter()
                    .any(|ext| name.ends_with(ext))
                {
                    continue;
                }
                if meta.is_dir() {
                    folders.push(path);
                } else if meta.is_file() {
                    self.record(path, &meta, group, None, stable)?;
                }
            }
            anyhow::ensure!(
                direct(&folder) && folder.canonicalize()? == actual,
                "A generated folder changed. Review the cleanup again."
            );
        }
        Ok(())
    }

    fn stable_outputs(&mut self, game: &Path) -> Result<()> {
        // Registered builds can recreate their executables and shader caches.
        self.category = "Built games";
        self.game_outputs(game, game, None, Some(game))?;
        self.category = "Game shader caches";
        let cache = game.join("cache");
        for file in children(game)?.into_iter().chain(children(&cache)?) {
            if let Some(name) = file.file_name().and_then(|n| n.to_str())
                && legacy_shader_cache(name.strip_suffix(".part").unwrap_or(name))
            {
                self.add(&file, game, None, Some(game))?;
            }
        }
        self.category = "Other generated files";
        for name in [
            "longshot",
            "longshot.exe",
            "longshot.png",
            "longshot_version.txt",
            "bin/libSDL2-2.0.so.0",
            "bin/SDL2.dll",
            "mkw_vk_pipelines.stamp",
            "image.bin",
            "prompts/Common.blarc",
            "prompts/Common.blarc.key",
            "prompts/Common.blarc.zs",
            "prompts/Common.bntx",
            "prompts/Common.bntx.zs",
            "prompts/Common.key",
            "prompts/External.bfotf",
            "prompts/External.bfotf.zs",
            "prompts/ring.bin",
            "prompts/rsizetable.zs",
        ] {
            self.add(&game.join(name), game, None, Some(game))?;
        }
        for device in ["keyboard", "ps4", "ps5", "switch", "steamdeck", "xbox"] {
            for name in ["hint.bin", "textures.bin"] {
                self.add(
                    &game.join("prompts").join(device).join(name),
                    game,
                    None,
                    Some(game),
                )?;
            }
        }
        for path in children(&game.join("ngx"))? {
            if let Some(name) = path.file_name().and_then(|n| n.to_str())
                && (matches!(name, "nvngx_dlss.dll" | "nvngx_dlssg.dll")
                    || ["libnvidia-ngx-dlss.so.", "libnvidia-ngx-dlssg.so."]
                        .iter()
                        .any(|p| {
                            name.strip_prefix(p).is_some_and(|v| {
                                !v.is_empty()
                                    && v.split('.').all(|n| {
                                        !n.is_empty() && n.bytes().all(|b| b.is_ascii_digit())
                                    })
                            })
                        }))
            {
                self.add(&path, game, None, Some(game))?;
            }
        }
        Ok(())
    }

    fn work(&mut self, root: &Path, libraries: &BTreeSet<PathBuf>) -> Result<()> {
        // Prefetches reside outside the installed library. Only reclaim the executable
        // identified by its release record, so an unrelated file in a cache stays put.
        self.category = "Downloaded builders";
        for folder in children(&root.join("longshot-builder/downloads"))? {
            if !direct(&folder) || !players::is_dir(&folder) {
                continue;
            }
            let manifest = folder.join("longshot_release.json");
            if let Some(record) =
                text_file(&manifest).and_then(|s| serde_json::from_str::<Value>(&s).ok())
                && record["v"] == 1
                && record["version"]
                    .as_str()
                    .is_some_and(|tag| folder.file_name().is_some_and(|n| n == tag))
                && let Some(program @ ("Longshot.x86_64" | "Longshot.exe")) =
                    record["program"].as_str()
                && let Some(size) = record["full"]["size"].as_u64().filter(|s| *s > 0)
                && fs::symlink_metadata(folder.join(program))
                    .is_ok_and(|m| m.is_file() && m.len() == size)
            {
                self.add(
                    &folder.join(program),
                    &folder,
                    Some(&folder.join(".launcher-fetch.lock")),
                    None,
                )?;
            }
        }
        self.category = "Compiler caches";
        let stamp = root.join("system_headers.txt");
        // Old make_game.py's compiler fingerprint is hexadecimal. Its object store uses the
        // first sixteen digits as its namespace. Unknown files and game dumps are left alone.
        if let Some(value) = text_file(&stamp) {
            let stamp_value = value.trim();
            if stamp_value.len() == 40 && stamp_value.bytes().all(|c| c.is_ascii_hexdigit()) {
                let old = root.join("objcache");
                for file in children(&old)?.into_iter().filter(|p| generated_object(p)) {
                    self.add(&file, &old, None, None)?;
                }
                let store = root.join("obj-store");
                for namespace in children(&store)? {
                    let key = namespace
                        .file_name()
                        .and_then(|n| n.to_str())
                        .unwrap_or_default();
                    if key.len() != 16 || !key.bytes().all(|c| c.is_ascii_hexdigit()) {
                        continue;
                    }
                    for file in children(&namespace)?
                        .into_iter()
                        .filter(|p| generated_object(p))
                    {
                        self.add(&file, &namespace, None, None)?;
                    }
                }
            }
        }
        self.category = "Build cache";
        let builds = children(&root.join("longshot-builder/builds"))?;
        let previous = children(&root.join("longshot-builder/work"))?;
        for work in builds.into_iter().chain(previous) {
            let Some(note) = text_file(&work.join("game_folder.txt")) else {
                continue;
            };
            let out = Path::new(note.trim());
            let stable_build =
                direct(out) && out.canonicalize().is_ok_and(|p| self.stable.contains(&p));
            if !out.is_absolute()
                || (!stable_build && self.protected.iter().any(|p| overlap(p, out)))
            {
                continue;
            }
            let owned_output = |out: &Path, roots: &BTreeSet<PathBuf>, managed: bool| {
                roots.iter().any(|root| out.strip_prefix(root).is_ok_and(|rel| {
                    let parts = rel.components().collect::<Vec<_>>();
                    if managed { matches!(parts.as_slice(),[Component::Normal(builds),Component::Normal(_)] if *builds == "builds") }
                    else { matches!(parts.as_slice(),[Component::Normal(_),Component::Normal(builds),Component::Normal(_)] if *builds == "builds") }
                }))
            };
            let unused = if stable_build {
                true
            } else if out.try_exists()? {
                direct(out) && owned_output(&out.canonicalize()?, &self.unused, true)
            } else {
                owned_output(out, libraries, false) || owned_output(out, &self.unused, true)
            };
            if !unused {
                continue;
            }
            let lock = work.join("longshot-builder.lock");
            let lock = Some(lock.as_path());
            for name in [
                "image/image.bin",
                "image/image.json",
                "build/compile_state.json",
                "build_times.json",
                "build.log",
            ] {
                self.add(&work.join(name), &work, lock, None)?;
            }
            let build = work.join("build");
            self.game_outputs(&build, &work, lock, None)?;
            self.game_outputs(&work.join("finish"), &work, lock, None)?;
            for name in [
                "longshot.map",
                "longshot.exe.map",
                "longshot_vk.so.map",
                "longshot_vk.dll.map",
            ] {
                self.add(&build.join(name), &work, lock, None)?;
            }
            let state = build.join("compile_state.json");
            if direct(&state) && players::is_file(&state) && state.metadata()?.len() <= 128 << 20 {
                #[derive(serde::Deserialize)]
                struct CompileState {
                    seconds: std::collections::HashMap<String, serde::de::IgnoredAny>,
                }
                if let Ok(state) = serde_json::from_reader::<_, CompileState>(
                    std::io::BufReader::new(fs::File::open(state)?),
                ) {
                    for name in state.seconds.keys() {
                        let file = Path::new(name);
                        if file.file_name() == Some(file.as_os_str())
                            && matches!(
                                file.extension().and_then(|ext| ext.to_str()),
                                Some("o" | "obj")
                            )
                        {
                            self.add(&build.join("obj").join(file), &work, lock, None)?;
                        }
                    }
                }
            }
        }
        Ok(())
    }

    fn old_tools(&mut self, root: &Path) -> Result<()> {
        for tool in children(&root.join("toolchains"))? {
            let Some(record) = text_file(&tool.join("toolchain.json"))
                .and_then(|s| serde_json::from_str::<Value>(&s).ok())
            else {
                continue;
            };
            if record["kind"] == "LongshotBuild"
                && let Some(
                    exe @ ("Longshot.exe"
                    | "Longshot.x86_64"
                    | "longshot-builder.exe"
                    | "longshot-builder"),
                ) = record["run"].as_str()
            {
                self.add(&tool.join(exe), &tool, None, None)?;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
pub(super) fn plan(root: &Path) -> Result<Plan> {
    plan_for(root, &Scope::All)
}

pub(super) fn plan_for(root: &Path, scope: &Scope) -> Result<Plan> {
    let settings = home::settings(root)?;
    let modern = game::registry::read(root, game::registry::OWN)?;
    let mut protected: Vec<_> = storage::Protected::gather(root, storage::Protection::Reclaim)?
        .into_paths()
        .collect();
    let stable_settings = home::read_json(&root.join("settings.json"))?;
    if home::stable_settings(&stable_settings) {
        for key in ["nup", "exefs", "romfs"] {
            if let Some(path) = stable_settings[key].as_str().filter(|s| !s.is_empty()) {
                protected.push(root.join(path));
            }
        }
    }
    let stable_data = game::registry::stable_view(root)?;
    for registry in [&modern, &stable_data] {
        for row in registry["builds"]
            .as_array()
            .context("Invalid versions registry.")?
        {
            for key in ["nup", "exefs", "romfs"] {
                if let Some(value) = row[key].as_str().filter(|s| !s.is_empty()).map(Path::new)
                    && value.is_relative()
                {
                    protected.push(root.join(value));
                    if let Some(folder) = row["folder"].as_str().filter(|s| !s.is_empty()) {
                        protected.push(root.join(folder).join(value));
                    }
                }
            }
        }
    }

    let managed = game::managed_folders(root)?;
    protected.extend(game::playable_folders(root)?);
    let retired: BTreeSet<_> = modern["retiredFolders"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .map(|p| root.join(p))
        .filter(|p| game::is_retired(p))
        .filter_map(|p| p.canonicalize().ok())
        .collect();
    let pinned = settings["rollbackVersion"].as_str();
    for folder in &managed {
        if pinned.is_some_and(|tag| folder.file_name().is_some_and(|n| n == tag))
            && !folder.canonicalize().is_ok_and(|p| retired.contains(&p))
        {
            protected.push(folder.clone());
        }
    }
    for key in ["nup", "exefs", "romfs"] {
        if let Some(path) = settings[key].as_str().filter(|s| !s.is_empty()) {
            protected.push(root.join(path));
        }
    }
    if let Some(romfs) = settings["romfs"].as_str().map(Path::new)
        && let Some(source) = romfs.parent()
    {
        protected.push(source.to_owned());
    }

    for row in modern["builds"]
        .as_array()
        .context("Invalid versions registry.")?
    {
        for key in ["folder", "previousFolder"] {
            if let Some(path) = row[key].as_str().filter(|s| !s.is_empty()) {
                let path = root.join(path);
                if key != "previousFolder"
                    || !path.canonicalize().is_ok_and(|p| retired.contains(&p))
                {
                    protected.push(path);
                }
            }
        }
    }
    // A standalone game or an installed executable launched directly still uses
    // the renderer default. Its contents cannot be attributed to an old launcher.
    protected.extend(storage::standalone_shader_store());
    let active_work = home::work(root, &settings);
    let store = home::shader_store(&active_work.join("longshot-builder"))?;
    if store != Path::new("0") {
        protected.push(store);
    }
    let protected = protected
        .into_iter()
        .map(|p| p.canonicalize().unwrap_or(p))
        .collect();
    let stable: Vec<_> = game::registry::stable_only(root)?
        .into_iter()
        .filter(|p| direct(p))
        .filter_map(|p| p.canonicalize().ok())
        .collect();
    let mut scan = Scan {
        category: "Build tools",
        plan: Plan::default(),
        protected,
        stable,
        unused: BTreeSet::new(),
    };
    if *scope != Scope::All {
        for game in scan.stable.clone() {
            scan.extraction(&game)?;
        }
    }
    scan.protected
        .extend(scan.stable.iter().map(|game| game.join("romfs")));
    let mut work = storage::work_folders(root, &settings);
    work.insert(home::previous_work(root));

    for version in managed {
        let actual = version.canonicalize()?;
        if scan.protected.iter().any(|p| overlap(p, &actual)) {
            continue;
        }
        let marker = actual.join(".launcher-installing");
        let unfinished =
            direct(&marker) && players::is_file(&marker) && marker.metadata()?.len() == 0;
        let prefetched = children(&actual.join("builds"))?.is_empty()
            && text_file(&actual.join("longshot_release.json"))
                .and_then(|s| serde_json::from_str::<Value>(&s).ok())
                .is_some_and(|record| {
                    record["v"] == 1
                        && record["version"]
                            .as_str()
                            .is_some_and(|tag| actual.file_name().is_some_and(|n| n == tag))
                });
        if retired.contains(&actual) || unfinished || prefetched {
            if *scope == Scope::All {
                scan.unused_version(&actual)?;
            }
            scan.unused.insert(actual);
        }
    }
    for path in scan.stable.clone() {
        scan.stable_outputs(&path)?;
    }
    if *scope == Scope::All {
        scan.old_tools(root)?;
    }
    let libs = BTreeSet::from([root.join("versions"), game::install_root(root, &settings)]);
    let mut previous_folders: BTreeSet<_> = scan.stable.iter().cloned().collect();
    if *scope != Scope::All {
        work = storage::stable_work_folders(root, &active_work)?.unwrap_or_default();
        for previous in &work {
            previous_folders.insert(previous.join("objcache"));
            previous_folders.insert(previous.join("obj-store"));
            let cache = previous.join("longshot-builder");
            for (part, category) in storage::PREVIOUS_CACHES {
                let dir = cache.join(part);
                if scan.protected.iter().any(|p| overlap(p, &dir)) {
                    continue;
                }
                scan.category = category;
                scan.generated_tree(&dir, &dir, None)?;
                previous_folders.insert(dir);
            }
            for part in ["downloads", "builds", "work"] {
                previous_folders.insert(cache.join(part));
            }
        }
    }
    for root in work {
        scan.work(&root, &libs)?
    }
    if let Scope::Folder(path, category) = scope {
        anyhow::ensure!(direct(path), "The previous launcher folder changed.");
        let folder = path.canonicalize()?;
        anyhow::ensure!(
            previous_folders
                .iter()
                .any(|dir| dir.canonicalize().is_ok_and(|p| p == folder)),
            "This folder is no longer listed by the previous launcher."
        );
        scan.plan.files.retain(|path, file| {
            path.starts_with(&folder) && (category.is_empty() || file.category == category)
        });
    }
    if let Scope::Selected(categories) = scope {
        scan.plan.select(categories.clone());
    } else {
        scan.plan.scope = scope.clone();
    }
    scan.plan.keep_shared();
    Ok(scan.plan)
}
