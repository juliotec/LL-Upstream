//! Per-mod approvals scoped to the builder and profile the player reviewed.
use super::builder::{MissingTools, args, execute, field, name, word};
use super::local;
use crate::game;
use anyhow::{Context, Result, ensure};
use serde_json::Value;
use std::{
    ffi::OsString,
    path::{Component, Path, PathBuf},
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Grant {
    executable: PathBuf,
    profile: PathBuf,
    request: Request,
}
#[derive(Clone, Debug, PartialEq, Eq)]
enum Request {
    Builder(BuilderGrant),
    Local(local::Approval),
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct BuilderGrant {
    id: String,
    name: String,
    warning: String,
    hash: String,
    permissions: Vec<String>,
}

impl BuilderGrant {
    pub(super) fn description(&self) -> String {
        let mut detail = format!("{} ({})\n\n{}", self.name, self.id, self.warning);
        if !self.permissions.is_empty() {
            detail.push_str(&format!(
                "\n\nRequired permissions\n{}",
                self.permissions.join(", ")
            ));
        }
        detail.push_str(&format!("\n\nContent SHA-256\n{}", self.hash));
        detail
    }

    pub(super) fn command(&self) -> Vec<OsString> {
        let mut command = args(&["allow", &self.id, "--yes", "--sha256", &self.hash]);
        if !self.permissions.is_empty() {
            command.extend(args(&["--permissions", &self.permissions.join(",")]));
        }
        command
    }

    pub(super) fn from_row(home: &Path, exe: &Path, profile: &Path, row: &Value) -> Result<Self> {
        ensure!(
            row["trust"]["asks"] == true,
            "The builder reports no pending permission request."
        );
        let id = word(field(row, "id"))?;
        let warning = row["warning"]
            .as_str()
            .filter(|s| !s.trim().is_empty())
            .context("The builder did not supply a warning for this mod.")?;
        let permissions: Vec<String> =
            serde_json::from_value(row["trust"]["unsafe"]["required"].clone())
                .context("The builder did not list this mod’s required permissions.")?;
        // allow --sha256 binds the entire mod. installed.code_hash covers only its executable parts.
        let hash = if let Some(pin) = row["sha256"].as_str() {
            pin.to_owned()
        } else {
            let rel = row["path"]
                .as_str()
                .map(Path::new)
                .with_context(|| format!("Add {id} to the profile in Mods before approving it."))?;
            ensure!(
                rel.starts_with("mods")
                    && rel.components().all(|c| matches!(c, Component::Normal(_))),
                "Invalid mod path from the builder."
            );
            let command = [
                OsString::from("add"),
                profile.join(rel).into_os_string(),
                OsString::from("--plan"),
            ];
            let (plan, _) = execute(game::mod_command_for(home, exe, profile)?, &command)?;
            ensure!(
                plan["id"] == id && plan["same_content"] == true,
                "The installed mod changed. Review it again."
            );
            field(&plan, "sha256").to_owned()
        };
        ensure!(
            hash.len() == 64 && hash.bytes().all(|c| c.is_ascii_hexdigit()),
            "The builder did not identify this mod’s content hash."
        );
        Ok(Self {
            id: id.into(),
            name: name(row).into(),
            warning: warning.into(),
            hash,
            permissions,
        })
    }
}

impl Grant {
    pub(crate) fn name(&self) -> &str {
        match &self.request {
            Request::Builder(grant) => &grant.name,
            Request::Local(a) => a.name(),
        }
    }
    pub(crate) fn description(&self) -> String {
        match &self.request {
            Request::Builder(grant) => grant.description(),
            Request::Local(a) => a.description(),
        }
    }
    pub(crate) fn approve(&self, home: &Path, exe: &Path, profile: &Path) -> Result<()> {
        ensure!(
            self.executable == exe && self.profile == profile,
            "The builder or profile changed. Review the mod again."
        );
        ensure!(
            pending(home, exe, profile)?.contains(self),
            "This mod or its requested permissions changed. Review it again."
        );
        let grant = match &self.request {
            Request::Builder(grant) => grant,
            Request::Local(approval) => return approval.apply(profile),
        };
        let (answer, _) = execute(game::mod_command_for(home, exe, profile)?, &grant.command())?;
        ensure!(
            answer["accepted"] == true,
            "The builder did not accept the mod approval."
        );
        Ok(())
    }
}

pub(crate) fn descriptions(grants: &[Grant]) -> String {
    grants
        .iter()
        .map(Grant::description)
        .collect::<Vec<_>>()
        .join("\n\n")
}

pub(crate) fn approve_all(home: &Path, exe: &Path, profile: &Path, grants: &[Grant]) -> Result<()> {
    let current = pending(home, exe, profile)?;
    ensure!(
        current.len() == grants.len() && current.iter().all(|grant| grants.contains(grant)),
        "The mod permissions changed. Review them again."
    );
    for grant in grants {
        grant.approve(home, exe, profile)?;
    }
    ensure!(
        pending(home, exe, profile)?.is_empty(),
        "Some mod permissions still need review."
    );
    Ok(())
}

pub(crate) fn pending(home: &Path, exe: &Path, profile: &Path) -> Result<Vec<Grant>> {
    let scoped = |request| Grant {
        executable: exe.into(),
        profile: profile.into(),
        request,
    };
    let command = game::mod_command_for(home, exe, profile)?;
    let (installed, _) = match execute(command, &args(&["installed"])) {
        Err(e) if e.is::<MissingTools>() => {
            return Ok(super::local::pending(profile)?
                .into_iter()
                .map(|approval| scoped(Request::Local(approval)))
                .collect());
        }
        result => result?,
    };
    let rows = installed["mods"]
        .as_array()
        .context("The builder returned an unreadable mod list.")?;
    // Unregistered folders cannot receive a grant until the player adds them to the profile.
    rows.iter()
        .filter(|row| {
            row["in_profile"] != false && row["on"] == true && row["trust"]["asks"] == true
        })
        .map(|row| {
            Ok(scoped(Request::Builder(BuilderGrant::from_row(
                home, exe, profile, row,
            )?)))
        })
        .collect()
}
