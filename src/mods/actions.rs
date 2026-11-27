use super::{
    Dependency, Review, Screen, Section,
    builder::{args, field, name, names, sourced, word},
    view::{description, release_details},
};
use crate::{game, players};
use anyhow::{Context, Result, bail, ensure};
use serde_json::Value;
use std::{
    ffi::OsString,
    path::{Path, PathBuf},
};

impl Screen {
    fn confirm(&mut self, root: &Path) -> Result<()> {
        let grants = std::mem::take(&mut self.pending_permissions);
        if !grants.is_empty() {
            let (profile, exe) = self
                .identity
                .as_ref()
                .context("Choose a profile and game first.")?;
            players::ensure_paths_idle(&[profile])?;
            super::trust::approve_all(root, exe, profile, &grants)?;
            return self.load(root, Section::Installed);
        }
        let review = self
            .review
            .take()
            .context("Choose an action to review first.")?;
        if let Some((query, approved)) = review.preview {
            ensure!(
                self.run(root, &query)?.0 == approved,
                "The mod or its dependencies changed. Review it again before continuing."
            );
        }
        self.change(root, &review.args)?;
        if matches!(
            review.args.first().and_then(|arg| arg.to_str()),
            Some("add" | "install" | "update" | "import")
        ) {
            self.review_pending_permissions(root)?;
        }
        Ok(())
    }

    fn change(&mut self, root: &Path, command: &[OsString]) -> Result<()> {
        let action = command.first().and_then(|arg| arg.to_str());
        if matches!(action, Some("add" | "import" | "install" | "update")) {
            let (profile, _) = self
                .identity
                .as_ref()
                .context("Choose a profile and game first.")?;
            players::ensure_paths_idle(&[profile])?;
        } else {
            players::ensure_idle()?;
        }
        if matches!(action, Some("install" | "update" | "source")) {
            let settings = crate::home::settings(root)?;
            crate::mod_store::ensure(
                &crate::home::work(root, &settings).join("longshot-builder"),
                &crate::home::data_folder(root, &settings),
            )?;
        }
        let (result, mut notes) = self.run(root, command)?;
        for item in result.as_array().into_iter().flatten() {
            let left = names(&item["left_out"]);
            if !left.is_empty() {
                notes.push_str(&format!(
                    "\n{} left out · {}",
                    field(item, "id"),
                    left.join(", ")
                ));
            }
        }
        let selected = self
            .rows
            .get(self.selection)
            .and_then(|r| r["id"].as_str())
            .map(str::to_owned);
        let section = if self.section == Section::Sources {
            Section::Sources
        } else {
            Section::Installed
        };
        self.load(root, section)?;
        if let Some(index) = self
            .rows
            .iter()
            .position(|r| selected.as_deref().is_some_and(|id| r["id"] == id))
        {
            self.select(index);
        }
        if !notes.trim().is_empty() {
            self.message = notes.trim().to_owned();
        }
        Ok(())
    }

    fn review_pending_permissions(&mut self, root: &Path) -> Result<()> {
        let (profile, exe) = self
            .identity
            .as_ref()
            .context("Choose a profile and game first.")?;
        self.pending_permissions = super::trust::pending(root, exe, profile)?;
        Ok(())
    }

    fn add_local_folder(
        &mut self,
        root: &Path,
        profile: &Path,
        import: super::local::Import,
    ) -> Result<()> {
        players::ensure_paths_idle(&[profile])?;
        let code = import.has_code();
        import.apply(profile)?;
        self.load(root, Section::Installed)?;
        if code {
            self.review_pending_permissions(root)?;
        }
        Ok(())
    }

