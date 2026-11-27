//! Custom work folders can overlap older caches, source files and the shared mod store.
use super::scan;
use crate::{home, players};
use anyhow::Result;
use serde_json::json;
use std::{fs, path::Path};

fn write(path: &Path, data: &[u8]) -> Result<()> {
    fs::create_dir_all(path.parent().unwrap())?;
    fs::write(path, data)?;
    Ok(())
}

#[test]
fn keeps_relative_originals_mod_assets_and_files_hard_linked_into_a_profile() -> Result<()> {
    let tmp = tempfile::tempdir()?;
    let home = tmp.path().join("launcher");
    let stable = home.join("stable-game");
    let work = tmp.path().join("chosen-workspace");
    let inside_mod = work.join("longshot-builder/mods/example");
    let original = stable.join("cache/longshot_prepared.bin");
    let shared = stable.join("cache/longshot_vk_pipelines.bin");
    let disposable = stable.join("cache/longshot_computes.bin");
    let object_hash = "ab".repeat(20);
    let mod_asset = inside_mod.join("objcache").join(format!("{object_hash}.o"));
    players::initialize(&home)?;
    write(&original, b"player's original")?;
    write(&shared, b"player data")?;
    write(&disposable, b"generated cache")?;
    write(&mod_asset, b"mod author's object")?;
    write(
        &inside_mod.join("system_headers.txt"),
        object_hash.as_bytes(),
    )?;
    let alias = home.join("profiles/default/my-file.bin");
    fs::hard_link(&shared, &alias)?;
    home::patch_settings(&home, &json!({"work":work, "earlierWork":[inside_mod]}))?;
    home::write_json(
        &home.join("builds.json"),
        &json!({"format":1, "builds":[{"id":"stable","folder":stable,"nup":original.strip_prefix(&home)?}]}),
    )?;
    let plan = scan::plan(&home)?;
    assert!(plan.files.contains_key(&disposable.canonicalize()?));
    for kept in [&original, &shared, &mod_asset] {
        assert!(
            !plan.files.contains_key(&kept.canonicalize()?),
            "{}",
            kept.display()
        );
    }
    plan.execute(&home)?;
    assert!(!disposable.exists());
    assert_eq!(fs::read(original)?, b"player's original");
    assert_eq!(fs::read(alias)?, b"player data");
    assert_eq!(fs::read(shared)?, b"player data");
    assert_eq!(fs::read(mod_asset)?, b"mod author's object");
    Ok(())
}

