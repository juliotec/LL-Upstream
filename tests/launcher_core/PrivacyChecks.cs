using LongshotLauncher.Core;

/// No user name in what the launcher writes down or sends (Privacy.Scrub): the home folder as %USERPROFILE% or ~ (never a longer folder that
/// starts like it), the user's name wherever it is a folder's name in a path, on any drive, in an 8.3 short form, under Linux's
/// /media, /run/media and /var/home, and never a word of a sentence; the rest of a path kept. The launcher's log keeps its lines
/// that way (LauncherLog.Add)
static partial class Program
{
    static void PrivacyChecks()
    {
        // (the home folder as %USERPROFILE% on Windows and ~ on Linux, as the game writes them)
        Require(Privacy.Scrub(@"C:\Users\Peter2\x and C:\Users\Peter.old and C:\Users\Peter", @"C:\Users\Peter\") == @"C:\Users\Peter2\x and C:\Users\Peter.old and %USERPROFILE%",
                "A folder that only starts like the home folder stays as it is.");
        Require(Privacy.Scrub("/home/pat/.local/share/longshot and /home/PAT/x and /home/pat", "/home/pat") == "~/.local/share/longshot and /home/PAT/x and ~",
                "Linux: the home folder as ~, in its own case only.");
        Require(Privacy.Scrub("/x", "/") == "/x" && Privacy.Scrub(@"C:\x", @"C:\") == @"C:\x" && Privacy.Scrub("x", null) == "x",
                "No home folder, or a root: nothing replaced.");
        // (Windows: either separator, also doubled as JSON writes it, and a file:// link)
        foreach (var (from, to) in new[]
                 {
                     ("C:/Users/Pat/work/build.log", "%USERPROFILE%/work/build.log"),
                     ("file:///C:/Users/Pat/work/x", "file:///%USERPROFILE%/work/x"),
                     (@"""C:\\Users\\Pat\\AppData""", @"""%USERPROFILE%\\AppData"""),
                     (@"c:/users\PAT\x", @"%USERPROFILE%\x"),
                 })
            Require(Privacy.Scrub(from, @"C:\Users\Pat") == to, $"Windows' other separators: {from} -> {Privacy.Scrub(from, @"C:\Users\Pat")}");
        Require(Privacy.Scrub(@"C:\Users\Pat Smith\work\build.log and C:/Users/Pat%20Smith/x and C:\Users\Pat-x", @"C:\Users\Pat")
                == @"C:\Users\Pat Smith\work\build.log and C:/Users/Pat%20Smith/x and C:\Users\Pat-x",
                "A longer folder beside the home folder, a space in its name, stays as it is.");
        Require(Privacy.Scrub(@"C:\Users\Pat Smith\x and file:///C:/Users/Pat%20Smith/y", @"C:\Users\Pat Smith") == @"%USERPROFILE%\x and file:///%USERPROFILE%/y",
                "A home folder with a space: also as %20 in a link.");
        Require(Privacy.Scrub("'C:\\Users\\Pat' is denied. Saved in C:\\Users\\Pat. C:\\Users\\Pat is full (C:\\Users\\Pat), C:\\Users\\Pat\nC:\\other\\x",
                              @"C:\Users\Pat") == "'%USERPROFILE%' is denied. Saved in %USERPROFILE%. %USERPROFILE% is full (%USERPROFILE%), %USERPROFILE%\nC:\\other\\x",
                "The home folder alone in a sentence, a quote or brackets, and before a line with a path.");

        // (the user's name wherever it is a folder's name: the rest of the path stays)
        const string jane = @"C:\Users\Jane Ann Doe";
        foreach (var (from, to, home, users) in new (string, string, string, string[]?)[]
                 {
                     (@"cannot open C:\Users\Jane Ann Doe\tools\make_game.py", @"cannot open %USERPROFILE%\tools\make_game.py", jane, null),
                     (@"D:\Users\Jane Ann Doe\Longshot\x and E:\Backup\jane ann doe\saves", @"D:\Users\<user>\Longshot\x and E:\Backup\<user>\saves", jane, null),
                     (@"C:\Users\JANEAN~1\AppData\Local\Temp\x and C:\PROGRA~1\x", @"C:\Users\<user>\AppData\Local\Temp\x and C:\PROGRA~1\x", jane, null),
                     (@"""D:\\Users\\Jane Ann Doe\\x""", @"""D:\\Users\\<user>\\x""", jane, null),
                     (@"D:\Users\Seán O'Brien\x is full", @"D:\Users\<user>\x is full", @"C:\Users\Seán O'Brien", null),
                     (@"C:\Users\Ana\OneDrive - Contoso\Documents\x and D:\OneDrive\Ana\Longshot", @"%USERPROFILE%\OneDrive - Contoso\Documents\x and D:\OneDrive\<user>\Longshot", @"C:\Users\Ana", null),
                     (@"D:\Users\jdoe\x and C:\Users\Jane Ann Doe\y", @"D:\Users\<user>\x and %USERPROFILE%\y", jane, new[] { "jdoe" }),
                     // (scrubbed already: left as it is, even for a user called user or USERPROFILE)
                     (@"%USERPROFILE%\x and D:\Users\<user>\y and /media/<user>/z", @"%USERPROFILE%\x and D:\Users\<user>\y and /media/<user>/z",
                      @"C:\Users\user", new[] { "USERPROFILE", "user" }),
                     ("~/x and /media/<user>/y", "~/x and /media/<user>/y", "/home/user", null),
                     ("/media/jane/My Very Private Drive/tools/x and /run/media/jane/x and /var/home/jane/x and /home/jane/x",
                      "/media/<user>/My Very Private Drive/tools/x and /run/media/<user>/x and /var/home/<user>/x and ~/x", "/home/jane", null),
                     ("/media/Zoë Ng/x and /media/ZOË NG/y", "/media/<user>/x and /media/ZOË NG/y", "/home/Zoë Ng", null),
                     ("/media/jo/x", "/media/<user>/x", "/home/jo", null),
                     ("/media/a/x and /home/a/x", "/media/a/x and ~/x", "/home/a", null),
                     // (prose, a longer name, a relative path: as they are)
                     (@"Ana's build: Ana started D:\Games\Ana2\x and D:\Ana.old\y; ana", @"Ana's build: Ana started D:\Games\Ana2\x and D:\Ana.old\y; ana", @"C:\Users\Ana", null),
                     (@"romfs\Actor\Link.sbfres in C:\Games\Longshot\work", @"romfs\Actor\Link.sbfres in C:\Games\Longshot\work", @"C:\Users\Ana", null),
                 })
            Require(Privacy.Scrub(from, home, users) == to, $"The user's name in a path: {from} -> {Privacy.Scrub(from, home, users)}, not {to}");

        // (this PC's own home folder and user, as the launcher's log and reports take them by default)
        var mine = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        var sep = OperatingSystem.IsWindows() ? @"\" : "/";
        var homeAs = OperatingSystem.IsWindows() ? Privacy.WindowsHome : Privacy.LinuxHome;
        var other = OperatingSystem.IsWindows() ? $@"D:\Users\{user}\x" : $"/media/{user}/x";
        var by = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (user.Length >= Privacy.MinName && mine.Length > 3)
        {
            Require(Privacy.Scrub($"{mine}{sep}x and {other}") == $"{homeAs}{sep}x and {other.Replace(user, Privacy.User)}",
                    "By default, this PC's home folder and user: " + Privacy.Scrub($"{mine}{sep}x and {other}"));
            var clock = LauncherLog.Clock;
            try
            {
                LauncherLog.Clear();
                LauncherLog.Add($"open {mine}{sep}Longshot failed, then {other}");
                var kept = LauncherLog.Tail(1).Single();
                // (the literal Users folder and the two placeholders hold the name too when the account is called user: compare
                // the whole scrubbed line, and only the clock before it, not the string)
                var expected = $"{homeAs}{sep}Longshot failed, then {other.Replace(user, Privacy.User)}";
                var before = kept.Length >= expected.Length ? kept[..^expected.Length] : kept;
                Require(kept.EndsWith(expected, by) && !before.Contains(user, by),
                        "The launcher's log keeps its lines with no user name: " + kept.Replace(user, "<the user>", by));
            }
            finally
            {
                LauncherLog.Clock = clock;
                LauncherLog.Clear();
            }
        }
    }
}
