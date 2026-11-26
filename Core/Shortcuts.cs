using System.Text;

namespace LongshotLauncher.Core;

/// The shortcuts a build's Ready screen makes: on the desktop and in the Start menu (Windows) or the applications menu (Linux).
/// On Linux they start the launcher itself (no arguments: the window, which checks for updates and then plays the default
/// build), or --launch &lt;folder&gt; for one build. Windows writes --play: that platform's shortcuts start the game. Linux
/// writes freedesktop .desktop files here; Windows' .lnk files are written by the window (the shell makes them)
public static class Shortcuts
{
    public const string Name = "Longshot";

    /// The logo the Linux desktop and applications-menu shortcuts point at: the launcher's longshot.png, written into its home,
    /// where the desktop and the menu can read it (KDE draws a .png path correctly, checked on the Deck 2026-11-15)
    public static string LogoPath(Func<string, string?>? getenv = null) =>
        Path.Combine(LauncherHome.Resolve(Platform.Linux, getenv).Root, "longshot.png");

    /// Write the logo where the shortcuts point. False on a refusal (a read-only home, a file held busy): a shortcut without a
    /// logo beats no shortcut
    public static bool TryWriteLogo(byte[] png, Func<string, string?>? getenv = null)
    {
        try { AtomicFile.Write(LogoPath(getenv), png); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    /// The AppUserModelIDs the Windows shortcuts name (ShellLinks): the launcher's own Start menu shortcut, and the game's, which
    /// start the launcher's Play. Told apart, the Start menu lists both; without them it listed only the game's (ShellLinks)
    public const string LauncherAppId = "MagicBean.LongshotLauncher", GameAppId = "MagicBean.Longshot";

    /// A .desktop entry that starts the launcher (its window), or the window on a folder (--launch)
    public static string DesktopEntry(string launcher, string? folder, string? icon)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\nType=Application\nName=").Append(Name).Append('\n');
        sb.Append("Comment=Longshot\n");
        sb.Append("Exec=").Append(Quote(launcher));
        if (folder != null) sb.Append(" --launch ").Append(Quote(folder));
        sb.Append('\n');
        if (folder != null) sb.Append("Path=").Append(folder).Append('\n');
        if (icon != null) sb.Append("Icon=").Append(icon).Append('\n');
        sb.Append("Terminal=false\nCategories=Game;\n");
        return sb.ToString();
    }

    /// An Exec argument quoted as the desktop entry specification says: in double quotes, with " ` $ \ escaped (and % doubled).
    /// The file's own string escapes are read before the quoting, so each of those backslashes is written twice (a literal \ is
    /// four): GLib does not start an Exec line holding \" or \$
    public static string Quote(string arg)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in arg)
        {
            if (c is '"' or '`' or '$') sb.Append(@"\\").Append(c);
            else if (c == '\\') sb.Append(@"\\\\");
            else if (c == '%') sb.Append("%%");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// Where the two Linux shortcuts go: ~/Desktop/longshot.desktop and $XDG_DATA_HOME/applications/longshot.desktop
    public static (string Desktop, string Menu) LinuxPaths(Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        var home = LauncherHome.Home(getenv);
        var data = getenv("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
        return (Path.Combine(home, "Desktop", "longshot.desktop"), Path.Combine(data, "applications", "longshot.desktop"));
    }

    /// Whether the Ready screen makes the shortcuts as it opens, both ticked: yes for the launcher that owns them (installed, the
    /// AppImage, a build from a checkout); not for a portable one, which promises to write nothing outside its folder (#161).
    /// Its ticks start as its own shortcuts are (ones that start it), and it makes or removes them only when the player ticks,
    /// so it never makes again one the player deleted
    public static bool MadeAtReady(LauncherLayout layout) => layout.Kind != LauncherKind.Portable;

    /// Whether this launcher takes over the game's shortcuts that start another launcher: the installed Windows launcher (they
    /// are its own, even when a portable copy pointed them elsewhere) and the AppImage that is not portable. A portable launcher
    /// never does, nor on Windows a copy that is not installed (a build from a checkout, a launcher on its own
    /// LONGSHOT_LAUNCHER_HOME)
    public static bool TakesOver(LauncherLayout layout) =>
        layout.Platform == Platform.Windows ? layout.Kind == LauncherKind.Installed : layout.Kind != LauncherKind.Portable;

    /// Whether launcher may make, write over or remove the shortcut that starts target (null: there is no shortcut): one that
    /// starts launcher itself, or a launcher that is no longer there; any, when it takes over (TakesOver)
    public static bool MayTouch(string? target, string launcher, bool takesOver) =>
        takesOver || target == null || Starts(target, launcher) || !File.Exists(target);

    /// Whether a shortcut's target is launcher (the same path, in any case on Windows)
    public static bool Starts(string? target, string launcher)
    {
        if (string.IsNullOrEmpty(target)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(launcher),
                                 OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// The program a .desktop file at path starts (its Exec's first argument, unquoted as Quote wrote it); "" when it names none
    /// that can be read, null when there is no file
    public static string? LinuxTarget(string path)
    {
        if (!File.Exists(path)) return null;
        var exec = File.ReadLines(path).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))?["Exec=".Length..];
        if (exec is not ['"', ..]) return exec?.Split(' ')[0] ?? "";
        var sb = new StringBuilder();
        for (var i = 1; i < exec.Length;)
        {
            if (exec.AsSpan(i).StartsWith(@"\\\\")) { sb.Append('\\'); i += 4; }
            else if (exec.AsSpan(i).StartsWith(@"\\") && i + 2 < exec.Length && exec[i + 2] is '"' or '`' or '$') { sb.Append(exec[i + 2]); i += 3; }
            else if (exec.AsSpan(i).StartsWith("%%")) { sb.Append('%'); i += 2; }
            else if (exec[i] == '"') return sb.ToString();
            else sb.Append(exec[i++]);
        }
        return "";
    }

    /// The Linux shortcuts made (or removed) as the two ticks say; the desktop one only when there is a Desktop folder. One
    /// that starts another launcher is left as it is unless this one takes over (TakesOver, MayTouch). Returns those left
    /// that were ticked
    public static IReadOnlyList<string> WriteLinux(string launcher, string? folder, bool desktop, bool menu, string? icon = null,
                                                   Func<string, string?>? getenv = null)
    {
        var (d, m) = LinuxPaths(getenv);
        var text = DesktopEntry(launcher, folder, icon);
        var takesOver = TakesOver(LauncherLayout.Of(Platform.Linux, launcher, getenv ?? Environment.GetEnvironmentVariable));
        var left = new List<string>();
        foreach (var (path, want) in new[] { (d, desktop), (m, menu) })
        {
            if (!MayTouch(LinuxTarget(path), launcher, takesOver))
            {
                if (want) left.Add(path);
                continue;
            }
            if (!want)
            {
                if (File.Exists(path)) File.Delete(path);
                continue;
            }
            if (path == d && !Directory.Exists(Path.GetDirectoryName(path))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, text);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        return left;
    }

    /// The two Linux shortcuts that start launcher (the Ready screen's ticks for a portable one, MadeAtReady)
    public static (bool Desktop, bool Menu) LinuxOwn(string launcher, Func<string, string?>? getenv = null)
    {
        var (d, m) = LinuxPaths(getenv);
        return (Starts(LinuxTarget(d), launcher), Starts(LinuxTarget(m), launcher));
    }

    // ---- the launcher itself in the Linux applications menu (Settings, "Add to applications menu") ----

    /// (also the window's Wayland app_id and X11 WM_CLASS, Program, and the entries' StartupWMClass: a taskbar finds the entry's icon)
    public const string LauncherEntryName = "longshot-launcher";

    /// Where the launcher's own menu entry and its icon go: $XDG_DATA_HOME/applications/longshot-launcher.desktop and
    /// $XDG_DATA_HOME/icons/hicolor/512x512/apps/longshot-launcher.png
    public static (string Entry, string Icon) LauncherMenuPaths(Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        var data = getenv("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(LauncherHome.Home(getenv), ".local", "share");
        return (Path.Combine(data, "applications", LauncherEntryName + ".desktop"),
                Path.Combine(data, "icons", "hicolor", "512x512", "apps", LauncherEntryName + ".png"));
    }

    /// The launcher's own .desktop entry: the AppImage where it is now, with no arguments (the window)
    public static string LauncherEntry(string appImage, string icon) =>
        "[Desktop Entry]\nType=Application\nName=Longshot Launcher\n" +
        "Comment=Install Longshot's builder, build the game from your own copy, and play\n" +
        "Exec=" + Quote(appImage) + "\n" +
        "TryExec=" + appImage + "\n" +
        "Icon=" + icon + "\nTerminal=false\nCategories=Game;\nStartupWMClass=" + LauncherEntryName + "\n";

    /// Whether the launcher is in the applications menu, and the AppImage its entry starts (null when it is not there)
    public static string? LauncherInMenu(Func<string, string?>? getenv = null)
    {
        var (entry, _) = LauncherMenuPaths(getenv);
        if (!File.Exists(entry)) return null;
        return File.ReadAllLines(entry).FirstOrDefault(l => l.StartsWith("TryExec=", StringComparison.Ordinal))?["TryExec=".Length..];
    }

    /// The launcher added to the applications menu (its entry and its icon, png), pointing at appImage; again after it moved
    public static void AddLauncherToMenu(string appImage, byte[] png, Func<string, string?>? getenv = null)
    {
        var (entry, icon) = LauncherMenuPaths(getenv);
        AtomicFile.Write(icon, png);
        AtomicFile.Write(entry, LauncherEntry(appImage, icon));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public static void RemoveLauncherFromMenu(Func<string, string?>? getenv = null)
    {
        var (entry, icon) = LauncherMenuPaths(getenv);
        File.Delete(entry);
        File.Delete(icon);
    }
}
