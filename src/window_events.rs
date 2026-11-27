use crate::{
    Game, GameStep, MainWindow, Page, Players, Preferences, SettingsGroup, busy, game, players,
};
use anyhow::Context;
use slint::{
    ComponentHandle, Timer, TimerMode,
    winit_030::{
        EventResult, WinitWindowAccessor,
        winit::{
            dpi::PhysicalPosition,
            event::{ElementState, WindowEvent},
        },
    },
};
use std::{cell::RefCell, path::PathBuf, rc::Rc, time::Duration};

pub fn bind(ui: &MainWindow, home: PathBuf) {
    // Winit delivers one event per file, even when a player drops a group in one gesture.
    let pending = Rc::new(RefCell::new(Vec::new()));
    let timer = Timer::default();
    let weak = ui.as_weak();
    let mut last_cursor = None;
    ui.window().on_winit_window_event(move |_, event| {
        if let Some(visible) = focus_visibility(event, &mut last_cursor)
            && let Some(ui) = weak.upgrade()
        {
            ui.global::<crate::Ink>().set_focus_visible(visible);
            if ui.get_controller_active() {
                ui.set_controller_active(false);
            }
        }
        if matches!(
            event,
            WindowEvent::Resized(_) | WindowEvent::ScaleFactorChanged { .. }
        ) {
            let weak = weak.clone();
            slint::Timer::single_shot(Duration::ZERO, move || {
                if let Some(ui) = weak.upgrade() {
                    scale_for_display(&ui);
                }
            });
        }
        let WindowEvent::DroppedFile(path) = event else {
            return EventResult::Propagate;
        };
        let Some(ui) = weak.upgrade() else {
            return EventResult::Propagate;
        };
        let page = ui.get_page();
        if !accepts_drop(&ui, page) {
            return EventResult::Propagate;
        }
        pending.borrow_mut().push(path.clone());
        let pending = pending.clone();
        let weak = ui.as_weak();
        let home = home.clone();
        timer.start(
            TimerMode::SingleShot,
            Duration::from_millis(40),
            move || {
                let files = std::mem::take(&mut *pending.borrow_mut());
                let Some(ui) = weak.upgrade() else {
                    return;
                };
                if files.is_empty() || !accepts_drop(&ui, page) {
                    return;
                }
                let result = match page {
                    Page::Settings => players::add_tags(&ui, &home, files),
                    Page::Mods => crate::mods::dropped(&ui, &files[0]),
                    Page::Saves => review_save_drop(&ui, &files),
                    Page::Build => game::drop_file(&ui, &files[0]),
                    _ => return,
                };
                if let Err(e) = result {
                    ui.set_notice(format!("{e:#}").into());
                }
            },
        );
        EventResult::PreventDefault
    });
}

pub(crate) fn focus_visibility(
    event: &WindowEvent,
    cursor: &mut Option<PhysicalPosition<f64>>,
) -> Option<bool> {
    desktop_input(event, cursor).then_some(matches!(event, WindowEvent::KeyboardInput { .. }))
}

// Winit receives physical desktop input. Gamepad navigation dispatches directly to Slint.
fn desktop_input(event: &WindowEvent, last_cursor: &mut Option<PhysicalPosition<f64>>) -> bool {
    match event {
        WindowEvent::CursorMoved { position, .. } => {
            if last_cursor
                .is_some_and(|last| (position.x - last.x).hypot(position.y - last.y) < 2.0)
            {
                return false;
            }
            last_cursor.replace(*position).is_some()
        }
        WindowEvent::KeyboardInput {
            event,
            is_synthetic: false,
            ..
        } => event.state.is_pressed(),
        WindowEvent::MouseInput {
            state: ElementState::Pressed,
            ..
        }
        | WindowEvent::Touch(_)
        | WindowEvent::MouseWheel { .. }
        | WindowEvent::Focused(false) => true,
        _ => false,
    }
}

fn review_save_drop(ui: &MainWindow, files: &[PathBuf]) -> anyhow::Result<()> {
    let [file] = files else {
        anyhow::bail!("Drop one save ZIP at a time.");
    };
    anyhow::ensure!(
        file.extension()
            .is_some_and(|ext| ext.eq_ignore_ascii_case("zip")),
        "Drop a save ZIP, or choose Import to select a save folder."
    );
    let file = file
        .to_str()
        .context("Choose this save ZIP through Import to use its native file name.")?;
    ui.global::<Players>()
        .invoke_action("drop-zip".into(), file.into());
    Ok(())
}

fn accepts_drop(ui: &MainWindow, page: Page) -> bool {
    if ui.get_page() != page || busy(ui) || ui.get_modal_open() || !ui.get_notice().is_empty() {
        return false;
    }
    match page {
        Page::Settings => ui.global::<Preferences>().get_section() == SettingsGroup::Nfc,
        Page::Mods | Page::Saves => true,
        Page::Build => {
            let game = ui.global::<Game>();
            matches!(game.get_step(), GameStep::Files | GameStep::Settings)
                && game.get_license().is_empty()
                && !game.get_folders_open()
        }
        _ => false,
    }
}

// Use the available pixels for a ten-foot view on a TV or a maximized desktop.
// Pointer coordinates, keyboard focus and the renderer all use Slint's window scale.
fn scale_for_display(ui: &MainWindow) {
    if std::env::var_os("SLINT_SCALE_FACTOR").is_some() {
        return;
    }
    let window = ui.window();
    let Some((size, scale)) = window.with_winit_window(|native| {
        let size = native.inner_size();
        let scale = if native.fullscreen().is_some() || native.is_maximized() {
            (size.width as f32 / 1280.0).min(size.height as f32 / 800.0)
        } else {
            native.scale_factor() as f32
        };
        (size, scale)
    }) else {
        return;
    };
    if scale <= 0.0 || (window.scale_factor() - scale).abs() < 0.01 {
        return;
    }
    use slint::platform::WindowEvent;
    let _ = window.dispatch_event_with_result(WindowEvent::ScaleFactorChanged {
        scale_factor: scale,
    });
    let size = slint::PhysicalSize::new(size.width, size.height).to_logical(scale);
    let _ = window.dispatch_event_with_result(WindowEvent::Resized { size });
}

#[cfg(test)]
mod tests {
    use super::*;
    use slint::winit_030::winit::{
        dpi::PhysicalPosition,
        event::{DeviceId, MouseButton},
    };

    #[test]
    fn mouse_takes_over_after_real_motion_or_a_click() {
        let mut last = None;
        let id = DeviceId::dummy();
        let at = |x| WindowEvent::CursorMoved {
            device_id: id,
            position: PhysicalPosition::new(x, 10.0),
        };
        assert!(!desktop_input(&at(24.0), &mut last));
        assert!(!desktop_input(&at(24.5), &mut last));
        assert!(desktop_input(&at(27.0), &mut last));
        assert!(desktop_input(
            &WindowEvent::MouseInput {
                device_id: id,
                state: ElementState::Pressed,
                button: MouseButton::Left,
            },
            &mut last
        ));
        assert!(desktop_input(&WindowEvent::Focused(false), &mut last));
    }
}
