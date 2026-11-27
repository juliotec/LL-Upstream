//! Feed mapped gamepads through the same focus and activation path as the keyboard.
#[cfg(target_os = "linux")]
pub(crate) mod udev;

use crate::{Game, MainWindow, Page, Popup, Shell, Updates};
use gilrs::{Axis, Button, Gilrs};
use slint::{
    ComponentHandle, Timer, TimerMode,
    platform::{Key, WindowEvent, WindowEventDispatchResult},
    winit_030::WinitWindowAccessor,
};
use std::time::{Duration, Instant};

pub fn tv_mode() -> bool {
    if let Ok(force) = std::env::var("LONGSHOT_LAUNCHER_TV")
        && !force.is_empty()
    {
        return force != "0";
    }
    ["SteamGamepadUI", "SteamTenfoot"]
        .iter()
        .any(|key| std::env::var(key).as_deref() == Ok("1"))
        || std::env::var("XDG_CURRENT_DESKTOP")
            .unwrap_or_default()
            .split(':')
            .any(|desktop| desktop.eq_ignore_ascii_case("gamescope"))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Press {
    Accept,
    Back,
    Previous,
    Next,
    Left,
    Right,
    Up,
    Down,
    ScrollUp,
    ScrollDown,
    Version,
    Save,
}

fn key(ui: &MainWindow, key: Key) -> bool {
    let text: slint::SharedString = key.into();
    let result = ui
        .window()
        .dispatch_event_with_result(WindowEvent::KeyPressed { text: text.clone() });
    let _ = ui
        .window()
        .dispatch_event_with_result(WindowEvent::KeyReleased { text });
    matches!(result, Ok(WindowEventDispatchResult::Accepted))
}

fn direction(pad: &gilrs::Gamepad<'_>) -> Option<Press> {
    let buttons = [
        (Button::South, Press::Accept),
        (Button::East, Press::Back),
        (Button::West, Press::Version),
        (Button::North, Press::Save),
        (Button::LeftTrigger, Press::Previous),
        (Button::RightTrigger, Press::Next),
        (Button::DPadUp, Press::Up),
        (Button::DPadDown, Press::Down),
        (Button::DPadLeft, Press::Left),
        (Button::DPadRight, Press::Right),
    ];
    if let Some((_, press)) = buttons.into_iter().find(|(b, _)| pad.is_pressed(*b)) {
        return Some(press);
    }
    for (axis, pos, neg) in [
        (Axis::LeftStickY, Press::Up, Press::Down),
        (Axis::LeftStickX, Press::Right, Press::Left),
        (Axis::RightStickY, Press::ScrollUp, Press::ScrollDown),
    ] {
        let val = pad.value(axis);
        if val > 0.6 {
            return Some(pos);
        }
        if val < -0.6 {
            return Some(neg);
        }
    }
    None
}

pub fn listen(ui: &MainWindow) -> Option<Timer> {
    let mut pads = Gilrs::new().ok()?;
    let timer = Timer::default();
    let weak = ui.as_weak();
    let mut held = None;
    let mut repeat = Instant::now();
    let mut last_input = None;
    timer.start(TimerMode::Repeated, Duration::from_millis(25), move || {
        while pads.next_event().is_some() {}
        let Some(ui) = weak.upgrade() else { return };
        let input = pads.gamepads().find_map(|(_, p)| direction(&p));
        if !ui
            .window()
            .with_winit_window(|w| w.has_focus())
            .unwrap_or(false)
            || ui.global::<Game>().get_playing()
        {
            held = input;
            last_input = None;
            if ui.get_controller_active() {
                ui.set_controller_active(false);
            }
            return;
        }
        let now = Instant::now();
        if ui.get_controller_active()
            && input.is_none()
            && last_input.is_none_or(|when| now.duration_since(when) >= HINT_TIMEOUT)
        {
            ui.set_controller_active(false);
        }
        if input.is_some() && ui.get_controller_active() {
            last_input = Some(now);
        }
        if input != held {
            held = input;
            repeat = now + Duration::from_millis(450);
        } else {
            if !ui.get_controller_active()
                || now < repeat
                || matches!(
                    input,
                    None | Some(
                        Press::Accept
                            | Press::Back
                            | Press::Save
                            | Press::Version
                            | Press::Previous
                            | Press::Next
                    )
                )
            {
                return;
            }
            repeat = now + Duration::from_millis(120);
        }
        let Some(input) = input else { return };
        last_input = Some(now);
        dispatch(&ui, input);
    });
    Some(timer)
}

const HINT_TIMEOUT: Duration = Duration::from_secs(8);

fn dispatch(ui: &MainWindow, input: Press) {
    ui.set_controller_active(true);
    ui.global::<crate::Ink>().set_focus_visible(true);
    let (primary, fallback) = match input {
        Press::Accept => (Key::Return, None),
        Press::Back if ui.get_asking_to_stop() => {
            ui.set_asking_to_stop(false);
            ui.set_close_after_stop(false);
            return;
        }
        Press::Back => (Key::Escape, None),
        Press::Version | Press::Save => {
            if ui.get_page() == Page::Home && shortcuts_available(ui) {
                ui.global::<Shell>().set_popup(if input == Press::Version {
                    Popup::Version
                } else {
                    Popup::Save
                });
            }
            return;
        }
        Press::Next | Press::Previous => {
            if ui.get_page() != Page::ChoosePlayer
                && !ui.global::<Game>().get_first_run()
                && shortcuts_available(ui)
            {
                const TABS: [Page; 5] = [
                    Page::Home,
                    Page::Saves,
                    Page::Versions,
                    Page::Mods,
                    Page::Settings,
                ];
                let index = TABS
                    .iter()
                    .position(|page| *page == ui.get_page())
                    .unwrap_or(0);
                let step = if input == Press::Next {
                    1
                } else {
                    TABS.len() - 1
                };
                ui.invoke_navigate(TABS[(index + step) % TABS.len()]);
            }
            return;
        }
        Press::Left => (Key::LeftArrow, Some(Key::Backtab)),
        Press::Right => (Key::RightArrow, Some(Key::Tab)),
        Press::Up => (Key::UpArrow, Some(Key::Backtab)),
        Press::Down => (Key::DownArrow, Some(Key::Tab)),
        Press::ScrollUp | Press::ScrollDown
            if ui.get_page() == Page::Home && shortcuts_available(ui) =>
        {
            let shell = ui.global::<Shell>();
            let movement = if input == Press::ScrollUp { -1 } else { 1 };
            shell.set_news_scroll(shell.get_news_scroll().wrapping_add(movement));
            return;
        }
        Press::ScrollUp => (Key::PageUp, None),
        Press::ScrollDown => (Key::PageDown, None),
    };
    if !key(ui, primary)
        && let Some(other) = fallback
    {
        key(ui, other);
    }
}

fn shortcuts_available(ui: &MainWindow) -> bool {
    !ui.get_navigation_locked()
        && !ui.get_modal_open()
        && ui.global::<Shell>().get_popup() == Popup::Closed
        && !ui.global::<Game>().get_show_notes()
        && !ui.global::<Updates>().get_show_notes()
}

#[cfg(test)]
mod tests {
    use super::*;
    use slint::platform::{
        Platform, WindowAdapter,
        software_renderer::{MinimalSoftwareWindow, RepaintBufferType},
    };
    use std::{cell::Cell, rc::Rc};

    struct TestDisplay(Rc<MinimalSoftwareWindow>);

    impl Platform for TestDisplay {
        fn create_window_adapter(&self) -> Result<Rc<dyn WindowAdapter>, slint::PlatformError> {
            Ok(self.0.clone())
        }
    }

    #[test]
    fn gamepad_operates_the_launcher_and_respects_the_build_lock() {
        let window = MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer);
        slint::platform::set_platform(Box::new(TestDisplay(window.clone()))).unwrap();
        let ui = MainWindow::new().unwrap();
        window.set_size(slint::PhysicalSize::new(1280, 800));
        let weak = ui.as_weak();
        ui.on_navigate(move |page| weak.unwrap().set_page(page));
        ui.show().unwrap();
        assert!(!ui.get_controller_active());
        dispatch(&ui, Press::Next);
        assert!(ui.get_controller_active());
        assert_eq!(ui.get_page(), Page::Saves);
        dispatch(&ui, Press::Previous);
        assert_eq!(ui.get_page(), Page::Home);
        dispatch(&ui, Press::ScrollDown);
        assert_eq!(ui.global::<Shell>().get_news_scroll(), 1);
        for (input, picker) in [(Press::Version, Popup::Version), (Press::Save, Popup::Save)] {
            dispatch(&ui, input);
            assert_eq!(ui.global::<Shell>().get_popup(), picker);
            slint::platform::update_timers_and_animations();
            dispatch(&ui, Press::Next);
            assert_eq!(ui.get_page(), Page::Home);
            dispatch(&ui, Press::Back);
            assert_eq!(ui.global::<Shell>().get_popup(), Popup::Closed);
        }
        let started = Rc::new(Cell::new(false));
        let flag = started.clone();
        ui.global::<Game>().on_begin_build(move || flag.set(true));
        ui.invoke_focus_home();
        slint::platform::update_timers_and_animations();
        dispatch(&ui, Press::Accept);
        assert!(started.get());

        let prefs = ui.global::<crate::Preferences>();
        ui.set_page(Page::Settings);
        prefs.set_section(crate::SettingsGroup::Storage);
        prefs.set_reclaim_list("Synthetic cache".into());
        slint::platform::update_timers_and_animations();
        assert!(ui.get_modal_open());
        dispatch(&ui, Press::Next);
        assert_eq!(ui.get_page(), Page::Settings);
        let confirmed = Rc::new(Cell::new(false));
        let flag = confirmed.clone();
        prefs.on_reclaim_build_files(move |confirm, _, _| flag.set(confirm));
        key(&ui, Key::Backtab);
        dispatch(&ui, Press::Accept);
        assert!(confirmed.get(), "Backtab wraps from Cancel to Clean up");
        key(&ui, Key::Tab);
        dispatch(&ui, Press::Back);
        assert!(!ui.get_modal_open());
        assert_eq!(ui.get_page(), Page::Settings);
        ui.set_page(Page::Home);

        let game = ui.global::<Game>();
        let weak = ui.as_weak();
        game.on_cancel_mod_trust(move || {
            weak.unwrap()
                .global::<Game>()
                .set_mod_trust_review("".into())
        });
        game.set_mod_trust_review("Synthetic permission request".into());
        slint::platform::update_timers_and_animations();
        assert!(ui.get_modal_open());
        dispatch(&ui, Press::Next);
        assert_eq!(ui.get_page(), Page::Home);
        dispatch(&ui, Press::Accept);
        assert!(
            !ui.get_modal_open(),
            "Cancel has initial focus on permission requests"
        );

        ui.global::<Game>().set_busy(true);
        for input in [Press::Next, Press::Version, Press::Save] {
            dispatch(&ui, input);
            assert_eq!(ui.get_page(), Page::Home);
            assert_eq!(ui.global::<Shell>().get_popup(), Popup::Closed);
        }
        ui.set_asking_to_stop(true);
        dispatch(&ui, Press::Back);
        assert!(!ui.get_asking_to_stop());
    }
}
