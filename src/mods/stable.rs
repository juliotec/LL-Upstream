//! The stable game loaded loose emulator mods beside each installed game. Import
//! each installation's load order into one profile, keeping stable usable offline.
use crate::{game::registry, home, players};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

const RECEIPT: &str = ".stable-import.json";

struct LooseMod {
    name: String,
    folder: PathBuf,
    parts: BTreeMap<String, PathBuf>,
    disabled: bool,
}

fn discover(
    root: &Path,
    dir: &Path,
    depth: usize,
    disabled: bool,
    found: &mut Vec<LooseMod>,
) -> Result<()> {
    if !players::is_dir(dir) {
        return Ok(());
    }
    let mut children = fs::read_dir(dir)?
        .map(|entry| Ok(entry?.path()))
        .collect::<std::io::Result<Vec<_>>>()?;
    children.sort();
    let mut parts = BTreeMap::new();
    for path in children.iter().filter(|path| players::is_dir(path)) {
        let name = path
            .file_name()
            .unwrap()
            .to_string_lossy()
            .to_ascii_lowercase();
        if matches!(name.as_str(), "romfs" | "exefs" | "cheats") {
            parts.insert(name, path.clone());
        }
    }
    if depth > 0 && !parts.is_empty() {
        found.push(LooseMod {
            name: dir
                .strip_prefix(root)?
                .to_string_lossy()
                .replace(std::path::MAIN_SEPARATOR, "/"),
            folder: dir.into(),
            parts,
            disabled,
        });
    } else if depth < 4 {
        for path in children {
            let name = path.file_name().unwrap().to_string_lossy();
            if !name.starts_with('.') {
                discover(
                    root,
                    &path,
                    depth + 1,
                    disabled || name.starts_with(['-', '_']),
                    found,
                )?;
            }
        }
    }
    Ok(())
}

pub(super) fn slug(name: &str) -> String {
    let mut slug = String::new();
    for ch in name.chars().map(|ch| ch.to_ascii_lowercase()) {
        if ch.is_ascii_alphanumeric() {
            slug.push(ch);
        } else if slug.ends_with(|last: char| last.is_ascii_alphanumeric()) {
            slug.push(if matches!(ch, '.' | '_') { ch } else { '-' });
        }
        if slug.len() == 64 {
            break;
        }
    }
    let slug = slug.trim_end_matches(['.', '-', '_']);
    if slug.is_empty() {
        "mod".into()
    } else {
        slug.into()
    }
}

#[derive(Default)]
struct LoadOrder {
    entries: Vec<(String, bool)>,
    original: Option<PathBuf>,
}

fn switches(game: &Path, user: &Path, fallback: &Path) -> Result<LoadOrder> {
    for path in [
        user.join("mods.json"),
        fallback.join("mods.json"),
        game.join("mods.json"),
    ] {
        if players::is_dir(path.parent().unwrap()) && players::is_file(&path) {
            // Stable falls back to enabling unlisted mods if its list is malformed.
            let state: Value = serde_json::from_slice(&fs::read(&path)?).unwrap_or_default();
            let entries = state["mods"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|r| {
                    Some((
                        r["id"].as_str()?.strip_prefix("local/")?.to_owned(),
                        r["enabled"].as_bool()?,
                    ))
                })
                .collect();
            return Ok(LoadOrder {
                entries,
                original: Some(path),
            });
        }
    }
    let old = game.join("longshot_mods.txt");
    if !players::is_file(&old) {
        return Ok(LoadOrder::default());
    }
    let entries = fs::read_to_string(&old)?
        .lines()
        .filter_map(|line| {
            let (state, name) = line.trim().split_once(' ')?;
            Some((
                name.to_owned(),
                match state {
                    "on" => true,
                    "off" => false,
                    _ => return None,
                },
            ))
        })
        .collect();
    Ok(LoadOrder {
        entries,
        original: Some(old),
    })
}

