use super::*;
use crate::home;
use std::io::Write;

fn save(root: &Path, slot: &str, contents: &str) -> Result<()> {
    fs::create_dir_all(root.join(slot))?;
    fs::write(root.join(slot).join("progress.sav"), contents)?;
    Ok(())
}

// Test data paths and process discovery in their own process. Run
// this test in its own process so the concurrently-running suite keeps its environment.
#[test]
fn current_profile_store_and_save_transfers_preserve_data() -> Result<()> {
    let Some(scratch) = std::env::var_os("LONGSHOT_TEST_PLAYER_ROOT") else {
        let sandbox = tempfile::tempdir()?;
        let status = std::process::Command::new(std::env::current_exe()?)
            .args([
                "--exact",
                "players::tests::current_profile_store_and_save_transfers_preserve_data",
                "--nocapture",
            ])
            .env("LONGSHOT_TEST_PLAYER_ROOT", sandbox.path())
            .env("LONGSHOT_TEST_PLAYER_PROCESSES", "LongshotLauncher")
            .env("XDG_DATA_HOME", sandbox.path())
            .env("APPDATA", sandbox.path())
            .env("LOCALAPPDATA", sandbox.path())
            .env("USERPROFILE", sandbox.path())
            .env("HOME", sandbox.path())
            .env("LONGSHOT_LAUNCHER_HOME", sandbox.path().join("launcher"))
            .status()?;
        ensure!(status.success(), "The isolated player test failed.");
        return Ok(());
    };
    let scratch = Path::new(&scratch);
    switch_after_save_store_error(&scratch.join("recovery"))?;
    stable_save_upgrade(scratch)?;
    let home = scratch.join("launcher");
    let moved_control = scratch.join("moved-launcher");
    let data = scratch.join("library");
    home::patch_settings(&moved_control, &json!({"dataFolder": data}))?;
    initialize(&moved_control)?;
    let profile = current_dir(&moved_control)?;
    assert_eq!(profile, data.join("profiles/default"));
    save(&saves::slots_dir(&profile)?, "slot_01", "moved save")?;
    patch_profile(&profile, |record| {
        record.insert("game".into(), json!("original.nup"));
        record.insert("mods".into(), json!([{"id":"example/mod","on":true}]));
    })?;
    let moved = profiles::list(&moved_control)?.remove(0);
    assert_eq!(moved.game_path(), Some(profile.join("original.nup")));
    let row = rows::profile_row(&moved, "en");
    assert_eq!((row.save_count, row.mod_count), (1, 1));
    assert!(!moved_control.join("profiles").exists());
    let detached = scratch.join("unmanaged/game/user/save").join(saves::UID);
    save(&detached, "slot_00", "unmanaged")?;
    let settings = json!({"format":2,"currentProfile":"p-1234abcd","unknown":{"value":2}});
    home::write_json(&home.join(home::SETTINGS_FILE), &settings)?;
    let selected = home.join("profiles/p-1234abcd");
    fs::create_dir_all(selected.join("saves"))?;
    home::write_json(
        &selected.join("profile.json"),
        &json!({"v":1,"name":"Sam","game":"example.nup","upscalers":["dlss"],"version":"build1"}),
    )?;
    let target = saves::slots_dir(&selected)?;
    save(&target, "slot_01", "second player")?;
    initialize(&home)?;
    assert_eq!(current_dir(&home)?, selected);
    assert_eq!(home::settings(&home)?, settings);
    patch_profile(&selected, |o| {
        o.insert("mods".into(), json!([{"id":"test/sample","on":true}]));
    })?;
    profiles::new_or_rename(&home, "rename", "p-1234abcd", "Player two")?;
    let file = profiles::read(&selected)?;
    assert_eq!(file["mods"][0]["id"], "test/sample");
    assert_eq!(file["game"], "example.nup");
    assert_eq!(file["upscalers"], json!(["dlss"]));
    assert_eq!(file["version"], "build1");
    {
        let _lock = saves::SaveLock::acquire(&target)?;
        assert!(profiles::new_or_rename(&home, "duplicate", "p-1234abcd", "Player copy").is_err());
    }
    profiles::new_or_rename(&home, "duplicate", "p-1234abcd", "Player copy")?;
    assert_eq!(profiles::list(&home)?.len(), 2);
    assert!(!home.join("profiles/default").exists());
    initialize(&home)?;
    assert_eq!(profiles::read(&selected)?, file);

    let incoming = scratch.join("external");
    save(&incoming, "slot_02", "imported")?;
    fs::write(target.join("option.sav"), "current")?;
    fs::write(incoming.join("option.sav"), "external")?;
    saves::merge_slots(&incoming, &target, &home, true)?;
    assert_eq!(fs::read_to_string(target.join("option.sav"))?, "current");
    assert!(target.join("slot_02/progress.sav").is_file());
    assert!(target.join("slot_01/progress.sav").is_file());
    let backups = disk::dirs(&profiles::backup_root(&home)?)?;
    let backup_path = profiles::backup_root(&home)?;
    assert_ne!(backup_path, home.join("save-backups"));
    assert!(
        crate::storage::cleanup::plan(&home, true)?
            .review()
            .contains(&home::display(&backup_path))
    );
    assert_eq!(backups.len(), 1);
    assert_eq!(
        fs::read(backups[0].join("slot_01/progress.sav"))?,
        b"second player"
    );
    assert_eq!(
        fs::read_to_string(target.join("slot_01/progress.sav"))?,
        "second player"
    );
    assert_eq!(
        fs::read(detached.join("slot_00/progress.sav"))?,
        b"unmanaged"
    );

    let archive = scratch.join("backup.zip");
    let save_root = target.parent().unwrap();
    fs::create_dir_all(save_root.join("extra/empty"))?;
    fs::write(save_root.join("extra/notes.bin"), [0, 2, 4])?;
    archive::export(save_root, &archive)?;
    let unpacked = archive::unpack(archive::open(&archive)?)?;
    assert!(unpacked.whole);
    let restored = scratch.join("unpacked/save");
    archive::install_whole(&unpacked.source, &restored, &home, false)?;
    assert_eq!(fs::read(restored.join("extra/notes.bin"))?, [0, 2, 4]);
    assert!(restored.join("extra/empty").is_dir());

    // Discover and import one real slot at a time. Other files and source bytes survive.
    let eden = scratch.join("eden/user/save/example-user");
    save(&eden, "slot_01", "external progress")?;
    save(&eden, "slot_05", "other external slot")?;
    fs::write(eden.join("slot_01/caption.sav"), b"fixture thumbnail")?;
    let found = imports::detected(&home)?;
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].label, "Eden");
    assert_eq!(found[0].slots.len(), 2);
    imports::import_slot(&found[0].slots[0], &target, "slot_01", &home)?;
    assert_eq!(
        fs::read(target.join("slot_01/progress.sav"))?,
        b"external progress"
    );
    assert_eq!(fs::read(target.join("slot_02/progress.sav"))?, b"imported");
    assert!(!target.join("slot_05").exists());
    let before = fs::read(target.join("slot_01/caption.sav"))?;
    profiles::set_save_name(&selected, "slot_01", "Forest")?;
    let first = imports::duplicate(&selected, "slot_01", "Forest")?;
    let second = imports::duplicate(&selected, "slot_01", "Forest")?;
    let record = profiles::read(&selected)?;
    assert_eq!(first, "slot_00");
    assert_eq!(second, "slot_03");
    assert_eq!(profiles::save_name(&record, &first), Some("Forest (copy)"));
    assert_eq!(
        profiles::save_name(&record, &second),
        Some("Forest (copy 2)")
    );
    assert_eq!(record["start_slot"].as_str(), Some(second.as_str()));
    assert_eq!(fs::read(target.join(&second).join("caption.sav"))?, before);
    assert_eq!(fs::read(target.join("slot_01/caption.sav"))?, before);

    let slot_zip = scratch.join("single-slot.zip");
    archive::export(&target.join(&second), &slot_zip)?;
    let zip = archive::open(&slot_zip)?;
    assert!(!archive::is_whole(&zip)?);
    let unpacked = archive::unpack(zip)?;
    assert_eq!(saves::slots(&unpacked.source)?.len(), 1);
    assert!(disk::same_tree(
        &target.join(&second),
        &unpacked.source.join(&second)
    )?);
    let raw = scratch.join("progress.sav");
    fs::write(&raw, b"standalone")?;
    imports::import_slot(&raw, &target, "slot_04", &home)?;
    assert_eq!(
        fs::read(target.join("slot_04/progress.sav"))?,
        b"standalone"
    );
    fs::write(&raw, [])?;
    assert!(imports::import_slot(&raw, &target, "slot_04", &home).is_err());
    assert_eq!(
        fs::read(target.join("slot_04/progress.sav"))?,
        b"standalone"
    );

    let interrupted = scratch.join("interrupted");
    let previous = interrupted.join(".save.longshot-old");
    save(&previous.join(saves::UID), "slot_01", "recovered")?;
    let dest = interrupted.join("save");
    saves::recover(&dest)?;
    assert_eq!(
        fs::read(dest.join(saves::UID).join("slot_01/progress.sav"))?,
        b"recovered"
    );
    let edit = saves::SaveEdit::begin(&dest)?;
    assert!(saves::SaveEdit::begin(&dest).is_err());
    drop(edit);
    assert!(saves::SaveEdit::begin(&dest).is_ok());

    fs::write(selected.join("profile.json"), r#"{"v":2,"extra":true}"#)?;
    assert!(patch_profile(&selected, |o| o.clear()).is_err());
    assert_eq!(
        fs::read_to_string(selected.join("profile.json"))?,
        r#"{"v":2,"extra":true}"#
    );
    let store = scratch.join("delete-transitions");
    initialize(&store)?;
    profiles::new_or_rename(&store, "new", "", "Second")?;
    profiles::new_or_rename(&store, "new", "", "Third")?;
    let sentinel = store.join("profiles/unrelated");
    fs::create_dir_all(&sentinel)?;
    fs::write(sentinel.join("notes"), b"keep")?;
    for count in [2, 1, 0] {
        let active = current_dir(&store)?;
        let id = profile_id(&active);
        let old = fs::read(active.join("profile.json"))?;
        let (backup, remaining) = profiles::remove(&store, id)?;
        assert_eq!(remaining, count);
        assert_eq!(fs::read(backup.join("profile.json"))?, old);
        assert!(!active.exists());
        initialize(&store)?;
        assert_eq!(profiles::list(&store)?.len(), count);
        if remaining == 1 {
            assert_eq!(
                home::settings(&store)?["currentProfile"],
                profile_id(&current_dir(&store)?)
            );
        }
    }
    assert!(current_dir(&store).is_err());
    assert_eq!(fs::read(sentinel.join("notes"))?, b"keep");
    profiles::new_or_rename(&store, "new", "", "New player")?;
    let profile = current_dir(&store)?;
    assert_eq!(profiles::read(&profile)?["name"], "New player");
    assert_eq!(fs::read_dir(profile.join("saves/save"))?.count(), 0);
    Ok(())
}

