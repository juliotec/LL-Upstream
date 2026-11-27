use serde_json::Value;
use std::path::{Path, PathBuf};

pub(super) fn read(record: &Value) -> Option<PathBuf> {
    if let Some(file) = record["nup"].as_str().filter(|value| !value.is_empty()) {
        return Some(file.into());
    }
    let romfs = Path::new(record["romfs"].as_str()?);
    let exefs = Path::new(record["exefs"].as_str()?);
    let folder = romfs.parent()?;
    (romfs.ends_with("romfs") && exefs.ends_with("exefs") && exefs.parent() == Some(folder))
        .then(|| folder.to_owned())
}

pub(super) fn fields(path: &Path) -> serde_json::Result<[(&'static str, Value); 3]> {
    if path.is_dir() {
        Ok([
            ("nup", Value::Null),
            ("romfs", serde_json::to_value(path.join("romfs"))?),
            ("exefs", serde_json::to_value(path.join("exefs"))?),
        ])
    } else {
        Ok([
            ("nup", serde_json::to_value(path)?),
            ("romfs", Value::Null),
            ("exefs", Value::Null),
        ])
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn only_matching_dump_folders_are_recovered() {
        assert_eq!(
            read(&json!({"romfs":"game/romfs","exefs":"game/exefs"})),
            Some("game".into())
        );
        assert_eq!(
            read(&json!({"romfs":"game/romfs","exefs":"other/exefs"})),
            None
        );
        assert_eq!(read(&json!({"nup":"game.nup"})), Some("game.nup".into()));
    }
}
