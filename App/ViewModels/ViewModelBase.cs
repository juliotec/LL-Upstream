using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    // (a change of language asks every live view model's texts again: Tr.Changed)
    static readonly List<WeakReference<ViewModelBase>> live = new();

    static ViewModelBase() => Tr.Changed += () => Ui.Post(() =>
    {
        ViewModelBase?[] each;
        lock (live) each = live.Select(w => w.TryGetTarget(out var vm) ? vm : null).ToArray();
        foreach (var vm in each) vm?.Raise(string.Empty);
    });

    protected ViewModelBase()
    {
        lock (live)
        {
            if (live.Count >= 256 && live.Count % 256 == 0) live.RemoveAll(w => !w.TryGetTarget(out _));
            live.Add(new WeakReference<ViewModelBase>(this));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll(params string[] names)
    {
        foreach (var n in names) Raise(n);
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// A button's action, and whether it can run now
public sealed class Command : ICommand
{
    readonly Action run;
    readonly Func<bool>? can;

    public Command(Action run, Func<bool>? can = null)
    {
        this.run = run;
        this.can = can;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => can?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) run();
    }

    public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// What the view models ask of the window: the system's pickers, the clipboard, opening a folder, the window's own buttons.
/// Null in the screenshots
public interface IWindowHost
{
    Task<string?> PickFileAsync(string title, string[]? patterns);
    /// Several files at once, of one kind ("NFC tag dumps": *.bin, *.nfc); none when the picker is closed
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string kind, IReadOnlyList<string> patterns);
    Task<string?> PickFolderAsync(string title, string? start);
    /// Where to write a new file: the system's Save As, with a name to start from (the file may be there already: it asked first)
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string[] patterns);
    Task CopyAsync(string text);
    void Open(string path);
    void Minimize();
    void Close();
}

/// A path as the screens show it, without the account's name: the home folder as ~ on Linux (~/Games/Longshot), as %USERPROFILE%
/// on Windows (%USERPROFILE%\Games\Longshot). Only for reading, tooltips too: a path handed to a program stays whole
public static class ShownPath
{
    const string WindowsHome = "%USERPROFILE%";

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');

    public static string Of(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        var home = Home;
        if (OperatingSystem.IsWindows())
        {
            // (C:\Users\Name: no shorter, so a drive's root is never taken for it)
            if (home.Length <= 3) return path;
            if (string.Equals(path.TrimEnd('\\', '/'), home, StringComparison.OrdinalIgnoreCase)) return WindowsHome;
            return path.Length > home.Length && path[home.Length] is '\\' or '/' && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                ? WindowsHome + path[home.Length..] : path;
        }
        if (home.Length <= 1) return path;
        if (path == home) return "~";
        return path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    /// A path a player typed (or left as it was shown), ~ (Linux) or %USERPROFILE% (Windows) read back as the home folder
    public static string Typed(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!path.StartsWith(WindowsHome, StringComparison.OrdinalIgnoreCase)) return path;
            var rest = path[WindowsHome.Length..];
            return rest.Length == 0 || rest[0] is '\\' or '/' ? Home + rest : path;
        }
        if (!(path == "~" || path.StartsWith("~/", StringComparison.Ordinal))) return path;
        return Home + path[1..];
    }
}

/// The UI thread: what a background task (a download, a build) changes is handed over here
public static class Ui
{
    public static Action<Action> Post { get; set; } = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);
}