#[test]
fn rejects_unsafe_and_unrelated_save_archive_entries() -> Result<()> {
    let temp = tempfile::tempdir()?;
    for (i, bad) in [
        "../outside",
        "C:outside",
        "save/../outside",
        "slot_01/unknown.txt",
        "extra.txt",
        "slot_02/caption.sav",
        "slot_02/CON.txt",
        "slot_01/Progress.sav",
    ]
    .iter()
    .enumerate()
    {
        let p = temp.path().join(format!("{i}.zip"));
        let mut w = zip::ZipWriter::new(fs::File::create(&p)?);
        for name in ["slot_01/progress.sav", *bad] {
            w.start_file(name, zip::write::SimpleFileOptions::default())?;
            w.write_all(b"synthetic")?;
        }
        w.finish()?;
        assert!(
            archive::open(&p).and_then(archive::unpack).is_err(),
            "{bad} accepted"
        );
    }
    assert!(!temp.path().join("outside").exists());
    Ok(())
}

#[test]
fn nfc_dumps_are_validated_and_filename_collisions_keep_both_tags() -> Result<()> {
    let temp = tempfile::tempdir()?;
    let dir = temp.path();
    let dest = dir.join("nfc");
    let src = dir.join("Figure.bin");
    let mut bytes = [0u8; 540];
    bytes[12..16].copy_from_slice(&[0xf1, 0x10, 0xff, 0xee]);
    bytes[0x5b] = 2;
    fs::write(&src, bytes)?;
    nfc::add(&dest, &src)?;
    nfc::add(&dest, &src)?;
    assert_eq!(nfc::files(&dest, false)?.len(), 1);
    bytes[30] = 9;
    fs::write(&src, bytes)?;
    nfc::add(&dest, &src)?;
    assert_eq!(nfc::files(&dest, false)?.len(), 2);
    fs::write(&src, b"wrong")?;
    assert!(nfc::add(&dest, &src).is_err());
    assert!(!nfc::candidate(Path::new("Key_test.BIN")));
    let mut flipper = String::from("Filetype: Flipper NFC device\n");
    for (i, page) in bytes.chunks(4).enumerate() {
        flipper += &format!(
            "Page {i}: {:02x} {:02x} {:02x} {:02x}\n",
            page[0], page[1], page[2], page[3]
        );
    }
    assert_eq!(
        nfc::identify(Path::new("figure.nfc"), flipper.as_bytes())?,
        nfc::identify(Path::new("figure.bin"), &bytes)?
    );
    let dir = temp.path().join("delete");
    fs::create_dir(&dir)?;
    let a = dir.join("a.bin");
    let b = dir.join("b.bin");
    fs::write(&a, bytes)?;
    fs::write(&b, bytes)?;
    let before = [
        nfc::SelectedTag::capture(&dir, "a.bin")?,
        nfc::SelectedTag::capture(&dir, "b.bin")?,
    ];
    fs::write(&b, b"changed")?;
    assert!(nfc::delete(&dir, &before).is_err());
    assert!(a.is_file());
    assert_eq!(fs::read(&b)?, b"changed");
    let fresh = [
        nfc::SelectedTag::capture(&dir, "a.bin")?,
        nfc::SelectedTag::capture(&dir, "b.bin")?,
    ];
    nfc::delete(&dir, &fresh)?;
    assert!(!a.exists() && !b.exists());
    Ok(())
}

