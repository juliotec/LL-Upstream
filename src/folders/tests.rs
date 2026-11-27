use super::*;
use std::sync::atomic::Ordering;
use tempfile::tempdir;

fn stub(file: &Path, bytes: &[u8]) -> Result<()> {
    fs::create_dir_all(file.parent().unwrap())?;
    fs::write(file, bytes)?;
    Ok(())
}

#[test]
fn workspace_move_is_cancellable_and_refuses_collisions_and_active_builders() -> Result<()> {
    let tmp = tempdir()?;
    let root = tmp.path().join("launcher");
    let from = tmp.path().join("work");
    let to = tmp.path().join("elsewhere");
    home::patch_settings(&root, &json!({"work":from,"unrelated":3}))?;
    fs::create_dir_all(from.join("longshot-builder/store/empty"))?;
    stub(&from.join("longshot-builder/.launcher-fetch.lock"), b"")?;
    assert!(!Change::prepare(&root, "work", Some(&to))?.has_data());
    stub(
        &from.join("longshot-builder/store/progress"),
        b"build cache",
    )?;
    stub(&from.join("keep"), b"neighbor")?;
    let change = Change::prepare(&root, "work", Some(&to))?;
    let cancel = AtomicBool::new(true);
    assert!(change.has_data() && change.apply(true, &cancel).is_err());
    cancel.store(false, Ordering::Release);
    stub(&to.join("longshot-builder/unrelated"), b"other user")?;
    assert!(change.apply(true, &cancel).is_err());
    assert_eq!(
        fs::read(to.join("longshot-builder/unrelated"))?,
        b"other user"
    );
    fs::rename(to.join("longshot-builder"), to.join("unrelated"))?;
    let lock = from.join("longshot-builder/work/compile/longshot-builder.lock");
    stub(&lock, b"")?;
    let lock = fs::File::options().read(true).write(true).open(lock)?;
    lock.try_lock()?;
    assert!(change.apply(true, &cancel).is_err());
    assert!(!to.join("longshot-builder").exists());
    assert_eq!(home::settings(&root)?["work"], json!(from));
    lock.unlock()?;
    drop(lock);
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink("store/progress", from.join("longshot-builder/relative"))?;
        std::os::unix::fs::symlink(
            from.join("longshot-builder/store/progress"),
            from.join("longshot-builder/absolute"),
        )?;
        std::os::unix::fs::symlink("../keep", from.join("longshot-builder/external"))?;
    }
    #[cfg(windows)]
    {
        junction::create(
            from.join("longshot-builder/store"),
            from.join("longshot-builder/internal"),
        )?;
        junction::create(
            from.join("longshot-builder/store/empty"),
            from.join("longshot-builder/another"),
        )?;
    }
    let note = change.apply(true, &cancel)?;
    assert!(note.contains("Data moved"), "{note}");
    assert!(!from.join("longshot-builder").exists());
    assert_eq!(fs::read(from.join("keep"))?, b"neighbor");
    assert_eq!(
        fs::read(to.join("longshot-builder/store/progress"))?,
        b"build cache"
    );
    #[cfg(unix)]
    for (name, data) in [
        ("relative", &b"build cache"[..]),
        ("absolute", b"build cache"),
        ("external", b"neighbor"),
    ] {
        let p = to.join("longshot-builder").join(name);
        assert!(fs::symlink_metadata(&p)?.is_symlink());
        assert_eq!(fs::read(p)?, data);
    }
    #[cfg(windows)]
    {
        let link = to.join("longshot-builder/internal");
        assert!(junction::exists(&link)?);
        assert_eq!(fs::read(link.join("progress"))?, b"build cache");
    }
    let settings = home::settings(&root)?;
    assert_eq!(settings["work"], json!(to.canonicalize()?));
    assert_eq!(settings["unrelated"], 3);
    let future = tmp.path().join("future");
    Change::prepare(&root, "work", Some(&future))?.apply(false, &cancel)?;
    assert!(to.join("longshot-builder/store/progress").is_file());
    assert!(!future.join("longshot-builder").exists());
    Ok(())
}

