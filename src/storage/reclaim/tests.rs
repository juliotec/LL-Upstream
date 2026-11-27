use super::*;
use serde_json::json;

fn file(path: &Path, value: &[u8]) -> Result<()> {
    fs::create_dir_all(path.parent().unwrap())?;
    fs::write(path, value)?;
    Ok(())
}

#[test]
fn reclaims_only_owned_obsolete_outputs_and_keeps_sources_backups_and_unknown_neighbors()
-> Result<()> {
    let tmp = tempfile::tempdir()?;
    let root = tmp.path().join("home");
    let work = tmp.path().join("work");
    let stable = tmp.path().join("older-game");
    let inputs = tmp.path().join("source-dump");
    let current = root.join("versions/v3.2.0");
    let prior = root.join("versions/v3.1.0");
    let incomplete = root.join("versions/nightly-99-01234567");
    let tools = root.join("versions/nightly-100-fedcba98");
    let exe = if cfg!(windows) {
        "Longshot.exe"
    } else {
        "Longshot.x86_64"
    };
    home::patch_settings(&root, &json!({"work":work}))?;
    crate::players::initialize(&root)?;
    let key = "acbd18db4cc2f85cedef654fccc4a4d800000001";
    let obj = work.join("objcache").join(format!("{key}.o"));
    let private = work.join("objcache/mod-author.o");
    let shared = work.join("objcache/0123456789abcdef0123456789abcdef01234567.o");
    let missing = root.join("versions/v3.0.0/builds/default");
    let orphan = work.join("longshot-builder/work/old-note");
    let source = stable.join("cache/longshot_prepared.bin");
    let runtime = stable.join("bin/longshot");
    let stable_shader = stable.join("cache/longshot_programs.bin");
    let absent_tool = tools.join(exe);
    let old_program = incomplete.join("builds/default/bin/longshot");
    let obj_dup = work
        .join("obj-store/0123456789abcdef")
        .join(format!("{key}.o"));
    for path in [
        &obj,
        &private,
        &shared,
        &source,
        &runtime,
        &stable_shader,
        &absent_tool,
        &old_program,
        &incomplete.join(exe),
        &incomplete.join(".launcher-installing"),
        &current.join(exe),
        &prior.join(exe),
        &prior.join("builds/default/bin/longshot"),
        &current.join("builds/default/bin/longshot"),
        &stable.join("mods/mine.o"),
        &stable.join("user/slot.sav"),
        &stable.join("romfs/game.pack"),
        &stable.join("cache/readme.txt"),
        &incomplete.join("builds/default/my-backup.zip"),
        &inputs.join("original.nup"),
        &orphan.join("image/image.bin"),
        &orphan.join("image/other.bin"),
    ] {
        file(path, b"fixture")?;
    }
    file(&incomplete.join(".launcher-installing"), b"")?;
    file(
        &orphan.join("game_folder.txt"),
        missing.to_str().unwrap().as_bytes(),
    )?;
    file(&work.join("system_headers.txt"), key.as_bytes())?;
    fs::hard_link(&shared, stable.join("mods/resident.data"))?;
    fs::create_dir_all(obj_dup.parent().unwrap())?;
    fs::hard_link(&obj, &obj_dup)?;
    home::write_json(
        &root.join("versions.json"),
        &json!({"format":1,"builds":[{"version":"v3.2.0","id":"mine","folder":current.join("builds/default"),"previousFolder":"versions/v3.1.0","nup":inputs.join("original.nup")}]}),
    )?;
    home::write_json(
        &root.join("builds.json"),
        &json!({"format":1,"builds":[{"id":"old", "folder":stable,"nup":source}]}),
    )?;
    home::write_json(
        &tools.join("longshot_release.json"),
        &json!({"v":1,"version":"nightly-100-fedcba98"}),
    )?;
    let retired = root.join("versions/nightly-833-abcabcab");
    let retired_game = retired.join("builds/default/bin/longshot");
    file(&retired.join(exe), b"retired builder")?;
    file(&retired_game, b"retired executable")?;
    file(
        &retired.join(".longshot-retired"),
        b"Longshot Launcher retired installation, format 1\n",
    )?;
    let mut modern = home::read_json(&root.join("versions.json"))?;
    modern["retiredFolders"] = json!([retired.strip_prefix(&root)?]);
    modern["builds"][0]["previousFolder"] = json!(retired.strip_prefix(&root)?);
    home::write_json(&root.join("versions.json"), &modern)?;
    home::patch_settings(&root, &json!({"rollbackVersion":"v3.1.0"}))?;
    let previous_work = work.join("longshot-builder/work/old-instance");
    let active_work = work.join("longshot-builder/work/still-installed");
    for (area, version) in [(&previous_work, &retired), (&active_work, &current)] {
        file(
            &area.join("game_folder.txt"),
            version.join("builds/default").to_str().unwrap().as_bytes(),
        )?;
        file(&area.join("build/obj/cpu-2.o"), b"unit")?;
        file(&area.join("build/obj/private.o"), b"private")?;
        file(
            &area.join("build/compile_state.json"),
            br#"{"seconds":{"cpu-2.o":14,"../../other.o":4}}"#,
        )?;
    }
    let prefetch = work.join("longshot-builder/downloads/v3.0.0");
    file(&prefetch.join(exe), b"builder")?;
    file(&prefetch.join("player-file"), b"private")?;
    home::write_json(
        &prefetch.join("longshot_release.json"),
        &json!({"v":1,"version":"v3.0.0","program":exe,"full":{"size":7}}),
    )?;
    let registry = fs::read(root.join("builds.json"))?;
    let plan = scan::plan(&root)?;
    assert!(plan.review().contains("file"));
    assert!(
        plan.files
            .contains_key(&previous_work.join("build/obj/cpu-2.o").canonicalize()?)
    );
    assert!(
        !plan
            .files
            .contains_key(&previous_work.join("build/obj/private.o").canonicalize()?)
    );
    assert!(
        !plan
            .files
            .contains_key(&active_work.join("build/obj/cpu-2.o").canonicalize()?)
    );
    assert!(plan.files.contains_key(&retired_game.canonicalize()?));
    for path in [
        &obj,
        &obj_dup,
        &stable_shader,
        &runtime,
        &absent_tool,
        &old_program,
        &orphan.join("image/image.bin"),
    ] {
        assert!(
            plan.files.contains_key(&path.canonicalize()?),
            "missing {}",
            path.display()
        );
    }
    for path in [
        &private,
        &shared,
        &source,
        &prior.join(exe),
        &current.join(exe),
        &orphan.join("image/other.bin"),
    ] {
        assert!(
            !plan.files.contains_key(&path.canonicalize()?),
            "unexpected {}",
            path.display()
        );
    }
    let mut counts = Vec::new();
    plan.execute_with_progress(&root, |removed, total| {
        assert_eq!(total, plan.files.len());
        counts.push(removed);
    })?;
    assert_eq!(counts, (0..=plan.files.len()).collect::<Vec<_>>());
    assert!(
        !runtime.exists()
            && !obj.exists()
            && !obj_dup.exists()
            && !absent_tool.exists()
            && !old_program.exists()
    );
    assert!(
        current.join(exe).exists()
            && prior.join(exe).exists()
            && source.exists()
            && private.exists()
            && shared.exists()
    );
    for p in [
        "mods/mine.o",
        "user/slot.sav",
        "romfs/game.pack",
        "cache/readme.txt",
    ] {
        assert_eq!(fs::read(stable.join(p))?, b"fixture");
    }
    assert!(!prefetch.join(exe).exists());
    assert_eq!(fs::read(prefetch.join("player-file"))?, b"private");
    assert!(incomplete.join("builds/default/my-backup.zip").exists());
    assert_eq!(fs::read(root.join("builds.json"))?, registry);
    Ok(())
}

#[test]
fn requires_new_confirmation_when_the_reviewed_files_change_and_skips_links() -> Result<()> {
    let tmp = tempfile::tempdir()?;
    let root = tmp.path().join("home");
    crate::players::initialize(&root)?;
    let tool = root.join("toolchains/v3.1.0");
    let exe = tool.join("Longshot.x86_64");
    file(&exe, b"old")?;
    home::write_json(
        &tool.join("toolchain.json"),
        &json!({"kind":"LongshotBuild","run":"Longshot.x86_64"}),
    )?;
    let plan = scan::plan(&root)?;
    assert_eq!(plan.size(), 3);
    file(&exe, b"changed after review")?;
    assert!(plan.execute(&root).is_err());
    assert_eq!(fs::read(&exe)?, b"changed after review");
    #[cfg(unix)]
    {
        fs::rename(&exe, tool.join("personal"))?;
        std::os::unix::fs::symlink("personal", &exe)?;
        assert!(scan::plan(&root)?.files.is_empty());
        assert_eq!(fs::read(tool.join("personal"))?, b"changed after review");
    }
    Ok(())
}