#[test]
fn previous_build_review_removes_only_selected_outputs_after_confirmation() -> Result<()> {
    const FIXTURE: &str = "LONGSHOT_TEST_PREVIOUS_STORAGE";
    if std::env::var_os(FIXTURE).is_none() {
        let sandbox = tempfile::tempdir()?;
        let status = std::process::Command::new(std::env::current_exe()?)
            .args(["--exact", "storage::reclaim::preservation::previous_build_review_removes_only_selected_outputs_after_confirmation", "--nocapture"])
            .env(FIXTURE, "1")
            .env("XDG_CACHE_HOME", sandbox.path())
            .env("XDG_DATA_HOME", sandbox.path())
            .env("APPDATA", sandbox.path())
            .env("LOCALAPPDATA", sandbox.path())
            .env_remove("LONGSHOT_SHADER_STORE")
            .status()?;
        assert!(status.success());
        return Ok(());
    }
    let tmp = tempfile::tempdir()?;
    let home = tmp.path().join("launcher");
    players::initialize(&home)?;
    let current_cache = tmp.path().join("active-cache");
    let modern_cache = tmp.path().join("old-modern-cache");
    let download = modern_cache.join("longshot-builder/downloads/v3.2.0");
    write(&download.join("Longshot.x86_64"), b"123")?;
    home::write_json(
        &download.join("longshot_release.json"),
        &json!({"v":1,"version":"v3.2.0","program":"Longshot.x86_64","full":{"size":3}}),
    )?;
    home::patch_settings(
        &home,
        &json!({"work":current_cache, "earlierWork":[modern_cache]}),
    )?;
    assert!(super::previous_plan(&home)?.files.is_empty());
    #[cfg(unix)]
    {
        fs::create_dir_all(&current_cache)?;
        let alias = tmp.path().join("old-cache-alias");
        std::os::unix::fs::symlink(&current_cache, &alias)?;
        home::write_json(
            &home.join("settings.json"),
            &json!({
                "format": 2, "work": alias, "relistedLostBuilds": true,
                "keptFolders": [], "keptFolderNames": [], "earlierWork":[]
            }),
        )?;
        let folders = crate::storage::stable_work_folders(&home, &current_cache)?.unwrap();
        assert!(!folders.contains(&current_cache.canonicalize()?));
    }
    let first = tmp.path().join("old-stable");
    let second = tmp.path().join("old-nightly");
    for dir in [&first, &second] {
        write(&dir.join("bin/longshot"), b"generated executable")?;
        write(
            &dir.join("cache/longshot_programs.bin"),
            b"generated shaders",
        )?;
        write(&dir.join("mods/example/romfs/a"), b"mod")?;
        write(&dir.join("user/save/slot_01/progress.sav"), b"progress")?;
        write(&dir.join("source.nup"), b"source")?;
    }
    home::write_json(
        &home.join("builds.json"),
        &json!({"format":1,"builds":[{"folder":first,"nup":first.join("source.nup")},{"folder":second}]}),
    )?;
    let old_cache = tmp.path().join("stable-cache");
    home::write_json(
        &home.join("settings.json"),
        &json!({
            "format":2, "work":old_cache, "relistedLostBuilds":true, "keptFolders":[], "keptFolderNames":[]
        }),
    )?;
    // These are shared by standalone games and directly launched executables,
    // even when stable has a registry and records a different builder workspace.
    let standalone = crate::storage::standalone_shader_store().unwrap();
    let earlier = old_cache.join("longshot-builder/shader-store");
    for store in [&standalone, &earlier] {
        for (name, magic) in [("spirv.pack", b"LSPK"), ("spirv.idx", b"LSPI")] {
            let mut header = [0_u8; 16];
            header[..4].copy_from_slice(magic);
            header[4..8].copy_from_slice(&1u32.to_le_bytes());
            header[8..12].copy_from_slice(&17u32.to_le_bytes());
            write(&store.join(name), &header)?;
        }
        write(&store.join("unrelated-file"), b"keep")?;
        assert!(
            scan::plan_for(&home, &super::Scope::Folder(store.clone(), String::new())).is_err()
        );
    }
    write(&second.join("mkw_vk_pipelines_play_2.bin"), b"old pipeline")?;
    write(&second.join("mkw_vk_pipelines_play_custom.bin"), b"keep")?;
    let snapshot = crate::storage::measure(&home)?;
    for store in [&standalone, &earlier] {
        let path = store.canonicalize()?;
        let row = snapshot.rows.iter().find(|row| row.path == path).unwrap();
        assert_eq!(row.group, "Shader caches");
        assert_eq!(row.cache, None);
        assert_eq!(row.bytes, Some(32));
    }
    let row = snapshot
        .rows
        .iter()
        .find(|row| row.path == second && row.cache == Some("Game shader caches"))
        .unwrap();
    assert_eq!(
        row.bytes,
        Some((b"generated shaders".len() + b"old pipeline".len()) as u64)
    );
    let scoped = scan::plan_for(
        &home,
        &super::Scope::Folder(second.clone(), "Game shader caches".into()),
    )?;
    assert_eq!(scoped.size(), row.bytes.unwrap());
    assert!(!scoped.files.contains_key(&second.join("bin/longshot")));
    let stable_work = old_cache.join("longshot-builder/work/launcher-stable");
    write(
        &stable_work.join("game_folder.txt"),
        first.to_string_lossy().as_bytes(),
    )?;
    write(&stable_work.join("build.log"), b"old builder log")?;
    let runtime = old_cache.join("longshot-builder/runtimes/v1/runtime.dll");
    write(&runtime, b"old runtime")?;
    let snapshot = crate::storage::measure(&home)?;
    assert!(
        snapshot
            .rows
            .iter()
            .filter(|row| row.group == "Previous launcher")
            .all(|row| row.cache.is_some())
    );
    assert!(
        snapshot
            .rows
            .iter()
            .any(|row| row.group == "Previous launcher" && row.label == "Previous upscalers")
    );
    write(&stable_work.join("something-else.txt"), b"keep me")?;
    assert!(!super::previous_plan(&home)?.files.is_empty());
    let plan = scan::plan_for(&home, &super::Scope::Folder(first.clone(), String::new()))?;
    assert_eq!(plan.files.len(), 2);
    assert!(plan.review().contains("longshot_programs.bin"));
    assert!(
        scan::plan_for(
            &home,
            &super::Scope::Folder(tmp.path().into(), String::new())
        )
        .is_err()
    );
    write(&first.join("bin/longshot"), b"changed after review")?;
    assert!(plan.execute(&home).is_err());
    assert!(first.join("cache/longshot_programs.bin").exists());
    scan::plan_for(&home, &super::Scope::Folder(first.clone(), String::new()))?.execute(&home)?;
    for name in ["bin/longshot", "cache/longshot_programs.bin"] {
        assert!(!first.join(name).exists());
        assert!(second.join(name).exists());
    }
    let mut caches = scan::plan_for(&home, &super::Scope::Previous)?;
    assert!(
        caches
            .categories()
            .iter()
            .any(|row| row.key == "Built games")
    );
    let selected = std::collections::BTreeSet::from(["Game shader caches".to_owned()]);
    let estimate = caches.selected_size(&selected);
    assert_eq!(Some(estimate), row.bytes);
    caches.select(selected);
    assert_eq!(estimate, caches.size());
    caches.execute(&home)?;
    assert!(!second.join("cache/longshot_programs.bin").exists());
    assert!(second.join("bin/longshot").is_file());
    assert!(second.join("mkw_vk_pipelines_play_custom.bin").is_file());
    assert!(stable_work.join("build.log").is_file());
    scan::plan_for(&home, &super::Scope::Previous)?.execute(&home)?;
    assert!(super::previous_plan(&home)?.files.is_empty());
    for store in [&standalone, &earlier] {
        assert!(store.join("spirv.pack").is_file());
        assert!(store.join("spirv.idx").is_file());
    }
    assert_eq!(fs::read(download.join("Longshot.x86_64"))?, b"123");
    assert!(!stable_work.join("build.log").exists());
    assert!(!runtime.exists());
    assert_eq!(
        fs::read(stable_work.join("something-else.txt"))?,
        b"keep me"
    );
    for dir in [&first, &second] {
        for name in [
            "mods/example/romfs/a",
            "user/save/slot_01/progress.sav",
            "source.nup",
        ] {
            assert!(dir.join(name).exists());
        }
    }
    Ok(())
}

