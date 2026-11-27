//! The launcher-owned data directory and the JSON files shared with previous releases.
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use std::fs::{self, File};
use std::io::{ErrorKind, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub(crate) use crate::fsx::FileLease;

pub fn variable(key: &str) -> Option<PathBuf> {
    std::env::var_os(key)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
}

#[cfg(not(windows))]
fn xdg_folder(variable_name: &str, fallback: &str) -> Option<PathBuf> {
    variable(variable_name)
        .filter(|path| path.is_absolute())
        .or_else(|| {
            std::env::home_dir()
                .filter(|p| p.is_absolute())
                .map(|p| p.join(fallback))
        })
}

#[cfg(not(windows))]
pub(crate) fn system_cache_folder() -> Option<PathBuf> {
    xdg_folder("XDG_CACHE_HOME", ".cache")
}

/// The per-user application data folder used by both launcher generations.
pub(crate) fn system_data_folder() -> Result<PathBuf> {
    #[cfg(windows)]
    let data = match variable("LOCALAPPDATA").filter(|p| p.is_absolute()) {
        Some(folder) => folder,
        None => known_folder(&windows::Win32::UI::Shell::FOLDERID_LocalAppData)?,
    };
    #[cfg(not(windows))]
    let data = xdg_folder("XDG_DATA_HOME", ".local/share")
        .context("The application data folder is unavailable")?;
    Ok(std::path::absolute(data)?)
}

#[cfg(windows)]
pub(crate) fn roaming_data_folder() -> Result<PathBuf> {
    match variable("APPDATA").filter(|p| p.is_absolute()) {
        Some(path) => Ok(std::path::absolute(path)?),
        None => known_folder(&windows::Win32::UI::Shell::FOLDERID_RoamingAppData),
    }
}

#[cfg(windows)]
pub(crate) fn known_folder(id: &windows::core::GUID) -> Result<PathBuf> {
    use std::{ffi::OsString, os::windows::ffi::OsStringExt};
    use windows::Win32::{
        System::Com::CoTaskMemFree,
        UI::Shell::{KF_FLAG_DEFAULT, SHGetKnownFolderPath},
    };

    // The shell allocates the returned UTF-16 string with the COM allocator.
    unsafe {
        let raw = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None)?;
        let dir = OsString::from_wide(raw.as_wide());
        CoTaskMemFree(Some(raw.as_ptr().cast()));
        Ok(PathBuf::from(dir))
    }
}

pub fn resolve() -> Result<PathBuf> {
    if let Some(root) = variable("LONGSHOT_LAUNCHER_HOME") {
        return Ok(std::path::absolute(root)?);
    }
    if let Some(root) = portable_home() {
        return Ok(root);
    }
    Ok(system_data_folder()?.join(if cfg!(windows) {
        "LongshotLauncher"
    } else {
        "longshot/launcher"
    }))
}

mod portable;
use portable::portable_home;
pub(crate) use portable::{enable_portable, is_portable, portable_at};

/// Resolve owned versions within the current portable copy. Repair missing game
/// inputs when the same relative path exists on the relocated drive.
pub(crate) fn relocate(root: &Path, registry: &mut Value) -> Result<bool> {
    let old_root = registry["home"].as_str().map(PathBuf::from);
    if old_root.as_deref() == Some(root) || registry.get("builds").is_none() {
        return Ok(false);
    }
    ensure!(
        registry["format"].as_u64() == Some(1),
        "Unknown portable version list format."
    );
    let here = root
        .parent()
        .context("Portable launcher needs a parent folder")?;
    let mut changed = false;
    for build in registry["builds"]
        .as_array_mut()
        .context("Invalid build list")?
    {
        let inferred = build["folder"].as_str().and_then(|p| {
            Path::new(p)
                .ancestors()
                .find(|a| a.file_name() == root.file_name())
                .map(Path::to_owned)
        });
        let Some(parent) = old_root
            .as_deref()
            .or(inferred.as_deref())
            .and_then(Path::parent)
        else {
            continue;
        };
        for field in ["folder", "nup", "romfs", "exefs"] {
            let Some(old) = build[field].as_str().map(Path::new) else {
                continue;
            };
            let Ok(relative) = old.strip_prefix(parent) else {
                continue;
            };
            if relative
                .components()
                .all(|c| matches!(c, std::path::Component::Normal(_)))
                && (field == "folder"
                    && old_root
                        .as_deref()
                        .is_some_and(|base| old.starts_with(base.join("versions")))
                    || !old.try_exists()?)
            {
                let candidate = here.join(relative);
                if candidate.try_exists()? {
                    build[field] = serde_json::to_value(candidate)?;
                    changed = true;
                }
            }
        }
    }
    if changed {
        registry["home"] = serde_json::to_value(root)?;
    }
    Ok(changed)
}

