#[cfg(unix)]
use super::{
    Screen,
    builder::{args, name, run},
};
use super::{Section, builder::word, view::description};
use serde_json::json;
#[cfg(unix)]
use std::fs;

#[test]
fn installed_problem_and_dependencies_come_from_the_builder() {
    let row = json!({"id":"quick", "on":true, "state":"held","why":"Needs Text Core.",
            "manifest":{"author":"Example","dependencies":[{"kind":"required","id":"text-core","range":"2.0"}],"permissions":{"required":["settings.read"]}}});
    let text = description(&row, Section::Installed);
    for part in [
        "Needs Text Core.",
        "By Example",
        "required · text-core 2.0",
        "required permissions · settings.read",
    ] {
        assert!(text.contains(part));
    }
    assert!(word("--yes").is_err());
}

#[cfg(unix)]
fn fixture() -> tempfile::TempDir {
    use std::os::unix::fs::PermissionsExt;
    let tmp = tempfile::tempdir().unwrap();
    let home = tmp.path();
    let version = home.join("versions/v3.2.0");
    fs::create_dir_all(version.join("builds/default")).unwrap();
    fs::create_dir_all(home.join("profiles/default")).unwrap();
    fs::create_dir_all(home.join("profiles/p-aabbccdd")).unwrap();
    let exe = version.join("Longshot.x86_64");
    fs::write(&exe,r#"#!/bin/sh
test "$1" = mod && test "$2" = --profile && test "$4" = --cache && test "$6" = --output && test "$7" = json || exit 2
profile=$3
shift 7
case "$1" in
 installed) if test -f "$profile/without-mod-tools"; then
     echo '{"v":1,"type":"error","code":"mod_tooling_missing","text":"mod tooling not built in to this builder"}'; exit 125
   fi
   if test -f "$profile/installed.json"; then cat "$profile/installed.json"; exit 0; fi
 echo '{"ok":true,"result":{"mods":[{"id":"demo","name":"Example","version":"1.0","on":true,"update":{"version":"2.0"},"source":"https://example.test/repo","sha256":"fixture-digest","code_hash":"distinct-code-hash","trust":{"asks":true,"unsafe":{"required":["unsafe.native"]}},"warning":"Review this code."}]}}' ;;
 details) echo '{"ok":true,"result":{"versions":[{"version":"2.0","sha256":"123","permissions":["settings.read"]}]}}' ;;
 gc) echo '{"ok":true,"result":{"removed":["example"],"freed":128,"dry_run":true}}' ;;
 enable|disable) test "$2" = demo || exit 2
   printf '%s' "$1" > "$profile/switch"; echo '{"ok":true,"result":[]}' ;;
 noisy) head -c 200000 /dev/zero >&2; echo '{"ok":true,"result":"complete"}' ;;
 oversized) head -c 18000000 /dev/zero ;;
 update)
   case " $* " in
     *" --plan "*)
       if test -f "$profile/update-plan.json"; then cat "$profile/update-plan.json"; else
         echo '{"ok":true,"result":{"plan":[{"id":"dependency","version":"1.2"}],"missing":[]}}'
       fi ;;
     *) touch "$profile/updated"; echo '{"ok":true,"result":[]}' ;;
   esac ;;
 import) touch "$profile/imported"; echo '{"ok":true,"result":[]}' ;;
 add)
   if test -f "$profile/reject-add"; then
     echo '{"ok":false,"error":{"code":"invalid_mod","text":"The builder rejected this mod."}}'; exit 1
   elif test -f "$profile/plan.json" && test "$3" = --plan; then
     cat "$profile/plan.json"
   elif test -d "$2/romfs"; then
     echo '{"v":1,"type":"error","code":"mod_tooling_missing","text":"mod tooling not built in to this builder"}'
     exit 125
   elif test "$3" = --plan; then
     echo 'Starting the mod tools'
     echo '{"ok":true,"result":{"id":"demo","version":"1.0","replaces":true,"code":true,"permissions":["settings.read"]}}'
   elif test "$3" = --replace; then
     printf '%s' "$2" > "$profile/added"
     echo '{"ok":true,"result":{"id":"demo"},"notes":["Added demo"]}'
   else exit 2; fi ;;
 allow) printf '%s\n' "$@" > "$profile/granted"; echo '{"ok":true,"result":{"accepted":true}}' ;;
 *) echo '{"ok":false,"error":{"text":"Unknown command"}}'; exit 1 ;;
esac
"#.replace("fixture-digest", &"ab".repeat(32))).unwrap();
    fs::set_permissions(&exe, fs::Permissions::from_mode(0o755)).unwrap();
    tmp
}

