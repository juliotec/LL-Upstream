using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LongshotLauncher.Controls;
using LongshotLauncher.Core;

namespace LongshotLauncher;

/// The launcher with a controller (#1186: Steam's Gaming Mode, a TV): the d-pad or left stick moves the focus to the next
/// control that way, A presses it (opens a list, chooses in it), B presses the page's or dialog's Back, Cancel, Keep or Done
/// (c:Pad.Back; closes an open list first), the bumpers go back and forth in the focus order, the right stick scrolls.
///
/// The moves are Avalonia's own: each is the key a gamepad would send (KeyDeviceType.Gamepad), which its directional focus
/// (XYFocus) and the controls answer as on a TV. The first press with nothing focused focuses the page's Play (or its main
/// button), or the dialog's first control; while a dialog is open the focus stays in it.
///
/// When something else turns the controller into keys (Steam's desktop layout sends arrows, Enter and Escape for it), a press
/// would count twice: once that is seen twice, the launcher leaves the controller to it
sealed class PadInput : IDisposable
{
    static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(16), Slow = TimeSpan.FromMilliseconds(500);
    const long Together = 150;

    readonly MainWindow window;
    readonly IPadSource? source;
    readonly DispatcherTimer? timer;
    readonly PadMerge merge = new();
    readonly PadNavigator navigator = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    long padAt = long.MinValue / 2, keyAt = long.MinValue / 2;
    Key padKey, keyKey;
    int twice;
    bool mirrored;

    /// read: the controllers plugged in are read (not for tools/screenshots, which presses for itself: Do)
    public PadInput(MainWindow window, bool read = true)
    {
        this.window = window;
        if (!read) return;
        try { source = PadSources.Open(Environment.GetEnvironmentVariable); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (source == null) return;
        window.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        timer = new DispatcherTimer { Interval = Slow };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    void Poll()
    {
        IReadOnlyList<(string Id, PadState State)> pads;
        try { pads = source!.Read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        timer!.Interval = pads.Count == 0 ? Slow : Fast;
        var now = clock.Elapsed;
        var (actions, scroll) = navigator.Step(now, merge.Step(now, pads));
        // (another window in front, a file picker open, or the controller already turned into keys: nothing)
        if (!window.IsActive || mirrored) return;
        foreach (var action in actions) Do(action);
        if (scroll != 0) Scroll(scroll);
    }

    /// A key from the keyboard, or from something that turns the controller into keys: the same as a press of the controller
    /// at the same moment, it is that press twice
    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyDeviceType != KeyDeviceType.Keyboard || !IsNavigation(e.Key)) return;
        var now = clock.ElapsedMilliseconds;
        if (now - padAt < Together && Same(e.Key, padKey))
        {
            e.Handled = true;
            Twice();
            return;
        }
        keyAt = now;
        keyKey = e.Key;
    }

    static bool IsNavigation(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.Space or Key.Escape or Key.Tab;

    static bool Same(Key a, Key b) => a == b || (a is Key.Enter or Key.Space && b is Key.Enter or Key.Space);

    void Twice()
    {
        if (++twice >= 2) mirrored = true;
    }

    static Key KeyOf(PadAction action) => action switch
    {
        PadAction.Up => Key.Up, PadAction.Down => Key.Down, PadAction.Left => Key.Left, PadAction.Right => Key.Right,
        PadAction.Accept => Key.Enter, PadAction.Back => Key.Escape, _ => Key.Tab,
    };

    public void Do(PadAction action)
    {
        var key = KeyOf(action);
        var now = clock.ElapsedMilliseconds;
        if (now - keyAt < Together && Same(key, keyKey))
        {
            Twice();
            return;
        }
        padAt = now;
        padKey = key;
        // (a controller in use: the pages' Back show B's glyph)
        window.Classes.Set("pad", true);

        var scope = window.PadScope;
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        if (action == PadAction.Back)
        {
            Back(focused, scope);
            return;
        }
        if (focused == null || !focused.IsEffectivelyVisible || !Within(focused, scope ?? window))
        {
            First(scope);
            return;
        }
        Send(focused, key, action == PadAction.Previous ? KeyModifiers.Shift : KeyModifiers.None);
        // (a dialog keeps the focus: a move that left it is undone)
        if (scope != null && window.FocusManager?.GetFocusedElement() is Control moved && !Within(moved, scope))
            focused.Focus(NavigationMethod.Directional);
    }

    /// B: an open list closes; otherwise the dialog's (or else the page's) Back, Cancel, Keep or Done is pressed
    void Back(Control? focused, Control? scope)
    {
        if (focused?.GetLogicalAncestors().Prepend(focused).OfType<ComboBox>().FirstOrDefault(c => c.IsDropDownOpen) is { } list)
        {
            list.IsDropDownOpen = false;
            list.Focus(NavigationMethod.Directional);
            return;
        }
        var root = scope ?? window.PadPage;
        var back = root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Pad.GetBack(b) && Usable(b))
                   // (a page the title bar's tabs move from has no Back of its own: B goes home, as their Home)
                   ?? (scope == null ? window.PadNav.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Pad.GetBack(b) && Usable(b)) : null);
        if (back != null) Send(back, Key.Enter, KeyModifiers.None);
    }

    /// Esc on the keyboard: what B does (an open list has closed itself first)
    public void GoBack() => Back(window.FocusManager?.GetFocusedElement() as Control, window.PadScope);

    /// The first press with nothing focused: the dialog's Keep or Cancel (or its first control), or the page's Play, its main
    /// button, the control marked as its first (c:Pad.First: its main list, its first setting), its first control but Back
    void First(Control? scope)
    {
        if (scope != null)
        {
            // (the control it marks first, else its safe button: Keep, Cancel, Decline)
            (Pad.SafeIn(scope) ?? FocusManager.FindFirstFocusableElement(scope) as Control)?.Focus(NavigationMethod.Directional);
            return;
        }
        var page = window.PadPage;
        var buttons = page.GetVisualDescendants().OfType<Button>().Where(Usable).ToList();
        var pick = buttons.FirstOrDefault(b => b.Classes.Contains("go")) ?? buttons.FirstOrDefault(b => b.Classes.Contains("primary"))
            ?? page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => Pad.GetFirst(c) && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            ?? page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled
                                                                               && !(c is Button b && b.Classes.Contains("back")))
            ?? FocusManager.FindFirstFocusableElement(page) as Control;
        pick?.Focus(NavigationMethod.Directional);
    }

    static bool Usable(Button b) => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Focusable;

    static void Send(Control target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, KeyDeviceType = KeyDeviceType.Gamepad });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers, KeyDeviceType = KeyDeviceType.Gamepad });
    }

    /// In the dialog or page, by the logical tree too (an open list's items are in a popup of their own)
    static bool Within(Control control, Control scope) =>
        control == scope || scope.IsVisualAncestorOf(control) || control.GetLogicalAncestors().Contains(scope);

    /// The right stick: the list or page around the focus, or else the first one that scrolls
    void Scroll(double by)
    {
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        var root = window.PadScope ?? window.PadPage;
        var viewer = focused?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(Scrolls)
                     ?? root.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.IsEffectivelyVisible && Scrolls(v));
        if (viewer == null) return;
        var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(viewer.Offset.Y + by, 0, max));
    }

    static bool Scrolls(ScrollViewer v) => v.Extent.Height > v.Viewport.Height + 1;

    public void Dispose()
    {
        timer?.Stop();
        source?.Dispose();
    }
}
