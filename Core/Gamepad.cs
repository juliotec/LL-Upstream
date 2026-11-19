namespace LongshotLauncher.Core;

/// A controller's buttons, by where they are on an Xbox-style pad (A: the bottom face button, B: the right one), so a
/// PlayStation, Switch or Steam Deck pad reads the same as Linux's BTN_SOUTH, BTN_EAST and XInput do
[Flags]
public enum PadButtons
{
    None = 0,
    Up = 1, Down = 2, Left = 4, Right = 8,
    A = 16, B = 32, X = 64, Y = 128,
    Lb = 256, Rb = 512, Start = 1024, Select = 2048,
}

/// One controller at one moment: its buttons, the left stick (-1..1; Y down) and the right stick's Y (-1..1; down), for
/// scrolling
public readonly record struct PadState(PadButtons Buttons, double X = 0, double Y = 0, double ScrollY = 0)
{
    public static readonly PadState Idle = new(PadButtons.None);

    /// Nothing pressed and the sticks at rest: another controller may take over (PadMerge)
    public bool IsIdle => Buttons == PadButtons.None && Math.Abs(X) < PadNavigator.StickRelease && Math.Abs(Y) < PadNavigator.StickRelease
                          && Math.Abs(ScrollY) < PadNavigator.ScrollDeadZone;
}

/// What a controller asks of the window: a move of the focus, Accept (A: click, open, choose), Back (B: the page's or dialog's
/// Back, Cancel or Keep), Previous and Next (the bumpers: the focus order, for what no direction reaches)
public enum PadAction { Up, Down, Left, Right, Accept, Back, Previous, Next }

/// A controller's states made into the window's actions: a direction (the d-pad or the left stick) at once, then again after
/// RepeatDelay and every RepeatEvery while it is held; a button once per press; the right stick a scroll, in pixels
public sealed class PadNavigator
{
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromMilliseconds(110);
    /// The stick counts as a direction past StickPress, and lets it go under StickRelease (so it does not flicker at the edge)
    public const double StickPress = 0.55, StickRelease = 0.35;
    public const double ScrollDeadZone = 0.25;
    /// The right stick pushed all the way scrolls this many pixels a second
    public const double ScrollSpeed = 1400;

    PadButtons before;
    PadAction? held;
    TimeSpan nextRepeat;
    TimeSpan? last;

    /// The actions of this state, at `now` (any steady clock), and how far to scroll (pixels, down positive)
    public (IReadOnlyList<PadAction> Actions, double Scroll) Step(TimeSpan now, PadState state)
    {
        var actions = new List<PadAction>();
        var pressed = state.Buttons & ~before;
        before = state.Buttons;

        var direction = Direction(state, held);
        if (direction != held)
        {
            held = direction;
            if (direction is { } d)
            {
                actions.Add(d);
                nextRepeat = now + RepeatDelay;
            }
        }
        else if (held is { } h && now >= nextRepeat)
        {
            actions.Add(h);
            nextRepeat = now + RepeatEvery;
        }

        if (pressed.HasFlag(PadButtons.A)) actions.Add(PadAction.Accept);
        if (pressed.HasFlag(PadButtons.B)) actions.Add(PadAction.Back);
        if (pressed.HasFlag(PadButtons.Lb)) actions.Add(PadAction.Previous);
        if (pressed.HasFlag(PadButtons.Rb)) actions.Add(PadAction.Next);

        var seconds = last is { } l ? Math.Clamp((now - l).TotalSeconds, 0, 0.1) : 0;
        last = now;
        var scroll = Math.Abs(state.ScrollY) < ScrollDeadZone ? 0
            : Math.Sign(state.ScrollY) * (Math.Abs(state.ScrollY) - ScrollDeadZone) / (1 - ScrollDeadZone) * ScrollSpeed * seconds;
        return (actions, scroll);
    }

