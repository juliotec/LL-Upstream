use super::*;
use crate::game::{process::executable, reuse};
use sha2::{Digest, Sha256};
use std::{
    fs,
    os::unix::fs::{MetadataExt, PermissionsExt},
};

#[test]
fn copies_verified_artifacts_without_sharing_files_or_profile_data() -> Result<()> {
    let temp = tempfile::tempdir()?;
    let root = temp.path();
    let profile = root.join("profiles/default");
    let tag = "v3.5.0";
    let original = root.join("versions").join(tag);
    let build = original.join("builds/default");
    let dest = root.join("versions").join(format!("{tag}.instance-2"));
    fs::create_dir_all(build.join("cache"))?;
    fs::create_dir_all(&profile)?;
    fs::create_dir_all(&dest)?;
    let game = root.join("test.nup");
    fs::write(&game, b"synthetic")?;
    home::write_json(
        &profile.join("profile.json"),
        &json!({"v":1,"name":"Test","game":game,"upscalers":[]}),
    )?;
    let program = r#"#!/usr/bin/env python3
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[0]).parent
if sys.argv[1] == 'info':
    out = root / 'builds/default'
    ready = (out/'recorded').is_file() and (out/'cache/longshot_prepared_key.txt').is_file()
    print(json.dumps(dict(v=1,type='info',version=dict(stamp='test'),upscalers=[],build=dict(folder=str(out),present=ready,up_to_date=ready))))
elif sys.argv[1] == 'verify':
    out = pathlib.Path(sys.argv[sys.argv.index('--out')+1])
    files = json.loads((out/'longshot_build.json').read_text())['files']
    ok = all(hashlib.sha256((out/name).read_bytes()).hexdigest() == obj['sha256'] for name,obj in files.items())
    print(json.dumps(dict(v=1,type='verify',ok=ok,checked=len(files))))
    sys.exit(0 if ok else 1)
else: sys.exit(1)
"#;
    for dir in [&original, &dest] {
        fs::write(dir.join(executable()), program)?;
        fs::set_permissions(dir.join(executable()), fs::Permissions::from_mode(0o755))?;
    }
    fs::write(build.join(".longshot-builder.lock"), [])?;
    fs::write(build.join("cache/longshot_prepared_key.txt"), "test stamp")?;
    fs::write(build.join("recorded"), b"installed")?;
    fs::write(build.join("longshot_options.ini"), "user settings")?;
    fs::create_dir(build.join("logs"))?;
    fs::write(build.join("logs/play.log"), "private")?;
    let hash = format!("{:x}", Sha256::digest(b"installed"));
    let record = json!({"format":1, "stamp":{"builder":"test"}, "game":{"nup":game}, "upscalers":[], "files":{"recorded":{"sha256":hash}}});
    home::write_json(&build.join("longshot_build.json"), &record)?;
    let p = Prepared {
        ctx: Context::load(root)?,
        tag: tag.into(),
        game,
        exe: dest.join(executable()),
        intent: BuildIntent::New,
    };
    let options = Options {
        dlss: false,
        fsr: false,
        xess: false,
        language: "fr".into(),
        shaders: true,
    };
    let try_copy = || reuse::existing(&p, &options, &Cancel::default(), &mut |_, _| {});
    let out = try_copy()?.context("Matching build was not reused")?;
    assert_eq!(fs::read_to_string(out.join("language.txt"))?, "fr\n");
    assert!(!out.join("longshot_options.ini").exists());
    assert!(!out.join("logs").exists());
    assert_ne!(
        fs::metadata(out.join("recorded"))?.ino(),
        fs::metadata(build.join("recorded"))?.ino()
    );
    fs::write(out.join("recorded"), b"separate")?;
    assert_eq!(fs::read(build.join("recorded"))?, b"installed");
    fs::remove_dir_all(&out)?;

    fs::write(build.join("recorded"), b"damaged")?;
    assert!(try_copy()?.is_none());
    assert!(!out.exists());
    fs::write(build.join("recorded"), b"installed")?;
    let mut changed = record.clone();
    changed["upscalers"] = json!(["dlss"]);
    home::write_json(&build.join("longshot_build.json"), &changed)?;
    assert!(try_copy()?.is_none());
    assert!(!out.exists());

    let mut tree = record.clone();
    tree["files"]["romfs/"] = json!({"sha256":"tree"});
    home::write_json(&build.join("longshot_build.json"), &tree)?;
    assert!(try_copy()?.is_none());
    assert!(!out.exists());
    Ok(())
}
