//! Release discovery is read-only. The builder installer verifies the player's chosen release separately.
use super::releases::{self, PLATFORM};
use crate::update;
use anyhow::Result;
use serde_json::Value;

#[derive(Clone, Default)]
pub(super) struct Catalog {
    pub stable: Vec<String>,
    pub nightly: Vec<String>,
    pub pulls: Value,
    main: Option<String>,
    notes: std::collections::BTreeMap<String, String>,
}

impl Catalog {
    pub fn fetch(installed: &[String]) -> Result<Self> {
        let releases = update::list_releases("Longshot")?;
        let by_channel = |channel: &str| {
            let mut rows = releases
                .iter()
                .filter(|r| !r.draft && (channel == "nightly" || !r.prerelease))
                .filter_map(|r| {
                    let key = update::channel_order(&r.tag_name, channel, 3)?;
                    let manifest = format!("longshot-builder-{}-{PLATFORM}.json", r.tag_name);
                    r.assets
                        .iter()
                        .any(|a| a.name == manifest)
                        .then(|| (key, r.tag_name.clone()))
                })
                .collect::<Vec<_>>();
            rows.sort_unstable_by(|a, b| b.cmp(a));
            rows.dedup_by(|a, b| a.1 == b.1);
            rows.into_iter().map(|(_, name)| name).collect()
        };
        let main = installed
            .iter()
            .any(|tag| tag.starts_with("main-"))
            .then(|| releases::resolve("Main", "").map(|head| head.tag().to_owned()))
            .and_then(Result::ok);
        let pulls = releases::pulls();
        let pulls = if installed.iter().any(|tag| tag.starts_with("pr-")) {
            pulls?
        } else {
            pulls.unwrap_or_default()
        };
        Ok(Self {
            stable: by_channel("stable"),
            nightly: by_channel("nightly"),
            pulls,
            main,
            notes: releases
                .iter()
                .map(|r| {
                    (
                        r.tag_name.clone(),
                        super::notes::launcher_body(&r.body).to_owned(),
                    )
                })
                .collect(),
        })
    }
    pub fn notes(&self, tag: &str) -> &str {
        self.notes.get(tag).map(String::as_str).unwrap_or_default()
    }
    pub fn versions(&self) -> Vec<String> {
        self.stable.iter().chain(&self.nightly).cloned().collect()
    }
    pub fn trusted_pulls(&self) -> Vec<String> {
        self.pulls["pulls"]
            .as_array()
            .into_iter()
            .flatten()
            .filter(|v| v["approved"] == true || v["author_bigbean"] == true)
            .filter_map(|v| {
                let n = v["number"].as_u64()?;
                releases::pull_row(v, n).ok()?;
                Some(format!("#{} · {}", n, v["title"].as_str()?))
            })
            .take(2000)
            .collect()
    }
    pub fn update_for(&self, tag: &str) -> Option<String> {
        if tag.starts_with("main-") {
            return self
                .main
                .as_ref()
                .filter(|latest| latest.as_str() != tag)
                .cloned();
        }
        if let Some(n) = super::channel::tag_pr(tag) {
            let source = releases::pull(&self.pulls, n).ok()?;
            return (source.tag != tag).then_some(source.tag);
        }
        let track = match super::channel::Channel::from_prefix(tag) {
            super::channel::Channel::Nightly => "nightly",
            _ => "stable",
        };
        let newest = if track == "nightly" {
            self.nightly.first()?
        } else {
            self.stable.first()?
        };
        (update::channel_order(newest, track, 3)? > update::channel_order(tag, track, 3)?)
            .then(|| newest.to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_install_follows_its_own_channel() {
        let catalog = Catalog {
            stable: vec!["v3.2.0".into()],
            nightly: vec!["nightly-62-0123abcd".into()],
            main: Some("main-fedcba98".into()),
            ..Default::default()
        };
        assert_eq!(catalog.update_for("v3.1.0").as_deref(), Some("v3.2.0"));
        assert_eq!(
            catalog.update_for("nightly-58-abcdef12").as_deref(),
            Some("nightly-62-0123abcd")
        );
        assert_eq!(
            catalog.update_for("main-01234567").as_deref(),
            Some("main-fedcba98")
        );
        assert_eq!(catalog.update_for("main-fedcba98"), None);
        assert_eq!(catalog.update_for("v3.2.0"), None);
        assert_eq!(catalog.update_for("nightly-62-0123abcd"), None);
    }
}
