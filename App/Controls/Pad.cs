using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LongshotLauncher.Controls;

/// What a controller's B presses on a page or dialog (App/PadInput.cs): its Back, Cancel, Keep or Done. The first one shown
/// and enabled is pressed, the dialog's while one is open.
///
/// Scope: a question or menu shown inside a page (the profile chip's menu) that a controller's moves stay in
/// while it is shown, as a dialog's do (MainWindow.PadScope): B presses its own Back (its Keep), and when it appears, the focus
/// goes to that safe button, never to the one that changes something
public sealed class Pad : AvaloniaObject
{
    public static readonly AttachedProperty<bool> BackProperty = AvaloniaProperty.RegisterAttached<Pad, Button, bool>("Back");
    public static readonly AttachedProperty<bool> ScopeProperty = AvaloniaProperty.RegisterAttached<Pad, Control, bool>("Scope");
    /// Where a controller's first press goes on a page with no Play or main button: its main list's first control, or its first
    /// setting (the first one shown and enabled)
    public static readonly AttachedProperty<bool> FirstProperty = AvaloniaProperty.RegisterAttached<Pad, Control, bool>("First");

    static Pad()
    {
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (!GetScope(c) || e.NewValue is not true) return;
            // (once it is laid out: its safe button, else its first control)
            Dispatcher.UIThread.Post(() => SafeIn(c)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
        });
    }

    public static bool GetBack(Button button) => button.GetValue(BackProperty);
    public static void SetBack(Button button, bool value) => button.SetValue(BackProperty, value);
    public static bool GetScope(Control control) => control.GetValue(ScopeProperty);
    public static bool GetFirst(Control control) => control.GetValue(FirstProperty);
    public static void SetFirst(Control control, bool value) => control.SetValue(FirstProperty, value);
    public static void SetScope(Control control, bool value) => control.SetValue(ScopeProperty, value);

    /// Where a scope's focus starts: the control it marks as first (a name to type, a dialog's main button), else its safe button
    /// (its Back), else its first control that takes the focus
    public static Control? SafeIn(Control scope) =>
        scope.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => GetFirst(c) && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
        ?? scope.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => GetBack(b) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)
        ?? scope.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);

    /// The question shown in page that the moves stay in: the first scope shown, null when none is
    public static Control? ShownScope(Control page) =>
        page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => GetScope(c) && c.IsEffectivelyVisible);
}
