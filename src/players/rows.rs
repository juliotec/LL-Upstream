use super::{PlayerRow, profiles::Profile, saves};
use crate::i18n;
use chrono::{DateTime, Local};
use slint::Color;
use std::{fs, time::SystemTime};

pub(super) fn when(time: SystemTime, locale: &str) -> String {
    let date: DateTime<Local> = time.into();
    match (Local::now().date_naive() - date.date_naive()).num_days() {
        0 => i18n::lookup(locale, "today {0}").replace("{0}", &date.format("%H:%M").to_string()),
        1 => {
            i18n::lookup(locale, "yesterday {0}").replace("{0}", &date.format("%H:%M").to_string())
        }
        _ => date.format("%d %b").to_string(),
    }
}

pub(super) fn profile_color(hex: &str) -> Color {
    hex.strip_prefix('#')
        .filter(|s| s.len() == 6)
        .and_then(|s| u32::from_str_radix(s, 16).ok())
        .map(|rgb| Color::from_argb_encoded(0xff000000 | rgb))
        .unwrap_or(Color::from_rgb_u8(127, 157, 83))
}

pub(super) fn profile_row(profile: &Profile, locale: &str) -> PlayerRow {
    let tr = |key| i18n::lookup(locale, key);
    let data = &profile.file;
    let colour = profile_color(data["color"].as_str().unwrap_or_default());
    let slots = saves::slots_dir(&profile.dir).and_then(|p| saves::slots(&p));
    let save_count = slots.as_ref().map_or(-1, |rows| rows.len() as i32);
    let mods = data["mods"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|entry| entry["id"].is_string());
    let installed_mod_count = mods.clone().count() as i32;
    let mod_count = mods.filter(|entry| entry["on"] != false).count() as i32;
    let has_options = super::disk::is_file(&profile.dir.join("settings.ini"));
    let last_played = data["last_played"]
        .as_str()
        .and_then(|timestamp| DateTime::parse_from_rfc3339(timestamp).ok())
        .map(|time| when(time.into(), locale))
        .unwrap_or_else(|| tr("Never"));
    let detail = match slots {
        Ok(slots) => {
            let last = slots
                .iter()
                .filter_map(|p| fs::metadata(p.join("progress.sav")).ok()?.modified().ok())
                .max();
            match last {
                Some(t) => tr(if slots.len() == 1 {
                    "1 save, last {1}"
                } else {
                    "{0} saves, last {1}"
                })
                .replace("{0}", &slots.len().to_string())
                .replace("{1}", &when(t, locale)),
                None => tr("No save yet"),
            }
        }
        Err(e) => e.to_string(),
    };
    let game_line = if let Some(why) = &profile.problem {
        tr("Damaged: {0}").replace("{0}", why)
    } else {
        let game = data["game"]
            .as_str()
            .and_then(|s| s.rsplit(['/', '\\']).find(|part| !part.is_empty()))
            .map_or_else(|| tr("no game copy yet"), str::to_owned);
        let upscalers: Vec<_> = data["upscalers"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(|value| value.as_str())
            .map(|name| match name {
                "dlss" => "DLSS",
                "fsr4" => "FSR 4",
                "xess" => "XeSS",
                other => other,
            })
            .collect();
        let upscalers = if upscalers.is_empty() {
            tr("no upscaler")
        } else {
            upscalers.join(", ")
        };
        tr("Game: {0} · {1}")
            .replace("{0}", &game)
            .replace("{1}", &upscalers)
    };
    let name = profile.name();
    PlayerRow {
        id: profile.id.clone().into(),
        name: name.into(),
        save_count,
        mod_count,
        installed_mod_count,
        has_options,
        last_played: last_played.into(),
        colour,
        detail: detail.into(),
        avatar: name
            .chars()
            .next()
            .unwrap_or('?')
            .to_uppercase()
            .to_string()
            .into(),
        game_line: game_line.into(),
        damaged: profile.problem.is_some(),
    }
}