pub fn read_json(path: &Path) -> Result<Value> {
    match File::open(path) {
        Ok(file) => serde_json::from_reader(std::io::BufReader::new(file))
            .with_context(|| format!("Cannot read {}", display(path))),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(json!({})),
        Err(error) => Err(error).with_context(|| format!("Cannot open {}", display(path))),
    }
}

pub fn write_json(path: &Path, value: &Value) -> Result<()> {
    crate::fsx::atomic_write(path, None, true, |file| {
        serde_json::to_writer_pretty(&mut *file, value)?;
        file.write_all(b"\n")?;
        Ok(())
    })
}

/// These fields are emitted together by the .NET launcher, which also calls
/// its current settings format 2. The integer alone cannot identify the writer.
pub(crate) fn stable_settings(value: &Value) -> bool {
    value["relistedLostBuilds"].is_boolean()
        && value["keptFolders"].is_array()
        && value["keptFolderNames"].is_array()
}

pub(crate) const SETTINGS_FILE: &str = "launcher-settings.json";

pub(crate) fn preferences_file(home: &Path) -> Result<(PathBuf, Value)> {
    let path = home.join(SETTINGS_FILE);
    let mut value = read_json(&path)?;
    let object = value
        .as_object_mut()
        .context("Launcher settings must be a JSON object.")?;
    let format = object.entry("format").or_insert(json!(2));
    ensure!(*format == 2, "This settings file needs a newer launcher.");
    Ok((path, value))
}

pub(crate) fn reset_preferences(home: &Path) -> Result<()> {
    crate::fsx::remove_file_if_present(&home.join(SETTINGS_FILE))?;
    Ok(())
}

pub fn settings(home: &Path) -> Result<Value> {
    Ok(preferences_file(home)?.1)
}

static SETTINGS_WRITE: Mutex<()> = Mutex::new(());

pub fn patch_settings(home: &Path, patch: &Value) -> Result<()> {
    let _guard = SETTINGS_WRITE
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let (path, mut current) = preferences_file(home)?;
    let object = current.as_object_mut().context("Invalid settings")?;
    for (key, value) in patch.as_object().context("Invalid settings change")? {
        if key == "work"
            && let Some(old) = object.get(key).and_then(Value::as_str)
            && Some(old) != value.as_str()
        {
            let old = json!(old);
            let mut earlier = object
                .get("earlierWork")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default();
            if !earlier.contains(&old) {
                earlier.push(old);
                earlier.drain(..earlier.len().saturating_sub(16));
                object.insert("earlierWork".into(), Value::Array(earlier));
            }
        }
        if value.is_null() {
            object.remove(key);
        } else {
            object.insert(key.clone(), value.clone());
        }
    }
    write_json(&path, &current)
}

#[cfg(unix)]
pub(crate) type Instance = FileLease;

#[cfg(unix)]
fn lock_instance(home: &Path) -> Result<Option<Instance>> {
    fs::create_dir_all(home)?;
    let file = File::options()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(home.join("launcher.lock"))?;
    match FileLease::try_acquire(file, crate::fsx::LockMode::Exclusive) {
        Ok(lease) => Ok(Some(lease)),
        Err(std::fs::TryLockError::WouldBlock) => Ok(None),
        Err(std::fs::TryLockError::Error(error)) => Err(error.into()),
    }
}

