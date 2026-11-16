namespace LongshotLauncher.Core;

/// The launcher's side of Longshot's crash reports (Longshot's docs/crash-report-window.md). After a crash the game's own window
/// sends a report when the player presses Report automatically, then Send. The Settings page's "Send crash reports automatically"
/// tick (LauncherSettings.SendCrashReportsAutomatically, off by default) is the player's yes to sending each crash's report
/// without that window: each game folder keeps it as longshot_crash.ini (send=1), which the game's crash helper reads after a
/// crash. A report that can't be sent shows the window after all; a version from before the tick ignores the file
public static class CrashReports
{
    public const string ChoiceName = "longshot_crash.ini";

    /// The folder's choice as the crash helper reads it (hos_crash_auto_send): a line send=1 in its longshot_crash.ini
    public static bool Chosen(string folder)
    {
        try
        {
            return File.ReadLines(Path.Combine(folder, ChoiceName)).Any(l => l.Trim() == "send=1");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The file as the launcher writes it
    public static string Text(bool on) =>
        "# Longshot's crash reports: the launcher's \"Send crash reports automatically\"\nsend=" + (on ? "1" : "0") + "\n";

    /// Before a game starts (LauncherService.PlayFolder): the folder's file made the player's answer, written only when it
    /// differs. Never stops the game from starting: a file that can't be written is left as it is
    public static void BeforePlay(string folder, bool on)
    {
        if (Chosen(folder) == on) return;
        var path = Path.Combine(folder, ChoiceName);
        if (!on && !File.Exists(path)) return;   // (off, and never chosen: nothing to write)
        try
        {
            File.WriteAllText(path, Text(on));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
