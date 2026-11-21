using System.Text.RegularExpressions;
using LongshotLauncher.Core;

/// A controller (#1186, Core/Gamepad.cs, Core/GamepadReaders.cs): a direction at once then repeated while held, a button once a
/// press, the stick's dead zones, two copies of one controller counting once, Steam's hidden controllers, the TV view, the
/// window's corners per platform; and every page and dialog with a button B presses
static partial class Program
{
    static void PadChecks(string repo)
    {
        var ms = (int n) => TimeSpan.FromMilliseconds(n);
        var nav = new PadNavigator();
        var down = new PadState(PadButtons.Down);
        Require(nav.Step(ms(0), down).Actions.SequenceEqual([PadAction.Down]), "The d-pad pressed does not move at once.");
        Require(nav.Step(ms(100), down).Actions.Count == 0, "A held d-pad repeats before RepeatDelay.");
        Require(nav.Step(ms(400), down).Actions.SequenceEqual([PadAction.Down]), "A held d-pad does not repeat after RepeatDelay.");
        Require(nav.Step(ms(450), down).Actions.Count == 0 && nav.Step(ms(510), down).Actions.SequenceEqual([PadAction.Down]),
            "A held d-pad does not repeat every RepeatEvery.");
        Require(nav.Step(ms(520), PadState.Idle).Actions.Count == 0, "Letting go of the d-pad moves.");
        Require(nav.Step(ms(530), down).Actions.SequenceEqual([PadAction.Down]), "The d-pad pressed again does not move at once.");

        nav = new PadNavigator();
        var a = new PadState(PadButtons.A | PadButtons.Rb);
        Require(nav.Step(ms(0), a).Actions.SequenceEqual([PadAction.Accept, PadAction.Next]), "A and RB pressed are not Accept and Next.");
        Require(nav.Step(ms(1000), a).Actions.Count == 0, "A held repeats.");
        Require(nav.Step(ms(1010), new PadState(PadButtons.B | PadButtons.Lb)).Actions.SequenceEqual([PadAction.Back, PadAction.Previous]),
            "B and LB are not Back and Previous.");

        nav = new PadNavigator();
        Require(nav.Step(ms(0), new PadState(PadButtons.None, 0.4, 0)).Actions.Count == 0, "The stick moves inside its dead zone.");
        Require(nav.Step(ms(10), new PadState(PadButtons.None, 0.7, -0.3)).Actions.SequenceEqual([PadAction.Right]), "The stick pushed right does not move right.");
        Require(nav.Step(ms(20), new PadState(PadButtons.None, 0.45, -0.3)).Actions.Count == 0, "The stick eased off but still held moves again.");
        Require(nav.Step(ms(30), new PadState(PadButtons.None, 0.1, 0)).Actions.Count == 0
                && nav.Step(ms(40), new PadState(PadButtons.None, -0.2, -0.9)).Actions.SequenceEqual([PadAction.Up]), "The stick pushed up does not move up.");
        Require(nav.Step(ms(50), new PadState(PadButtons.Left, 0, -0.9)).Actions.SequenceEqual([PadAction.Left]), "The d-pad does not come before the stick.");

        nav = new PadNavigator();
        nav.Step(ms(0), new PadState(PadButtons.None, ScrollY: 1));
        var (_, scroll) = nav.Step(ms(50), new PadState(PadButtons.None, ScrollY: 1));
        Require(Math.Abs(scroll - PadNavigator.ScrollSpeed * 0.05) < 0.01, "The right stick does not scroll ScrollSpeed a second.");
        Require(nav.Step(ms(100), new PadState(PadButtons.None, ScrollY: -0.2)).Scroll == 0, "The right stick scrolls inside its dead zone.");
        Require(nav.Step(ms(5100), new PadState(PadButtons.None, ScrollY: -1)).Scroll >= -PadNavigator.ScrollSpeed * 0.1 - 0.01,
            "A long pause between reads scrolls a long way at once.");

        // (Steam's copy and the controller itself: whichever moved first leads until it rests)
        var merge = new PadMerge();
        Require(merge.Step(ms(0), [("real", new PadState(PadButtons.A)), ("steam", PadState.Idle)]).Buttons == PadButtons.A, "A press is lost.");
        Require(merge.Step(ms(16), [("real", PadState.Idle), ("steam", PadState.Idle)]).Buttons == PadButtons.None, "A press let go is kept.");
        Require(merge.Step(ms(32), [("real", PadState.Idle), ("steam", new PadState(PadButtons.A))]).Buttons == PadButtons.None,
            "The late copy of a press, from Steam's copy of the controller, counts again.");
        Require(merge.Step(ms(300), [("real", PadState.Idle), ("steam", new PadState(PadButtons.B))]).Buttons == PadButtons.B,
            "Another controller does not take over once the one used last rests.");
        Require(merge.Step(ms(310), [("steam", PadState.Idle)]).Buttons == PadButtons.None, "A controller unplugged is not at rest.");
        Require(merge.Step(ms(320), []).Buttons == PadButtons.None, "No controller is not at rest.");

        var filter = new PadFilter(n => n == "SDL_GAMECONTROLLER_IGNORE_DEVICES" ? "0x28de/0x1205, 0x045E/0x028e,bad,0xzz/0x1" : null);
        Require(!filter.Allows(0x28de, 0x1205) && !filter.Allows(0x045e, 0x028e) && filter.Allows(0x28de, 0x11ff),
            "The controllers Steam hides are read, or its own copy is not.");
        var only = new PadFilter(n => n == "SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT" ? "0x28de/0x11ff" : null);
        Require(only.Allows(0x28de, 0x11ff) && !only.Allows(0x045e, 0x028e), "SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT is not kept.");
        Require(new PadFilter(_ => null).Allows(0x045e, 0x028e), "With no list, a controller is hidden.");

        var evdev = new EvdevPad(axis => axis == EvdevPad.AbsRy ? (0, 255) : (-32768, 32767));
        evdev.Apply(EvdevPad.EvKey, 0x130, 1);
        evdev.Apply(EvdevPad.EvKey, 0x137, 1);
        evdev.Apply(EvdevPad.EvAbs, EvdevPad.AbsHat0Y, -1);
        evdev.Apply(EvdevPad.EvAbs, EvdevPad.AbsX, 32767);
        evdev.Apply(EvdevPad.EvAbs, EvdevPad.AbsRy, 255);
        var s = evdev.State;
        Require(s.Buttons == (PadButtons.A | PadButtons.Rb | PadButtons.Up) && s.X > 0.99 && s.ScrollY > 0.99, "evdev's BTN_SOUTH, BTN_TR, hat or axes are misread.");
        evdev.Apply(EvdevPad.EvKey, 0x130, 0);
        evdev.Apply(EvdevPad.EvAbs, EvdevPad.AbsHat0Y, 0);
        evdev.Apply(EvdevPad.EvAbs, EvdevPad.AbsRy, 128);
        Require(evdev.State.Buttons == PadButtons.Rb && Math.Abs(evdev.State.ScrollY) < 0.01, "evdev's released button, hat or centred axis is kept.");
        Require(EvdevPad.IsGamepad("7fdb000000000000 0 0 0 0") && !EvdevPad.IsGamepad("10000 0 0 0 0")
                && !EvdevPad.IsGamepad("1000000000007 ff9f207ac14057ff febeffdfffefffff fffffffffffffffe"),
            "A gamepad's key bitmap (BTN_SOUTH, bit 0x130) is misread, or a keyboard's taken for one.");
        var x = XInputPads.State(0x1000 | 0x0001, 32767, 32767, -32768);
        Require(x.Buttons == (PadButtons.A | PadButtons.Up) && x.X > 0.99 && x.Y < -0.99 && x.ScrollY > 0.99, "XInput's buttons or sticks (Y up) are misread.");

        Require(TvMode.Detect(n => n == "SteamGamepadUI" ? "1" : null) && TvMode.Detect(n => n == "SteamTenfoot" ? "1" : null)
                && TvMode.Detect(n => n == "XDG_CURRENT_DESKTOP" ? "gamescope" : null) && !TvMode.Detect(n => n == "XDG_CURRENT_DESKTOP" ? "KDE" : null)
                && !TvMode.Detect(n => n switch { "SteamGamepadUI" => "1", "LONGSHOT_LAUNCHER_TV" => "0", _ => null })
                && TvMode.Detect(n => n == "LONGSHOT_LAUNCHER_TV" ? "1" : null),
            "The TV view is not on in Gaming Mode, Big Picture or gamescope, is on on a desktop, or LONGSHOT_LAUNCHER_TV is not kept.");

        Require(WindowCorners.Radius(Platform.Windows, 22631, null, false, true) == 8 && WindowCorners.Radius(Platform.Windows, 19045, null, false, true) == 0,
            "Windows 11's corners are not 8 px, or Windows 10's not square.");
        Require(WindowCorners.Radius(Platform.Linux, 0, "ubuntu:GNOME", false, true) == 12 && WindowCorners.Radius(Platform.Linux, 0, "KDE", false, true) == 8
                && WindowCorners.Radius(Platform.Linux, 0, "gamescope", false, true) == 0, "GNOME's, other desktops' or gamescope's corners are wrong.");
        Require(WindowCorners.Radius(Platform.Windows, 22631, null, true, true) == 0 && WindowCorners.Radius(Platform.Linux, 0, "GNOME", false, false) == 0,
            "A window that fills the screen, or that cannot be see-through, has round corners.");

        // (every dialog and page but the home has a button B presses: the views read, as tests/launcher_core reads sources)
        var views = Path.Combine(repo, "App", "Views");
        foreach (var file in Directory.GetFiles(views, "*.axaml"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            // (the pages the title bar's tabs move between: their B is the tabs' Home, checked below)
            if (name is "HomeView" or "SettingsView" or "SavesView" or "NfcView" or "StorageView") continue;
            var text = File.ReadAllText(file);
            if (!Regex.IsMatch(text, "<Button\\b")) continue;
            Require(text.Contains("c:Pad.Back=\"True\""), name + " has no button a controller's B presses (c:Pad.Back).");
        }
        var window = File.ReadAllText(Path.Combine(repo, "App", "MainWindow.axaml"));
        Require(Regex.IsMatch(window, "Command=\"\\{Binding GoHomeCommand\\}\"[^>]*c:Pad\\.Back=\"True\""),
                "The title bar's Home tab is not a controller's B on the pages the tabs move between (c:Pad.Back).");
        // (#113: a window too narrow for a longer translation, or too tall for a laptop's screen, can be resized; it has no system
        // frame to hold, so its own edges are the grips, off while it fills the screen, where the pages are scaled instead)
        var code = File.ReadAllText(Path.Combine(repo, "App", "MainWindow.axaml.cs"));
        Require(window.Contains("CanResize=\"True\"") && Regex.IsMatch(window, "MinWidth=\"\\d+\" MinHeight=\"\\d+\"")
                && window.Contains("<Panel Name=\"Grips\"/>") && !code.Contains("CanResize = false")
                && code.Contains("BeginResizeDrag(side, e)") && code.Contains("Grips.IsVisible = !fills;") && code.Contains("FitScreen();"),
                "The window cannot be resized by its edges, or has no minimum size, or does not open within a smaller screen.");
    }
}