// Use the named mutex held by previous Windows releases during an upgrade.
#[cfg(windows)]
pub struct Instance(windows_sys::Win32::Foundation::HANDLE);

#[cfg(windows)]
impl Drop for Instance {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::System::Threading::ReleaseMutex(self.0);
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}

#[cfg(windows)]
fn lock_instance(home: &Path) -> Result<Option<Instance>> {
    use sha2::{Digest, Sha256};
    use windows_sys::Win32::{Foundation::*, System::Threading::*};
    fs::create_dir_all(home)?;
    let normalized: PathBuf = std::path::absolute(home)?.components().collect();
    let hash = format!(
        "{:x}",
        Sha256::digest(normalized.to_string_lossy().to_uppercase().as_bytes())
    );
    let name: Vec<u16> = format!(r"Local\LongshotLauncher-{}", &hash[..32])
        .encode_utf16()
        .chain(Some(0))
        .collect();
    let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
    ensure!(
        !handle.is_null(),
        "Cannot open the launcher mutex: {}",
        std::io::Error::last_os_error()
    );
    match unsafe { WaitForSingleObject(handle, 0) } {
        WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(Some(Instance(handle))),
        result => {
            let error = std::io::Error::last_os_error();
            unsafe {
                CloseHandle(handle);
            }
            if result == WAIT_TIMEOUT {
                Ok(None)
            } else {
                Err(error.into())
            }
        }
    }
}

pub fn acquire(home: &Path) -> Result<Option<Instance>> {
    let Some(guard) = lock_instance(home)? else {
        return Ok(None);
    };
    let (path, initial) = preferences_file(home)?;
    if !path.try_exists()? {
        write_json(&path, &initial)?;
    }
    if is_portable(home) {
        portable::relocate_state(home)?;
    }
    preserve_installed_locations(home)?;
    Ok(Some(guard))
}

/// Keep stable's launcher control directory for the single-instance lock and upgrade
/// registry. Personal data and rebuildable cache each have their own default.
fn installed_folders(control: &Path) -> Option<(PathBuf, PathBuf)> {
    if variable("LONGSHOT_LAUNCHER_HOME").is_some() || is_portable(control) {
        return None;
    }
    let base = system_data_folder().ok()?;
    #[cfg(windows)]
    {
        (base.join("LongshotLauncher") == control)
            .then(|| (base.join("Longshot/data"), base.join("Longshot/cache")))
    }
    #[cfg(not(windows))]
    {
        let data = base.join("longshot");
        if data.join("launcher") != control {
            return None;
        }
        Some((data, system_cache_folder()?.join("longshot")))
    }
}

/// The prior default is still included when looking for old caches the player can clean.
pub fn previous_work(control: &Path) -> PathBuf {
    #[cfg(not(windows))]
    if let Some((_, cache)) = installed_folders(control) {
        return cache.join("launcher/work");
    }
    control.join("work")
}

fn preserve_installed_locations(control: &Path) -> Result<()> {
    if installed_folders(control).is_none() {
        return Ok(());
    }
    let settings = settings(control)?;
    let migration = if cfg!(windows) {
        "windowsFolderDefaults"
    } else {
        "linuxFolderDefaults"
    };
    if settings[migration] == 1 {
        return Ok(());
    }
    let mut patch = json!({(migration):1});
    let populated = |dir: &Path| match fs::read_dir(dir) {
        Ok(mut entries) => entries.next().is_some(),
        Err(error) => error.kind() != ErrorKind::NotFound,
    };
    let old_data = ["profiles", "mods", "versions"]
        .into_iter()
        .any(|name| populated(&control.join(name)));
    if settings["dataFolder"]
        .as_str()
        .is_none_or(|p| !Path::new(p).is_absolute())
        && (old_data
            || settings["buildsFolder"]
                .as_str()
                .is_some_and(|s| !s.is_empty()))
    {
        patch["dataFolder"] = json!(control);
    }
    let old_work = previous_work(control);
    if settings["work"].as_str().is_none_or(str::is_empty) && populated(&old_work) {
        patch["work"] = json!(old_work);
    }
    patch_settings(control, &patch)
}

