using LongshotLauncher.Core;

/// Launching the game through gamescope (Core/MakeGameCommand.cs GamePlay, LauncherSettings.Gamescope): the options typed in
/// Settings split as a shell would, and the command that starts gamescope, its options, -- and the game's own arguments
static partial class Program
{
    static void GamescopeChecks()
    {
        static string Words(IEnumerable<string> words) => string.Join("|", words);

        Require(Words(GamePlay.SplitOptions("-W 3840 -H 2160 -r 164 -f --hdr-enabled")) == "-W|3840|-H|2160|-r|164|-f|--hdr-enabled",
                "Gamescope options are not split on spaces into the words typed.");
        Require(Words(GamePlay.SplitOptions("  --scaler 'fit'   --adaptive-sync ")) == "--scaler|fit|--adaptive-sync",
                "Quoted options, or several spaces between them, are not split as a shell does.");
        Require(Words(GamePlay.SplitOptions("--x \"a b\" --y 'c d' --z a\\ b")) == "--x|a b|--y|c d|--z|a b",
                "Double quotes, single quotes or a backslash-escaped space do not group a word.");
        Require(Words(GamePlay.SplitOptions("\"a \\\"b\\\" c\" 'it'\\''s'")) == "a \"b\" c|it's",
                "A quote inside double quotes, or a single quote in a word, is not kept as typed.");
        Require(Words(GamePlay.SplitOptions("\"a\\b \\\\ \\$ \\` \\\" x\\\ny\"")) == "a\\b \\ $ ` \" xy",
                "A backslash in double quotes is kept before ordinary characters and escapes shell special characters.");
        Require(Words(GamePlay.SplitOptions("'' \"\"")) == "|", "Empty quoted options are not kept as empty words.");
        Require(GamePlay.SplitOptions(null).Count == 0 && GamePlay.SplitOptions("   ").Count == 0,
                "No options, or blank ones, are not an empty list (gamescope must run with its defaults).");

        var folder = Folder("gamescope-launch");
        var current = new Dictionary<string, string>();
        var exe = GameFolder.ExePath(folder, Platform.Linux);

        var si = GamePlay.StartInfo(folder, Platform.Linux, current, user: "/tmp/user",
                                    gamescope: true, gamescopeOptions: "-W 3840 -H 2160 -f");
        Require(si.FileName == "gamescope", "The game is not started through gamescope when the setting is on.");
        Require(Words(si.ArgumentList.ToList()) == "-W|3840|-H|2160|-f|--|" + exe + "|--user|/tmp/user",
                "The gamescope command does not put the options, --, the game and its own arguments in that order.");
        Require(si.WorkingDirectory == folder, "The game started through gamescope does not run in its folder.");
        Require(si.Environment.TryGetValue("LONGSHOT_NO_UPDATE_CHECK", out var check) && check == "1",
                "The environment the game is started with through gamescope is not the one it always has.");
        Require(si.UseShellExecute == false, "The gamescope command would go through a shell.");

        var plain = GamePlay.StartInfo(folder, Platform.Linux, current, user: "/tmp/user");
        Require(plain.FileName == exe, "The game is no longer started on its own when gamescope is off.");
        Require(Words(plain.ArgumentList.ToList()) == "--user|/tmp/user",
                "The game's own arguments changed when it starts without gamescope.");

        var windows = GamePlay.StartInfo(folder, Platform.Windows, current, gamescope: true, gamescopeOptions: "-f");
        Require(windows.FileName == GameFolder.ExePath(folder, Platform.Windows),
                "Windows, which has no gamescope, no longer starts the game itself.");
    }
}