#[test]
fn moves_only_the_old_library_and_preserves_stable_and_profile_data() -> Result<()> {
    let tmp = tempdir()?;
    let home = tmp.path().join("launcher");
    let src = home.join("versions");
    let target = tmp.path().join("other");
    let tag = "nightly-3-abc12345";
    let old = src.join(tag);
    let real = if cfg!(windows) {
        "Longshot.exe"
    } else {
        "Longshot.x86_64"
    };
    stub(&old.join(real), b"fixture builder")?;
    stub(&old.join("builds/default/game.bin"), b"game")?;
    let previous = Path::new("versions").join("nightly-2-01234567");
    stub(&home.join(&previous).join(real), b"previous version")?;
    let stable = src.join("v3.1.0");
    stub(&stable.join(real), b"stable builder")?;
    stub(&src.join("neighbor/file"), b"personal")?;
    let stable_data = br#"{"future":"do not rewrite"}"#;
    home::write_json(
        &home.join("builds.json"),
        &json!({"format":1,"builds":[{"folder":stable,"extra":stable_data}]}),
    )?;
    let stable_bytes = fs::read(home.join("builds.json"))?;
    home::write_json(
        &home.join(game::registry::OWN),
        &json!({"format":1,"extra":8,"retiredFolders":[previous,stable],"builds":[{"previousFolder":previous,"previousTags":["nightly-2-01234567"],"id":"keep","version":tag,"folder":old.join("builds/default"),"default":true,"extra":true}]}),
    )?;
    home::write_json(
        &home.join("profiles/default/profile.json"),
        &json!({"v":1,"version":tag,"extra":9}),
    )?;
    let profile = fs::read(home.join("profiles/default/profile.json"))?;
    fs::create_dir_all(&target)?;
    let change = Change::prepare(&home, "buildsFolder", Some(&target))?;
    assert!(change.has_data());
    let cancel = AtomicBool::new(false);
    change.apply(true, &cancel)?;
    let moved = target.canonicalize()?.join(tag);
    assert!(!old.exists() && game::owned_folder(&home, &moved));
    assert_eq!(fs::read(moved.join("builds/default/game.bin"))?, b"game");
    let registry = game::registry::read(&home, game::registry::OWN)?;
    assert_eq!(
        registry["builds"][0]["folder"],
        json!(moved.join("builds/default"))
    );
    assert_eq!(registry["builds"][0]["extra"], true);
    assert_eq!(registry["builds"][0]["default"], true);
    assert_eq!(registry["extra"], 8);
    let moved_previous = target.canonicalize()?.join("nightly-2-01234567");
    assert!(moved_previous.join(real).is_file());
    assert_eq!(
        registry["builds"][0]["previousFolder"],
        json!(moved_previous)
    );
    assert_eq!(registry["retiredFolders"], json!([moved_previous, stable]));
    assert_eq!(fs::read(home.join("builds.json"))?, stable_bytes);
    assert!(stable.join(real).exists() && src.join("neighbor/file").exists());
    assert_eq!(
        fs::read(home.join("profiles/default/profile.json"))?,
        profile
    );
    Change::prepare(&home, "buildsFolder", None)?.apply(true, &cancel)?;
    let registry = game::registry::read(&home, game::registry::OWN)?;
    assert_eq!(registry["builds"][0]["previousFolder"], json!(previous));
    assert_eq!(registry["retiredFolders"], json!([previous, stable]));
    assert!(old.join("builds/default/game.bin").is_file());
    assert!(!moved.exists() && home::settings(&home)?["buildsFolder"].is_null());
    Ok(())
}

#[test]
fn never_moves_a_registered_original_game_with_workspace_data() -> Result<()> {
    let tmp = tempdir()?;
    let root = tmp.path().join("launcher");
    let work = tmp.path().join("work");
    let dest = tmp.path().join("dest");
    let game = work.join("longshot-builder/my-file.nup");
    stub(&game, b"original")?;
    home::write_json(
        &root.join("profiles/default/profile.json"),
        &json!({"v":1,"game":game}),
    )?;
    home::patch_settings(&root, &json!({"work":work}))?;
    let plan = Change::prepare(&root, "work", Some(&dest))?;
    assert!(plan.apply(true, &AtomicBool::new(false)).is_err());
    assert_eq!(fs::read(game)?, b"original");
    assert!(!dest.exists());
    Ok(())
}

