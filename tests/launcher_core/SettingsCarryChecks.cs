using LongshotLauncher.Core;

static partial class Program
{
    /// The player's settings brought to a build whose own file has none (SettingsCarry), the user folder a build starts with
    /// (GamePlay.UserFolder), and Hookshot's settings imported into the file a build started with --user reads
    static void SettingsCarryChecks()
    {
        var root = Folder("settings-carry");
        string Ini(string folder) => Path.Combine(folder, SettingsCarry.FileName);
        string Make(string name, string? text, int minutesAgo)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            if (text != null)
            {
                File.WriteAllText(Ini(folder), text);
                File.SetLastWriteTimeUtc(Ini(folder), DateTime.UtcNow.AddMinutes(-minutesAgo));
            }
            return folder;
        }

        Require(SettingsCarry.HasSettings("dlss=1\n") && SettingsCarry.HasSettings("# a note\ncompile_shaders=1\nkey_a=30\n")
                && !SettingsCarry.HasSettings("compile_shaders=1\n") && !SettingsCarry.HasSettings("") && !SettingsCarry.HasSettings("# dlss=1\n=2\n"),
                "A setting of the player's is a key=value line other than make_game.py's compile_shaders=.");
        Require(SettingsCarry.Merge("compile_shaders=0\n", "dlss=1\ncompile_shaders=1\nkey_a=30\n") == "dlss=1\nkey_a=30\ncompile_shaders=0\n"
                && SettingsCarry.Merge(null, "dlss=1\r\ncompile_shaders=1\r\n") == "dlss=1\r\n",
                "The settings come with their lines and line ends; the build's own compile_shaders= stays, the source's does not.");

        // (an update into a new folder: compile_shaders= alone; the newest of the user folder's and the other builds' fills it)
        var older = Make("v1", "dlss=1\nframe_generation=2\n", 60);
        var newer = Make("v2", "dlss=3\nframe_generation=0\nkey_a=30\n", 10);
        var made = Make("v2-made", "compile_shaders=1\n", 1);   // (make_game.py's, the newest file of all: still not settings)
        var user = Make("user", "dlss=4\n", 30);
        var fresh = Make("v3", "compile_shaders=1\n", 0);
        var done = SettingsCarry.Fill(fresh, user, new[] { older, newer, made, fresh });
        Require(done != null && done.From == Ini(newer) && done.To == Ini(fresh)
                && File.ReadAllText(Ini(fresh)) == "dlss=3\nframe_generation=0\nkey_a=30\ncompile_shaders=1\n"
                && File.ReadAllText(Ini(newer)) == "dlss=3\nframe_generation=0\nkey_a=30\n" && File.ReadAllText(Ini(user)) == "dlss=4\n",
                "A new build's folder is filled from the newest settings, its compile_shaders= kept; the source and the user folder's as they were.");
        Require(SettingsCarry.Fill(fresh, user, new[] { older, newer }) == null,
                "A build with settings of its own is left alone (the next start changes nothing).");

        File.SetLastWriteTimeUtc(Ini(user), DateTime.UtcNow);   // (the user folder's, written by a Longshot that keeps them there)
        var none = Make("v4", null, 0);
        done = SettingsCarry.Fill(none, user, new[] { older, newer });
        Require(done != null && done.From == Ini(user) && File.ReadAllText(Ini(none)) == "dlss=4\n",
                "A build with no file at all takes the newest: the user folder's when it is.");
        var empty = Make("v5", null, 0);
        Require(SettingsCarry.Fill(empty, null, new[] { made, Make("v0", null, 0) }) == null && !File.Exists(Ini(empty)),
                "With no settings anywhere nothing is written.");

        // (the user folder a build starts with: Windows always; Linux unless the build's folder has a user/ of its own)
        var data = Path.Combine(root, "data");
        var linux = new SaveEnvironment(Platform.Linux, k => k == "XDG_DATA_HOME" ? data : k == "HOME" ? root : null);
        var windows = new SaveEnvironment(Platform.Windows, k => k == "APPDATA" ? Path.Combine(root, "appdata") : k == "USERPROFILE" ? root : null);
        var game = Make("game", null, 0);
        Require(GamePlay.UserFolder(windows, game) == Path.GetFullPath(Path.Combine(root, "appdata", "Longshot"))
                && GamePlay.UserFolder(linux, game) == Path.GetFullPath(Path.Combine(data, "longshot")),
                "A build starts with the shared user folder, on Linux too.");
        Directory.CreateDirectory(Path.Combine(game, "user"));
        Require(GamePlay.UserFolder(linux, game) == null && GamePlay.UserFolder(windows, game) != null,
                "A Linux build whose folder has a user/ of its own is left to take it in itself (no --user).");

        // (Hookshot's settings into the file a build started with --user reads; language.txt stays the build's)
        var source = Make("Hookshot", null, 0);
        File.WriteAllText(Path.Combine(source, "hookshot_options.ini"), "options_version=4\nresolution=2\n");
        File.WriteAllText(Path.Combine(source, "language.txt"), "fr\n");
        var target = Make("Longshot", "compile_shaders=1\n", 0);
        var shared = Path.Combine(root, "shared", SettingsCarry.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, "dlss=1\n");
        var noneRunning = () => (IReadOnlyList<string>)Array.Empty<string>();
        HookshotSettings.Import(source, target, Path.Combine(root, "backups"), overwrite: true, running: noneRunning, optionsFile: shared);
        Require(File.ReadAllText(shared).Contains("dlss=1") && File.ReadAllText(shared).Contains("resolution=2")
                && File.ReadAllText(Ini(target)) == "compile_shaders=1\n" && File.ReadAllText(Path.Combine(target, "language.txt")) == "fr\n",
                "Hookshot's settings go into the file given (the user folder's), its language into the build's folder.");
    }
}
