use crate::{MainWindow, Preferences, controller::udev};
use slint::ComponentHandle;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};

pub(super) fn bind(ui: &MainWindow) {
    let weak = ui.as_weak();
    let probing = Arc::new(AtomicBool::new(false));
    ui.global::<Preferences>()
        .on_check_controller_rules(move || {
            let Some(ui) = weak.upgrade() else { return };
            if ui.global::<Preferences>().get_installing_controller_rules()
                || probing.swap(true, Ordering::Relaxed)
            {
                return;
            }
            let probing = Arc::clone(&probing);
            let weak = ui.as_weak();
            std::thread::spawn(move || {
                let needed = udev::applicable();
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    probing.store(false, Ordering::Relaxed);
                    let p = ui.global::<Preferences>();
                    if !p.get_installing_controller_rules() {
                        p.set_controller_rules_needed(needed);
                    }
                });
            });
        });
    let weak = ui.as_weak();
    ui.global::<Preferences>()
        .on_install_controller_rules(move || {
            let Some(ui) = weak.upgrade() else { return };
            let p = ui.global::<Preferences>();
            if !p.get_controller_rules_needed() || p.get_installing_controller_rules() {
                return;
            }
            p.set_installing_controller_rules(true);
            p.set_status("".into());
            let weak = ui.as_weak();
            std::thread::spawn(move || {
                let result = udev::install();
                let needed = udev::applicable();
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    let p = ui.global::<Preferences>();
                    p.set_installing_controller_rules(false);
                    p.set_controller_rules_needed(needed);
                    p.set_status(match result {
                        Ok(()) => "Reconnect your controller to apply its new permissions.".into(),
                        Err(e) => format!("{e:#}").into(),
                    });
                });
            });
        });
}
