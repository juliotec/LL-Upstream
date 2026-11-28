//! The safe launcher preferences the previous (v1.3.x) launcher had chosen.
//!
//! Only what a player would otherwise have to set again is reused: the launcher
//! language, the game language, and a custom build-files/cache folder. The
//! launcher's update channel, profile choice and every consent (telemetry,
//! licences) start fresh: those are deliberate choices, not setup to repeat.
//! The stable launcher's files are only read.

use crate::{fsx, game, home, i18n};
use anyhow::Result;
use serde_json::{Map, Value, json};
use std::path::Path;

/// Marks the one-time read, beside `stable-saves-v1.json`.
const MARKER: &str = "stable-prefs-v1.json";
/// The previous launcher's settings file in this launcher's home.
const STABLE_SETTINGS: &str = "settings.json";

pub(crate) fn link_from_stable(home: &Path) -> Result<()> {
    let marker = home.join(MARKER);
    if marker.try_exists()? {
        return Ok(());
    }
    let stable = home::read_json(&home.join(STABLE_SETTINGS))?;
    if !home::stable_settings(&stable) {
        return Ok(());
    }
    let current = home::settings(home)?;
    let mut patch = Map::new();
    // The launcher's own language, when it is one the launcher knows.
    if current.get("uiLanguage").is_none()
        && let Some(language) = stable["uiLanguage"].as_str().and_then(i18n::pick)
    {
        patch.insert("uiLanguage".into(), json!(language));
    }
    // The game's language, from the same menu the wizard offers.
    if current.get("language").is_none()
        && let Some(language) = stable["language"]
            .as_str()
            .filter(|language| game::LANGS.contains(language))
    {
        patch.insert("language".into(), json!(language));
    }
    // A custom build-files folder only: the old cache's contents are not reused.
    if current.get("work").is_none()
        && let Some(folder) = stable["work"]
            .as_str()
            .filter(|folder| !folder.is_empty())
            .map(Path::new)
            .filter(|folder| folder.is_absolute() && folder.is_dir())
    {
        patch.insert("work".into(), json!(fsx::plain(folder).to_string_lossy()));
    }
    if !patch.is_empty() {
        home::patch_settings(home, &Value::Object(patch))?;
    }
    home::write_json(&marker, &json!({"v":1}))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::fs;

    fn home_with_stable(root: &Path, stable: Value) -> Result<()> {
        let home = root.join("launcher");
        home::patch_settings(&home, &json!({"format":2}))?;
        home::write_json(&home.join(STABLE_SETTINGS), &stable)?;
        Ok(())
    }

    fn stable(extra: Value) -> Value {
        let mut value =
            json!({"format":2,"relistedLostBuilds":false,"keptFolders":[],"keptFolderNames":[]});
        for (key, field) in extra.as_object().unwrap() {
            value[key] = field.clone();
        }
        value
    }

    #[test]
    fn carries_the_safe_preferences_once() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = root.path().join("launcher");
        let cache = root.path().join("my cache");
        fs::create_dir_all(&cache)?;
        home_with_stable(
            root.path(),
            stable(json!({
                "uiLanguage":"fr",
                "language":"de",
                "work": cache.to_string_lossy(),
                "launcherChannel":"nightly",
                "sendPerformanceStatistics":true,
            })),
        )?;
        let stable_before = fs::read(home.join(STABLE_SETTINGS))?;

        link_from_stable(&home)?;
        let settings = home::settings(&home)?;
        assert_eq!(settings["uiLanguage"], "fr");
        assert_eq!(settings["language"], "de");
        assert_eq!(
            settings["work"].as_str(),
            Some(fsx::plain(&cache).to_string_lossy().as_ref())
        );
        // Deliberate choices and consents start fresh.
        assert!(settings["launcherChannel"].is_null());
        assert!(settings["sendPerformanceStatistics"].is_null());
        assert!(home.join(MARKER).is_file());
        // The previous launcher's file is only read.
        assert_eq!(fs::read(home.join(STABLE_SETTINGS))?, stable_before);

        // A second start changes nothing, and a player's later choice stays.
        home::patch_settings(&home, &json!({"uiLanguage":"it"}))?;
        link_from_stable(&home)?;
        assert_eq!(home::settings(&home)?["uiLanguage"], "it");
        Ok(())
    }

    #[test]
    fn a_value_chosen_here_is_not_replaced() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = root.path().join("launcher");
        home::patch_settings(
            &home,
            &json!({"format":2,"uiLanguage":"es","language":"ja"}),
        )?;
        home::write_json(
            &home.join(STABLE_SETTINGS),
            &stable(json!({"uiLanguage":"fr","language":"de"})),
        )?;

        link_from_stable(&home)?;
        let settings = home::settings(&home)?;
        assert_eq!(settings["uiLanguage"], "es");
        assert_eq!(settings["language"], "ja");
        Ok(())
    }

    #[test]
    fn no_stable_launcher_is_left_alone() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = root.path().join("launcher");
        home::patch_settings(&home, &json!({"format":2}))?;

        link_from_stable(&home)?;
        assert!(!home.join(MARKER).exists());
        assert!(home::settings(&home)?["uiLanguage"].is_null());
        Ok(())
    }

    #[test]
    fn an_unknown_language_or_missing_folder_is_skipped() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = root.path().join("launcher");
        home_with_stable(
            root.path(),
            stable(json!({
                "uiLanguage":"xx",
                "language":"xx",
                "work": root.path().join("gone").to_string_lossy(),
            })),
        )?;

        link_from_stable(&home)?;
        let settings = home::settings(&home)?;
        assert!(settings["uiLanguage"].is_null());
        assert!(settings["language"].is_null());
        assert!(settings["work"].is_null());
        Ok(())
    }
}