#[test]
fn recognises_game_processes_and_numbers_zero_based_slots() {
    for process in [
        "Longshot.exe",
        "/tmp/longshot-builder",
        "eden-nightly.AppImage",
        "nx2navi",
    ] {
        assert!(running::game_process(process).is_some(), "{process}");
    }
    assert!(running::game_process("LongshotLauncher.exe").is_none());
    assert_eq!(slot_number("slot_00"), 1);
    assert_eq!(slot_label("slot_099"), "Slot 100");
}

#[test]
fn save_names_preserve_game_bytes_and_unrelated_profile_data() -> anyhow::Result<()> {
    let home = tempfile::tempdir()?;
    initialize(home.path())?;
    let dir = current_dir(home.path())?;
    let slot = saves::slots_dir(&dir)?.join("slot_00");
    std::fs::create_dir_all(&slot)?;
    std::fs::write(slot.join("progress.sav"), b"synthetic unchanged slot")?;
    patch_profile(&dir, |profile| {
        profile.insert("extra".into(), serde_json::json!({"future":29}));
    })?;
    profiles::set_save_name(&dir, "slot_00", "  Forest run  ")?;
    let profile = profiles::read(&dir)?;
    assert_eq!(profiles::save_name(&profile, "slot_00"), Some("Forest run"));
    assert_eq!(profile["extra"]["future"], 29);
    assert!(profiles::set_save_name(&dir, "../slot_00", "other").is_err());
    assert!(profiles::set_save_name(&dir, "slot_00", "has\ncontrol").is_err());
    profiles::set_save_name(&dir, "slot_00", "")?;
    assert!(profiles::save_name(&profiles::read(&dir)?, "slot_00").is_none());
    assert_eq!(
        std::fs::read(slot.join("progress.sav"))?,
        b"synthetic unchanged slot"
    );
    Ok(())
}