#[test]
fn old_extraction_requires_its_original_and_can_be_cleaned_in_parts() -> Result<()> {
    let tmp = tempfile::tempdir()?;
    let home = tmp.path().join("launcher");
    players::initialize(&home)?;
    let old = tmp.path().join("stable");
    let package = tmp.path().join("fixture.nup");
    home::write_json(
        &home.join("builds.json"),
        &json!({"format":1,"builds":[{"folder":old,"nup":package}]}),
    )?;
    write(
        &old.join(scan::UNPACK_STAMP),
        b"unpacked by this builder: fixture.nup 4",
    )?;
    write(&old.join("romfs/assets/scene"), b"scene")?;
    write(&old.join("romfs/save/keep.sav"), b"save")?;
    write(&old.join(".longshot_exefs/main"), b"code")?;
    let rom = super::Scope::Folder(old.clone(), "Extracted RomFS".into());
    assert!(scan::plan_for(&home, &rom)?.files.is_empty());
    write(&package, b"game")?;
    scan::plan_for(&home, &rom)?.execute(&home)?;
    assert!(!old.join(scan::UNPACK_STAMP).exists());
    assert!(!old.join("romfs/assets/scene").exists());
    assert_eq!(fs::read(old.join(".longshot_exefs/main"))?, b"code");
    let all = scan::plan_for(&home, &super::Scope::Previous)?;
    assert!(
        all.files
            .contains_key(&old.join(".longshot_exefs/main").canonicalize()?)
    );
    all.execute(&home)?;
    assert_eq!(fs::read(package)?, b"game");
    assert_eq!(fs::read(old.join("romfs/save/keep.sav"))?, b"save");
    Ok(())
}
