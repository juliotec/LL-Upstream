//! The Forgejo catalogue locates files. Only signed checksums authorize their contents.
use anyhow::{Context, Result, bail, ensure};
use base64::{Engine, engine::general_purpose::STANDARD};
use p256::ecdsa::{Signature, VerifyingKey, signature::Verifier};
use p256::pkcs8::DecodePublicKey;
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    env, fs,
    io::{Read, Write},
    sync::LazyLock,
    time::Duration,
};

const ORIGIN: &str = "https://git.magicbean.dev";
const CDN: &str = "https://git-cdn.magicbean.dev";
const RELEASE_KEYS: &str = include_str!("../../assets/keys/release_keys.json");
const MAX_ASSET: u64 = 16 << 30;

#[derive(Clone, Debug)]
pub struct Asset {
    pub url: String,
    pub size: u64,
    pub sha256: Option<String>,
}

pub struct Release {
    pub tag: String,
    pub assets: BTreeMap<String, Asset>,
}

#[derive(Deserialize)]
pub struct ListedRelease {
    pub tag_name: String,
    pub draft: bool,
    pub prerelease: bool,
    #[serde(default)]
    pub body: String,
    #[serde(default)]
    pub assets: Vec<ListedAsset>,
}

#[derive(Deserialize)]
pub struct ListedAsset {
    pub name: String,
    pub browser_download_url: String,
    pub size: u64,
}

static HTTP: LazyLock<ureq::Agent> = LazyLock::new(|| {
    ureq::Agent::config_builder()
        .https_only(true)
        .timeout_connect(Some(Duration::from_secs(15)))
        .timeout_per_read(Some(Duration::from_secs(60)))
        .timeout_recv_response(Some(Duration::from_secs(30)))
        .build()
        .into()
});

fn body(url: &str) -> Result<ureq::Body> {
    ensure!(
        url.starts_with("https://"),
        "A release file must use HTTPS."
    );
    let get = |url: &str| {
        HTTP.get(url)
            .header("User-Agent", "LongshotLauncher")
            .call()
    };
    if let Some(path) = url.strip_prefix("https://git.magicbean.dev/") {
        if env::var("LONGSHOT_LAUNCHER_NO_CDN").as_deref() != Ok("1") {
            match get(&format!("{CDN}/{path}")) {
                Ok(r) => return Ok(r.into_body()),
                Err(ureq::Error::StatusCode(code)) if !matches!(code, 403 | 404 | 500..=599) => {
                    bail!("The release server answered HTTP {code}.")
                }
                Err(_) => {}
            }
        }
        if path.contains("/releases/download/") || path.starts_with("api/packages/") {
            let separator = if url.contains('?') { '&' } else { '?' };
            return Ok(get(&format!("{url}{separator}cdn=0"))?.into_body());
        }
    }
    Ok(get(url)?.into_body())
}

fn bytes(url: &str, max: u64) -> Result<Vec<u8>> {
    let mut buf = Vec::new();
    body(url)?
        .into_reader()
        .take(max + 1)
        .read_to_end(&mut buf)?;
    ensure!(
        buf.len() as u64 <= max,
        "The release server returned more data than expected."
    );
    Ok(buf)
}

fn api(repo: &str) -> Result<String> {
    ensure!(
        matches!(repo, "Longshot" | "LongshotLauncher"),
        "Unknown release repository."
    );
    Ok(format!(
        "{ORIGIN}/api/v1/repos/MagicBeanDev/{repo}/releases"
    ))
}

pub fn list_releases(repo: &str) -> Result<Vec<ListedRelease>> {
    let base = api(repo)?;
    let mut releases = Vec::new();
    for page in 1..=20 {
        let list: Vec<ListedRelease> =
            serde_json::from_slice(&bytes(&format!("{base}?limit=50&page={page}"), 16 << 20)?)?;
        let last = list.len() < 50;
        releases.extend(list.into_iter().filter(|r| !r.draft));
        if last {
            break;
        }
    }
    Ok(releases)
}