fn stable_save_upgrade(scratch: &Path) -> Result<()> {
    let user = if cfg!(windows) {
        scratch.join("Longshot")
    } else {
        scratch.join("longshot/user")
    };
    let old = user.join("save").join(saves::UID);
    save(&old, "slot_01", "stable progress")?;
    fs::write(old.join("account.sav"), "stable account")?;
    fs::create_dir_all(user.join("save-work"))?;
    fs::write(user.join("save-work/unfinished"), "pending transaction")?;
    fs::write(user.join("longshot_options.ini"), "stable settings")?;
    let build = scratch.join("Games/Longshot/stable");
    let ledger = json!({"format":1,"builds":[{"id":"before","folder":build,"version":"v2.0.0","default":true}]});
    for existing in [false, true] {
        let control = scratch.join(if existing {
            "existing modern profile"
        } else {
            "fresh upgrade"
        });
        initialize(&control)?;
        let default = current_dir(&control)?;
        let target = saves::slots_dir(&default)?;
        if existing {
            save(&target, "slot_01", "my progress")?;
            fs::write(default.join("settings.ini"), "my settings")?;
        }
        stable::migrate(&control)?;
        assert!(stable::selected_profile(&control)?.is_none());
        home::write_json(&control.join("builds.json"), &ledger)?;
        stable::migrate(&control)?;
        let id = stable::selected_profile(&control)?.unwrap();
        let dest = control.join("profiles").join(&id);
        assert_eq!(id == "default", !existing);
        assert_eq!(
            fs::read(dest.join("saves/save-work/unfinished"))?,
            b"pending transaction"
        );
        assert_eq!(fs::read(dest.join("settings.ini"))?, b"stable settings");
        assert_eq!(
            fs::read(saves::slots_dir(&dest)?.join("slot_01/progress.sav"))?,
            b"stable progress"
        );
        assert_eq!(
            fs::read(old.join("slot_01/progress.sav"))?,
            b"stable progress"
        );
        if existing {
            assert_eq!(
                fs::read(target.join("slot_01/progress.sav"))?,
                b"my progress"
            );
            assert_eq!(fs::read(default.join("settings.ini"))?, b"my settings");
        }
        fs::write(
            saves::slots_dir(&dest)?.join("slot_01/progress.sav"),
            "played after import",
        )?;
        stable::migrate(&control)?;
        assert_eq!(
            profiles::list(&control)?.len(),
            if existing { 2 } else { 1 }
        );
        assert_eq!(
            fs::read(saves::slots_dir(&dest)?.join("slot_01/progress.sav"))?,
            b"played after import"
        );
    }

    let empty = scratch.join("removed stable game");
    initialize(&empty)?;
    home::write_json(
        &empty.join("settings.json"),
        &json!({"format":2,"relistedLostBuilds":true,"keptFolders":[],"keptFolderNames":[]}),
    )?;
    home::write_json(
        &empty.join("builds.json"),
        &json!({"format":1,"home":empty,"builds":[]}),
    )?;
    stable::migrate(&empty)?;
    assert_eq!(
        stable::selected_profile(&empty)?.as_deref(),
        Some("default")
    );
    assert_eq!(
        stable::profile_for_user(&empty, &user)?.as_deref(),
        Some("default")
    );
    // Losing the registry must not lose the shared save or mods. These fields
    // match settings.json serialized by v1.3.1 even with no installed version.
    let no_registry = scratch.join("lost registry");
    initialize(&no_registry)?;
    let stable_prefs =
        json!({"format":2,"relistedLostBuilds":false,"keptFolders":[],"keptFolderNames":[]});
    home::write_json(&no_registry.join("settings.json"), &stable_prefs)?;
    let loose = user.join("mods/Forest/romfs/test");
    fs::create_dir_all(loose.parent().unwrap())?;
    fs::write(&loose, b"synthetic mod")?;
    migrate_stable(&no_registry)?;
    let imported = current_dir(&no_registry)?;
    assert_eq!(
        fs::read(saves::slots_dir(&imported)?.join("slot_01/progress.sav"))?,
        b"stable progress"
    );
    assert_eq!(
        fs::read(imported.join("mods/local~forest/romfs/test"))?,
        b"synthetic mod"
    );
    assert_eq!(fs::read(loose)?, b"synthetic mod");
    home::patch_settings(&no_registry, &json!({"work":scratch.join("fresh cache")}))?;
    assert_eq!(
        home::read_json(&no_registry.join("settings.json"))?,
        stable_prefs
    );
    assert!(!no_registry.join("builds.json").exists());
    // Stable displays the first recognized account when the regular game UID is absent.
    let other = user.join("save/other-account");
    fs::rename(&old, &other)?;
    let fallback = scratch.join("fallback account");
    initialize(&fallback)?;
    home::write_json(&fallback.join("builds.json"), &ledger)?;
    stable::migrate(&fallback)?;
    let selected = current_dir(&fallback)?;
    assert_eq!(
        fs::read(saves::slots_dir(&selected)?.join("slot_01/progress.sav"))?,
        b"stable progress"
    );
    assert!(other.join("slot_01/progress.sav").is_file());
    Ok(())
}