    fn add(&mut self, root: &Path, path: PathBuf) -> Result<()> {
        ensure!(path.is_absolute(), "Choose a local mod file or folder.");
        self.load(root, Section::Manual)?;
        let mut command = args(&["add"]);
        command.push(path.clone().into_os_string());
        let mut preview = command.clone();
        preview.push("--plan".into());
        let plan = match self.run(root, &preview) {
            Ok((plan, _)) => plan,
            Err(error) if error.is::<super::builder::MissingTools>() => {
                self.basic = true;
                ensure!(
                    !path.is_file(),
                    "Extract the mod first, then choose the folder containing romfs or exefs."
                );
                let profile = players::current_dir(root)?;
                let import = super::local::review(&profile, &path)?;
                return self.add_local_folder(root, &profile, import);
            }
            Err(error) => return Err(error),
        };
        let id = word(field(&plan, "id"))?;
        if plan["replaces"] != true {
            self.change(root, &command)?;
            return self.review_pending_permissions(root);
        }
        command.push("--replace".into());
        let mut text = format!(
            "Add {id} {} to this profile? This replaces the installed copy.",
            field(&plan, "version")
        );
        if plan["code"] == true {
            text.push_str("\n\nThis mod includes code.");
        }
        let perms = names(&plan["permissions"]);
        if !perms.is_empty() {
            text.push_str(&format!("\n\nPermissions · {}", perms.join(", ")));
        }
        self.review = Some(Review {
            preview: Some((preview, plan)),
            text,
            args: command,
        });
        Ok(())
    }

    fn install(&mut self, root: &Path, row: &Value, updating: bool) -> Result<()> {
        let version = if updating {
            &row["update"]["version"]
        } else {
            &row["latest"]["version"]
        };
        let version = word(
            version
                .as_str()
                .context("This mod has no release to install.")?,
        )?;
        let (detail, _) = self.run(root, &sourced("details", row)?)?;
        let release = detail["versions"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|r| r["version"] == version)
            .context("The selected release has disappeared.")?;
        ensure!(
            release["withdrawn"] != true,
            "This release has been withdrawn."
        );
        let mut command = sourced(if updating { "update" } else { "install" }, row)?;
        command.extend(args(&["--version", version]));
        let hash = word(field(release, "sha256"))?;
        command.extend(args(&["--sha256", hash]));
        let mut preview = command.clone();
        preview.push("--plan".into());
        let (plan, _) = self.run(root, &preview)?;
        let missing = plan["missing"]
            .as_array()
            .into_iter()
            .flatten()
            .map(|d| {
                format!(
                    "{} {} {}",
                    field(d, "id"),
                    field(d, "range"),
                    field(d, "why")
                )
            })
            .collect::<Vec<_>>();
        ensure!(
            missing.is_empty(),
            "Missing dependencies\n{}",
            missing.join("\n")
        );
        ensure!(
            plan["plan"].is_array(),
            "The builder did not return an installation plan."
        );
        if updating {
            let changes = plan["plan"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|entry| {
                    let id = entry["id"].as_str()?;
                    let version = entry["version"].as_str()?;
                    Some(format!("{id} {version}"))
                })
                .collect::<Vec<_>>();
            let mut text = format!(
                "Update {} from {} to {version}?\n\n{}",
                name(row),
                field(row, "version"),
                release_details(release)
            );
            if !changes.is_empty() {
                text.push_str(&format!("\n\nInstall or update\n{}", changes.join("\n")));
            }
            self.review = Some(Review {
                text,
                args: command,
                preview: Some((preview, plan)),
            });
            return Ok(());
        }
        self.change(root, &command)?;
        self.review_pending_permissions(root)
    }