#[test]
fn publication_failure_restores_both_installations_and_all_registry_rows() -> Result<()> {
    use sha2::{Digest, Sha256};
    let tmp = tempdir()?;
    let home = tmp.path().join("launcher");
    let library = home.join("versions");
    let to = tmp.path().join("new-library");
    let a = "v3.4.0";
    let b = "v3.5.0";
    let exe = if cfg!(windows) {
        "Longshot.exe"
    } else {
        "Longshot.x86_64"
    };
    let mut rows = Vec::new();
    for tag in [a, b] {
        stub(&library.join(tag).join(exe), tag.as_bytes())?;
        let folder = library.join(tag).join("builds/default");
        stub(&folder.join("game.bin"), tag.as_bytes())?;
        rows.push(json!({"version":tag,"default":tag==a,"folder":folder}));
    }
    let registry = json!({"format":1,"builds":rows,"unknown":"keep"});
    home::write_json(&home.join(game::registry::OWN), &registry)?;
    fs::create_dir(&to)?;
    let target = to.canonicalize()?.join(b);
    let hash = Sha256::digest(target.as_os_str().as_encoded_bytes());
    let occupied = home
        .join("version-locations")
        .join(format!("{hash:x}.json"));
    stub(&occupied, b"unrelated ownership record")?;
    let plan = Change::prepare(&home, "buildsFolder", Some(&to))?;
    let result = plan.apply(true, &AtomicBool::new(false));
    assert!(result.is_err());
    assert_eq!(fs::read(&occupied)?, b"unrelated ownership record");
    assert!(home::settings(&home)?["buildsFolder"].is_null());
    assert_eq!(home::read_json(&home.join(game::registry::OWN))?, registry);
    assert!(fs::read_dir(&to)?.next().is_none());
    for tag in [a, b] {
        assert_eq!(
            fs::read(library.join(tag).join("builds/default/game.bin"))?,
            tag.as_bytes()
        );
    }
    fs::remove_file(occupied)?;
    plan.apply(true, &AtomicBool::new(false))?;
    for tag in [a, b] {
        assert!(!library.join(tag).exists());
        assert!(to.join(tag).join(exe).is_file());
    }
    Ok(())
}

#[cfg(unix)]
#[test]
fn moving_workspace_preserves_file_modes_links_and_times_across_volumes() -> Result<()> {
    use std::os::unix::fs::{MetadataExt, PermissionsExt};
    let tmp = tempdir()?;
    // Package builds and temporary data often live on separate volumes. Exercise the same
    // operation in either layout, including the byte-copy fallback when hard links cannot cross.
    let binary = std::env::current_exe()?;
    let volume = tempfile::tempdir_in(binary.parent().unwrap())?;
    let home = tmp.path().join("launcher");
    let work = tmp.path().join("work");
    let folder = work.join("longshot-builder/store");
    let contents = vec![42; 700_000];
    let tool = folder.join("tool");
    stub(&tool, &contents)?;
    fs::set_permissions(&tool, fs::Permissions::from_mode(0o751))?;
    let time = std::time::UNIX_EPOCH + std::time::Duration::from_secs(12345);
    fs::File::open(&tool)?.set_modified(time)?;
    fs::hard_link(&tool, folder.join("alias"))?;
    home::patch_settings(&home, &json!({"work":work}))?;
    Change::prepare(&home, "work", Some(volume.path()))?.apply(true, &AtomicBool::new(false))?;
    assert!(!folder.exists());
    let moved = volume.path().join("longshot-builder/store");
    let tool = moved.join("tool");
    let meta = tool.metadata()?;
    assert_eq!(fs::read(tool)?, contents);
    assert_eq!(meta.permissions().mode() & 0o777, 0o751);
    assert_eq!(meta.modified()?, time);
    assert_eq!(moved.join("alias").metadata()?.ino(), meta.ino());
    Ok(())
}

