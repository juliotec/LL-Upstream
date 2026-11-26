using LongshotLauncher.Core;

/// Core/ProfileStore.cs and Core/LauncherProfiles.cs: versions → profiles → saves. A profile is its folder (profile.json v1,
/// settings.ini, saves/ with save/ and save-work/), read and written keeping what it does not know; a builder's version is its
/// folder (versions/&lt;v&gt;: Longshot.exe, builds/default), played as Longshot.exe --profile &lt;folder&gt; --output json --cache &lt;the
/// launcher's&gt; with none of the older games' switches, and built for the profile (build --profile); the move from the launcher's
/// --user folder and its per-version game folders to the profiles' and versions' folders, once, every original kept; the
/// profiles' copy, delete and moving a save between them; the game's features= line (start_slot, save_dir). Text files only:
/// no game file
static class ProfileModelChecks
{
    public static int Count;

    static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("profiles: " + message);
        Count++;
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static void Run(string root, string checkout)
    {
        repo = checkout;
        FileChecks(root);
        PlayChecks(root);
        BuildChecks(root);
        FeatureChecks(root);
        MigrationChecks(Path.Combine(root, "migrate"));
        VersionMoveChecks(Path.Combine(root, "versions-move"));
        VersionDeleteChecks(Path.Combine(root, "versions-delete"));
        VersionPruneChecks(Path.Combine(root, "versions-prune"));
        WorkCarryChecks(Path.Combine(root, "work-carry"));
    }

    /// A version folder of the builder's made up: Longshot.x86_64 and its game in builds/default (a stamp, a record)
    static string MadeVersion(string data, string v)
    {
        var version = BuilderVersions.FolderOf(data, v);
        Write(BuilderVersions.ExeOf(version, Platform.Linux), "not a program");
        Write(Path.Combine(BuilderVersions.GameFolderOf(version), GameFolder.StampName), $"version={v}\n");
        Write(Path.Combine(BuilderVersions.GameFolderOf(version), "longshot_build.json"), "{}");
        return version;
    }

    // ---- gap (b): a builder's version deleted whole, Longshot.exe and all; never one holding the player's data

    static void VersionDeleteChecks(string root)
    {
        var data = Path.Combine(root, "data");
        var plain = MadeVersion(data, "v4.0.0");
        Require(BuilderVersions.WhyNotDelete(plain, data) == null, "A version folder of a builder and its game may be deleted.");
        BuilderVersions.Delete(plain, data);
        Require(!Directory.Exists(plain) && !File.Exists(BuilderVersions.ExeOf(plain, Platform.Linux)),
                "Deleted whole: its builder (Longshot.exe) goes with its game folder, nothing of it left.");
        foreach (var (what, path) in new[]
                 {
                     ("the builder's default profile", Path.Combine("profile", "saves", "save", "x")),
                     ("a profile.json", ProfileStore.FileName),
                     ("a game folder's user/", Path.Combine("builds", "default", "user", "save", "x")),
                     ("a game folder's save/", Path.Combine("builds", "default", "save", "x")),
                 })
        {
            var v = MadeVersion(data, "v4.0.1");
            Write(Path.Combine(v, path), "the player's");
            Require(BuilderVersions.WhyNotDelete(v, data) is { } why && why.Contains(v, StringComparison.Ordinal), $"Not deleted while it holds {what}.");
            var threw = false;
            try { BuilderVersions.Delete(v, data); } catch (IOException) { threw = true; }
            Require(threw && File.Exists(Path.Combine(v, path)) && File.Exists(BuilderVersions.ExeOf(v, Platform.Linux)), $"Refused with {what}: nothing touched.");
            Directory.Delete(v, true);
        }
        var outside = Path.Combine(root, "elsewhere", "v4.0.0");
        Write(Path.Combine(outside, "Longshot.x86_64"), "x");
        Require(BuilderVersions.WhyNotDelete(outside, data) != null, "Only a folder of the launcher's versions/ is deleted as a version.");
        Require(BuilderVersions.WhyNotDelete(BuilderVersions.FolderOf(data, "v9.9.9"), data) == null, "One already gone: nothing to refuse.");
    }

    // ---- gap (a): after an update, the version folders nothing uses go; the one the update replaced stays, for going back