fn switch_after_save_store_error(home: &Path) -> Result<()> {
    use slint::platform::{
        Platform, WindowAdapter,
        software_renderer::{MinimalSoftwareWindow, RepaintBufferType},
    };
    struct TestDisplay;
    impl Platform for TestDisplay {
        fn create_window_adapter(
            &self,
        ) -> Result<std::rc::Rc<dyn WindowAdapter>, slint::PlatformError> {
            Ok(MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer))
        }
    }
    slint::platform::set_platform(Box::new(TestDisplay)).unwrap();
    initialize(home)?;
    profiles::new_or_rename(home, "new", "", "Healthy")?;
    let healthy = profiles::list(home)?
        .into_iter()
        .find(|p| p.id != "default")
        .unwrap();
    fs::write(
        home.join("profiles/default/saves/save"),
        "bad save directory",
    )?;
    home::patch_settings(home, &json!({"currentProfile":"default"}))?;
    let ui = MainWindow::new()?;
    assert!(bind(&ui, home.to_owned()).is_err());
    let players = ui.global::<Players>();
    assert_eq!(players.get_profiles().row_count(), 2);
    players.invoke_action("switch".into(), healthy.id.clone().into());
    assert_eq!(players.get_profile_id(), healthy.id);
    assert_eq!(current_dir(home)?, healthy.dir);
    Ok(())
}