    fn add_source(&mut self, root: &Path, entry: &str) -> Result<()> {
        let url = word(entry.trim())?;
        ensure!(
            url.starts_with("https://"),
            "Enter an HTTPS repository address."
        );
        let mut command = args(&["source", "add", url]);
        let (found, _) = self.run(root, &command)?;
        let key = field(&found, "key");
        command.push("--yes".into());
        if !key.is_empty() {
            command.extend(args(&["--accept-key", word(key)?]));
        }
        let mut text = format!(
            "Add {}?\n{}\n\n{} mods",
            name(&found),
            url,
            names(&found["mods"]).len()
        );
        if !key.is_empty() {
            text.push_str(&format!("\n\nCheck the repository signing key\n{key}"));
        }
        self.review = Some(Review {
            preview: None,
            text,
            args: command,
        });
        Ok(())
    }
    fn source_action(&mut self, root: &Path, action: &str, row: &Value) -> Result<()> {
        let url = word(field(row, "url"))?;
        match action {
            "up" | "down" => {
                let index = self.selection;
                let target = if action == "up" {
                    index.checked_sub(1)
                } else {
                    index.checked_add(1)
                }
                .filter(|i| *i < self.rows.len())
                .context("The repository is already at the end of the list.")?;
                let mut urls = self
                    .rows
                    .iter()
                    .map(|r| word(field(r, "url")).map(OsString::from))
                    .collect::<Result<Vec<_>>>()?;
                urls.swap(index, target);
                let mut command = args(&["source", "order"]);
                command.extend(urls);
                self.change(root, &command)?;
                self.select(target.min(self.rows.len().saturating_sub(1)));
            }
            "refresh-source" => return self.change(root, &args(&["source", "refresh", url])),
            "remove-source" => {
                self.review = Some(Review {
                    preview: None,
                    text: format!("Remove {} from your repositories?\n{url}", name(row)),
                    args: args(&["source", "remove", url]),
                })
            }
            _ => bail!("Choose a repository action."),
        }
        Ok(())
    }
    fn review_code(&mut self, root: &Path, row: &Value) -> Result<()> {
        let (profile, exe) = self
            .identity
            .as_ref()
            .context("Choose a profile and game first.")?;
        let grant = super::trust::BuilderGrant::from_row(root, exe, profile, row)?;
        self.review = Some(Review {
            preview: None,
            text: grant.description(),
            args: grant.command(),
        });
        Ok(())
    }
    fn resolve_needs(&mut self, root: &Path, row: &Value) -> Result<()> {
        let (result, _) = self.run(root, &args(&["resolve"]))?;
        let id = field(row, "id");
        if let Some(held) = result["off"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|item| item["id"] == id)
        {
            let why = field(held, "text");
            if !why.is_empty() && why != field(row, "why") {
                self.details.push_str(&format!("\n\n{why}"));
            }
        }
        for need in result["needs"][id].as_array().into_iter().flatten() {
            let dep = &need["dep"];
            let name = field(dep, "id");
            let state = field(need, "state");
            self.details
                .push_str(&format!("\n{} {} · {state}", name, field(dep, "range")));
            if self.dependency.is_none() && matches!(field(dep, "kind"), "required" | "unordered") {
                self.dependency = match state {
                    "off" => Some(Dependency::Enable(name.to_owned())),
                    "missing" | "wrong-version" if !name.starts_with("system/") => {
                        Some(Dependency::Find(name.to_owned()))
                    }
                    _ => None,
                };
            }
        }
        Ok(())
    }

