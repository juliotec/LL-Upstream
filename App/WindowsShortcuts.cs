using System.Runtime.InteropServices;
using LongshotLauncher.Core;

namespace LongshotLauncher;

/// The Ready screen's Windows shortcuts (Desktop, Start menu): Longshot.lnk starting the launcher with --play, made by the shell's
/// own ShellLink (Core's ShellLinks) with the game's AppUserModelID, so the Start menu lists it apart from the launcher's own
/// Longshot Launcher.lnk (the same program: without the ids it showed only this one), or removed when unticked.
public static class WindowsShortcuts
{
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Longshot.lnk");
    public static string StartLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Longshot.lnk");

    /// A PowerShell single-quoted string: each quote doubled, the typographic ones too (PowerShell ends a string on ‘ ’ ‚ ‛ as on ',
    /// and a folder named O’Brien would end it early)
    public static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder("'");
        foreach (var c in s)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// The two shortcuts made (or removed) as the ticks say. One that starts another launcher is left as it is unless this one
    /// takes over (the installed launcher: Shortcuts.TakesOver, MayTouch), so a portable copy or one from a checkout never points
    /// the installed launcher's shortcuts at itself, nor removes them (#161). Returns those left that were ticked
    public static IReadOnlyList<string> Write(string launcher, bool desktop, bool start)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var takesOver = Shortcuts.TakesOver(LauncherLayout.Of(Platform.Windows, launcher, Environment.GetEnvironmentVariable));
        var left = new List<string>();
        foreach (var (link, want) in new[] { (DesktopLink, desktop), (StartLink, start) })
        {
            if (!Shortcuts.MayTouch(Target(link), launcher, takesOver))
            {
                if (want) left.Add(link);
                continue;
            }
            if (!want)
            {
                if (File.Exists(link)) File.Delete(link);
                continue;
            }
            ShellLinks.Write(link, launcher, "--play", Path.GetDirectoryName(launcher) ?? "", launcher, "Longshot", Shortcuts.GameAppId);
        }
        return left;
    }

    /// The two shortcuts that start launcher (the Ready screen's ticks for a portable one, Shortcuts.MadeAtReady)
    public static (bool Desktop, bool Start) Own(string launcher) =>
        (Shortcuts.Starts(Target(DesktopLink), launcher), Shortcuts.Starts(Target(StartLink), launcher));

    /// What the shortcut at link starts: null when there is none, "" when it cannot be read (then it is anyone's)
    static string? Target(string link)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(link)) return null;
        try { return ShellLinks.Read(link)?.Target ?? ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidCastException) { return ""; }
    }

    /// The game's shortcuts that are there made again, starting launcher (after the launcher's own update: Installer.Refresh)
    public static void Refresh(string launcher) => Write(launcher, File.Exists(DesktopLink), File.Exists(StartLink));

    /// The game's shortcuts taken away with the launcher they start (its uninstall)
    public static void RemoveGame()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var link in new[] { DesktopLink, StartLink })
            if (File.Exists(link)) File.Delete(link);
    }
}