    static void VersionPruneChecks(string root)
    {
        var data = Path.Combine(root, "data");
        var v1 = MadeVersion(data, "v4.0.0");
        var v2 = MadeVersion(data, "v4.1.0");
        var v3 = MadeVersion(data, "v4.2.0");
        var named = MadeVersion(data, "v3.9.0");
        var held = MadeVersion(data, "v3.8.0");
        Write(Path.Combine(held, "profile", "saves", "save", "x"), "the player's");
        var copying = MadeVersion(data, "v4.3.0");
        Directory.CreateDirectory(Path.Combine(copying, "builds", "default.part"));
        // (v4.2.0 played by a row, v3.9.0 a profile's version, v4.1.0 the one the last update replaced)
        var pruned = BuilderVersions.Prunable(data, ["v4.2.0", "v3.9.0"], keep: "v4.1.0");
        Require(pruned.Count == 1 && pruned[0] == v1,
                "Prunable: only the version nothing uses (no row, no profile, not the rollback one, no user data, no copy going on): " + string.Join(", ", pruned));
        foreach (var v in pruned) BuilderVersions.Delete(v, data);
        Require(!Directory.Exists(v1) && Directory.Exists(v2) && Directory.Exists(v3) && Directory.Exists(named) && Directory.Exists(held) && Directory.Exists(copying),
                "The previous version is kept for going back; the one in use, a profile's, one with the player's data and one being copied into stay.");
        // (the next update, v4.2.0 → v4.3.0: v4.1.0 is no longer the rollback one, and goes)
        Require(BuilderVersions.Prunable(data, ["v4.3.0", "v3.9.0"], keep: "v4.2.0").SequenceEqual([v2]),
                "At the next update the version before goes, the one it replaced is kept.");
        var service = File.ReadAllText(Path.Combine(Repo(), "App", "LauncherService.cs"));
        var step = File.ReadAllText(Path.Combine(Repo(), "App", "ViewModels", "BuildStepViewModel.cs"));
        Require(service.Contains("if ((running?.Invoke() ?? Saves.Running(Platform)).Contains(Saves.Longshot.Display)) return [];")
                && service.Contains(".Concat(profiles.Select(p => p.File?.Version))")
                && step.Contains("s.RollbackVersion = Path.GetFileName(left);") && step.Contains("await service.PruneVersions();"),
                "Pruned only while no game or builder runs, with every profile's version in use, after an update that left one behind (kept as the rollback).");
    }

    /// The checkout the checks read the sources of (Run's repo)
    static string repo = "";
    static string Repo() => repo;

    // ---- gap (c): a moved version's first Play updates warm: the same --cache as the builds, the row's work folder carried to
    // the one the builder takes for the new game folder by itself (no --work at Play)

    static void WorkCarryChecks(string root)
    {
        Require(LongshotBuilder.DefaultWorkFolder("/c", "/nonexistent-l259/versions/v1/builds/default", Platform.Linux)
                == Path.Combine("/c", "work", "default-17c3649c70"),
                "The builder's own work folder for a game folder: its name and the first 10 hex of the SHA-1 of its path (layout.rs work_of).");
        if (OperatingSystem.IsWindows())
            Require(Path.GetFileName(LongshotBuilder.DefaultWorkFolder(@"C:\c", @"C:\Games\Longshot\versions\v1\builds\Default", Platform.Windows)) == "default-5e69f61071",
                    "On Windows the path and the name in lower case.");
        var work = Path.Combine(root, "work");
        var cache = Path.Combine(work, Disk.LongshotBuildCache);
        var data = Path.Combine(root, "data");
        var to = BuilderVersions.GameFolderOf(BuilderVersions.FolderOf(data, "v4.0.0"));
        Directory.CreateDirectory(to);
        var row = new BuildEntry { Id = "a1b2c3", Folder = Path.Combine(root, "builds", "stable", "v4.0.0"), Version = "v4.0.0" };
        var old = LongshotBuilder.WorkFolder(work, row.Id);
        Write(Path.Combine(old, "gen", "unit.ll"), "the lift");
        Write(Path.Combine(old, LongshotBuilder.WorkGameNote), row.Folder);
        var move = new VersionFolderMigration.Move(row, row.Folder, to);
        Require(VersionFolderMigration.CarryWork(move, work, Platform.Linux), "The row's work folder carried.");
        var carried = LongshotBuilder.DefaultWorkFolder(cache, to, Platform.Linux);
        Require(!Directory.Exists(old) && File.ReadAllText(Path.Combine(carried, "gen", "unit.ll")) == "the lift"
                && File.ReadAllText(Path.Combine(carried, LongshotBuilder.WorkGameNote)) == Path.GetFullPath(to),
                "Moved, not copied, to the work folder Play's update takes for the new game folder, its note naming that folder.");
        Require(VersionFolderMigration.CarryWork(move, work, Platform.Linux) && Directory.Exists(carried), "Carried once; again, nothing to do.");
        // (Play's --cache is the builds': Work/longshot-builder; and a version folder's build takes no --work, so the two share it)
        Require(BuilderVersions.PlayArguments("/p", Path.Combine("/w", Disk.LongshotBuildCache)).SkipWhile(a => a != "--cache").Skip(1).First()
                == Path.Combine("/w", Disk.LongshotBuildCache), "Play's --cache: the launcher's build files folder's longshot-builder.");
        var service = File.ReadAllText(Path.Combine(Repo(), "App", "LauncherService.cs"));
        Require(service.Contains("BuilderVersions.StartInfo(version, Platform, profile, Path.Combine(Work, Disk.LongshotBuildCache), env,")
                && service.Contains("VersionFolderMigration.CarryWork(m, Work, Platform)"),
                "Play gives the same --cache as the builds (Work), and the move carries the row's work folder.");
    }


    // ---- profile.json: the known keys, and the unknown kept where they are

