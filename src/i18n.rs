use std::collections::HashMap;
use std::sync::{LazyLock, Mutex};

pub const LANGUAGES: &[(&str, &str)] = &[
    ("en-US", "English (US)"),
    ("en-GB", "English (UK)"),
    ("fr", "Français (France)"),
    ("fr-CA", "Français (Canada)"),
    ("de", "Deutsch"),
    ("it", "Italiano"),
    ("es", "Español (España)"),
    ("es-419", "Español (Latinoamérica)"),
    ("pt-BR", "Português (Brasil)"),
    ("nl", "Nederlands"),
    ("pl", "Polski"),
    ("ru", "Русский"),
    ("ja", "日本語"),
    ("ko", "한국어"),
    ("zh-Hans", "简体中文"),
    ("zh-Hant", "繁體中文"),
];

pub fn pick(name: &str) -> Option<&'static str> {
    let normal = name.split(['.', '@']).next()?.replace('_', "-");
    if let Some(&(code, _)) = LANGUAGES
        .iter()
        .find(|(code, _)| code.eq_ignore_ascii_case(&normal))
    {
        return Some(code);
    }
    let mut parts = normal.split('-');
    let lang = parts.next()?.to_lowercase();
    let regions: Vec<_> = parts.map(str::to_uppercase).collect();
    match lang.as_str() {
        "en" => Some(if regions.is_empty() || regions.iter().any(|p| p == "US") {
            "en-US"
        } else {
            "en-GB"
        }),
        "es" => Some(if regions.is_empty() || regions.iter().any(|p| p == "ES") {
            "es"
        } else {
            "es-419"
        }),
        "fr" => Some(if regions.iter().any(|p| p == "CA") {
            "fr-CA"
        } else {
            "fr"
        }),
        "pt" => Some("pt-BR"),
        "zh" => Some(
            if regions
                .iter()
                .any(|p| matches!(p.as_str(), "TW" | "HK" | "MO" | "HANT"))
            {
                "zh-Hant"
            } else {
                "zh-Hans"
            },
        ),
        _ => LANGUAGES.iter().find(|(c, _)| *c == lang).map(|&(c, _)| c),
    }
}

pub fn system() -> &'static str {
    for key in ["LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG"] {
        if let Ok(value) = std::env::var(key)
            && let Some(code) = value.split(':').find_map(pick)
        {
            return code;
        }
    }
    pick(&sys_locale::get_locale().unwrap_or_default()).unwrap_or("en-US")
}

fn catalog(code: &str) -> Option<&'static str> {
    macro_rules! catalogs {
        ($($name:literal),*) => {match code { $($name => Some(include_str!(concat!("../assets/lang/", $name,".json"))),)* _=> None }}
    }
    catalogs!(
        "en-GB", "fr", "fr-CA", "de", "it", "es", "es-419", "pt-BR", "nl", "pl", "ru", "ja", "ko",
        "zh-Hans", "zh-Hant"
    )
}

type Catalogs = HashMap<String, HashMap<String, serde_json::Value>>;
static LOADED: LazyLock<Mutex<Catalogs>> = LazyLock::new(Default::default);

pub(crate) fn bind(locale: &crate::Locale<'_>, settings: &serde_json::Value) -> &'static str {
    let language = settings["uiLanguage"]
        .as_str()
        .and_then(pick)
        .unwrap_or_else(system);
    locale.set_language(language.into());
    bind_messages(locale);
    language
}

/// Mark model strings translated later by Slint for catalog extraction.
pub const fn key(text: &'static str) -> &'static str {
    text
}

pub fn bind_messages(locale: &crate::Locale<'_>) {
    locale.on_lookup(|language, key| lookup(&language, &key).into());
    locale.on_fill(|pattern, first, second| {
        let mut rendered = String::new();
        let mut rest = pattern.as_str();
        while let Some(i) = rest.find('{') {
            rendered.push_str(&rest[..i]);
            rest = &rest[i + 1..];
            if let Some(tail) = rest.strip_prefix("0}") {
                rendered.push_str(&first);
                rest = tail;
            } else if let Some(tail) = rest.strip_prefix("1}") {
                rendered.push_str(&second);
                rest = tail;
            } else {
                rendered.push('{');
            }
        }
        rendered.push_str(rest);
        rendered.into()
    });
}

pub fn lookup(code: &str, key: &str) -> String {
    let mut loaded = LOADED.lock().unwrap_or_else(|error| error.into_inner());
    let parent = match code {
        "es-419" => "es",
        "fr-CA" => "fr",
        _ => "en-US",
    };
    for language in [code, parent] {
        let Some(source) = catalog(language) else {
            continue;
        };
        let texts = loaded.entry(language.to_owned()).or_insert_with(|| {
            serde_json::from_str(source).expect("valid bundled translation catalog")
        });
        if let Some(text) = texts.get(key).and_then(|v| v.as_str()) {
            return text.to_owned();
        }
    }
    key.to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn translates_using_the_existing_catalog() {
        assert_eq!(pick("es_MX.UTF-8"), Some("es-419"));
        assert_eq!(pick("zh_HK"), Some("zh-Hant"));
        assert_eq!(lookup("fr", "Settings"), "Paramètres");
        assert_eq!(lookup("fr", "A missing phrase"), "A missing phrase");
    }
}
