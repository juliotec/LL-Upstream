use anyhow::{Context, Result, ensure};
use std::{
    fs::File,
    io::{Read, Seek, SeekFrom},
    path::{Path, PathBuf},
};

pub(super) const LIBRARY: &str = "amd_fidelityfx_upscaler_vk.so";

pub(super) fn open_library(path: &Path) -> Result<File> {
    let mut file = File::open(path).context("Could not open the FSR 4 library.")?;
    let mut magic = [0; 5];
    file.read_exact(&mut magic)
        .context("This is not a Vulkan FSR 4 library.")?;
    ensure!(
        magic == *b"\x7fELF\x02",
        "Choose a 64-bit Vulkan FSR 4 shared library."
    );
    file.seek(SeekFrom::Start(0))?;
    Ok(file)
}
fn install(library: &Path, folder: &Path) -> Result<()> {
    let target = folder.join(LIBRARY);
    ensure!(
        !super::versions::linked(&target),
        "The installed FSR library is linked."
    );
    if target.canonicalize().ok().as_ref() == Some(&library.canonicalize()?) {
        return Ok(());
    }
    let mut input = open_library(library)?;
    let mut temp = tempfile::NamedTempFile::new_in(folder)?;
    std::io::copy(&mut input, &mut temp)?;
    temp.as_file().sync_all()?;
    temp.persist(target)?;
    Ok(())
}

/// Older installations kept the library only beside the built game.
pub(super) fn library_for(version: &Path) -> Option<PathBuf> {
    let pin = version.join(LIBRARY);
    if crate::players::is_file(&pin) {
        return Some(pin);
    }
    let builds = version.join("builds");
    let default = builds.join("default");
    let legacy = default.join(LIBRARY);
    (crate::players::is_dir(&builds)
        && crate::players::is_dir(&default)
        && crate::players::is_file(&legacy))
    .then_some(legacy)
}

pub(super) fn pin(version: &Path, source: &Path) -> Result<()> {
    ensure!(
        crate::players::is_dir(version),
        "The installation folder is missing or linked."
    );
    install(source, version)
}

/// Standalone launches can find FSR beside the game after a successful build.
pub(super) fn install_for_build(version: &Path, build: &Path) -> Result<()> {
    let builds = version.join("builds");
    ensure!(
        build.parent() == Some(&builds)
            && crate::players::is_dir(version)
            && !super::versions::linked(&builds)
            && !super::versions::linked(build),
        "The FSR build folder is outside this installation or linked."
    );
    if let Some(source) = library_for(version) {
        std::fs::create_dir_all(build)?;
        install(&source, build)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fsr_library_survives_source_removal_and_is_copied_to_the_profile_build() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let version = tmp.path().join("version");
        std::fs::create_dir(&version)?;
        let file = tmp.path().join("selected.so");
        std::fs::write(&file, b"\x7fELF\x02fixture")?;
        pin(&version, &file)?;
        std::fs::remove_file(file)?;
        let build = version.join("builds/modded");
        install_for_build(&version, &build)?;
        assert_eq!(std::fs::read(build.join(LIBRARY))?, b"\x7fELF\x02fixture");
        assert!(install_for_build(&version, tmp.path()).is_err());
        Ok(())
    }
}