fn stage(folder: &Path, found: &[LooseMod], order: &LoadOrder, key: &str) -> Result<Value> {
    fs::create_dir(folder)?;
    let mut pins = Vec::new();
    for m in found {
        let base = slug(&m.name);
        let mut slug = base.clone();
        let mut n = 1;
        while folder.join(format!("local~{slug}")).try_exists()? {
            n += 1;
            slug = format!("{}-{n}", &base[..base.len().min(54)]);
        }
        let dest_name = format!("local~{slug}");
        let dest = folder.join(&dest_name);
        fs::create_dir(&dest)?;
        for (part, source) in &m.parts {
            players::copy_tree(source, &dest.join(part))?;
        }
        let id = format!("local/{slug}");
        let mut manifest = json!({"id":id,"name":m.name,"version":"1.0.0"});
        if m.parts.contains_key("exefs") {
            manifest["permissions"] = json!(["exefs"]);
        }
        home::write_json(&dest.join("mod.json"), &manifest)?;
        // Manifest mods cannot execute legacy cheats. Keep their files for the
        // owner and do not advertise a cheat-only mod as active.
        let supported = m.parts.len() > usize::from(m.parts.contains_key("cheats"));
        let on = supported
            && !m.disabled
            && order
                .entries
                .iter()
                .find(|(name, _)| name == &m.name)
                .is_none_or(|(_, on)| *on);
        pins.push(json!({"id":id,"path":format!("mods/{dest_name}"),"on":on}));
    }
    if let Some(path) = &order.original {
        // Preserve the original cheat switches and pre-manifest local IDs.
        fs::copy(
            path,
            folder.join(format!(
                ".stable-{}",
                path.file_name().unwrap().to_string_lossy()
            )),
        )?;
    }
    let receipt = json!({"source":key,"mods":pins});
    home::write_json(&folder.join(RECEIPT), &receipt)?;
    Ok(receipt)
}

fn vacant(profile: &Value) -> bool {
    profile["mods"].is_null() || profile["mods"].as_array().is_some_and(Vec::is_empty)
}

fn destination(profiles: &Path, preferred: Option<&str>, key: &str) -> Result<(PathBuf, bool)> {
    if let Some(id) = preferred {
        ensure!(players::valid_id(id), "Invalid imported profile ID.");
        let dir = profiles.join(id);
        if players::is_dir(&dir) && players::is_file(&dir.join("profile.json")) {
            let profile = home::read_json(&dir.join("profile.json"))?;
            let mods = dir.join("mods");
            let ours = players::is_dir(&mods)
                && players::is_file(&mods.join(RECEIPT))
                && home::read_json(&mods.join(RECEIPT))?["source"] == key;
            let available = !mods.try_exists()?
                || players::is_dir(&mods) && fs::read_dir(&mods)?.next().transpose()?.is_none();
            if profile.is_object()
                && profile.get("v").is_none_or(|v| v == 1)
                && (ours || vacant(&profile) && available)
            {
                return Ok((dir, false));
            }
        }
    }
    for salt in 0u32.. {
        let digest = format!("{:x}", Sha256::digest(format!("{key}/{salt}")));
        let dir = profiles.join(format!("p-{}", &digest[..8]));
        if !dir.try_exists()? {
            return Ok((dir, true));
        }
        let receipt = dir.join("mods").join(RECEIPT);
        if players::is_dir(&dir)
            && players::is_dir(&dir.join("mods"))
            && players::is_file(&receipt)
            && home::read_json(&receipt)?["source"] == key
        {
            return Ok((dir, false));
        }
    }
    unreachable!()
}

pub(crate) fn migrate(control: &Path) -> Result<()> {
    if !control.join(registry::STABLE).try_exists()? && !players::empty_stable_registry(control)? {
        return Ok(());
    }
    migrate_from(control, &players::stable_user(control)?)
}

