//! Reuse the game file the previous (v1.3.x) launcher had selected.
//!
//! The stable launcher remembered the player's `.nup`/`.nx` or dump folder in
//! `settings.json`. A player upgrading to this launcher should not have to find
//! a 20+ GiB file again, so the first launcher start links that file as this
//! launcher's own source: the build wizard then opens with it already selected.
//! The stable launcher's files are only read.

use crate::{fsx, home, players};
use anyhow::Result;
use serde_json::{Map, Value, json};
use std::path::Path;

use super::{process, source};

/// Marks the one-time link, beside `stable-saves-v1.json` and `stable-mods-v1.json`.
pub(crate) const MARKER: &str = "stable-source-v1.json";

/// The stable launcher's settings file in this launcher's home.
const STABLE_SETTINGS: &str = "settings.json";
// Stored in the same atomic profile write as `game`, so an interrupted link
// can be distinguished from a source selected by the player.
const PENDING: &str = "_stable_source_pending";

enum Outcome {
    /// Nothing to do: no stable launcher, or this launcher already has a source.
    Nothing,
    /// The remembered game file is now this launcher's source.
    Linked,
    /// The previous launcher had a source, but it cannot be used any more.
    Unavailable,
}

/// The startup entry point: never fails a start; a link that could not be made
/// is only recorded in the log, and the player sees the normal picker.
pub(crate) fn link_stable_source(home: &Path) -> Result<()> {
    let linked = link(home);
    match &linked {
        Ok(Outcome::Linked) => crate::diagnostics::event("Previous launcher game file linked"),
        Ok(Outcome::Unavailable) => {
            crate::diagnostics::event("Previous launcher game file is no longer available")
        }
        Ok(Outcome::Nothing) => {}
        Err(_) => crate::diagnostics::event("Previous launcher game file could not be linked"),
    }
    linked.map(|_| ())
}

fn link(home: &Path) -> Result<Outcome> {
    link_with_settings_writer(home, home::patch_settings)
}

fn link_with_settings_writer(
    home: &Path,
    write_settings: impl FnOnce(&Path, &Value) -> Result<()>,
) -> Result<Outcome> {
    let marker = home.join(MARKER);
    if marker.try_exists()? {
        return Ok(Outcome::Nothing);
    }
    // Never replace a source the player chose in this launcher (or a newer
    // launcher's profile). The marker records that this launcher is settled.
    let current = home::settings(home)?;
    // Before the first profile exists (the onboarding state after every profile
    // was removed) there is nothing to attach the source to; a later start does it.
    if players::list_profiles(home)?.is_empty() {
        return Ok(Outcome::Nothing);
    }
    let profile = players::current_dir(home)?;
    let profile_file = home::read_json(&profile.join("profile.json"))?;
    let pending = profile_file[PENDING]
        .as_str()
        .filter(|path| !path.is_empty() && profile_file["game"].as_str() == Some(*path));
    if source::read(&current).is_some()
        || (non_empty(profile_file["game"].as_str()) && pending.is_none())
    {
        if let Some(saved) = profile_file.get(PENDING) {
            players::patch_profile(&profile, |record| {
                if record.get(PENDING) == Some(saved) {
                    record.remove(PENDING);
                }
            })?;
        }
        write_marker(home, None)?;
        return Ok(Outcome::Nothing);
    }
    let remembered = if let Some(path) = pending {
        path.into()
    } else {
        let stable = home::read_json(&home.join(STABLE_SETTINGS))?;
        if !home::stable_settings(&stable) {
            return Ok(Outcome::Nothing);
        }
        let Some(remembered) = source::read(&stable) else {
            return Ok(Outcome::Nothing);
        };
        remembered
    };
    let remembered = if remembered.is_absolute() {
        remembered
    } else {
        home.join(remembered)
    };
    // Content is checked by the builder when the player builds; here only the
    // path's shape and existence, as when the player picks it in the wizard.
    let Ok(validated) = process::validated_source(&remembered) else {
        return Ok(Outcome::Unavailable);
    };
    // Plain spelling: the builder and clang cannot read the verbatim form
    // `fs::canonicalize` gives on Windows (#297).
    let source_path = fsx::plain(&validated).into_owned();
    let text = source_path.to_string_lossy();
    let mut attached = false;
    players::patch_profile(&profile, |record| {
        let game = record.get("game").and_then(Value::as_str);
        let own_pending = game == Some(text.as_ref())
            && record.get(PENDING).and_then(Value::as_str) == game;
        if !non_empty(game) || own_pending {
            record.insert("game".into(), json!(text));
            record.insert(PENDING.into(), json!(text));
            attached = true;
        }
    })?;
    if !attached {
        write_marker(home, None)?;
        return Ok(Outcome::Nothing);
    }
    let mut patch = Map::new();
    for (key, value) in source::fields(&source_path)? {
        patch.insert(key.to_owned(), value);
    }
    write_settings(home, &Value::Object(patch))?;
    players::patch_profile(&profile, |record| {
        if record.get(PENDING).and_then(Value::as_str) == Some(text.as_ref()) {
            record.remove(PENDING);
        }
    })?;
    write_marker(home, Some(&source_path))?;
    Ok(Outcome::Linked)
}

