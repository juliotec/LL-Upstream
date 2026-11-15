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

    public static void Write(string launcher, bool desktop, bool start)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var (link, want) in new[] { (DesktopLink, desktop), (StartLink, start) })
        {
            if (!want)
            {
                if (File.Exists(link)) File.Delete(link);
                continue;
            }
            ShellLinks.Write(link, launcher, "--play", Path.GetDirectoryName(launcher) ?? "", launcher, "Longshot", Shortcuts.GameAppId);
        }
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
