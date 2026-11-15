using Avalonia.Data.Converters;
using Avalonia.Media;
using LongshotLauncher.Core;

namespace LongshotLauncher.Controls;

public static class Converters
{
    static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#143D63E6"));

    /// The build whose saves the page shows: tinted
    public static readonly IValueConverter ThisRow = new FuncValueConverter<bool, IBrush>(b => b ? Selected : Brushes.Transparent);

    /// A version as players see it: a nightly as "Nightly 2029 · c544828f" (Versions.Shown), any other as it is
    public static readonly IValueConverter ShownVersion = new FuncValueConverter<string?, string>(Versions.Shown);
}

/// The build details' colour for each log level: its line's prefix, and its swatch in the level picker
public static class LogColors
{
    static readonly Dictionary<LogLevel, IBrush> Brushes = new()
    {
        [LogLevel.Error] = new SolidColorBrush(Color.Parse("#E2776B")),     // (App.axaml's Bad)
        [LogLevel.Warning] = new SolidColorBrush(Color.Parse("#E2B86B")),   // (Warn)
        [LogLevel.Info] = new SolidColorBrush(Color.Parse("#6F95F2")),      // (Brand, lighter for text on the dark log)
        [LogLevel.Debug] = new SolidColorBrush(Color.Parse("#8A919B")),     // (InkFaintText)
        [LogLevel.Trace] = new SolidColorBrush(Color.Parse("#5A616B")),
    };

    public static IBrush Of(LogLevel level) => Brushes[level];
}
