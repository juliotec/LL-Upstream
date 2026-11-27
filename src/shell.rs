//! Home and session-log presentation for the native shell.
use crate::{MainWindow, Players, Shell};
use slint::{ComponentHandle, Model};

pub fn refresh(ui: &MainWindow) {
    let players = ui.global::<Players>();
    if let Some(row) = players
        .get_profiles()
        .iter()
        .find(|row| row.id == players.get_profile_id())
    {
        let shell = ui.global::<Shell>();
        shell.set_last_played(if row.last_played.is_empty() {
            "—".into()
        } else {
            row.last_played
        });
        shell.set_profile_mods(row.installed_mod_count);
    }
}

pub fn bind(ui: &MainWindow) {
    ui.global::<crate::MenuValues>().on_index(|model, value| {
        model
            .iter()
            .position(|entry| entry == value)
            .map_or(-1, |i| i as i32)
    });
    let weak = ui.as_weak();
    ui.global::<Shell>().on_open_posts(move || {
        if let Err(e) =
            open::that_detached("https://git.magicbean.dev/MagicBeanDev/Longshot/releases")
            && let Some(ui) = weak.upgrade()
        {
            ui.set_notice(e.to_string().into());
        }
    });
    let weak = ui.as_weak();
    ui.global::<Shell>().on_navigate(move |page| {
        if let Some(ui) = weak.upgrade() {
            ui.invoke_navigate(page);
        }
    });
    refresh(ui);
}