#[cfg(unix)]
#[test]
fn manual_add_requires_review_and_cannot_cross_profiles() {
    let tmp = fixture();
    let home = tmp.path();
    let file = home.join("manual mod.lsmod");
    fs::write(&file, []).unwrap();
    let mut screen = Screen::default();
    screen.act(home, "installed", "", None).unwrap();
    assert_eq!(name(&screen.rows[0]), "Example");
    screen.act(home, "add", "", Some(file.clone())).unwrap();
    let review = &screen.review.as_ref().unwrap().text;
    assert!(review.contains("replaces") && review.contains("settings.read"));
    assert!(!home.join("profiles/default/added").exists());
    crate::home::patch_settings(home, &json!({"currentProfile":"p-aabbccdd"})).unwrap();
    assert!(screen.act(home, "confirm", "", None).is_err());
    assert!(!home.join("profiles/p-aabbccdd/added").exists());
    screen.act(home, "add", "", Some(file.clone())).unwrap();
    // Exercise the same reviewed argv without depending on the workstation's running-game state.
    let (result, _) = run(
        home,
        &screen.review.as_ref().unwrap().args,
        screen.identity.as_ref(),
        None,
    )
    .unwrap();
    assert_eq!(result["id"], "demo");
    assert_eq!(
        fs::read_to_string(home.join("profiles/p-aabbccdd/added")).unwrap(),
        file.to_str().unwrap()
    );
}

#[cfg(unix)]
#[test]
fn rejected_add_never_retries_as_import() -> anyhow::Result<()> {
    let tmp = fixture();
    let home = tmp.path();
    let profile = home.join("profiles/default");
    fs::write(profile.join("reject-add"), [])?;
    let archive = home.join("demo.zip");
    fs::write(&archive, [])?;
    let folder = home.join("demo");
    fs::create_dir_all(folder.join("romfs"))?;
    fs::write(folder.join("romfs/asset"), [])?;
    let mut screen = Screen::default();
    for source in [archive, folder] {
        let error = screen.act(home, "add", "", Some(source)).unwrap_err();
        assert!(error.to_string().contains("The builder rejected this mod."));
    }
    assert!(!profile.join("mods").exists());
    assert!(!profile.join("imported").exists());
    Ok(())
}

#[cfg(unix)]
#[test]
fn update_rechecks_dependencies_and_then_reviews_code_permissions() -> anyhow::Result<()> {
    let tmp = fixture();
    let home = tmp.path();
    let profile = home.join("profiles/default");
    let plan = profile.join("update-plan.json");
    let mut screen = Screen::default();
    screen.act(home, "installed", "", None)?;
    screen.act(home, "update", "", None)?;
    fs::write(
        &plan,
        r#"{"ok":true,"result":{"plan":[],"missing":[{"id":"required"}]}}"#,
    )?;
    assert!(screen.act(home, "confirm", "", None).is_err());
    assert!(!profile.join("updated").exists());
    let error = screen.act(home, "update", "", None).unwrap_err();
    assert!(error.to_string().contains("Missing dependencies"));
    fs::remove_file(&plan)?;
    screen.act(home, "update", "", None)?;
    screen.act(home, "confirm", "", None)?;
    assert!(profile.join("updated").is_file());
    assert!(!screen.pending_permissions.is_empty());
    assert!(!profile.join("granted").exists());
    Ok(())
}

#[cfg(unix)]
#[test]
fn update_pins_the_reviewed_release_and_code_grants_pin_the_reviewed_hash() {
    let tmp = fixture();
    let home = tmp.path();
    let mut screen = Screen::default();
    screen.act(home, "installed", "", None).unwrap();
    screen.act(home, "update", "", None).unwrap();
    let update = screen.review.as_ref().unwrap();
    assert!(update.text.contains("settings.read"));
    assert!(update.text.contains("dependency 1.2"));
    assert!(update.preview.is_some());
    assert_eq!(
        update.args,
        args(&[
            "update",
            "demo",
            "--source",
            "https://example.test/repo",
            "--version",
            "2.0",
            "--sha256",
            "123"
        ])
    );
    screen.act(home, "cancel", "", None).unwrap();
    screen.act(home, "allow", "", None).unwrap();
    let grant = screen.review.as_ref().unwrap();
    assert!(grant.text.contains("Review this code.") && grant.text.contains("unsafe.native"));
    assert_eq!(
        grant.args,
        args(&[
            "allow",
            "demo",
            "--yes",
            "--sha256",
            &"ab".repeat(32),
            "--permissions",
            "unsafe.native"
        ])
    );
}

#[cfg(unix)]
#[test]
fn read_errors_and_large_responses_without_blocking_the_builder() {
    let tmp = fixture();
    let home = tmp.path();
    assert_eq!(
        run(home, &args(&["noisy"]), None, None).unwrap().0,
        "complete"
    );
    assert!(
        run(home, &args(&["oversized"]), None, None)
            .unwrap_err()
            .to_string()
            .contains("16 MiB")
    );
    assert_eq!(super::store_gc(home, true).unwrap()["freed"], 128);
}

