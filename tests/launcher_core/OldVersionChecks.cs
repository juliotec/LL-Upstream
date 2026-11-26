using LongshotLauncher.Core;

/// The clean break (Core/OldVersions.cs): a version built with make_game.py is shown as not supported, never played, built or
/// updated by this launcher, and never deleted by it unasked; a release made for make_game.py is not built. No game, no network
static partial class Program
{
    static void OldVersionChecks(string repo)
    {
        var p = Platforms.Current;
        var data = Folder("old-data");
        // (a game folder make_game.py built: its program and stamp, no longshot-builder record, outside the version folders)
        var old = Path.Combine(Folder("old-games"), "stable", "v3.0.0");
        Game(old, "v3.0.0");
        var row = new BuildEntry { Id = "a", Folder = old, Version = "v3.0.0" };
        Require(OldVersions.Is(old, row, p, data) && OldVersions.Is(old, null, p, data), "A game make_game.py built: an old version.");
        // (longshot-builder's: its record, its row's mark, or a version folder; and a folder with no game is nothing known)
        var recorded = Path.Combine(Folder("old-recorded"), "v4.0.0");
        Game(recorded, "v4.0.0");
        File.WriteAllText(Path.Combine(recorded, LongshotBuilder.RecordFile), "{\"format\":1,\"builder\":\"v4.0.0\"}");
        var inVersion = BuilderVersions.GameFolderOf(BuilderVersions.FolderOf(data, "v4.0.1"));
        Game(inVersion, "v4.0.1");
        var empty = Folder("old-empty");
        Require(!OldVersions.Is(recorded, null, p, data) && !OldVersions.Is(old, new BuildEntry { Folder = old, NupInPlace = true }, p, data)
                && !OldVersions.Is(inVersion, null, p, data) && !OldVersions.Is(empty, null, p, data),
                "longshot-builder's games (its record, its row's mark, a version folder), and a folder with no game: no old version.");
        Require(OldVersions.Shown == "Not supported by this launcher: use the stable Longshot Launcher to play it"
                && OldVersions.NotPlayed("v3.0.0").Contains("stable Longshot Launcher", StringComparison.Ordinal)
                && OldVersions.NotBuiltHere("v3.0.0").StartsWith("v3.0.0 ", StringComparison.Ordinal),
                "What an old version's card, its Play and its build say.");

        // (the window, in its source: Play refused before anything starts, no update offered, the card's own state and way back,
        // and Delete left to the player)
        var service = File.ReadAllText(Path.Combine(repo, "App", "LauncherService.cs"));
        var play = service[service.IndexOf("public string? PlayFolder(", StringComparison.Ordinal)..];
        Require(play.IndexOf("throw new OldVersionException(", StringComparison.Ordinal) is var refused and > 0
                && refused < play.IndexOf("PlayVersion(folder, entry)", StringComparison.Ordinal)
                && refused < play.IndexOf("GatherSaves()", StringComparison.Ordinal),
                "Play of an old version is refused before its saves are gathered or anything starts.");
        Require(service.Contains("IsOld(b) ? null") && service.Contains("var oldListed = Registry.Builds.Any(IsOld) || stable.Count > 0;")
                && service.Contains("Registry.Builds.Concat(stable).Select(x => x.Version)")
                && service.Contains("Disk.PruneMakeGame(work, sdks, oldListed)"),
                "No update is offered to an old version, and make_game.py's build files, SDKs and tools stay while one is listed, here or in the stable launcher's list.");
        var home = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "HomeViewModel.cs"));
        Require(home.Contains("() => IsBuilt && !IsOld && !IsDeleting") && home.Contains("Update = IsOld ? null : update;")
                && home.Contains("DeleteCommand = new Command(() => home.AskDelete(this), () => !IsDeleting);")
                && home.Contains("{ IsDeleting: true } or { IsOld: true } or null => false"),
                "An old version's card: no Play, no update, Delete as any other's; Home's button never plays it.");
        var versions = File.ReadAllText(Path.Combine(repo, "App", "Views", "VersionsView.axaml"));
        Require(versions.Contains("Text=\"{Binding OldText}\" IsVisible=\"{Binding ShowOld}\"") && versions.Contains("Command=\"{Binding StableCommand}\" IsVisible=\"{Binding ShowOld}\""),
                "The card says it is not supported, with the way to the stable launcher.");
        // (nothing of make_game.py's is left to run: no make_game.py command, no Python, no tools.lock, no source over its tools)
        var left = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "Core"), "*.cs").Concat(Directory.EnumerateFiles(Path.Combine(repo, "App"), "*.cs", SearchOption.AllDirectories)))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (var gone in new[] { "MakeGameBuilder", "PullRequestSource", "ToolsLock.", "InstallAppImageAsync", "InstallPullRequest", "LONGSHOT_ROMFS_FROM", "\"--fetch-ngx-sdk\"" })
                if (text.Contains(gone, StringComparison.Ordinal)) left.Add(Path.GetFileName(file) + ": " + gone);
        }
        Require(left.Count == 0, "make_game.py's paths left: " + string.Join(", ", left));
    }
}
