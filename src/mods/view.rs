use super::{
    Dependency, Screen, Section,
    builder::{args, field, name, names, word},
};
use crate::{MainWindow, ModRow, Mods};
use anyhow::{Context, Result};
use serde_json::Value;
use slint::{ComponentHandle, ModelRc};
use std::path::Path;
pub(super) fn description(row: &Value, section: Section) -> String {
    let mut lines: Vec<String> = Vec::new();
    let mut add = |text: &str| {
        if !text.is_empty() {
            lines.push(text.to_owned());
        }
    };
    for key in ["summary", "why", "error"] {
        add(field(row, key));
    }
    add(field(&row["manifest_problem"], "text"));
    if let Some(author) = row["manifest"]["author"].as_str() {
        add(&format!("By {author}"));
    }
    if let Some(risk) = row["risk"].as_str() {
        add(&format!("Permission risk · {risk}"));
    }
    if row["fits"] == false {
        add("This release does not fit the selected game's mod API.");
    }
    if row["replaces_game_files"] == true {
        add("Replaces game files.");
    }
    if let Some(deps) = row["manifest"]["dependencies"].as_array() {
        for dep in deps {
            add(&format!(
                "{} · {} {}",
                field(dep, "kind"),
                field(dep, "id"),
                field(dep, "range")
            ));
        }
    }
    for key in ["required", "optional"] {
        let p = names(&row["manifest"]["permissions"][key]);
        if !p.is_empty() {
            add(&format!("{key} permissions · {}", p.join(", ")));
        }
    }
    if !field(row, "source").is_empty() {
        add(field(row, "source"));
    }
    match section {
        Section::Sources => {
            add(field(row, "url"));
            if let Some(n) = row["mods"].as_u64() {
                add(&format!("{n} mods"));
            }
            if let Some(key) = row["key"].as_str() {
                add(&format!("Repository key\n{key}"));
            }
        }
        Section::Emulators => {
            add(field(row, "emulator"));
            add(field(row, "folder"));
            if row["code"] == true {
                add("Contains code patches.");
            }
        }
        Section::Discover => {
            let others = names(&row["clash"]);
            if !others.is_empty() {
                add(&format!("Also listed by {}", others.join(", ")));
            }
        }
        _ => (),
    }
    lines.join("\n\n")
}

pub(super) fn release_details(release: &Value) -> String {
    let permissions = names(&release["permissions"]);
    let dependencies = names(&release["dependencies"]);
    let mut lines = vec![format!("Version {}", field(release, "version"))];
    if let Some(risk) = release["risk"].as_str() {
        lines.push(format!("Permission risk · {risk}"));
    }
    if !permissions.is_empty() {
        lines.push(format!("Permissions · {}", permissions.join(", ")));
    }
    if !dependencies.is_empty() {
        lines.push(format!("Needs · {}", dependencies.join(", ")));
    }
    if let Some(licence) = release["licence"].as_str() {
        lines.push(licence.to_owned());
    }
    lines.join("\n")
}

impl Screen {
    pub(super) fn select(&mut self, index: usize) {
        self.selection = index;
        self.review = None;
        self.pending_permissions.clear();
        self.details.clear();
        self.showing_details = false;
        self.dependency = None;
    }

    pub(super) fn category(&mut self, index: usize) {
        let Some(category) = self.categories.get(index) else {
            return;
        };
        self.category = index;
        self.rows = self
            .catalog
            .iter()
            .filter(|r| index == 0 || names(&r["categories"]).contains(&category.as_str()))
            .cloned()
            .collect();
        self.select(0);
    }

    pub(super) fn load(&mut self, root: &Path, section: Section) -> Result<()> {
        self.section = section;
        self.rows.clear();
        self.catalog.clear();
        self.categories.clear();
        self.category = 0;
        self.select(0);
        if section == Section::Manual {
            return Ok(());
        }
        let cmd = match section {
            Section::Installed => args(&["installed"]),
            Section::Discover => {
                let mut cmd = args(&["search"]);
                for token in self.query.split_whitespace() {
                    cmd.push(word(token)?.into());
                }
                cmd.extend(args(&["--per-page", "100"]));
                cmd
            }
            Section::Sources => args(&["source", "list"]),
            Section::Emulators => args(&["emulators"]),
            Section::Manual => unreachable!(),
        };
        let (data, notes) = match self.run(root, &cmd) {
            Ok(reply) => {
                self.basic = false;
                reply
            }
            Err(e) if e.is::<super::builder::MissingTools>() => {
                self.basic = true;
                if section == Section::Installed {
                    self.rows = super::local::installed_rows(&crate::players::current_dir(root)?)?;
                } else {
                    self.section = Section::Manual;
                }
                return Ok(());
            }
            Err(e) => return Err(e),
        };
        let rows = if section == Section::Sources {
            &data
        } else {
            &data["mods"]
        };
        self.rows = rows
            .as_array()
            .context("The builder returned an unreadable mod list.")?
            .clone();
        if section == Section::Discover {
            self.catalog = self.rows.clone();
            let available = self
                .rows
                .iter()
                .flat_map(|r| names(&r["categories"]))
                .collect::<std::collections::BTreeSet<_>>();
            self.categories.push("All categories".to_owned());
            self.categories
                .extend(available.into_iter().map(str::to_owned));
        }
        self.message = notes;
        Ok(())
    }

