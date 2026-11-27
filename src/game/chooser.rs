//! The builder keeps a draft source until the check passes. Settings records a
//! validated source path immediately and only in the current player's profile.
use super::process::validated_source;
use crate::{Game, MainWindow, Preferences, players};
use anyhow::{Result, ensure};
use slint::ComponentHandle;
use std::path::{Path, PathBuf};

fn pick(folder: bool) -> Option<PathBuf> {
    let dialog = rfd::FileDialog::new();
    if folder {
        dialog
            .set_title("Choose your extracted game folder")
            .pick_folder()
    } else {
        dialog
            .set_title("Choose your game file")
            .add_filter("Game file", &["nup", "nx"])
            .pick_file()
    }
}

fn save_for_player(home: &Path, choice: Option<&Path>) -> Result<Option<PathBuf>> {
    let Some(choice) = choice else {
        return Ok(None);
    };
    let path = validated_source(choice)?;
    let profile = players::current_dir(home)?;
    let value = serde_json::to_value(&path)?;
    players::patch_profile(&profile, |record| {
        record.insert("game".into(), value);
    })?;
    Ok(Some(path))
}

fn settings_choice(ui: &MainWindow, home: &Path, pick: impl FnOnce() -> Option<PathBuf>) -> bool {
    let prefs = ui.global::<Preferences>();
    let result = || -> Result<Option<PathBuf>> {
        let game = ui.global::<Game>();
        ensure!(
            !game.get_busy() && !game.get_playing(),
            "Finish the current game operation first."
        );
        save_for_player(home, pick().as_deref())
    };
    match result() {
        Ok(Some(path)) => {
            prefs.set_original_game(path.to_string_lossy().as_ref().into());
            prefs.set_status("".into());
            true
        }
        Ok(None) => false,
        Err(e) => {
            prefs.set_status(format!("{e:#}").into());
            false
        }
    }
}

fn build_choice(ui: &MainWindow, choice: Option<PathBuf>) -> bool {
    let Some(path) = choice else { return false };
    match super::drop_file(ui, &path) {
        Ok(()) => true,
        Err(e) => {
            let g = ui.global::<Game>();
            g.set_file_error(true);
            g.set_status(format!("{e:#}").into());
            false
        }
    }
}

pub(super) fn bind(ui: &MainWindow, home: &Path) {
    let game = ui.global::<Game>();
    let weak = ui.as_weak();
    game.on_choose_source(move |folder| {
        weak.upgrade()
            .is_some_and(|ui| build_choice(&ui, pick(folder)))
    });
    let weak = ui.as_weak();
    game.on_select_source(move |path| {
        weak.upgrade()
            .is_some_and(|ui| build_choice(&ui, Some(PathBuf::from(path.as_str()))))
    });
    let prefs = ui.global::<Preferences>();
    let (weak, home_dir) = (ui.as_weak(), home.to_owned());
    prefs.on_choose_source(move |folder| {
        weak.upgrade()
            .is_some_and(|ui| settings_choice(&ui, &home_dir, || pick(folder)))
    });
    let (weak, home_dir) = (ui.as_weak(), home.to_owned());
    prefs.on_select_source(move |path| {
        weak.upgrade().is_some_and(|ui| {
            settings_choice(&ui, &home_dir, || Some(PathBuf::from(path.as_str())))
        })
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::home;
    use std::fs;
    #[test]
    fn suggestions_and_custom_choices_change_only_the_active_profile() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let home = temp.path().join("launcher");
        players::initialize(&home)?;
        let profile = home.join("profiles/p-12345678");
        fs::create_dir(&profile)?;
        home::write_json(
            &profile.join("profile.json"),
            &serde_json::json!({"v":1,"name":"Test","unknown":12}),
        )?;
        home::patch_settings(&home, &serde_json::json!({"currentProfile":"p-12345678"}))?;
        let default = home.join("profiles/default/profile.json");
        let prior = fs::read(&default)?;
        let demo = temp.path().join("demo.nup");
        fs::write(&demo, [])?;
        assert_eq!(
            save_for_player(&home, Some(&demo))?,
            Some(demo.canonicalize()?)
        );
        let dump = temp.path().join("custom");
        fs::create_dir_all(dump.join("romfs"))?;
        fs::create_dir(dump.join("exefs"))?;
        fs::write(dump.join("exefs/main"), [])?;
        assert_eq!(
            save_for_player(&home, Some(&dump.join("romfs")))?,
            Some(dump.canonicalize()?)
        );
        let saved = fs::read(profile.join("profile.json"))?;
        assert_eq!(save_for_player(&home, None)?, None);
        assert!(save_for_player(&home, Some(temp.path())).is_err());
        assert_eq!(fs::read(profile.join("profile.json"))?, saved);
        assert_eq!(fs::read(default)?, prior);
        assert_eq!(
            super::super::original_game(&profile, &home::settings(&home)?)?,
            Some(dump.canonicalize()?)
        );
        assert!(!home.join("versions").exists());
        Ok(())
    }
}
