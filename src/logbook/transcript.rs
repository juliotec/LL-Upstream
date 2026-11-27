//! Build output stays local. Stream the complete transcript to disk while the list shows a bounded tail.
use anyhow::{Result, ensure};
use std::{
    fs,
    io::{LineWriter, Write},
    path::Path,
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum BuildLog {
    Build,
    Launch,
}

impl BuildLog {
    pub fn filename(self) -> &'static str {
        match self {
            Self::Build => "last-build.log",
            Self::Launch => "last-launch.log",
        }
    }
}

pub(crate) struct Transcript {
    file: LineWriter<fs::File>,
}

impl Transcript {
    pub(crate) fn start(home: &Path, kind: BuildLog) -> Result<Self> {
        let dir = home.join("logs");
        ensure!(
            !dir.symlink_metadata()
                .is_ok_and(|m| crate::storage::is_link(&m)),
            "The logs folder points outside the launcher."
        );
        fs::create_dir_all(&dir)?;
        let path = dir.join(kind.filename());
        let file = tempfile::NamedTempFile::new_in(dir)?.persist(path)?;
        Ok(Self {
            file: LineWriter::new(file),
        })
    }

    pub fn flush(&mut self) -> std::io::Result<()> {
        self.file.flush()
    }

    pub(crate) fn line(&mut self, source: &str, line: &str) -> std::io::Result<()> {
        writeln!(
            self.file,
            "{}\t{source}\t{line}",
            chrono::Local::now().format("%H:%M:%S%.3f")
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn latest_entry_can_be_read_while_builder_is_quiet() -> Result<()> {
        let home = tempfile::tempdir()?;
        let mut transcript = Transcript::start(home.path(), BuildLog::Build)?;
        transcript.line("stdout", "Preparing shaders")?;
        let preview = fs::read_to_string(home.path().join("logs/last-build.log"))?;
        assert!(preview.ends_with("\tstdout\tPreparing shaders\n"));
        Ok(())
    }
}