    fn details(&mut self, root: &Path, row: &Value) -> Result<()> {
        self.showing_details = true;
        self.details = description(row, self.section);
        self.dependency = None;
        if self.section == Section::Installed
            && row["on"] == true
            && matches!(
                field(row, "state"),
                "held" | "not-loaded" | "broken" | "missing"
            )
        {
            self.resolve_needs(root, row)?;
        }
        if self.section == Section::Discover || !field(row, "source").is_empty() {
            let (doc, _) = self.run(root, &sourced("details", row)?)?;
            let mut parts = vec![];
            if !field(&doc, "summary").is_empty() && field(&doc, "summary") != field(row, "summary")
            {
                parts.push(field(&doc, "summary").to_owned());
            }
            if let Some(versions) = doc["versions"].as_array() {
                parts.extend(versions.iter().map(release_details));
            }
            if let Some(readme) = doc["readme"].as_str() {
                parts.push(readme.to_owned());
            }
            self.details
                .push_str(&format!("\n\n{}", parts.join("\n\n")));
        }
        Ok(())
    }
    pub(super) fn act(
        &mut self,
        root: &Path,
        action: &str,
        entry: &str,
        path: Option<PathBuf>,
    ) -> Result<()> {
        self.message.clear();
        let profile = players::current_dir(root)?;
        if action == "folder" {
            let dir = profile.join("mods");
            std::fs::create_dir_all(&dir)?;
            open::that(dir)?;
            return Ok(());
        }
        let identity = (
            profile,
            game::mod_command(root)
                .ok()
                .map(|cmd| PathBuf::from(cmd.get_program()))
                .unwrap_or_default(),
        );
        if self.identity.as_ref() != Some(&identity) {
            *self = Self {
                identity: Some(identity),
                ..Default::default()
            };
            if !matches!(
                action,
                "installed" | "discover" | "manual" | "add" | "sources" | "emulators"
            ) {
                self.load(root, Section::Installed)?;
                bail!("The active profile or game version changed. Select the mod again.")
            }
        }
        if action != "confirm" {
            self.pending_permissions.clear();
            self.review = None;
        }
        match action {
            "installed" => return self.load(root, Section::Installed),
            "manual" => return self.load(root, Section::Manual),
            "discover" if self.basic => return self.load(root, Section::Manual),
            "discover" | "search" => {
                self.query = if action == "search" {
                    entry.to_owned()
                } else {
                    String::new()
                };
                return self.load(root, Section::Discover);
            }
            "sources" => return self.load(root, Section::Sources),
            "emulators" => return self.load(root, Section::Emulators),
            "confirm" => return self.confirm(root),
            "cancel" => return Ok(()),
            "back-to-list" => {
                self.showing_details = false;
                return Ok(());
            }
            "add" => return self.add(root, path.context("Choose a mod to add.")?),
            "add-source" => return self.add_source(root, entry),
            _ => (),
        }
        let row = self
            .rows
            .get(self.selection)
            .context("Choose a mod first.")?
            .clone();
        if self.basic && self.section == Section::Installed {
            match action {
                "toggle" | "import" if row["loose"] == true => {
                    return self.add(root, PathBuf::from(field(&row, "folder")));
                }
                "toggle" => {
                    players::ensure_idle()?;
                    let folder = row["folder"]
                        .as_str()
                        .context("This mod has no profile folder.")?;
                    super::local::set_switch(
                        &players::current_dir(root)?,
                        Path::new(folder),
                        row["on"] == false,
                    )?;
                    let index = self.selection;
                    self.load(root, Section::Installed)?;
                    if index < self.rows.len() {
                        self.select(index);
                    }
                    return Ok(());
                }
                "details" => return self.details(root, &row),
                _ => bail!("This action needs the builder's full mod tools."),
            }
        }
        if self.section == Section::Sources {
            return self.source_action(root, action, &row);
        }
        if action == "import" && self.section == Section::Emulators {
            let path = Path::new(field(&row, "folder"));
            ensure!(path.is_absolute(), "The emulator mod has no local folder.");
            self.review = Some(Review {
                preview: None,
                text: format!(
                    "Import {} into this profile?\n{}",
                    name(&row),
                    crate::home::display(path)
                ),
                args: vec!["import".into(), path.into()],
            });
            return Ok(());
        }
        let id = word(field(&row, "id"))?;
        match action {
            "toggle" => {
                return self.change(
                    root,
                    &args(&[
                        if row["on"] == true {
                            "disable"
                        } else {
                            "enable"
                        },
                        id,
                    ]),
                );
            }
            "up" | "down" => {
                return self.change(
                    root,
                    &args(&["order", id, if action == "up" { "--up" } else { "--down" }]),
                );
            }
            "remove" => {
                self.review = Some(Review {
                    preview: None,
                    text: if row["kind"] == "manual"
                        || row["path"].as_str().is_some_and(|p| p.starts_with("mods/"))
                    {
                        format!("Remove {} and its files from this profile?", name(&row))
                    } else {
                        format!("Remove {} from this profile?", name(&row))
                    },
                    args: args(&[
                        "remove",
                        if row["in_profile"] == false && !field(&row, "path").is_empty() {
                            word(field(&row, "path"))?
                        } else {
                            id
                        },
                    ]),
                })
            }
            "install" | "update" => return self.install(root, &row, action == "update"),
            "fix-dependency" => match self
                .dependency
                .as_ref()
                .context("Select a mod with an unmet dependency.")?
            {
                Dependency::Enable(name) => {
                    self.review = Some(Review {
                        text: format!("Turn on {name} in this profile?"),
                        args: args(&["enable", word(name)?]),
                        preview: None,
                    });
                }
                Dependency::Find(name) => {
                    self.query = word(name)?.to_owned();
                    return self.load(root, Section::Discover);
                }
            },
            "allow" => return self.review_code(root, &row),
            "details" => return self.details(root, &row),
            _ => bail!("Unrecognized mod action."),
        }
        Ok(())
    }
}