pub fn channel_order(tag: &str, channel: &str, min_major: u64) -> Option<(u64, u64, u64)> {
    fn number(s: &str) -> Option<u64> {
        if !s.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        s.parse::<i64>().ok().map(|v| v as u64)
    }
    match channel {
        "stable" => {
            let mut parts = tag.strip_prefix('v')?.split('.');
            let key = (
                number(parts.next()?)?,
                number(parts.next()?)?,
                number(parts.next()?)?,
            );
            (key.0 >= min_major && parts.next().is_none()).then_some(key)
        }
        "nightly" => {
            let clean = tag.strip_suffix("-dirty").unwrap_or(tag);
            let (count, sha) = clean.strip_prefix("nightly-")?.split_once('-')?;
            if count.len() > 9 || sha.len() != 8 || !lower_hex(sha) {
                return None;
            }
            Some((number(count)?, u64::from(clean != tag), 0))
        }
        _ => None,
    }
}

fn lower_hex(s: &str) -> bool {
    s.bytes()
        .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn production_keys() -> Result<Vec<VerifyingKey>> {
    #[derive(Deserialize)]
    struct Keyring {
        keys: Vec<String>,
    }
    serde_json::from_str::<Keyring>(RELEASE_KEYS)?
        .keys
        .iter()
        .map(|key| Ok(VerifyingKey::from_public_key_der(&STANDARD.decode(key)?)?))
        .collect()
}

fn keys() -> Result<Vec<VerifyingKey>> {
    let mut keys = production_keys()?;
    if let Some(path) = env::var_os("LONGSHOT_LAUNCHER_TRUST")
        && let Ok(pem) = fs::read_to_string(path)
        && let Ok(key) = VerifyingKey::from_public_key_pem(&pem)
    {
        keys.push(key);
    }
    Ok(keys)
}

fn signed_sums(
    sums: &[u8],
    signature: &[u8],
    keys: &[VerifyingKey],
) -> Result<BTreeMap<String, String>> {
    let der = STANDARD.decode(std::str::from_utf8(signature)?.trim())?;
    let signature = Signature::from_der(&der)?;
    ensure!(
        keys.iter().any(|k| k.verify(sums, &signature).is_ok()),
        "The release's SHA256SUMS is not signed by a trusted key."
    );
    let mut out = BTreeMap::new();
    for line in std::str::from_utf8(sums)?.lines().filter(|s| !s.is_empty()) {
        let (sha, name) = line.split_once(' ').context("Invalid SHA256SUMS entry.")?;
        let name = name
            .strip_prefix([' ', '*'])
            .context("Invalid SHA256SUMS entry.")?;
        ensure!(
            sha.len() == 64
                && lower_hex(sha)
                && !name.is_empty()
                && !name.contains(['/', '\\'])
                && !name.chars().any(char::is_control),
            "Invalid SHA256SUMS entry."
        );
        ensure!(
            out.insert(name.to_string(), sha.to_string()).is_none(),
            "Duplicate filename in SHA256SUMS."
        );
    }
    Ok(out)
}

pub fn signed_release(repo: &str, tag: &str) -> Result<Release> {
    ensure!(
        tag.len() <= 128
            && !matches!(tag, "" | "." | "..")
            && tag
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"._+-".contains(&b)),
        "Invalid release tag."
    );
    let listed: ListedRelease =
        serde_json::from_slice(&bytes(&format!("{}/tags/{tag}", api(repo)?), 2 << 20)?)?;
    ensure!(
        !listed.draft && listed.tag_name == tag,
        "The requested release is unavailable."
    );
    open_release(listed)
}

pub(super) fn open_release(listed: ListedRelease) -> Result<Release> {
    let mut assets = BTreeMap::new();
    for item in listed.assets {
        if item.browser_download_url.starts_with("https://") {
            ensure!(
                assets
                    .insert(
                        item.name,
                        Asset {
                            url: item.browser_download_url,
                            size: item.size,
                            sha256: None
                        }
                    )
                    .is_none(),
                "Duplicate release asset."
            );
        }
    }
    let metadata = |name: &str, max: u64| -> Result<Vec<u8>> {
        let asset = assets
            .get(name)
            .with_context(|| format!("The release is missing {name}."))?;
        ensure!(asset.size <= max, "{name} is too large.");
        let data = bytes(&asset.url, asset.size)?;
        ensure!(
            data.len() as u64 == asset.size,
            "{name} has the wrong size."
        );
        Ok(data)
    };
    let sums = signed_sums(
        &metadata("SHA256SUMS", 1 << 20)?,
        &metadata("SHA256SUMS.sig", 4096)?,
        &keys()?,
    )?;
    for (name, sha) in sums {
        if let Some(asset) = assets.get_mut(&name) {
            asset.sha256 = Some(sha);
        }
    }
    Ok(Release {
        tag: listed.tag_name,
        assets,
    })
}