/// Profiles and versions can move while settings, locks and the registry stay in the launcher home.
pub fn data_folder(root: &Path, settings: &Value) -> PathBuf {
    settings["dataFolder"]
        .as_str()
        .map(Path::new)
        .filter(|path| path.is_absolute())
        .map(|path| crate::fsx::plain(path).into_owned())
        .unwrap_or_else(|| {
            installed_folders(root).map_or_else(|| root.to_owned(), |(data, _)| data)
        })
}

/// Stable's builder licence receipts remain available when switching launcher versions.
pub(crate) fn builder_data(root: &Path) -> PathBuf {
    let original = root.join("builder-data");
    if original.exists() {
        return original;
    }
    installed_folders(root).map_or(original, |(data, _)| data.join("builder-data"))
}

pub fn data_dir(root: &Path) -> Result<PathBuf> {
    Ok(data_folder(root, &settings(root)?))
}

/// A configured cache folder overrides the platform default. Folders are read in their plain
/// spelling: a launcher before this one saved them verbatim (`\\?\X:\...`), which
/// longshot-builder's clang can't read (#297).
pub fn work(root: &Path, settings: &Value) -> PathBuf {
    if let Some(path) = settings["work"].as_str().filter(|s| !s.is_empty()) {
        return crate::fsx::plain(Path::new(path)).into_owned();
    }
    if root.file_name().is_some_and(|name| name == "data")
        && let Some(parent) = root.parent()
        && crate::players::is_file(&parent.join(".portable"))
        && is_portable(root)
    {
        return parent.join("cache");
    }
    installed_folders(root).map_or_else(|| root.join("work"), |(_, cache)| cache)
}

/// Preparation and play must use the same SPIR-V store. The engine treats "0"
/// as disabled. Resolve relative overrides before changing the child's working directory.
pub fn shader_store(cache: &Path) -> Result<PathBuf> {
    resolve_shader_store(cache, variable("LONGSHOT_SHADER_STORE"))
}

