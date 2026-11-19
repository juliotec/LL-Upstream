using Avalonia;
using Avalonia.Controls;

namespace LongshotLauncher.Controls;

/// What a controller's B presses on a page or dialog (App/PadInput.cs): its Back, Cancel, Keep or Done. The first one shown
/// and enabled is pressed, the dialog's while one is open
public sealed class Pad : AvaloniaObject
{
    public static readonly AttachedProperty<bool> BackProperty = AvaloniaProperty.RegisterAttached<Pad, Button, bool>("Back");

    public static bool GetBack(Button button) => button.GetValue(BackProperty);
    public static void SetBack(Button button, bool value) => button.SetValue(BackProperty, value);
}