pub fn download(asset: &Asset, out: &mut impl Write, progress: impl FnMut(u64)) -> Result<()> {
    let sha = asset
        .sha256
        .as_deref()
        .context("The requested file has no signed checksum.")?;
    ensure!(
        asset.size <= MAX_ASSET && sha.len() == 64 && lower_hex(sha),
        "Invalid release file size or digest."
    );
    copy_checked(
        body(&asset.url)?.into_reader(),
        out,
        asset.size,
        sha,
        progress,
    )
}

fn copy_checked(
    mut from: impl Read,
    out: &mut impl Write,
    size: u64,
    sha: &str,
    mut progress: impl FnMut(u64),
) -> Result<()> {
    let mut hash = Sha256::new();
    let mut written = 0u64;
    let mut buf = [0u8; 64 * 1024];
    loop {
        let n = from.read(&mut buf)?;
        if n == 0 {
            break;
        }
        written += n as u64;
        ensure!(
            written <= size,
            "The download exceeds the release's file size."
        );
        hash.update(&buf[..n]);
        out.write_all(&buf[..n])?;
        progress(written);
    }
    ensure!(
        written == size && format!("{:x}", hash.finalize()) == sha,
        "The download failed its size or SHA-256 check."
    );
    Ok(())
}

pub fn verified_bytes(asset: &Asset, max: u64) -> Result<Vec<u8>> {
    ensure!(
        asset.size <= max,
        "Release metadata exceeds its size limit."
    );
    let mut data = Vec::new();
    download(asset, &mut data, |_| {})?;
    Ok(data)
}

#[cfg(test)]
mod tests {
    use super::*;
    const SUMS: &[u8] = include_bytes!("../../tests/fixtures/release/SHA256SUMS");
    const SIG: &[u8] = include_bytes!("../../tests/fixtures/release/SHA256SUMS.sig");

    #[test]
    fn release_py_fixture_verifies_exact_bytes_only() {
        let test = VerifyingKey::from_public_key_pem(include_str!(
            "../../tests/fixtures/release/test-key.pub.pem"
        ))
        .unwrap();
        let keys = &[test];
        let list = signed_sums(SUMS, SIG, keys).unwrap();
        let hash = &list["LongshotLauncher-v1.0.0-x86_64.AppImage"];
        copy_checked(&b"not a launcher\n"[..], &mut Vec::new(), 15, hash, |_| {}).unwrap();
        assert!(copy_checked(&b"not a launcher!"[..], &mut Vec::new(), 15, hash, |_| {}).is_err());
        assert!(signed_sums(&SUMS[1..], SIG, keys).is_err());
        let trusted = production_keys().unwrap();
        for (tag, manifest, signature) in [
            (
                "v1.3.0",
                &include_bytes!("../../tests/fixtures/release/v1.3.0/SHA256SUMS")[..],
                &include_bytes!("../../tests/fixtures/release/v1.3.0/SHA256SUMS.sig")[..],
            ),
            (
                "v1.3.1",
                &include_bytes!("../../tests/fixtures/release/v1.3.1/SHA256SUMS")[..],
                &include_bytes!("../../tests/fixtures/release/v1.3.1/SHA256SUMS.sig")[..],
            ),
        ] {
            let published = signed_sums(manifest, signature, &trusted).unwrap();
            for suffix in [
                "x86_64.AppImage",
                "windows-x64-setup.exe",
                "windows-x64-portable.zip",
            ] {
                assert!(published.contains_key(&format!("LongshotLauncher-{tag}-{suffix}")));
            }
        }
        assert!(signed_sums(SUMS, SIG, &trusted).is_err());
    }

    #[test]
    fn strict_channel_order() {
        assert!(channel_order("v3.0.10", "stable", 3) > channel_order("v3.0.9", "stable", 3));
        for invalid in ["v+3.0.1", "v2.0.0", "v3.0.0-rc.1", "v3.0.0.0"] {
            assert_eq!(channel_order(invalid, "stable", 3), None);
        }
        assert!(
            channel_order("nightly-7-abcd1234-dirty", "nightly", 1)
                > channel_order("nightly-7-abcd1234", "nightly", 1)
        );
        assert_eq!(channel_order("nightly-7-ABCD1234", "nightly", 1), None);
    }
}