    /// The d-pad first, then the stick along its larger axis; a stick already held keeps its direction until under StickRelease
    static PadAction? Direction(PadState s, PadAction? held)
    {
        var b = s.Buttons;
        if (b.HasFlag(PadButtons.Up) && !b.HasFlag(PadButtons.Down)) return PadAction.Up;
        if (b.HasFlag(PadButtons.Down) && !b.HasFlag(PadButtons.Up)) return PadAction.Down;
        if (b.HasFlag(PadButtons.Left) && !b.HasFlag(PadButtons.Right)) return PadAction.Left;
        if (b.HasFlag(PadButtons.Right) && !b.HasFlag(PadButtons.Left)) return PadAction.Right;
        var still = held switch
        {
            PadAction.Up => -s.Y > StickRelease,
            PadAction.Down => s.Y > StickRelease,
            PadAction.Left => -s.X > StickRelease,
            PadAction.Right => s.X > StickRelease,
            _ => false,
        };
        if (still) return held;
        if (Math.Max(Math.Abs(s.X), Math.Abs(s.Y)) < StickPress) return null;
        return Math.Abs(s.Y) >= Math.Abs(s.X) ? (s.Y < 0 ? PadAction.Up : PadAction.Down) : (s.X < 0 ? PadAction.Left : PadAction.Right);
    }
}

/// Several controllers as one: the one used last leads, and another takes over only once it has been at rest for Hold. Steam
/// shows a game its own copy of each controller, so a press can come from two devices, one a little after the other: it
/// counts once
public sealed class PadMerge
{
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(150);

    string? leader;
    TimeSpan busy;

    public PadState Step(TimeSpan now, IReadOnlyList<(string Id, PadState State)> pads)
    {
        var lead = pads.FirstOrDefault(p => p.Id == leader);
        if (lead.Id != null && !lead.State.IsIdle)
        {
            busy = now;
            return lead.State;
        }
        if (lead.Id != null && now - busy < Hold) return PadState.Idle;
        var other = pads.FirstOrDefault(p => !p.State.IsIdle);
        if (other.Id != null)
        {
            leader = other.Id;
            busy = now;
            return other.State;
        }
        return PadState.Idle;
    }
}

/// The controllers Steam hides from a game it started (it gives the game its own virtual copy of each): SDL's
/// SDL_GAMECONTROLLER_IGNORE_DEVICES ("0x28de/0x1205,0x045e/0x028e"), and SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT, which
/// hides every controller but those
public sealed class PadFilter
{
    readonly HashSet<(int, int)> ignore, only;
    readonly bool hasOnly;

    public PadFilter(Func<string, string?> env)
    {
        ignore = Parse(env("SDL_GAMECONTROLLER_IGNORE_DEVICES"));
        var except = env("SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT");
        hasOnly = !string.IsNullOrWhiteSpace(except);
        only = Parse(except);
    }

    public bool Allows(int vendor, int product) =>
        !ignore.Contains((vendor, product)) && (!hasOnly || only.Contains((vendor, product)));

    static HashSet<(int, int)> Parse(string? list)
    {
        var set = new HashSet<(int, int)>();
        foreach (var item in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('/');
            if (parts.Length == 2 && Hex(parts[0]) is { } v && Hex(parts[1]) is { } p) set.Add((v, p));
        }
        return set;
    }

    static int? Hex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : null;
    }
}

/// The TV (ten-foot) view: the window fills the screen, its content scaled up, with square corners. On in Steam's Gaming Mode
/// and Big Picture (Steam sets SteamGamepadUI or SteamTenfoot for what it starts there) and in a gamescope session;
/// LONGSHOT_LAUNCHER_TV=1 turns it on anywhere, =0 off
public static class TvMode
{
    public static bool Detect(Func<string, string?> env)
    {
        if (env("LONGSHOT_LAUNCHER_TV") is { Length: > 0 } forced) return forced != "0";
        if (env("SteamGamepadUI") == "1" || env("SteamTenfoot") == "1") return true;
        return (env("XDG_CURRENT_DESKTOP") ?? "").Split(':').Any(d => d.Equals("gamescope", StringComparison.OrdinalIgnoreCase));
    }
}

/// The window's corners, as the platform draws its own windows: Windows 11's 8 px, Windows 10's square ones, GNOME's 12 px,
/// 8 px on other Linux desktops; square when the window fills the screen (full screen, maximized, the TV view) or the desktop
/// cannot make a window see-through (the corners would show black)
public static class WindowCorners
{
    public static double Radius(Platform platform, int windowsBuild, string? desktop, bool fillsScreen, bool seeThrough)
    {
        if (fillsScreen || !seeThrough) return 0;
        if (platform == Platform.Windows) return windowsBuild >= 22000 ? 8 : 0;
        var desktops = (desktop ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (desktops.Any(d => d.Equals("gamescope", StringComparison.OrdinalIgnoreCase))) return 0;
        if (desktops.Any(d => d.Equals("GNOME", StringComparison.OrdinalIgnoreCase))) return 12;
        return 8;
    }
}