#[cfg(unix)]
#[test]
fn builder_without_mod_tools_adds_romfs_without_confirmation() -> anyhow::Result<()> {
    let tmp = fixture();
    let home = tmp.path();
    let profile = home.join("profiles/default");
    fs::write(profile.join("profile.json"), r#"{"v":1,"mods":[]}"#)?;
    fs::write(profile.join("without-mod-tools"), [])?;
    let folder = home.join("loose-mod");
    fs::create_dir_all(folder.join("romfs"))?;
    fs::write(folder.join("romfs/example"), b"example")?;
    let installed = profile.join("mods/local~loose-mod");
    let mut screen = Screen::default();
    screen.act(home, "add", "", Some(folder.clone()))?;
    assert!(screen.basic && screen.pending_permissions.is_empty());
    assert!(screen.review.is_none());
    assert_eq!(fs::read(installed.join("romfs/example"))?, b"example");
    assert!(installed.join("mod.json").is_file());
    assert!(folder.join("romfs/example").is_file());
    Ok(())
}

#[cfg(unix)]
#[test]
fn installed_switches_can_change_while_the_game_is_running() -> anyhow::Result<()> {
    const CHILD: &str = "LONGSHOT_TEST_MOD_SWITCH_CHILD";
    if std::env::var_os(CHILD).is_none() {
        let status = std::process::Command::new(std::env::current_exe()?)
            .args([
                "--exact",
                "mods::tests::installed_switches_can_change_while_the_game_is_running",
                "--nocapture",
            ])
            .env(CHILD, "1")
            .env("LONGSHOT_TEST_PLAYER_PROCESSES", "Longshot")
            .status()?;
        assert!(status.success());
        return Ok(());
    }

    let tmp = fixture();
    let home = tmp.path();
    let profile = home.join("profiles/default");
    let mut screen = Screen::default();
    let busy = crate::players::ensure_idle().unwrap_err().to_string();
    screen.act(home, "installed", "", None)?;
    assert!(!screen.basic);
    screen.act(home, "toggle", "", None)?;
    assert_eq!(fs::read_to_string(profile.join("switch"))?, "disable");
    assert_eq!(
        screen.act(home, "up", "", None).unwrap_err().to_string(),
        busy
    );
    fs::write(
        profile.join("installed.json"),
        r#"{"ok":true,"result":{"mods":[{"id":"demo","on":false}]}}"#,
    )?;
    screen.act(home, "installed", "", None)?;
    screen.act(home, "toggle", "", None)?;
    assert_eq!(fs::read_to_string(profile.join("switch"))?, "enable");

    fs::write(profile.join("without-mod-tools"), [])?;
    fs::write(profile.join("profile.json"), r#"{"v":1,"mods":[]}"#)?;
    let folder = home.join("fixture-mod");
    fs::create_dir_all(folder.join("romfs"))?;
    super::local::review(&profile, &folder)?.apply(&profile)?;
    screen.act(home, "installed", "", None)?;
    assert!(screen.basic);
    assert_eq!(screen.rows[0]["on"], true);
    screen.act(home, "toggle", "", None)?;
    assert_eq!(screen.rows[0]["on"], false);
    screen.act(home, "toggle", "", None)?;
    assert_eq!(screen.rows[0]["on"], true);
    Ok(())
}

#[cfg(unix)]
#[test]
fn romfs_and_two_exefs_mods_produce_one_code_permissions_review() -> anyhow::Result<()> {
    let tmp = fixture();
    let home = tmp.path();
    let profile = home.join("profiles/default");
    let exe = home.join("versions/v3.2.0/Longshot.x86_64");
    fs::write(profile.join("profile.json"), r#"{"v":1,"mods":[]}"#)?;
    fs::write(profile.join("without-mod-tools"), [])?;

    let mixed = home.join("Mixed Mod");
    let code = profile.join("mods/Code Mod");
    let assets = profile.join("mods/Asset Mod");
    for path in [&mixed, &code] {
        fs::create_dir_all(path.join("exefs"))?;
        fs::write(path.join("exefs/code.ips"), b"PATCHEOF")?;
    }
    for path in [&mixed, &assets] {
        fs::create_dir_all(path.join("romfs"))?;
        fs::write(path.join("romfs/asset"), b"fixture asset")?;
    }

    let mut screen = Screen::default();
    screen.act(home, "add", "", Some(mixed))?;
    assert!(screen.review.is_none());
    assert_eq!(screen.pending_permissions.len(), 2);
    let text = super::trust::descriptions(&screen.pending_permissions);
    assert!(text.contains("Code Mod") && text.contains("Mixed Mod"));
    assert!(!text.contains("Asset Mod"));

    fs::write(code.join("exefs/code.ips"), b"changed after review")?;
    assert!(screen.act(home, "confirm", "", None).is_err());
    assert_eq!(super::trust::pending(home, &exe, &profile)?.len(), 2);
    screen.pending_permissions = super::trust::pending(home, &exe, &profile)?;
    screen.act(home, "confirm", "", None)?;
    assert!(screen.pending_permissions.is_empty());
    assert!(super::trust::pending(home, &exe, &profile)?.is_empty());
    Ok(())
}

#[cfg(unix)]
#[test]
fn preflight_uses_the_builders_enabled_state_and_pins_the_reviewed_content() {
    use super::trust::pending;
    let tmp = fixture();
    let home = tmp.path();
    let exe = home.join("versions/v3.2.0/Longshot.x86_64");
    let profile = home.join("profiles/default");
    let hash = "dc".repeat(32);
    let code = json!({"id":"me/code","name":"Demo code","on":true,"live":false,
        "code_hash":"different", "path":"mods/code","in_profile":true,
        "trust":{"asks":true,"unsafe":{"required":["memory.read"]}},"warning":"Can read game memory."});
    let mut off = code.clone();
    off["on"] = json!(false);
    let mut trusted = code.clone();
    trusted["trust"]["asks"] = json!(false);
    let mut loose = code.clone();
    loose["in_profile"] = json!(false);
    loose["path"] = serde_json::Value::Null;
    fs::write(
        profile.join("installed.json"),
        json!({"ok":true,"result":{"mods":[off, trusted, loose, code]}}).to_string(),
    )
    .unwrap();
    let plan = |hash: &str| {
        fs::write(
            profile.join("plan.json"),
            json!({"ok":true,"result":{"id":"me/code","same_content":true,"sha256":hash}})
                .to_string(),
        )
        .unwrap()
    };
    plan(&hash);
    let grants = pending(home, &exe, &profile).unwrap();
    assert_eq!(grants.len(), 1);
    let grant = &grants[0];
    assert!(grant.description().contains("memory.read"));
    assert_eq!(grant.name(), "Demo code");
    assert!(
        grant
            .approve(home, &exe, &home.join("profiles/p-aabbccdd"))
            .is_err()
    );
    plan(&"ab".repeat(32));
    assert!(grant.approve(home, &exe, &profile).is_err());
    assert!(!profile.join("granted").exists());
    plan(&hash);
    grant.approve(home, &exe, &profile).unwrap();
    assert_eq!(
        fs::read_to_string(profile.join("granted")).unwrap(),
        format!("allow\nme/code\n--yes\n--sha256\n{hash}\n--permissions\nmemory.read\n")
    );
}

#[cfg(unix)]
#[test]
fn preflight_recognizes_old_builders_and_propagates_query_errors() {
    let tmp = fixture();
    let home = tmp.path();
    let exe = home.join("versions/v3.2.0/Longshot.x86_64");
    let profile = home.join("profiles/default");
    fs::write(
        profile.join("installed.json"),
        r#"{"ok":false,"error":{"text":"Profile unavailable"}}"#,
    )
    .unwrap();
    assert!(
        super::trust::pending(home, &exe, &profile)
            .unwrap_err()
            .to_string()
            .contains("Profile unavailable")
    );
    fs::write(profile.join("without-mod-tools"), []).unwrap();
    assert!(
        super::trust::pending(home, &exe, &profile)
            .unwrap()
            .is_empty()
    );
}

#[test]
fn a_panicking_worker_leaves_the_screen_available() {
    use super::Screen;
    let screen = std::sync::Mutex::new(Screen::default());
    screen.lock().unwrap().run_job(|state| {
        state.rows.push(json!({"id":"partial"}));
        panic!("synthetic worker panic");
    });
    let mut state = screen.lock().unwrap();
    assert!(state.rows.is_empty());
    assert!(state.message.contains("stopped unexpectedly"));
    state.run_job(|state| {
        state.rows.push(json!({"id":"next"}));
        Ok(())
    });
    assert_eq!(state.rows[0]["id"], "next");
}

#[cfg(unix)]
#[test]
fn hung_mod_tools_release_the_worker() {
    let mut worker = std::process::Command::new("sleep");
    let timeout = std::time::Duration::from_millis(50);
    let start = std::time::Instant::now();
    worker.arg("30");
    let err = super::builder::execute_with_timeout(worker, &[], timeout).unwrap_err();
    assert!(err.to_string().contains("took too long"));
    assert!(start.elapsed() < std::time::Duration::from_secs(5));
}