fn migrate_from(control: &Path, shared: &Path) -> Result<()> {
    let marker = control.join("stable-mods-v1.json");
    let mut completed: BTreeMap<String, String> = if marker.try_exists()? {
        serde_json::from_value(home::read_json(&marker)?)?
    } else {
        BTreeMap::new()
    };
    let builds = registry::stable_view(control)?;
    let legacy = registry::stable_only(control)?;
    if legacy.is_empty() && !players::empty_stable_registry(control)? {
        return Ok(());
    }
    let mut rows: Vec<_> = builds["builds"]
        .as_array()
        .context("Missing stable build list")?
        .iter()
        .filter_map(|row| {
            let path = PathBuf::from(row["folder"].as_str()?);
            (path.is_absolute() && legacy.contains(&path) && players::is_dir(&path))
                .then(|| (path, row.clone()))
        })
        .collect();
    rows.sort_by_key(|(_, row)| row["default"] != true);
    for game in players::old_stable_builds(control)? {
        if !rows.iter().any(|(path, _)| path == &game) {
            let name = game.file_name().and_then(|name| name.to_str());
            let row = json!({"name": name});
            rows.push((game, row));
        }
    }
    if rows.is_empty() && players::is_dir(&shared.join("mods")) {
        rows.push((shared.to_owned(), json!({"id":"shared-user-mods"})));
    }
    let primary = players::stable_saves_profile(control)?.unwrap_or_else(|| "default".into());
    let profiles = home::data_dir(control)?.join("profiles");
    players::require_dir(&profiles)?;
    for (i, (game, row)) in rows.iter().enumerate() {
        let identity = match game.to_str() {
            Some(path) => path.to_owned(),
            None => {
                use sha2::{Digest, Sha256};
                format!(
                    "bytes:{:x}",
                    Sha256::digest(game.as_os_str().as_encoded_bytes())
                )
            }
        };
        let key = row["id"].as_str().unwrap_or(&identity);
        let source = format!("folder:{identity}");
        if completed.contains_key(key) || completed.contains_key(&source) {
            continue;
        }
        let mut found = Vec::new();
        let own = game.join("mods");
        discover(&own, &own, 0, false, &mut found)?;
        let user = game.join("user");
        if players::is_dir(&user) {
            let mods = user.join("mods");
            discover(&mods, &mods, 0, false, &mut found)?;
        }
        if i == 0
            && game != shared
            && players::is_dir(shared)
            && (!players::is_dir(&user) || shared.canonicalize()? != user.canonicalize()?)
        {
            let mods = shared.join("mods");
            discover(&mods, &mods, 0, false, &mut found)?;
        }
        if found.is_empty() {
            continue;
        }
        let per_game = players::stable_profile_for_user(control, &user)?;
        let order = if per_game.is_some() {
            switches(game, &user, shared)?
        } else {
            switches(game, shared, &user)?
        };
        found.sort_by_key(|m| {
            (
                order
                    .entries
                    .iter()
                    .position(|(name, _)| name == &m.name)
                    .unwrap_or(usize::MAX),
                m.name.to_lowercase(),
                m.folder.clone(),
            )
        });
        let preferred = per_game
            .as_deref()
            .or_else(|| (i == 0).then_some(primary.as_str()));
        let (dir, new) = destination(&profiles, preferred, key)?;
        let staging = tempfile::tempdir_in(&profiles)?;
        let mods = dir.join("mods");
        let receipt = if players::is_file(&mods.join(RECEIPT)) {
            home::read_json(&mods.join(RECEIPT))?
        } else {
            stage(&staging.path().join("mods"), &found, &order, key)?
        };
        if new {
            let label = row["name"]
                .as_str()
                .or_else(|| row["version"].as_str())
                .unwrap_or("game");
            home::write_json(
                &staging.path().join("profile.json"),
                &json!({"v":1,"name":format!("Stable mods · {label}"),"color":"#D8A840","mods":receipt["mods"]}),
            )?;
            fs::rename(staging.path(), &dir)?;
        } else {
            if staging.path().join("mods").is_dir() {
                if mods.try_exists()? {
                    // remove_dir only succeeds while the destination is empty.
                    fs::remove_dir(&mods)
                        .context("The profile's mod folder changed during import.")?;
                }
                fs::rename(staging.path().join("mods"), &mods)?;
            }
            let mut attached = false;
            players::patch_profile(&dir, |p| {
                if p.get("mods").is_none_or(|v| {
                    v.as_array().is_some_and(Vec::is_empty) || v == &receipt["mods"]
                }) {
                    p.insert("mods".into(), receipt["mods"].clone());
                    attached = true;
                }
            })?;
            ensure!(
                attached,
                "The selected profile's mods changed during import."
            );
        }
        let id = dir.file_name().unwrap().to_string_lossy();
        completed.insert(key.into(), id.to_string());
        completed.insert(source, id.into_owned());
        home::write_json(&marker, &serde_json::to_value(&completed)?)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(unix)]
    #[test]
    fn imports_mods_from_a_discovered_non_unicode_build() -> Result<()> {
        use std::{ffi::OsStr, os::unix::ffi::OsStrExt};
        let temp = tempfile::tempdir()?;
        let control = temp.path().join("control");
        let library = temp.path().join("games");
        let game = library.join(OsStr::from_bytes(b"legacy-\xff"));
        let romfs = game.join("mods/Forest/romfs");
        fs::create_dir_all(&romfs)?;
        fs::write(game.join("longshot_version.txt"), b"v3.0.0")?;
        fs::write(romfs.join("fixture.bin"), b"offline")?;
        players::initialize(&control)?;
        home::write_json(
            &control.join("settings.json"),
            &json!({
                "format": 2, "relistedLostBuilds": true, "keptFolders": [],
                "keptFolderNames": [], "buildsFolder": library
            }),
        )?;
        let shared = temp.path().join("shared");
        migrate_from(&control, &shared)?;
        let profile = control.join("profiles/default");
        assert_eq!(
            fs::read(profile.join("mods/local~forest/romfs/fixture.bin"))?,
            b"offline"
        );
        migrate_from(&control, &shared)?;
        Ok(())
    }

    #[test]
    fn imports_two_stable_load_orders_offline_once() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let control = tmp.path().join("control");
        let shared = tmp.path().join("shared");
        let a = tmp.path().join("stable");
        let b = tmp.path().join("second");
        for (root, path) in [
            (&a, "Pack/Forest/romfs"),
            (&a, "Moon/exefs"),
            (&b, "Moon/cheats"),
        ] {
            let path = root.join("mods").join(path);
            fs::create_dir_all(&path)?;
            fs::write(path.join("fixture.bin"), b"offline")?;
        }
        fs::create_dir_all(&control)?;
        players::initialize(&control)?;
        home::write_json(
            &control.join("settings.json"),
            &json!({"format":2,"buildsFolder":tmp.path().join("games")}),
        )?;
        home::write_json(
            &control.join("builds.json"),
            &json!({"format":1,"builds":[{"id":"b","folder":b},{"id":"a","folder":a,"default":true}]}),
        )?;
        home::write_json(
            &a.join("mods.json"),
            &json!({"mods":[{"id":"local/Pack/Forest","enabled":false},{"id":"local/Moon","enabled":true}]}),
        )?;
        migrate_from(&control, &shared)?;
        let default = control.join("profiles/default");
        let profile = home::read_json(&default.join("profile.json"))?;
        assert_eq!(
            profile["mods"][0],
            json!({"id":"local/pack-forest","path":"mods/local~pack-forest","on":false})
        );
        assert_eq!(profile["mods"][1]["on"], true);
        assert_eq!(
            fs::read(default.join("mods/local~pack-forest/romfs/fixture.bin"))?,
            b"offline"
        );
        assert_eq!(
            home::read_json(&default.join("mods/local~moon/mod.json"))?["permissions"],
            json!(["exefs"])
        );
        assert!(!default.join("trust.json").exists());
        assert_eq!(
            fs::read(default.join("mods/.stable-mods.json"))?,
            fs::read(a.join("mods.json"))?
        );
        let marker = home::read_json(&control.join("stable-mods-v1.json"))?;
        let second = control.join("profiles").join(marker["b"].as_str().unwrap());
        assert_eq!(
            fs::read(second.join("mods/local~moon/cheats/fixture.bin"))?,
            b"offline"
        );
        assert_eq!(
            home::read_json(&second.join("profile.json"))?["mods"][0]["on"],
            false
        );
        // Retrying after a crash between publication and the completion record
        // reuses the completed profile. Deleting a completed profile stays final.
        fs::remove_file(control.join("stable-mods-v1.json"))?;
        migrate_from(&control, &shared)?;
        assert_eq!(players::list_profiles(&control)?.len(), 2);
        fs::remove_dir_all(&second)?;
        migrate_from(&control, &shared)?;
        assert_eq!(players::list_profiles(&control)?.len(), 1);
        assert_eq!(
            fs::read(a.join("mods/Pack/Forest/romfs/fixture.bin"))?,
            b"offline"
        );
        Ok(())
    }

    #[test]
    fn shared_mods_survive_removal_of_the_last_stable_build() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path();
        players::initialize(root)?;
        let shared = root.join("user");
        let payload = shared.join("mods/Forest/romfs");
        fs::create_dir_all(&payload)?;
        fs::write(payload.join("demo"), b"data")?;
        home::write_json(
            &root.join("builds.json"),
            &json!({"format":1,"home":root,"builds":[]}),
        )?;
        migrate_from(root, &shared)?;
        assert!(!root.join("profiles/default/mods").exists());
        home::write_json(
            &root.join("settings.json"),
            &json!({"relistedLostBuilds":true,"keptFolders":[],"keptFolderNames":[],"buildsFolder":root.join("games")}),
        )?;
        migrate_from(root, &shared)?;
        assert_eq!(
            fs::read(root.join("profiles/default/mods/local~forest/romfs/demo"))?,
            b"data"
        );
        Ok(())
    }

    #[test]
    fn recovers_shared_and_unlisted_mods_when_the_registered_game_is_gone() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let root = tmp.path().join("control");
        players::initialize(&root)?;
        fs::create_dir(root.join("profiles/default/mods"))?;
        let library = tmp.path().join("library");
        let orphan = library.join("nightly/v3.0.0");
        let shared = tmp.path().join("user");
        let stable = json!({"format":2, "buildsFolder":library, "relistedLostBuilds":true,
            "keptFolders":[orphan], "keptFolderNames":[]});
        home::write_json(&root.join("settings.json"), &stable)?;
        let ledger = json!({"format":1,"home":root,"builds":[{"id":"removed","folder":library.join("gone"),"default":true}]});
        home::write_json(&root.join("builds.json"), &ledger)?;
        for (folder, name) in [(&shared, "Village"), (&orphan, "Forest")] {
            let part = folder.join("mods").join(name).join("romfs");
            fs::create_dir_all(&part)?;
            fs::write(part.join("fixture"), name)?;
        }
        fs::write(orphan.join("longshot_version.txt"), "version=v3.0.0")?;
        migrate_from(&root, &shared)?;
        let profile = root.join("profiles/default/mods");
        assert_eq!(
            fs::read(profile.join("local~village/romfs/fixture"))?,
            b"Village"
        );
        assert_eq!(
            fs::read(profile.join("local~forest/romfs/fixture"))?,
            b"Forest"
        );
        assert_eq!(
            fs::read(orphan.join("mods/Forest/romfs/fixture"))?,
            b"Forest"
        );
        assert_eq!(home::read_json(&root.join("builds.json"))?, ledger);
        assert_eq!(home::read_json(&root.join("settings.json"))?, stable);
        migrate_from(&root, &shared)?;
        assert_eq!(players::list_profiles(&root)?.len(), 1);
        // A second installation with no discoverable game still gets its shared mods.
        let second = tmp.path().join("other");
        players::initialize(&second)?;
        home::write_json(
            &second.join("settings.json"),
            &json!({"format":2,"buildsFolder":tmp.path().join("absent")}),
        )?;
        home::write_json(&second.join("builds.json"), &ledger)?;
        migrate_from(&second, &shared)?;
        assert_eq!(
            fs::read(second.join("profiles/default/mods/local~village/romfs/fixture"))?,
            b"Village"
        );
        Ok(())
    }

    #[test]
    fn preserves_existing_profile_source_pins() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let control = tmp.path();
        let pin = json!([{"id":"demo/pinned","version":"1.2","hash":"kept","on":false}]);
        players::initialize(control)?;
        home::write_json(
            &control.join("settings.json"),
            &json!({"format":2,"buildsFolder":control.join("games")}),
        )?;
        players::patch_profile(&control.join("profiles/default"), |p| {
            p.insert("mods".into(), pin.clone());
        })?;
        let game = control.join("game");
        fs::create_dir_all(game.join("mods/Forest/romfs"))?;
        fs::write(game.join("mods/Forest/romfs/fixture.bin"), b"original")?;
        home::write_json(
            &control.join("builds.json"),
            &json!({"format":1,"builds":[{"id":"test","folder":game}]}),
        )?;
        migrate_from(control, &control.join("user"))?;
        migrate_from(control, &control.join("user"))?;
        assert_eq!(
            home::read_json(&control.join("profiles/default/profile.json"))?["mods"],
            pin
        );
        assert_eq!(players::list_profiles(control)?.len(), 2);
        Ok(())
    }
}