#[test]
fn moves_profiles_and_custom_installs_together_and_can_restore_the_default() -> Result<()> {
    let tmp = tempdir()?;
    let control = tmp.path().join("launcher");
    let cache = tmp.path().join("independent-cache");
    let custom = tmp.path().join("my-games");
    let data = tmp.path().join("Longshot data");
    let tag = "nightly-12-abcdef01";
    let folder = control.join("versions").join(tag);
    let exe = if cfg!(windows) {
        "Longshot.exe"
    } else {
        "Longshot.x86_64"
    };
    stub(&folder.join(exe), b"fixture")?;
    stub(&folder.join("builds/default/game.bin"), b"version")?;
    home::write_json(
        &control.join("profiles/default/profile.json"),
        &json!({"v":1,"version":tag}),
    )?;
    stub(
        &control.join("profiles/default/saves/slot1"),
        b"fixture save",
    )?;
    stub(&control.join("profiles/default/mods/one"), b"fixture mod")?;
    stub(&control.join("logs/current"), b"fixture log")?;
    stub(
        &cache.join("longshot-builder/store/cache-file"),
        b"fixture cache",
    )?;
    stub(
        &cache.join("longshot-builder/mods/blobs/package"),
        b"shared mod",
    )?;
    home::patch_settings(&control, &json!({"work":cache}))?;
    home::write_json(
        &control.join(game::registry::OWN),
        &json!({"format":1,"builds":[{"id":tag,"version":tag,"folder":folder.join("builds/default")}]}),
    )?;
    let cancel = AtomicBool::new(false);
    Change::prepare(&control, "buildsFolder", Some(&custom))?.apply(true, &cancel)?;
    let external = custom.join(tag);
    stub(&custom.join("other/file"), b"unrelated")?;
    #[cfg(unix)]
    std::os::unix::fs::symlink(
        control.join("profiles/default/saves"),
        external.join("active-saves"),
    )?;
    let plan = Change::prepare(&control, "dataFolder", Some(&data))?;
    assert!(plan.has_data());
    assert!(plan.apply(false, &cancel).is_err());
    assert!(plan.apply(true, &AtomicBool::new(true)).is_err());
    assert!(!data.exists());
    plan.apply(true, &cancel)?;
    let dest = data.canonicalize()?;
    let settings = home::settings(&control)?;
    assert!(settings["buildsFolder"].is_null());
    // (saved in its plain spelling: no \\?\ on Windows, #297)
    assert_eq!(
        home::data_folder(&control, &settings),
        crate::fsx::plain(&dest)
    );
    assert_eq!(home::work(&control, &settings), cache);
    assert_eq!(fs::read(dest.join("mods/blobs/package"))?, b"shared mod");
    assert_eq!(
        cache.join("longshot-builder/mods").canonicalize()?,
        dest.join("mods")
    );
    assert_eq!(
        fs::read(dest.join("profiles/default/saves/slot1"))?,
        b"fixture save"
    );
    assert_eq!(
        fs::read(dest.join("profiles/default/mods/one"))?,
        b"fixture mod"
    );
    assert!(!external.exists() && !control.join("profiles").exists());
    let moved = dest.join("versions").join(tag);
    assert_eq!(fs::read(moved.join("builds/default/game.bin"))?, b"version");
    assert_eq!(
        game::registry::read(&control, game::registry::OWN)?["builds"][0]["folder"],
        json!(moved.join("builds/default"))
    );
    #[cfg(unix)]
    assert_eq!(
        fs::read_link(moved.join("active-saves"))?,
        dest.join("profiles/default/saves")
    );
    Change::prepare(&control, "dataFolder", None)?.apply(true, &cancel)?;
    assert!(control.join("profiles/default/saves/slot1").is_file());
    assert!(folder.join("builds/default/game.bin").is_file());
    assert!(home::settings(&control)?["dataFolder"].is_null());
    assert!(!dest.join("profiles").exists());
    assert!(control.join("mods/blobs/package").is_file());
    assert_eq!(
        cache.join("longshot-builder/mods").canonicalize()?,
        control.canonicalize()?.join("mods")
    );
    assert!(custom.join("other/file").exists() && control.join("logs/current").exists());
    assert!(cache.join("longshot-builder/store/cache-file").is_file());
    Ok(())
}

#[test]
fn data_folder_rejects_existing_profiles_and_linked_destinations() -> Result<()> {
    let tmp = tempdir()?;
    let root = tmp.path().join("launcher");
    let target = tmp.path().join("existing");
    stub(&target.join("profiles/player/saves/slot"), b"foreign save")?;
    assert!(Change::prepare(&root, "dataFolder", Some(&target)).is_err());
    #[cfg(unix)]
    {
        let nested = tmp.path().join("nested");
        fs::create_dir(&nested)?;
        std::os::unix::fs::symlink(target.join("profiles"), nested.join("versions"))?;
        assert!(Change::prepare(&root, "dataFolder", Some(&nested)).is_err());
    }
    assert_eq!(
        fs::read(target.join("profiles/player/saves/slot"))?,
        b"foreign save"
    );
    Ok(())
}

#[test]
fn moving_data_retargets_mods_in_previously_used_caches() -> Result<()> {
    let tmp = tempdir()?;
    let root = tmp.path().join("control");
    let first = home::work(&root, &Value::Null);
    let other = tmp.path().join("second-cache");
    let dest = tmp.path().join("data");
    let pins = "longshot-builder/mods/sources.json";
    let contents = b"[]";
    stub(&first.join(pins), contents)?;
    let cancel = AtomicBool::new(false);
    Change::prepare(&root, "work", Some(&other))?.apply(false, &cancel)?;
    assert_eq!(fs::read(other.join(pins))?, contents);
    Change::prepare(&root, "dataFolder", Some(&dest))?.apply(true, &cancel)?;
    let stored = dest.join("mods/sources.json").canonicalize()?;
    assert_eq!(first.join(pins).canonicalize()?, stored);
    assert_eq!(other.join(pins).canonicalize()?, stored);
    Ok(())
}