    pub(super) fn paint(&self, ui: &MainWindow) {
        let m = ui.global::<Mods>();
        m.set_basic(self.basic);
        m.set_section(self.section.name().into());
        m.set_categories(ModelRc::from(
            self.categories
                .iter()
                .map(slint::SharedString::from)
                .collect::<Vec<_>>()
                .as_slice(),
        ));
        m.set_category_index(self.category as i32);
        m.set_enabled_count(self.rows.iter().filter(|row| row["on"] == true).count() as i32);
        m.set_index(if self.rows.is_empty() {
            -1
        } else {
            self.selection as i32
        });
        let rows = self
            .rows
            .iter()
            .map(|r| {
                let title = name(r);
                let initials = title
                    .split(|c: char| !c.is_alphanumeric())
                    .filter_map(|w| w.chars().next())
                    .take(2)
                    .collect::<String>()
                    .to_uppercase();
                let detail = ["summary", "why", "url", "emulator", "id"]
                    .iter()
                    .find_map(|k| r[k].as_str().filter(|s| !s.is_empty()))
                    .unwrap_or("");
                let badge = match self.section {
                    Section::Installed if r["loose"] == true => "Needs import",
                    Section::Installed if r["trust"]["asks"] == true => "Review",
                    Section::Installed if r["on"] == true => "On",
                    Section::Installed => "Off",
                    Section::Discover if r["installed"].is_string() => "Installed",
                    Section::Discover => field(&r["latest"], "version"),
                    Section::Sources => field(r, "kind"),
                    _ => "",
                };
                ModRow {
                    name: title.into(),
                    detail: detail.into(),
                    badge: badge.into(),
                    initials: initials.into(),
                    active: r["on"] == true,
                    tone: field(r, "id")
                        .bytes()
                        .fold(0u32, |acc, byte| {
                            acc.wrapping_mul(31).wrapping_add(u32::from(byte))
                        })
                        .rem_euclid(5) as i32,
                    update: field(&r["update"], "version").into(),
                    kind: if r["unsafe"] == true || r["native"] == true {
                        "Unsafe"
                    } else if r["code_hash"].is_string() {
                        "Code"
                    } else {
                        ""
                    }
                    .into(),
                    problem: if r["on"] == true
                        && matches!(
                            field(r, "state"),
                            "broken" | "not-loaded" | "held" | "missing"
                        ) {
                        "Won’t load"
                    } else {
                        ""
                    }
                    .into(),
                }
            })
            .collect::<Vec<_>>();
        m.set_rows(ModelRc::from(rows.as_slice()));
        let row = self.rows.get(self.selection).unwrap_or(&Value::Null);
        m.set_selected_name(name(row).into());
        m.set_selected_author(field(&row["manifest"], "author").into());
        let required = row["manifest"]["dependencies"].as_array().map(|deps| {
            deps.iter()
                .filter(|d| matches!(field(d, "kind"), "required" | "unordered"))
                .map(|d| field(d, "id"))
                .collect::<Vec<_>>()
        });
        m.set_selected_needs(
            required
                .map_or_else(String::new, |ids| {
                    if ids.is_empty() {
                        "Nothing else".into()
                    } else {
                        ids.join(", ")
                    }
                })
                .into(),
        );
        m.set_needs_import(row["loose"] == true);
        m.set_selected_version(
            row["version"]
                .as_str()
                .or(row["latest"]["version"].as_str())
                .unwrap_or("")
                .into(),
        );
        m.set_update_version(field(&row["update"], "version").into());
        m.set_can_allow(row["trust"]["asks"] == true && !field(row, "code_hash").is_empty());
        m.set_dependency_action(
            match &self.dependency {
                Some(Dependency::Enable(name)) => format!("Switch on {name}"),
                Some(Dependency::Find(name)) => format!("Find {name}"),
                None => String::new(),
            }
            .into(),
        );
        m.set_showing_details(self.showing_details);
        m.set_full_detail(self.details.as_str().into());
        let text = if row.is_null() {
            match self.section {
                Section::Manual | Section::Installed if self.basic => "Add an extracted folder containing romfs or exefs. Before building with an ExeFS patch, the launcher asks for your approval.",
                Section::Manual => "The builder checks each mod before adding it to this profile.",
                Section::Installed => "Discover mods from the repositories you follow, or add a .zip, .lsmod or mod folder from your computer.",
                Section::Sources => {
                    "Add the address of a mod repository to inspect its contents and signing key."
                }
                _ => "Choose a mod to see its details.",
            }
            .to_owned()
        } else {
            description(row, self.section)
        };
        m.set_selected_detail(text.into());
        m.set_reviewing_permissions(!self.pending_permissions.is_empty());
        m.set_review(if self.pending_permissions.is_empty() {
            self.review.as_ref().map_or("", |r| r.text.as_str()).into()
        } else {
            super::trust::descriptions(&self.pending_permissions).into()
        });
        m.set_status(self.message.as_str().into());
    }
}
