use crate::home;
use anyhow::{Result, bail};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::path::Path;

pub(super) struct License {
    pub setting: &'static str,
    pub receipt: Option<(&'static str, &'static str)>,
    pub summary: &'static str,
    pub document: &'static str,
    pub source: Option<&'static str>,
}

pub(super) fn for_module(key: &str, windows: bool) -> Result<License> {
    Ok(match key {
        "dlss" => License {
            setting: "dlssLicense",
            receipt: Some(("nvidia", "9981c6350dc58c0ce21f024fd65e31e851c31689")),
            summary: crate::i18n::key(
                "The builder downloads NVIDIA's DLSS SDK v310.9.16. Its runtime files stay on this PC with your build.",
            ),
            document: include_str!("../../assets/licenses/NVIDIA-DLSS.txt"),
            source: Some(
                "https://github.com/NVIDIA/DLSS/blob/9981c6350dc58c0ce21f024fd65e31e851c31689/LICENSE.txt",
            ),
        },
        "fsr4" if windows => License {
            setting: "fsrLicense",
            receipt: Some(("amd", "7539ae8b6568ad29ed78a753404351daf3b5cca7")),
            summary: crate::i18n::key(
                "The builder downloads AMD's signed FSR 4 Vulkan runtime from FidelityFX SDK v2.4.0 and includes AMD's licence with your build.",
            ),
            document: include_str!("../../assets/licenses/AMD-FSR.md"),
            source: Some(
                "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/7539ae8b6568ad29ed78a753404351daf3b5cca7/Kits/FidelityFX/docs/license.md",
            ),
        },
        "fsr4" => License {
            setting: "fsrLinuxNotice",
            receipt: None,
            summary: crate::i18n::key(
                "Choose your Vulkan FSR 4 library to use it with this build.",
            ),
            document: "On Linux the builder compiles FSR 4 into the game's Vulkan renderer. The game loads amd_fidelityfx_upscaler_vk.so from your build folder. Choose a library you can use under its own terms. The launcher copies it to the build folder on this PC.",
            source: None,
        },
        "xess" if windows => License {
            setting: "xessLicense",
            receipt: Some(("intel", "de0fb9c1c510661c571164e1418ceca8101dab69")),
            summary: crate::i18n::key(
                "The builder downloads Intel's signed XeSS Super Resolution library 2.0.2.68 and includes Intel's licence and third-party notices with your build.",
            ),
            document: concat!(
                include_str!("../../assets/licenses/Intel-XeSS.txt"),
                "\n\n",
                include_str!("../../assets/licenses/Intel-XeSS-notices.txt"),
            ),
            source: Some(
                "https://github.com/intel/xess/blob/de0fb9c1c510661c571164e1418ceca8101dab69/LICENSE.txt",
            ),
        },
        _ => bail!("This module is unavailable on this platform."),
    })
}

pub(super) fn vendor(key: &str) -> Option<&'static str> {
    for_module(key, cfg!(windows))
        .ok()?
        .receipt
        .map(|(vendor, _)| vendor)
}

pub(super) fn accepted(key: &str, settings: &serde_json::Value) -> bool {
    for_module(key, cfg!(windows)).is_ok_and(|license| settings[license.setting] == true)
}

impl License {
    /// Return whether to expand the document on this open. Viewing it never grants consent.
    pub fn first_view(&self, home: &Path) -> Result<bool> {
        let field = format!("{}Viewed", self.setting);
        let digest = format!("{:x}", Sha256::digest(self.document.as_bytes()));
        if home::settings(home)?[&field] == digest {
            return Ok(false);
        }
        home::patch_settings(home, &json!({ field: digest }))?;
        Ok(true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // These are the licence file SHA-256 values pinned by the builder alongside its SDKs.
    #[test]
    fn embeds_the_vendor_documents_byte_for_byte() -> Result<()> {
        for (text, digest) in [
            (
                for_module("dlss", false)?.document,
                "d4216e39ebef5f9b50a6712ebb37beeb5379862a67733a9999c651f21592aaf0",
            ),
            (
                for_module("fsr4", true)?.document,
                "32357259b965d6a8001e9e508ba60eda6034027c5923a7d226ffbaff813c2bfb",
            ),
            (
                include_str!("../../assets/licenses/Intel-XeSS.txt"),
                "784be42c39a4a4d03cf85d88e15e59d75828fca773ce45b8dbfffbd7eaf208df",
            ),
            (
                include_str!("../../assets/licenses/Intel-XeSS-notices.txt"),
                "e8d8899dbfc4de0e8ca25560be9358e03c1405d7c956d20a1838c3c709b9b51c",
            ),
        ] {
            assert_eq!(format!("{:x}", Sha256::digest(text.as_bytes())), digest);
        }
        for name in ["dlss", "fsr4", "xess"] {
            let license = for_module(name, true)?;
            let (_, pin) = license.receipt.unwrap();
            assert!(license.source.unwrap().contains(&format!("/blob/{pin}/")));
        }
        let intel = for_module("xess", true)?;
        assert!(
            intel
                .document
                .starts_with(include_str!("../../assets/licenses/Intel-XeSS.txt"))
        );
        assert!(
            intel
                .document
                .ends_with(include_str!("../../assets/licenses/Intel-XeSS-notices.txt"))
        );
        Ok(())
    }

    #[test]
    fn first_view_is_per_document_and_survives_restart_without_consent() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let root = temp.path();
        let nvidia = for_module("dlss", false)?;
        assert!(nvidia.first_view(root)?);
        assert!(!for_module("dlss", true)?.first_view(root)?);
        assert!(home::settings(root)?[nvidia.setting].is_null());
        assert!(for_module("fsr4", true)?.first_view(root)?);
        assert!(for_module("fsr4", false)?.first_view(root)?);
        assert!(!for_module("fsr4", false)?.first_view(root)?);
        let revised = License {
            document: "Updated terms",
            ..nvidia
        };
        assert!(revised.first_view(root)?);
        assert!(!revised.first_view(root)?);
        assert!(for_module("xess", false).is_err());
        Ok(())
    }
}