    static void FileChecks(string root)
    {
        var dir = Path.Combine(root, "profiles", "p-0a0b0c0d");
        Write(ProfileStore.FileOf(dir), """
            { "v": 1, "name": "Ana", "builder_note": {"kept": [1, 2]}, "mods": [ {"id": "local/a", "on": false} ],
              "upscalers": ["dlss", "xess"], "game": "/games/oot.nup", "start_slot": "slot_02" }
            """);
        var f = ProfileStore.Load(dir)!;
        Require(f.V == 1 && f.Name == "Ana" && f.Game == "/games/oot.nup" && f.StartSlot == "slot_02" && f.Upscalers.SequenceEqual(["dlss", "xess"]),
                "profile.json's keys are read.");
        f.Name = "Ana B";
        f.Color = "#7CC242";
        f.LastPlayed = new DateTime(2026, 11, 24, 21, 4, 0, DateTimeKind.Utc);
        ProfileStore.Save(dir, f);
        var again = ProfileStore.Load(dir)!;
        var text = File.ReadAllText(ProfileStore.FileOf(dir));
        Require(again.Name == "Ana B" && again.Color == "#7CC242" && again.LastPlayed == f.LastPlayed
                && text.Contains("\"builder_note\"") && text.Contains("\"kept\"") && text.Contains("\"mods\"") && text.Contains("\"local/a\"")
                && text.IndexOf("\"v\"", StringComparison.Ordinal) < text.IndexOf("\"name\"", StringComparison.Ordinal),
                "profile.json written again keeps the keys the launcher does not know (the Mod API's mods too), and the keys' order.");
        Require(ProfileStore.All(Path.Combine(root, "profiles")).Select(x => x.Id).SequenceEqual(["p-0a0b0c0d"]),
                "The profiles are the folders that hold a profile.json.");
        Require(new ProfileFile().V == ProfileFile.Version1 && new ProfileFile().Write().Contains("\"v\": 1"), "A new profile.json is v1.");
        Require(ProfileFile.Parse("""{"v":1,"start_slot":2}""").StartSlot == "slot_02" && ProfileFile.Parse("""{"v":1,"start_slot":"7"}""").StartSlot == "slot_07"
                && ProfileFile.Parse("""{"v":1,"start_slot":100}""").StartSlot == null,
                "A start slot written as the slot's number (2 or \"2\") is the slot's folder, as the game reads it.");
        // (a newer format: read for what it can, never written over)
        var newer = Path.Combine(root, "profiles", "p-0e0e0e0e");
        Write(ProfileStore.FileOf(newer), """{"v": 2, "name": "Later", "what": "a newer launcher's"}""");
        var later = ProfileStore.Load(newer)!;
        later.Name = "Mine";
        ProfileStore.Save(newer, later);
        Require(later.Newer && File.ReadAllText(ProfileStore.FileOf(newer)).Contains("\"Later\""), "A profile.json of a newer format is never written over.");
        Directory.Delete(newer, true);
        Require(!LauncherProfiles.IsId("p-0A0B0C0D") && !LauncherProfiles.IsId("../x") && LauncherProfiles.IsId("default") && LauncherProfiles.IsId("p-0a0b0c0d"),
                "A profile's id is default or p- and 8 lowercase hex digits.");
    }

    // ---- Play: the builder, --profile, --output json, the launcher's cache, and none of the older games' switches

