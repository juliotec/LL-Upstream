//! Game build tracks. Old registry rows and signed manifests retain their prefix-based format.
#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum Channel {
    Stable,
    Nightly,
    Main,
    PullRequest,
}

impl Channel {
    pub(super) fn from_prefix(tag: &str) -> Self {
        if tag.starts_with("nightly-") {
            Self::Nightly
        } else if tag.starts_with("pr-") {
            Self::PullRequest
        } else if tag.starts_with("main-") {
            Self::Main
        } else {
            Self::Stable
        }
    }

    pub(super) fn of(tag: &str) -> Self {
        match Self::from_prefix(tag) {
            Self::Nightly if crate::update::channel_order(tag, "nightly", 3).is_none() => {
                Self::Stable
            }
            Self::PullRequest
                if !tag
                    .strip_prefix("pr-")
                    .and_then(|value| value.split_once('-'))
                    .is_some_and(|(number, hash)| {
                        number.bytes().all(|byte| byte.is_ascii_digit())
                            && number.parse::<u32>().is_ok_and(|id| id != 0)
                            && hash.len() == 8
                            && hash
                                .bytes()
                                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
                    }) =>
            {
                Self::Stable
            }
            channel => channel,
        }
    }

    pub(super) fn label(self) -> &'static str {
        match self {
            Self::Stable => "Stable",
            Self::Nightly => "Nightly",
            Self::Main => "Main",
            Self::PullRequest => "Pull request",
        }
    }

    pub(super) fn registry_name(self) -> &'static str {
        match self {
            Self::Stable => "stable",
            Self::Nightly => "nightly",
            Self::Main | Self::PullRequest => "pr",
        }
    }

    pub(super) fn is_source(self) -> bool {
        matches!(self, Self::Main | Self::PullRequest)
    }
}

pub(super) fn tag_pr(tag: &str) -> Option<u64> {
    tag.strip_prefix("pr-")?.split('-').next()?.parse().ok()
}