fn non_empty(text: Option<&str>) -> bool {
    text.is_some_and(|value| !value.is_empty())
}

fn write_marker(home: &Path, source: Option<&Path>) -> Result<()> {
    let mut record = Map::new();
    record.insert("v".into(), json!(1));
    if let Some(source) = source {
        record.insert("source".into(), json!(source.to_string_lossy()));
    }
    home::write_json(&home.join(MARKER), &Value::Object(record))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::players::{current_dir, initialize};
    use std::fs;
    use std::path::PathBuf;

    /// A launcher home whose data folder is the test's own temp folder.
    fn launcher_home(root: &Path) -> Result<PathBuf> {
        let home = root.join("launcher");
        home::patch_settings(&home, &json!({"dataFolder": root.join("data")}))?;
        initialize(&home)?;
        Ok(home)
    }

    fn stable_settings(home: &Path, fields: Value) -> Result<()> {
        let mut stable =
            json!({"format":2,"relistedLostBuilds":false,"keptFolders":[],"keptFolderNames":[]});
        for (key, value) in fields.as_object().unwrap() {
            stable[key] = value.clone();
        }
        home::write_json(&home.join(STABLE_SETTINGS), &stable)
    }

    fn profile(home: &Path) -> Result<Value> {
        home::read_json(&current_dir(home)?.join("profile.json"))
    }

    fn plain(path: &Path) -> Result<String> {
        Ok(fsx::plain(&fs::canonicalize(path)?)
            .to_string_lossy()
            .into_owned())
    }

    #[test]
    fn links_the_stable_game_file_without_a_click() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let game = root.path().join("Ocarina of Time.nup");
        fs::write(&game, [])?;
        stable_settings(&home, json!({"nup": game.to_string_lossy()}))?;
        let stable_before = fs::read(home.join(STABLE_SETTINGS))?;

        assert!(matches!(link(&home)?, Outcome::Linked));
        let settings = home::settings(&home)?;
        assert_eq!(settings["nup"].as_str(), Some(plain(&game)?.as_str()));
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(plain(&game)?.as_str())
        );
        assert!(home.join(MARKER).is_file());
        // The stable launcher's own files are only read.
        assert_eq!(fs::read(home.join(STABLE_SETTINGS))?, stable_before);

        // A second start changes nothing.
        let after = home::settings(&home)?;
        assert!(matches!(link(&home)?, Outcome::Nothing));
        assert_eq!(home::settings(&home)?, after);
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(plain(&game)?.as_str())
        );
        Ok(())
    }

    #[test]
    fn replaces_an_empty_profile_source() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let game = root.path().join("game.nup");
        fs::write(&game, [])?;
        stable_settings(&home, json!({"nup": game.to_string_lossy()}))?;
        players::patch_profile(&current_dir(&home)?, |record| {
            record.insert("game".into(), json!(""));
        })?;

        assert!(matches!(link(&home)?, Outcome::Linked));
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(plain(&game)?.as_str())
        );
        assert_eq!(
            super::super::original_game(&current_dir(&home)?, &home::settings(&home)?)?,
            Some(fsx::plain(&fs::canonicalize(&game)?).into_owned())
        );
        Ok(())
    }

    #[test]
    fn resumes_after_profile_write_fails_before_settings_write() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let game = root.path().join("game.nup");
        fs::write(&game, [])?;
        stable_settings(&home, json!({"nup": game.to_string_lossy()}))?;

        assert!(link_with_settings_writer(&home, |_, _| anyhow::bail!("write failed")).is_err());
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(plain(&game)?.as_str())
        );
        assert!(home::settings(&home)?["nup"].is_null());
        assert!(!home.join(MARKER).exists());

        assert!(matches!(link(&home)?, Outcome::Linked));
        assert_eq!(
            home::settings(&home)?["nup"].as_str(),
            Some(plain(&game)?.as_str())
        );
        assert!(profile(&home)?[PENDING].is_null());
        assert!(home.join(MARKER).is_file());
        Ok(())
    }

    #[test]
    fn a_new_profile_choice_after_an_interruption_is_preserved() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let game = root.path().join("game.nup");
        let chosen = root.path().join("chosen.nup");
        fs::write(&game, [])?;
        fs::write(&chosen, [])?;
        stable_settings(&home, json!({"nup": game.to_string_lossy()}))?;
        assert!(link_with_settings_writer(&home, |_, _| anyhow::bail!("write failed")).is_err());
        players::patch_profile(&current_dir(&home)?, |record| {
            record.insert("game".into(), json!(chosen.to_string_lossy()));
        })?;

        assert!(matches!(link(&home)?, Outcome::Nothing));
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(chosen.to_string_lossy().as_ref())
        );
        assert!(home::settings(&home)?["nup"].is_null());
        Ok(())
    }

    #[test]
    fn links_a_dump_folder_the_same_way() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let dump = root.path().join("dump");
        fs::create_dir_all(dump.join("romfs"))?;
        fs::create_dir_all(dump.join("exefs"))?;
        fs::write(dump.join("exefs/main"), [])?;
        stable_settings(
            &home,
            json!({
                "romfs": dump.join("romfs").to_string_lossy(),
                "exefs": dump.join("exefs").to_string_lossy(),
            }),
        )?;

        assert!(matches!(link(&home)?, Outcome::Linked));
        let settings = home::settings(&home)?;
        let plain_dump = plain(&dump)?;
        assert_eq!(settings["nup"], Value::Null);
        assert_eq!(
            settings["romfs"].as_str(),
            Some(
                Path::new(&plain_dump)
                    .join("romfs")
                    .to_string_lossy()
                    .as_ref()
            )
        );
        assert_eq!(
            settings["exefs"].as_str(),
            Some(
                Path::new(&plain_dump)
                    .join("exefs")
                    .to_string_lossy()
                    .as_ref()
            )
        );
        assert_eq!(profile(&home)?["game"].as_str(), Some(plain_dump.as_str()));
        Ok(())
    }

    #[test]
    fn a_missing_file_is_not_linked_and_is_retried_next_start() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let gone = root.path().join("moved.nup");
        stable_settings(&home, json!({"nup": gone.to_string_lossy()}))?;

        assert!(matches!(link(&home)?, Outcome::Unavailable));
        assert!(home::settings(&home)?["nup"].is_null());
        assert!(profile(&home)?["game"].is_null());
        assert!(!home.join(MARKER).exists());

        // The drive is connected again: the next start links it.
        fs::write(&gone, [])?;
        assert!(matches!(link(&home)?, Outcome::Linked));
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(plain(&gone)?.as_str())
        );
        Ok(())
    }

    #[test]
    fn a_source_chosen_in_this_launcher_is_never_replaced() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        let stable_game = root.path().join("stable.nup");
        let mine = root.path().join("mine.nup");
        fs::write(&stable_game, [])?;
        fs::write(&mine, [])?;
        stable_settings(&home, json!({"nup": stable_game.to_string_lossy()}))?;
        home::patch_settings(&home, &json!({"nup": mine.to_string_lossy()}))?;
        players::patch_profile(&current_dir(&home)?, |record| {
            record.insert("game".into(), json!(mine.to_string_lossy()));
        })?;

        assert!(matches!(link(&home)?, Outcome::Nothing));
        assert_eq!(
            home::settings(&home)?["nup"].as_str(),
            Some(mine.to_string_lossy().as_ref())
        );
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(mine.to_string_lossy().as_ref())
        );
        assert!(home.join(MARKER).is_file());
        Ok(())
    }

    #[test]
    fn no_stable_launcher_is_left_alone() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = launcher_home(root.path())?;
        assert!(matches!(link(&home)?, Outcome::Nothing));
        assert!(!home.join(MARKER).exists());
        assert!(home::settings(&home)?["nup"].is_null());
        Ok(())
    }

    #[test]
    fn no_profile_yet_waits_for_one() -> Result<()> {
        let root = tempfile::tempdir()?;
        let home = root.path().join("launcher");
        let data = root.path().join("data");
        home::patch_settings(&home, &json!({"dataFolder": data}))?;
        // The onboarding state: profiles/ exists but every profile was removed.
        fs::create_dir_all(data.join("profiles"))?;
        let game = root.path().join("game.nup");
        fs::write(&game, [])?;
        stable_settings(&home, json!({"nup": game.to_string_lossy()}))?;

        assert!(matches!(link(&home)?, Outcome::Nothing));
        assert!(!home.join(MARKER).exists());

        // Once a profile exists, the next start links the file.
        fs::create_dir_all(data.join("profiles/default/saves"))?;
        home::write_json(
            &data.join("profiles/default/profile.json"),
            &json!({"v":1,"name":"Default","color":"#7F9D53"}),
        )?;
        assert!(matches!(link(&home)?, Outcome::Linked));
        assert_eq!(
            profile(&home)?["game"].as_str(),
            Some(plain(&game)?.as_str())
        );
        Ok(())
    }
}