#[test]
fn interrupted_cache_move_resumes_without_removing_the_original() -> Result<()> {
    let temp = tempfile::tempdir()?;
    let root = temp.path();
    let old = root.join("old");
    let new = root.join("new");
    for dir in [&new, &old] {
        fs::create_dir(dir)?;
    }
    home::patch_settings(root, &json!({"work": old}))?;
    home::write_json(
        &root.join(MOVE_RECEIPT),
        &json!({
            "key": "work", "before": old, "after": new, "destinations": [new]
        }),
    )?;
    resume_move(root)?;
    assert_eq!(home::settings(root)?["work"], json!(new));
    assert!(old.is_dir());
    resume_move(root)?;
    Ok(())
}

#[test]
fn resume_publication_on_either_side_of_directory_and_link_switches() -> Result<()> {
    // Reopen the same journal at each point a process may disappear, including
    // after the shared link changes but before preferences commit.
    for completed in 0..=3 {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let old = root.join("old");
        let new = root.join("new");
        let cache = root.join("cache");
        stub(&old.join("mods/sources.json"), b"[]")?;
        stub(&old.join("profiles/default/test"), b"save")?;
        mod_store::ensure(&cache, &old)?;
        fs::create_dir(&new)?;
        let stage = tempfile::Builder::new()
            .prefix(".longshot-move-")
            .tempdir_in(&new)?;
        let entries = ["mods", "profiles"].map(|name| Entry {
            old: old.join(name),
            new: new.join(name),
            version: false,
        });
        stub(&stage.path().join("0/sources.json"), b"[]")?;
        stub(&stage.path().join("1/default/test"), b"save")?;
        home::patch_settings(root, &json!({"dataFolder": old}))?;
        home::write_json(
            &root.join(MOVE_RECEIPT),
            &json!({
                "key":"dataFolder","before":old,"after":new,"destinations":[new.join("mods"),new.join("profiles")],
                "publication":{"stage":stage.path(),"entries":entries,"caches":[cache],"shortcuts":[],"old_data":old,"destination":new}
            }),
        )?;
        for (i, entry) in entries.iter().take(completed).enumerate() {
            fs::rename(stage.path().join(i.to_string()), &entry.new)?;
        }
        if completed == 3 {
            mod_store::repoint(&cache, &old, &new)?;
        }
        resume_move(root)?;
        resume_move(root)?;
        assert_eq!(home::settings(root)?["dataFolder"], json!(new));
        assert_eq!(
            cache.join("mods").canonicalize()?,
            new.join("mods").canonicalize()?
        );
        assert_eq!(fs::read(new.join("profiles/default/test"))?, b"save");
        assert_eq!(fs::read(old.join("profiles/default/test"))?, b"save");
    }
    Ok(())
}

/// A folder chosen in Settings is saved, and a saved one read, in its plain spelling: a verbatim one
/// (`\\?\X:\...`, as `fs::canonicalize` gives it on Windows) went to longshot-builder as its cache, and
/// clang found no header from it (#297).
#[cfg(windows)]
#[test]
fn a_chosen_folder_is_saved_and_read_without_a_verbatim_prefix() -> Result<()> {
    let verbatim = |p: &Path| p.as_os_str().to_string_lossy().starts_with(r"\\?\");
    let tmp = tempdir()?;
    let root = tmp.path().join("launcher");
    let chosen = tmp.path().join("build files");
    fs::create_dir_all(&chosen)?;
    Change::prepare(&root, "work", Some(&chosen))?.apply(true, &AtomicBool::new(false))?;
    let saved = PathBuf::from(home::settings(&root)?["work"].as_str().unwrap_or_default());
    assert!(!verbatim(&saved), "saved as {}", saved.display());
    assert_eq!(fs::canonicalize(&saved)?, fs::canonicalize(&chosen)?);
    // (a setting an earlier launcher saved verbatim: read plain)
    let old = fs::canonicalize(&chosen)?;
    assert!(verbatim(&old));
    home::patch_settings(&root, &json!({"work": old, "dataFolder": old}))?;
    let settings = home::settings(&root)?;
    for read in [
        home::work(&root, &settings),
        home::data_folder(&root, &settings),
        game::install_root(&root, &settings),
    ] {
        assert!(!verbatim(&read), "read as {}", read.display());
    }
    home::patch_settings(&root, &json!({"dataFolder": null, "buildsFolder": old}))?;
    let builds = game::install_root(&root, &home::settings(&root)?);
    assert!(!verbatim(&builds), "read as {}", builds.display());
    Ok(())
}