fn resolve_shader_store(cache: &Path, explicit: Option<PathBuf>) -> Result<PathBuf> {
    match explicit {
        Some(path) if path == Path::new("0") => Ok(path),
        Some(path) => Ok(std::path::absolute(path)?),
        None => Ok(cache.join("shader-store")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shader_store_follows_cache_unless_overridden_or_disabled() -> Result<()> {
        let t = tempfile::tempdir()?;
        let cache = t.path().join("custom-cache");
        assert_eq!(
            resolve_shader_store(&cache, None)?,
            cache.join("shader-store")
        );
        assert_eq!(
            resolve_shader_store(&cache, Some("0".into()))?,
            Path::new("0")
        );
        let relative = PathBuf::from("relative-store");
        assert_eq!(
            resolve_shader_store(&cache, Some(relative.clone()))?,
            std::path::absolute(relative)?
        );
        Ok(())
    }

    #[cfg(windows)]
    #[test]
    fn windows_keeps_the_stable_control_folder_and_separates_fresh_data_and_cache() -> Result<()> {
        const CHILD: &str = "LONGSHOT_TEST_WINDOWS_FOLDERS";
        if let Ok(case) = std::env::var(CHILD) {
            let base = system_data_folder()?;
            let root = resolve()?;
            assert_eq!(root, base.join("LongshotLauncher"));
            if case == "existing" {
                fs::create_dir_all(root.join("profiles/default"))?;
                fs::create_dir_all(root.join("work/longshot-builder"))?;
            }
            let _guard = acquire(&root)?.context("Fixture is already running")?;
            let settings = settings(&root)?;
            let (data, cache) = if case == "existing" {
                (root.clone(), root.join("work"))
            } else {
                (base.join("Longshot/data"), base.join("Longshot/cache"))
            };
            assert_eq!(data_folder(&root, &settings), data);
            assert_eq!(work(&root, &settings), cache);
        } else {
            let temp = tempfile::tempdir()?;
            for case in ["fresh", "existing"] {
                let output = std::process::Command::new(std::env::current_exe()?)
                    .args(["--exact", "home::tests::windows_keeps_the_stable_control_folder_and_separates_fresh_data_and_cache"])
                    .env(CHILD, case)
                    .env("LOCALAPPDATA", temp.path().join(case))
                    .env_remove("LONGSHOT_LAUNCHER_HOME")
                    .env_remove("LONGSHOT_LAUNCHER_EXE")
                    .output()?;
                assert!(
                    output.status.success(),
                    "{}",
                    String::from_utf8_lossy(&output.stdout)
                );
            }
        }
        Ok(())
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn stable_linux_home_uses_absolute_xdg_paths_and_the_account_home() -> Result<()> {
        // Isolate process-wide environment changes from the other launcher tests.
        const CHILD: &str = "LONGSHOT_TEST_LINUX_HOME";
        if let Ok(scenario) = std::env::var(CHILD) {
            let (data, cache_dir) = if scenario != "account" && scenario != "relative" {
                (std::path::absolute("data")?, std::path::absolute("cache")?)
            } else {
                let user = std::env::home_dir().context("Missing account home")?;
                (user.join(".local/share"), user.join(".cache"))
            };
            let root = resolve()?;
            assert_eq!(root, data.join("longshot/launcher"));
            assert_eq!(data_folder(&root, &Value::Null), data.join("longshot"));
            assert_eq!(work(&root, &Value::Null), cache_dir.join("longshot"));
            assert_eq!(
                previous_work(&root),
                cache_dir.join("longshot/launcher/work")
            );
            if scenario == "account" || scenario == "relative" {
                // With HOME unset this is the actual account home. Only resolve it.
                return Ok(());
            }
            let base_data = data.join("longshot");
            let base_cache = cache_dir.join("longshot");
            let old_work = previous_work(&root);
            let mut expected_data = base_data.clone();
            let mut expected_cache = base_cache.clone();
            fs::create_dir_all(root.join("versions"))?;
            fs::create_dir_all(&old_work)?;
            if scenario == "legacy" || scenario == "custom" {
                fs::create_dir_all(root.join("profiles/default"))?;
                fs::create_dir_all(root.join("builder-data/licenses"))?;
                fs::create_dir_all(old_work.join("longshot-builder/store"))?;
                fs::write(old_work.join("longshot-builder/store/old"), b"keep")?;
                expected_data = root.clone();
                expected_cache = old_work.clone();
            }
            if scenario == "custom" {
                expected_data = std::path::absolute("elsewhere/data")?;
                expected_cache = std::path::absolute("elsewhere/cache")?;
                patch_settings(
                    &root,
                    &json!({"dataFolder":expected_data,"work":expected_cache}),
                )?;
            }
            let _instance = acquire(&root)?.context("isolated home is already running")?;
            let config = settings(&root)?;
            assert_eq!(data_folder(&root, &config), expected_data);
            assert_eq!(work(&root, &config), expected_cache);
            let profile = expected_data.join("profiles/default");
            fs::create_dir_all(&profile)?;
            let exe = root.join("fixture-builder");
            let cmd = crate::game::mod_command_for(&root, &exe, &profile)?;
            let env: std::collections::HashMap<_, _> = cmd.get_envs().collect();
            let physical_cache = expected_cache.join("longshot-builder");
            let receipts = if scenario == "xdg" {
                base_data.join("builder-data")
            } else {
                root.join("builder-data")
            };
            for (key, expected) in [
                ("LONGSHOT_DATA", receipts),
                ("LONGSHOT_CACHE", physical_cache.clone()),
                ("LONGSHOT_STORE", physical_cache.join("store")),
                ("LONGSHOT_SHADER_STORE", physical_cache.join("shader-store")),
            ] {
                assert_eq!(env[std::ffi::OsStr::new(key)], Some(expected.as_os_str()));
            }
            if scenario != "xdg" {
                assert_eq!(
                    fs::read(old_work.join("longshot-builder/store/old"))?,
                    b"keep"
                );
                patch_settings(&root, &json!({"dataFolder":null, "work":null}))?;
                preserve_installed_locations(&root)?;
                let config = settings(&root)?;
                assert_eq!(data_folder(&root, &config), base_data);
                assert_eq!(work(&root, &config), base_cache);
            }
        } else {
            let tmp = tempfile::tempdir()?;
            for scenario in ["xdg", "legacy", "custom", "relative", "account"] {
                let scenario_root = tmp.path().join(scenario);
                fs::create_dir(&scenario_root)?;
                let mut command = std::process::Command::new(std::env::current_exe()?);
                command.current_dir(&scenario_root)
                    .args(["--exact", "home::tests::stable_linux_home_uses_absolute_xdg_paths_and_the_account_home"])
                    .env(CHILD, scenario)
                    .env_remove("LONGSHOT_LAUNCHER_HOME")
                    .env_remove("HOME")
                    .env_remove("APPIMAGE")
                    .env_remove("LONGSHOT_SHADER_STORE")
                    .env_remove("XDG_DATA_HOME")
                    .env_remove("XDG_CACHE_HOME");
                if scenario == "relative" {
                    command
                        .env("HOME", &scenario_root)
                        .env("XDG_DATA_HOME", "data")
                        .env("XDG_CACHE_HOME", "cache");
                } else if scenario != "account" {
                    command
                        .env("XDG_DATA_HOME", scenario_root.join("data"))
                        .env("XDG_CACHE_HOME", scenario_root.join("cache"));
                }
                let child = command.output()?;
                assert!(
                    child.status.success(),
                    "{}",
                    String::from_utf8_lossy(&child.stdout)
                );
            }
        }
        Ok(())
    }

    #[test]
    fn moving_a_portable_install_repairs_missing_paths_but_keeps_live_inputs() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("new/portable.home");
        let previous = tmp.path().join("old/portable.home");
        let original_dump = previous.join("dump");
        fs::create_dir_all(&original_dump)?;
        fs::create_dir_all(root.join("game"))?;
        fs::create_dir_all(root.join("dump"))?;
        for recorded_home in [Value::Null, json!(&previous)] {
            write_json(
                &root.join(crate::game::registry::OWN),
                &json!({
                    "format": 1, "home": recorded_home, "custom": true,
                    "builds": [{"folder": previous.join("game"), "nup": original_dump, "custom": 42}]
                }),
            )?;
            crate::game::registry::prepare(&root, true)?;
            let registry = read_json(&root.join(crate::game::registry::OWN))?;
            assert_eq!(registry["home"], json!(&root));
            assert_eq!(registry["builds"][0]["folder"], json!(root.join("game")));
            assert_eq!(registry["builds"][0]["nup"], json!(&original_dump));
            assert_eq!(registry["builds"][0]["custom"], 42);
            assert_eq!(registry["custom"], true);
        }
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn dropping_instance_releases_lock_even_with_an_inherited_descriptor() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let instance = acquire(temp.path())?.unwrap();
        let _inherited = instance.0.try_clone()?;
        assert!(acquire(temp.path())?.is_none());
        drop(instance);
        assert!(acquire(temp.path())?.is_some());
        Ok(())
    }

    #[test]
    fn fresh_preferences_do_not_import_old_launcher_settings() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let original = json!({"format":2,"work":"stable cache","currentProfile":"p-1234abcd"});
        let old = root.join("settings.json");
        let preview = root.join("settings-v2.json");
        write_json(&old, &original)?;
        write_json(&preview, &json!({"format":2,"uiLanguage":"de"}))?;
        let _instance = acquire(root)?.expect("isolated launcher");
        assert!(root.join(SETTINGS_FILE).is_file());
        assert_eq!(settings(root)?, json!({"format":2}));
        patch_settings(root, &json!({"uiLanguage":"fr","work":"new cache"}))?;
        assert_eq!(settings(root)?["uiLanguage"], "fr");
        patch_settings(root, &json!({"work":null}))?;
        assert_eq!(settings(root)?["earlierWork"], json!(["new cache"]));
        assert!(read_json(&root.join(SETTINGS_FILE))?.get("work").is_none());
        assert_eq!(read_json(&old)?, original);
        reset_preferences(root)?;
        assert_eq!(settings(root)?, json!({"format":2}));
        assert_eq!(read_json(&preview)?["uiLanguage"], "de");
        Ok(())
    }

    #[test]
    fn preferences_leave_a_previous_stable_installation_intact() -> Result<()> {
        let home = tempfile::tempdir()?;
        let path = home.path().join("settings.json");
        let stable = br#"{"format":1,"work":"old","custom":42}"#;
        fs::write(&path, stable)?;
        patch_settings(home.path(), &json!({"work":"new"}))?;
        assert_eq!(fs::read(&path)?, stable);
        assert_eq!(settings(home.path())?, json!({"format":2,"work":"new"}));
        fs::write(home.path().join(SETTINGS_FILE), b"broken")?;
        assert!(patch_settings(home.path(), &json!({"work":null})).is_err());

        let home = tempfile::tempdir()?;
        let path = home.path().join("settings.json");
        let stable = br#"{"format":2,"relistedLostBuilds":true,"keptFolders":[],"keptFolderNames":[],"work":"original","currentProfile":"p-1234abcd"}"#;
        fs::write(&path, stable)?;
        assert!(settings(home.path())?["work"].is_null());
        patch_settings(home.path(), &json!({"work":"moved"}))?;
        assert_eq!(fs::read(&path)?, stable);
        // Returning to stable can update its own preferences. The Rust choice
        // and profile selection survive the next upgrade.
        write_json(
            &path,
            &json!({"format":2, "relistedLostBuilds":true,"keptFolders":[],"keptFolderNames":[], "work":"stable again"}),
        )?;
        assert_eq!(settings(home.path())?["work"], "moved");
        assert!(settings(home.path())?["currentProfile"].is_null());
        Ok(())
    }
}

/// A local path as shown to the player. Filesystem operations retain the original path.
pub fn display(path: &Path) -> String {
    let path = crate::fsx::plain(path);
    let home = std::env::home_dir();
    let home = home.as_deref().map(crate::fsx::plain);
    abbreviate_home(path.as_ref(), home.as_deref())
        .to_string_lossy()
        .into_owned()
}

fn abbreviate_home(path: &Path, home: Option<&Path>) -> PathBuf {
    let Some(home) = home.filter(|dir| dir.is_absolute()) else {
        return path.to_owned();
    };
    let mut parts = path.components();
    for expected in home.components() {
        let Some(actual) = parts.next() else {
            return path.to_owned();
        };
        let equal = if cfg!(windows) {
            actual
                .as_os_str()
                .eq_ignore_ascii_case(expected.as_os_str())
        } else {
            actual == expected
        };
        if !equal {
            return path.to_owned();
        }
    }
    if parts.as_path().as_os_str().is_empty() {
        PathBuf::from("~")
    } else {
        Path::new("~").join(parts.as_path())
    }
}

#[cfg(test)]
mod display_tests {
    use super::*;
    #[test]
    fn abbreviates_only_home_and_its_descendants() {
        let home = std::env::temp_dir().join("player");
        assert_eq!(abbreviate_home(&home, Some(&home)), Path::new("~"));
        let nested = Path::new("saves").join("slot");
        assert_eq!(
            abbreviate_home(&home.join(&nested), Some(&home)),
            Path::new("~").join(nested)
        );
        let sibling = home.with_file_name("player-two");
        assert_eq!(abbreviate_home(&sibling, Some(&home)), sibling);
        assert_eq!(abbreviate_home(&home, None), home);
    }
}
