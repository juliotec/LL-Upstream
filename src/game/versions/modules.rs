//! The registry owns module choices for each installation. The builder reads the
//! selected installation's choices on Play and decides if its output is stale.
use super::{Builder, Version, by_folder, registry};
use crate::game::licenses;
use crate::{home, players};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use std::path::Path;

const MODULES: [(&str, &str); 3] = [("dlss", "dlss"), ("fsr", "fsr4"), ("xess", "xess")];

pub(super) fn recorded(row: &Value) -> Option<Vec<&'static str>> {
    MODULES
        .iter()
        .any(|(key, _)| row[key].is_boolean())
        .then(|| {
            MODULES
                .into_iter()
                .filter_map(|(key, name)| (row[key] == true).then_some(name))
                .collect()
        })
}

pub(super) fn write(row: &mut Value, enabled: &[&str]) {
    for (key, name) in MODULES {
        row[key] = json!(enabled.contains(&name));
    }
}

impl Version {
    pub(in crate::game) fn modules(&self) -> Result<Vec<&'static str>> {
        let mut modules = self.stored_modules()?;
        if !cfg!(windows) {
            modules.retain(|name| *name != "xess");
        }
        Ok(modules)
    }

    pub(super) fn stored_modules(&self) -> Result<Vec<&'static str>> {
        if let Some(modules) = &self.modules {
            return Ok(modules.clone());
        }
        let record = home::read_json(&self.folder.join("builds/default/longshot_build.json"))?;
        Ok(MODULES
            .into_iter()
            .map(|(_, name)| name)
            .filter(|name| {
                record["upscalers"]
                    .as_array()
                    .is_some_and(|list| list.iter().any(|item| item == name))
            })
            .collect())
    }
}

pub(in crate::game) fn set_modules(home_dir: &Path, folder: &Path, enabled: &[&str]) -> Result<()> {
    for &module in enabled {
        licenses::for_module(module, cfg!(windows))?;
    }
    let selected = by_folder(home_dir, folder)?;
    let mut data = registry(home_dir)?;
    let rows = data["builds"].as_array_mut().expect("validated registry");
    let row = if let Some(index) = rows.iter().position(|row| {
        row["folder"]
            .as_str()
            .is_some_and(|path| selected.owns_build(Path::new(path)))
    }) {
        &mut rows[index]
    } else {
        ensure!(
            !rows.iter().any(|row| row["id"] == selected.id),
            "Another installation already uses this identifier."
        );
        rows.push(json!({
            "id": selected.id,
            "name": selected.tag,
            "version": selected.tag,
            "folder": selected.folder.join("builds/default"),
            "default": selected.default,
        }));
        rows.last_mut().unwrap()
    };
    let keep_xess = !cfg!(windows) && selected.stored_modules()?.contains(&"xess");
    write(row, enabled);
    if keep_xess {
        row["xess"] = json!(true);
    }
    crate::game::registry::write(home_dir, &data)
}

pub(super) fn prepare_licenses(ctx: &Builder, enabled: &[&str]) -> Result<()> {
    // Play consumes --upscaler, but unlike the build subcommand it forwards
    // --accept-licenses to the game. Write the builder's documented profile
    // receipt for the exact SDK revision whose terms the user accepted.
    let mut receipts = Vec::new();
    for name in enabled {
        let license = licenses::for_module(name, cfg!(windows))?;
        if ctx.settings[license.setting] == true
            && let Some(receipt) = license.receipt
        {
            receipts.push(receipt);
        }
    }
    if receipts.is_empty() {
        return Ok(());
    }
    let profile = home::read_json(&ctx.profile.join("profile.json"))?;
    if receipts
        .iter()
        .all(|&(vendor, pin)| profile["licenses"][vendor] == pin)
    {
        return Ok(());
    }
    players::edit_profile(&ctx.profile, |profile| {
        let saved = profile
            .entry("licenses")
            .or_insert_with(|| json!({}))
            .as_object_mut()
            .context("Invalid licence receipts in profile.json.")?;
        for (vendor, pin) in receipts {
            saved.insert(vendor.into(), json!(pin));
        }
        Ok(())
    })
}