#[test]
fn stable_migration_and_slot_transfers_share_a_profile_lock() -> Result<()> {
    let temp = tempfile::tempdir()?;
    let profile = temp.path().join("default");
    let whole = profile.join("saves");
    let account = whole.join("save").join(saves::UID);
    let slot = account.join("slot_00");
    let other = temp.path().join("another/saves/save").join(saves::UID);
    let migration = saves::SaveEdit::begin(&whole)?;
    assert!(saves::SaveEdit::begin(&account).is_err());
    assert!(saves::SaveLock::acquire(&slot).is_err());
    assert!(saves::SaveLock::acquire(&other).is_ok());
    drop(migration);

    let transfer = saves::SaveLock::acquire(&slot)?;
    assert!(saves::SaveEdit::begin(&whole).is_err());
    drop(transfer);
    assert!(saves::SaveEdit::begin(&whole).is_ok());
    Ok(())
}

#[test]
fn startup_profile_selects_by_id_and_ignores_missing_profiles() -> Result<()> {
    let temp = tempfile::tempdir()?;
    let home = temp.path();
    let data = home.join("library");
    home::patch_settings(home, &json!({"dataFolder":data,"currentProfile":"default"}))?;
    initialize(home)?;
    let other = data.join("profiles/p-12345678");
    fs::create_dir(&other)?;
    crate::home::write_json(
        &other.join("profile.json"),
        &profiles::default_file("Player Two"),
    )?;
    for choice in [Value::Null, json!("p-00000000"), json!("p-12345678")] {
        home::patch_settings(home, &json!({"startupProfile":choice}))?;
        assert_eq!(
            profiles::apply_startup_profile(home)?,
            choice == "p-12345678"
        );
        assert_eq!(
            home::settings(home)?["currentProfile"],
            if choice == "p-12345678" {
                "p-12345678"
            } else {
                "default"
            }
        );
    }
    Ok(())
}