    static void PlayChecks(string root)
    {
        var data = Path.Combine(root, "data");
        var version = BuilderVersions.FolderOf(data, "v4.0.0");
        Require(version == Path.GetFullPath(Path.Combine(data, "versions", "v4.0.0")) && BuilderVersions.GameFolderOf(version) == Path.Combine(version, "builds", "default"),
                "A version's folder is versions/<v>, its game folder builds/default.");
        var game = BuilderVersions.GameFolderOf(version);
        Directory.CreateDirectory(game);
        Require(!BuilderVersions.Is(version, Platform.Windows) && BuilderVersions.VersionOfGame(game, Platform.Linux) == null
                && BuilderVersions.InVersions(game, data) && !BuilderVersions.InVersions(Path.Combine(root, "builds", "v4.0.0"), data),
                "A folder without Longshot.exe is not a builder's version yet; its game folder is in the versions' folder.");
        var tools = Path.Combine(root, "tools", "v4.0.0", "longshot-builder");
        Write(tools, "not a program");
        BuilderVersions.Install(version, tools, Platform.Linux);
        Write(BuilderVersions.ExeOf(version, Platform.Windows), "not a program");
        Require(BuilderVersions.Is(version, Platform.Windows) && BuilderVersions.Is(version, Platform.Linux)
                && File.ReadAllText(BuilderVersions.ExeOf(version, Platform.Linux)) == "not a program"
                && BuilderVersions.ExeName(Platform.Windows) == "Longshot.exe" && BuilderVersions.ExeName(Platform.Linux) == "Longshot.x86_64"
                && BuilderVersions.VersionOfGame(game, Platform.Linux) == version,
                "A builder's version is a folder with the release's builder as Longshot.exe (Longshot.x86_64 on Linux).");
        if (!OperatingSystem.IsWindows())
            Require((File.GetUnixFileMode(BuilderVersions.ExeOf(version, Platform.Linux)) & UnixFileMode.UserExecute) != 0, "The builder put there runs.");
        BuilderVersions.Install(version, tools, Platform.Linux);
        Require(!File.Exists(BuilderVersions.ExeOf(version, Platform.Linux) + ".part"), "Installed again: the one there is kept.");
        var profile = Path.Combine(root, "profiles", "p-0a0b0c0d");
        var cache = Path.Combine(root, "work", "longshot-builder");
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin", ["HOS_MODS_LIST"] = "/x/mods.json", ["HOS_OPTIONS_FILE"] = "/x/o.ini", ["HOS_SAVE_DIR"] = "/x",
            ["HOS_START_SLOT"] = "3", ["HOS_PROFILE"] = "/elsewhere", ["LONGSHOT_PROFILE"] = "/elsewhere", ["LONGSHOT_STARTED_BY"] = "builder",
        };
        var si = BuilderVersions.StartInfo(version, Platform.Linux, profile, cache, env);
        Require(si.FileName == BuilderVersions.ExeOf(version, Platform.Linux)
                && si.ArgumentList.SequenceEqual(["--profile", Path.GetFullPath(profile), "--output", "json", "--cache", cache, "--no-self-update"])
                && si.WorkingDirectory == version && si.Environment["PATH"] == "/usr/bin" && !si.RedirectStandardOutput
                && BuilderVersions.OldSwitches.All(k => !si.Environment.ContainsKey(k)),
                "Play is Longshot.exe --profile <the profile's folder> --output json --cache <the launcher's> --no-self-update, no --launcher, with none of the launcher's switches for older games: "
                + string.Join(" ", si.ArgumentList));
        Require(!si.ArgumentList.Contains("--json") && !si.ArgumentList.Contains("--send-build-statistics"), "Play takes no --json (a usage error now).");
        Require(BuilderVersions.StartInfo(version, Platform.Linux, profile, cache, env, statistics: true).ArgumentList[^1] == "--send-build-statistics",
                "The player's yes to the statistics: play sends every build's end itself (--send-build-statistics).");
        Require(BuilderVersions.Ended(0, null) == null && BuilderVersions.Ended(1, null) == null && BuilderVersions.Ended(BuilderVersions.NotBuilt, null) != null
                && BuilderVersions.Ended(BuilderVersions.Failed, "the .nup is gone") is { } failed && failed.Contains("the .nup is gone")
                && BuilderVersions.Ended(BuilderVersions.InUse, null) != null && BuilderVersions.Ended(122, null) == null,
                "Play's own ends (120 not set up, 121 failed, 123 the profile in use; no 122 any more) are said; the game's own are not.");
    }

    // ---- Build: a version folder's builder builds the profile's game (build --profile, update --profile), naming the launcher

    static void BuildChecks(string root)
    {
        var home = new LauncherHome(Path.Combine(root, "home"));
        var data = Path.Combine(root, "data");
        var version = BuilderVersions.FolderOf(data, "v4.0.0");
        var tools = new Toolchain("v4.0.0", Path.Combine(root, "tools", "v4.0.0"), "longshot-builder");
        var release = new LongshotBuilder(tools, tools.BuilderExe!, Platform.Linux, home, "/L/Longshot.AppImage", _ => null);
        var inVersion = release.In(version);
        var profile = Path.Combine(root, "profiles", "p-0a0b0c0d");
        var request = new BuildRequest(new BuildSource.Nup("/g/game.nup"), BuilderVersions.GameFolderOf(version), Work: "/w", Dlss: true, DlssLicense: true,
                                       Profile: profile);
        var build = inVersion.Build(request).Command;
        Require(build.FileName == BuilderVersions.ExeOf(version, Platform.Linux), "The version folder's builder builds it.");
        Require(build.Arguments.Take(7).SequenceEqual(["build", "--profile", Path.GetFullPath(profile), "--game", "/g/game.nup", "--output", "json"])
                && !build.Arguments.Contains("--launcher") && !build.Arguments.Contains("--out") && !build.Arguments.Contains("--json")
                && build.Arguments.SkipWhile(a => a != "--upscaler").Skip(1).First() == "dlss",
                "A build for the profile: build --profile <its folder> --game <the .nup> --output json, no --launcher, no --out: " + string.Join(" ", build.Arguments));
        var update = inVersion.Build(request with { Source = new BuildSource.Update(BuilderVersions.GameFolderOf(version)) }).Command;
        Require(update.Arguments.Take(5).SequenceEqual(["update", "--profile", Path.GetFullPath(profile), "--output", "json"]) && !update.Arguments.Contains("--launcher"),
                "An update for the profile: update --profile <its folder>, no --launcher: " + string.Join(" ", update.Arguments));
        // (the record a builder now writes, and the one before it)
        var game = Path.Combine(root, "record-game");
        Write(Path.Combine(game, "longshot_builder.json"), """{"format":1,"builder":"v4.0.0","game":{"nup":"/old.nup"}}""");
        Require(LongshotBuilder.ReadRecord(game)?.Nup == "/old.nup", "A record of the older name is read.");
        Write(Path.Combine(game, LongshotBuilder.RecordFile), """{"format":1,"builder":"v4.0.1","game":{"nup":"/new.nup"}}""");
        Require(LongshotBuilder.RecordFile == "longshot_build.json" && LongshotBuilder.ReadRecord(game)?.Nup == "/new.nup", "longshot_build.json wins.");
    }

    // ---- features=: what the game's stamp says it takes

    static void FeatureChecks(string root)
    {
        var game = Path.Combine(root, "features");
        Directory.CreateDirectory(game);
        Require(GameFeatures.Of(game).Count == 0, "No stamp: no feature.");
        Write(Path.Combine(game, "longshot_version.txt"), "version=v4.0.0\nfeatures=profile,start_slot\tsave_dir\n");
        Require(GameFeatures.Has(game, "profile") && GameFeatures.Has(game, "start_slot") && GameFeatures.Has(game, "save_dir") && !GameFeatures.Has(game, "save"),
                "features= names them, comma, space or tab between.");
    }

    // ---- the move: the --user folder's saves and settings into each profile's folder, once, originals kept

    static void MigrationChecks(string root)
    {
        var env = new SaveEnvironment(Platform.Linux, k => k == "XDG_DATA_HOME" ? Path.Combine(root, "data") : k == "HOME" ? root : null)
        {
            ProfilesRoot = Path.Combine(root, "launcher", "profiles"),
        };
        var userSlot = Path.Combine(env.LongshotUser, "save", Saves.UserId, "slot_00", "progress.sav");
        Write(userSlot, "default's");
        Write(Path.Combine(env.LongshotUser, "save-work", "ready-1", "x"), "w");
        Write(Path.Combine(env.LongshotUser, "cache", "big.bin"), "not a save");
        Write(Path.Combine(env.LongshotUser, SettingsCarry.FileName), "fps=60\n");
        var oldProfiles = Path.Combine(env.LongshotRoot, "profiles");
        Write(Path.Combine(oldProfiles, "p-00000001", "save", Saves.UserId, "slot_01", "progress.sav"), "ana's");
        var builds = new List<BuildEntry>
        {
            new() { Id = "b1", Folder = Path.Combine(root, "builds", "v3.1.0"), Version = "v3.1.0", Default = true, Nup = "/games/oot.nup", Dlss = true, Xess = true },
            new() { Id = "b2", Folder = Path.Combine(root, "builds", "v3.0.0"), Version = "v3.0.0", Romfs = "/dump/romfs", Exefs = "/dump/exefs" },
        };
        var profiles = new List<LauncherProfile>
        {
            new() { Id = LauncherProfiles.DefaultId, Name = "Charlie", Color = "#7CC242", StartSlot = "slot_00" },
            new() { Id = "p-00000001", Name = "Ana", Color = "#F3C24F", LastPlayed = "b2", CopyOf = LauncherProfiles.DefaultId },
            new() { Id = "../escape", Name = "Bad" },
        };
        var result = ProfileMigration.Run(env.ProfilesRoot!, profiles, env, builds, knownGame: "/games/known.nup", oldProfilesRoot: oldProfiles);
        var defDir = ProfileStore.Dir(env.ProfilesRoot!, LauncherProfiles.DefaultId);
        var anaDir = ProfileStore.Dir(env.ProfilesRoot!, "p-00000001");
        var def = ProfileStore.Load(defDir)!;
        var ana = ProfileStore.Load(anaDir)!;
        Require(result != null && result.Profiles.SequenceEqual([LauncherProfiles.DefaultId, "p-00000001"]) && ProfileMigration.Done(env.ProfilesRoot!),
                "Every profile a launcher's settings listed becomes a folder, once; an id that is no profile's is passed over.");
        Require(def.Name == "Charlie" && def.Color == "#7CC242" && def.StartSlot == "slot_00" && def.Version == "b1"
                && ana.Name == "Ana" && ana.CopyOf == LauncherProfiles.DefaultId && ana.Version == "b2",
                "A profile's name, colour, start slot and last version come from the settings.");
        Require(def.Game == "/games/oot.nup" && def.Upscalers.SequenceEqual(["dlss", "xess"]) && ana.Game == "/dump" && ana.Upscalers.Count == 0,
                "A profile's game copy (a .nup, or a dump's folder) and upscalers come from its version's build.");
        Require(File.ReadAllText(ProfileStore.SettingsOf(defDir)) == "fps=60\n" && File.ReadAllText(ProfileStore.SettingsOf(anaDir)) == "fps=60\n",
                "Each profile's settings.ini starts as the game's settings every version played with.");
        var defSaves = ProfileStore.SavesOf(defDir);
        Require(File.ReadAllText(Path.Combine(defSaves, "save", Saves.UserId, "slot_00", "progress.sav")) == "default's"
                && File.Exists(Path.Combine(defSaves, "save-work", "ready-1", "x")) && !Directory.Exists(Path.Combine(defSaves, "cache"))
                && File.ReadAllText(Path.Combine(ProfileStore.SavesOf(anaDir), "save", Saves.UserId, "slot_01", "progress.sav")) == "ana's"
                && File.Exists(userSlot) && File.Exists(Path.Combine(oldProfiles, "p-00000001", "save", Saves.UserId, "slot_01", "progress.sav")),
                "The saves (save/ and save-work/, nothing else) are copied into each profile's saves/, every original left where it was.");
        Require(ProfileMigration.Run(env.ProfilesRoot!, profiles, env, builds) == null, "The move is done once.");
        // (none listed: the default made all the same, its game the one the launcher knows)
        var fresh = new SaveEnvironment(Platform.Linux, k => k == "XDG_DATA_HOME" ? Path.Combine(root, "data2") : k == "HOME" ? root : null)
        {
            ProfilesRoot = Path.Combine(root, "launcher2", "profiles"),
        };
        ProfileMigration.Run(fresh.ProfilesRoot!, [], fresh, [], knownGame: "/games/known.nup");
        Require(ProfileStore.Load(ProfileStore.Dir(fresh.ProfilesRoot!, LauncherProfiles.DefaultId)) is { Game: "/games/known.nup" } d && d.Name.Length > 0
                && Directory.Exists(ProfileStore.SavesOf(ProfileStore.Dir(fresh.ProfilesRoot!, LauncherProfiles.DefaultId))),
                "With no profile listed, the default is made, playing the game file the launcher knows.");
        // (after it: every profile's saves are its folder's, the default's too; the settings' list is the folders')
        Require(LauncherProfiles.SavesHome(env, LauncherProfiles.DefaultId) == defSaves
                && LauncherProfiles.SlotsFolder(env, "p-00000001") == Path.Combine(ProfileStore.SavesOf(anaDir), "save", Saves.UserId)
                && (env with { SavesHome = defSaves }).SaveFolder == Path.Combine(defSaves, "save"),
                "Every profile's saves are its folder's saves/, the default's too, and the Saves page's.");
        var settings = new LauncherSettings { CurrentProfile = "p-0000dead" };
        var listed = LauncherProfiles.All(env);
        Require(listed.Select(p => p.Id).SequenceEqual([LauncherProfiles.DefaultId, "p-00000001"])
                && listed[1].Name == "Ana" && listed[0].StartSlot == "slot_00" && listed.All(p => !p.Damaged),
                "The launcher's profiles are the folders of profiles/, read from each profile.json.");
        Require(LauncherProfiles.Current(env, settings).Id == LauncherProfiles.DefaultId && LauncherProfiles.Find(env, "p-0000dead") == null,
                "A current profile that is gone is the default again.");
        var renamed = LauncherProfiles.Rename(env, "p-00000001", "Ana B");
        Require(renamed.Name == "Ana B" && renamed.Id == "p-00000001" && ProfileStore.Load(anaDir) is { Name: "Ana B", Game: "/dump" }
                && Directory.Exists(anaDir), "A rename is written to the profile's profile.json, its folder and game copy kept.");
        Require(LauncherProfiles.BadName(env, "ana b") != null && LauncherProfiles.BadName(env, " ") != null
                && LauncherProfiles.BadName(env, new string('x', 41)) != null && LauncherProfiles.BadName(env, "Sam") == null,
                "A profile's name: not empty, at most 40 characters, not another's.");
        var sam = LauncherProfiles.Create(env, "Sam", knownGame: "/games/oot.nup");
        Require(LauncherProfiles.IsId(sam.Id) && ProfileStore.Load(ProfileStore.Dir(env.ProfilesRoot!, sam.Id)) is { Game: "/games/oot.nup", Name: "Sam" }
                && Directory.Exists(ProfileStore.SavesOf(ProfileStore.Dir(env.ProfilesRoot!, sam.Id))) && LauncherProfiles.All(env).Any(p => p.Id == sam.Id),
                "A new profile is a folder, playing the game copy the launcher knows, listed at once.");
        // (the data of a profile is its profile.json's: written at once, every key not known kept)
        var anaFile = ProfileStore.Load(anaDir)!;
        anaFile.Json["mods"] = new System.Text.Json.Nodes.JsonArray("a");
        ProfileStore.Save(anaDir, anaFile);
        LauncherProfiles.SetStartSlot(env, "p-00000001", "slot_01");
        LauncherProfiles.SetColor(env, "p-00000001", LauncherProfiles.Colors[4]);
        LauncherProfiles.MarkPlayed(env, "p-00000001", "b1", new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc));
        Require(LauncherProfiles.Find(env, "p-00000001") is { StartSlot: "slot_01", LastPlayed: "b1", LastPlayedAt: not null } now
                && now.Color == LauncherProfiles.Colors[4] && ProfileStore.Load(anaDir)!.Json["mods"] is not null,
                "The start slot, colour and last played are the profile.json's, written at once; keys it does not know stay.");
        var copy = LauncherProfiles.Duplicate(env, "p-00000001", "Ana C");
        var copyDir = ProfileStore.Dir(env.ProfilesRoot!, copy.Id);
        Require(ProfileStore.Load(copyDir) is { Game: "/dump", CopyOf: "p-00000001", Name: "Ana C", LastPlayed: null, StartSlot: "slot_01" } cf
                && cf.Json["mods"] is not null && File.Exists(ProfileStore.SettingsOf(copyDir))
                && File.Exists(Path.Combine(ProfileStore.SavesOf(copyDir), "save", Saves.UserId, "slot_01", "progress.sav")),
                "A copy of a profile is its folder copied: game copy, settings, saves and the keys it holds, not its last played.");
        // (a save moved between profiles: into the same slot when it is free there, else the first free one)
        var into = LauncherProfiles.CopySlot(env, "p-00000001", sam.Id, "slot_01", move: false);
        var again = LauncherProfiles.CopySlot(env, "p-00000001", sam.Id, "slot_01", move: true);
        Require(into == "slot_01" && again == "slot_00"
                && File.ReadAllText(Path.Combine(LauncherProfiles.SlotsFolder(env, sam.Id), "slot_00", "progress.sav")) == "ana's"
                && !Directory.Exists(Path.Combine(LauncherProfiles.SlotsFolder(env, "p-00000001"), "slot_01")),
                "A save copied to another profile keeps its slot when free there; moved, it leaves this one.");
        settings.CurrentProfile = copy.Id;
        var kept = LauncherProfiles.Delete(settings, env, copy.Id);
        Require(kept != null && !Directory.Exists(copyDir) && File.Exists(Path.Combine(kept, ProfileStore.FileName))
                && LauncherProfiles.All(env).All(p => p.Id != copy.Id) && settings.CurrentProfile == null,
                "A profile deleted: its whole folder kept in the backups.");
        var refused = false;
        try { LauncherProfiles.Delete(settings, env, LauncherProfiles.DefaultId); }
        catch (ProfileFailure) { refused = true; }
        Require(refused, "The default profile cannot be deleted.");
        ProfileFolderChecks(root);
    }

    // ---- the folders are the profiles: a damaged one is listed as damaged, two launchers see the same list, the settings' list moves

    static void ProfileFolderChecks(string root)
    {
        var env = new SaveEnvironment(Platform.Linux, k => k == "XDG_DATA_HOME" ? Path.Combine(root, "data3") : k == "HOME" ? root : null)
        {
            ProfilesRoot = Path.Combine(root, "launcher3", "profiles"),
        };
        Require(LauncherProfiles.All(env) is [{ Id: LauncherProfiles.DefaultId, Damaged: false }],
                "Before any folder, the default is the one profile listed.");
        LauncherProfiles.Ensure(env, "/games/oot.nup");
        var ana = LauncherProfiles.Create(env, "Ana");
        var sam = LauncherProfiles.Create(env, "Sam");
        var samDir = ProfileStore.Dir(env.ProfilesRoot!, sam.Id);
        // (a profile.json that is not JSON: listed as damaged, not dropped, and not changed or copied; deleted into the backups)
        File.WriteAllText(ProfileStore.FileOf(samDir), "{ not json");
        var anaDir = ProfileStore.Dir(env.ProfilesRoot!, ana.Id);
        var noFile = Path.Combine(env.ProfilesRoot!, "p-0000beef");
        Directory.CreateDirectory(noFile);
        Directory.CreateDirectory(Path.Combine(env.ProfilesRoot!, "not-a-profile"));
        var all = LauncherProfiles.All(env);
        Require(all.Select(p => p.Id).SequenceEqual(new[] { LauncherProfiles.DefaultId, ana.Id, "p-0000beef", sam.Id }.Order(StringComparer.Ordinal)
                    .OrderBy(i => i == LauncherProfiles.DefaultId ? 0 : 1)),
                "Every folder named as a profile is listed, in order, the default first; other folders are not profiles.");
        Require(all.Single(p => p.Id == sam.Id) is { Damaged: true, Name: var n, Problem: { Length: > 0 } } && n == sam.Id
                && all.Single(p => p.Id == "p-0000beef").Damaged && !all.Single(p => p.Id == ana.Id).Damaged,
                "A folder with a damaged or missing profile.json is listed as damaged, under its id; the others are fine.");
        string Failure(Action act)
        {
            try { act(); }
            catch (ProfileFailure e) { return e.Message; }
            return "";
        }
        Require(Failure(() => LauncherProfiles.Rename(env, sam.Id, "Sammy")).Length > 0 && Failure(() => LauncherProfiles.Duplicate(env, sam.Id, "Sam 2")).Length > 0
                && File.ReadAllText(ProfileStore.FileOf(samDir)) == "{ not json",
                "A damaged profile is not changed or copied, and its file is left as it is.");
        var settings = new LauncherSettings { CurrentProfile = sam.Id };
        var keptDamaged = LauncherProfiles.Delete(settings, env, sam.Id);
        Require(keptDamaged != null && File.ReadAllText(Path.Combine(keptDamaged, ProfileStore.FileName)) == "{ not json" && !Directory.Exists(samDir)
                && settings.CurrentProfile == null && LauncherProfiles.Delete(settings, env, "p-0000beef") != null,
                "A damaged profile is deleted like any other: its folder to the backups.");
        // (two launchers on the same profiles: each call reads the folders, so one's change is the other's list at once)
        var other = env with { };
        LauncherProfiles.Create(other, "Zed");
        LauncherProfiles.Rename(other, ana.Id, "Anna");
        Require(LauncherProfiles.All(env).Select(p => p.Name).Contains("Zed") && LauncherProfiles.Find(env, ana.Id)!.Name == "Anna",
                "Two launchers on the same profiles: a profile one made or renamed is on the other's list at once, no stale list.");
        LauncherProfiles.Delete(new LauncherSettings(), other, ana.Id);
        Require(LauncherProfiles.Find(env, ana.Id) == null && !Directory.Exists(anaDir) && Failure(() => LauncherProfiles.Rename(env, ana.Id, "Back")).Length > 0,
                "A profile the other launcher deleted is gone from this one's list.");
        // (the move of the settings' own profiles: into each profile.json, out of settings.json, the old file kept)
        var home = Path.Combine(root, "launcher4");
        Directory.CreateDirectory(home);
        var settingsPath = Path.Combine(home, "settings.json");
        var env4 = new SaveEnvironment(Platform.Linux, k => k == "XDG_DATA_HOME" ? Path.Combine(root, "data4") : k == "HOME" ? root : null)
        {
            ProfilesRoot = Path.Combine(home, "profiles"),
        };
        File.WriteAllText(settingsPath, """
            {"format":2,"currentProfile":"p-00000002","uiLanguage":"de",
             "profiles":[{"id":"default","name":"Charlie","color":"#3D63E6","startSlot":"slot_02","lastPlayedAt":"2026-11-01T10:00:00"},
                         {"id":"p-00000002","name":"Ana","color":"#F3C24F","startSlot":"slot_01","lastPlayed":"b9","copyOf":"default"}]}
            """);
        var moved = LauncherSettings.Load(settingsPath);
        Require(moved.LegacyProfiles is { Count: 2 }, "A settings.json of a launcher before the folders still holds its profiles.");
        // (a profile.json there already keeps what it holds: only what it lacks comes from the settings)
        var pre = new ProfileFile { Name = "Ana (file)" };
        ProfileStore.Save(ProfileStore.Dir(env4.ProfilesRoot!, "p-00000002"), pre);
        var original = File.ReadAllText(settingsPath);
        var backup = settingsPath + ".before-profile-folders";
        ProfileMigration.MoveFromSettings(moved, settingsPath, backup, env4, [], "/games/known.nup");
        var def = ProfileStore.Load(ProfileStore.Dir(env4.ProfilesRoot!, "default"))!;
        var ana2 = ProfileStore.Load(ProfileStore.Dir(env4.ProfilesRoot!, "p-00000002"))!;
        Require(def.Name == "Charlie" && def.Color == "#3D63E6" && def.StartSlot == "slot_02" && def.LastPlayed != null
                && ana2.Name == "Ana (file)" && ana2.Color == "#F3C24F" && ana2.StartSlot == "slot_01" && ana2.Version == "b9" && ana2.CopyOf == "default",
                "The migration puts each profile's name, colour, start slot, last played and copy_of in its profile.json, keeping what the file held.");
        var after = File.ReadAllText(settingsPath);
        Require(File.ReadAllText(backup) == original && !after.Contains("\"profiles\"") && !after.Contains("Charlie") && after.Contains("p-00000002")
                && after.Contains("\"de\"") && LauncherSettings.Load(settingsPath).LegacyProfiles == null,
                "settings.json then holds no profile data (only the current profile's folder name and the other settings); the old file is kept beside it.");
        var again = LauncherSettings.Load(settingsPath);
        Require(ProfileMigration.MoveFromSettings(again, settingsPath, backup, env4, []) == null && File.ReadAllText(backup) == original
                && LauncherProfiles.Find(env4, "p-00000002") is { Name: "Ana (file)" },
                "The migration is done once: nothing more is written or backed up.");
    }

    // ---- the move of longshot-builder's versions into their version folders, once each, originals kept

    static void VersionMoveChecks(string root)
    {
        var data = Path.Combine(root, "data");
        var built = Path.Combine(root, "builds", "nightly", "nightly-70-aaaaaaaa");
        Write(Path.Combine(built, "longshot"), "game");
        Write(Path.Combine(built, LongshotBuilder.RecordFile), """{"format":1,"builder":"nightly-70-aaaaaaaa","game":{"nup":"/g.nup"}}""");
        Write(Path.Combine(built, "cache", "pipelines.bin"), "shaders");
        Write(Path.Combine(built, "user", "save", "x"), "a save in the game folder");
        Write(Path.Combine(built, LongshotBuilder.GameLockFile), "");
        var made = Path.Combine(root, "builds", "stable", "v3.1.0");
        Write(Path.Combine(made, "longshot"), "make_game's game");
        var builds = new List<BuildEntry>
        {
            new() { Id = "n1", Folder = built, Version = "nightly-70-aaaaaaaa", NupInPlace = true },
            new() { Id = "s1", Folder = made, Version = "v3.1.0" },
        };
        var plan = VersionFolderMigration.Plan(builds, data);
        var to = BuilderVersions.GameFolderOf(BuilderVersions.FolderOf(data, "nightly-70-aaaaaaaa"));
        Require(plan.Count == 1 && plan[0].Build.Id == "n1" && plan[0].To == to,
                "Only a version longshot-builder built moves into its version folder; make_game.py's stays where it is.");
        VersionFolderMigration.Copy(plan[0]);
        Require(File.ReadAllText(Path.Combine(to, "longshot")) == "game" && File.Exists(Path.Combine(to, "cache", "pipelines.bin"))
                && File.Exists(Path.Combine(to, LongshotBuilder.RecordFile)) && !Directory.Exists(Path.Combine(to, "user"))
                && !File.Exists(Path.Combine(to, LongshotBuilder.GameLockFile)) && !Directory.Exists(to + ".part"),
                "Its game folder is copied whole (its shader caches too) but for its user/ and the build's lock.");
        Require(File.Exists(Path.Combine(built, "longshot")) && File.Exists(Path.Combine(built, "user", "save", "x")), "The original stays where it is.");
        builds[0].Folder = to;
        Require(VersionFolderMigration.Plan(builds, data).Count == 0, "Once in its version folder, it is not moved again.");
        builds[0].Folder = built;
        Require(VersionFolderMigration.Plan(builds, data).Count == 0, "Nor when its version folder has a game folder already.");
    }
}
