using LongshotLauncher.Core;

/// Instances' folders (Core/InstanceFolders.cs, BuildRegistry.FolderOf, LauncherSettings.FolderFor), through the path a build
/// takes: a new instance goes in builds/<track>/<version> and make_game.py is given that folder (--out); a second instance of
/// the same version is "(2)"; an update or a go-back renames its folder to the new version's name before make_game.py starts,
/// everything in it kept, and back when the build does not finish; a rename that cannot happen changes nothing; a folder the
/// player chose before is never renamed; and the instances from before this layout move into it only when the player says so
static partial class Program
{
    /// Step 2 in its source: its Change picks the builds folder, Settings' own, never a folder for this one instance, and the
    /// build's folder is worked out from it (BuildJob.Place: BuildRegistry.FolderOf)
    static void StepTwoFolderSourceChecks(string repo)
    {
        var step = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "SettingsStepViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(repo, "App", "Views", "SettingsStepView.axaml"));
        var job = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "WizardViewModel.cs"));
        Require(!step.Contains("PickedFolder") && !step.Contains("Job.InstallDir =") && !view.Contains("ChangeInstallCommand"),
                "Step 2 has no way to pick a folder for this one instance.");
        Require(step.Contains("Job.Service.Settings.BuildsFolder = ") && step.Contains("Job.Place();") && view.Contains("ChangeBuildsCommand"),
                "Step 2's Change sets the builds folder, the same setting as Settings', and the build's folder is worked out again.");
        Require(job.Contains("InstallDir = s.Registry.FolderOf(") && job.Contains("s.BuildsFolder"),
                "The build's folder comes from the builds folder.");
    }

    /// The home's layout in its source, at 960x600 with a banner over it: the scroll bar has a gutter of its own (Fluent draws it over
    /// the content's edge), the title is on a row of its own so both columns start level and from the top, and every top banner is
    /// a title and one line, the rest under Details (a toggle, not a tooltip only: a touch screen reaches it)
    static void HomeLayoutSourceChecks(string repo)
    {
        var view = File.ReadAllText(Path.Combine(repo, "App", "Views", "HomeView.axaml"));
        Require(view.Contains("<ScrollViewer VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">")
                && view.Contains("<Panel Margin=\"0,0,16,0\">"),
                "The home's scrolled content keeps a gutter for the scroll bar.");
        Require(view.Contains("ColumnDefinitions=\"300,*\" RowDefinitions=\"Auto,Auto\"")
                && view.Contains("<StackPanel Grid.Row=\"1\" VerticalAlignment=\"Top\">")
                && view.Contains("<StackPanel Grid.Row=\"1\" Grid.Column=\"1\" VerticalAlignment=\"Top\""),
                "The title has a row of its own and both columns start from the top, level.");
        foreach (var more in new[] { "LauncherMore", "HookshotMore", "Nx2naviMore", "LeftoverMore", "RenameMore", "NoticeMore" })
            Require(view.Contains($"x:Name=\"{more}\" Classes=\"details\"") && view.Contains($"IsVisible=\"{{Binding !#{more}.IsChecked}}\""),
                    $"A home banner is one line with Details ({more}).");
        Require(System.Text.RegularExpressions.Regex.Matches(view, "MaxLines=\"1\" TextTrimming=\"CharacterEllipsis\"").Count >= 6,
                "Each banner's line is one line, cut with an ellipsis.");
    }

    static void InstanceFolderChecks()
    {
        var root = Folder("instances");
        var builds = Path.Combine(root, "Builds");
        Directory.CreateDirectory(builds);
        var p = Platform.Linux;
        string Game(string folder, string version)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, GameFolder.StampName), $"version={version}\n");
            return folder;
        }
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        string Read(string path) => File.ReadAllText(path);
        void Things(string folder)
        {
            Write(Path.Combine(folder, "user", "save", "slot_00", "progress.sav"), "adventure");
            Write(Path.Combine(folder, "cache", "pipelines.bin"), "caches");
            Write(Path.Combine(folder, "mods", "pack", "a.dds"), "mod");
            Write(Path.Combine(folder, "longshot_options.ini"), "fps=60");
        }
        bool HasThings(string folder) =>
            Read(Path.Combine(folder, "user", "save", "slot_00", "progress.sav")) == "adventure"
            && Read(Path.Combine(folder, "cache", "pipelines.bin")) == "caches"
            && Read(Path.Combine(folder, "mods", "pack", "a.dds")) == "mod"
            && Read(Path.Combine(folder, "longshot_options.ini")) == "fps=60";
        string[] None() => Array.Empty<string>();

        // ---- a new instance, through the path a build takes ----

        var home = new LauncherHome(Path.Combine(root, "home"));
        var lin = new Toolchain("v3.3.0", Path.Combine(home.Toolchains, "v3.3.0"), ListKind.Linux, null);
        string[] Out(string folder) => MakeGameCommand.For(lin, new BuildRequest(new BuildSource.Nup("/d/game.nup"), folder), p, home,
                                                            "/L/Longshot.AppImage", _ => false).Arguments.ToArray();
        bool OutIs(string folder) => Out(folder).SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() == folder;
        const string n200 = "nightly-200-aaaaaaaa", n201 = "nightly-201-bbbbbbbb", n205 = "nightly-205-cccccccc", n199 = "nightly-199-dddddddd";

        var listPath = Path.Combine(root, "builds.json");
        var registry = BuildRegistry.Load(listPath);
        var first = registry.FolderOf(null, builds, Versions.Nightly, n200, p);              // (the first nightly: Build)
        Require(first == Path.Combine(builds, "nightly", n200) && OutIs(first),
                "A first nightly is made in Builds/nightly/<its version>, and make_game.py is given that folder as it is (--out).");
        Game(first, n200);                                                                 // (make_game.py writes there: nx2/make_game.py out = --out)
        var one = registry.Add(first, n200, Versions.Nightly, name: n200, makeDefault: true);

        var second = registry.FolderOf(null, builds, Versions.Nightly, n201, p);             // (Build another version, the same track)
        Require(second == Path.Combine(builds, "nightly", n201) && OutIs(second),
                "Build another version on a track that has one: Builds/nightly/<that version>, no \"(2)\".");
        Game(second, n201);
        Things(second);
        var two = registry.Add(second, n201, Versions.Nightly, name: n201);
        var again = registry.FolderOf(null, builds, Versions.Nightly, n201, p);
        Require(again == Path.Combine(builds, "nightly", n201 + " (2)"), "The same version a second time: Builds/nightly/<version> (2), the only suffix.");
        Require(registry.FolderOf(null, builds, Versions.Stable, "v3.0.1", p) == Path.Combine(builds, "stable", "v3.0.1"),
                "Another track's first build: Builds/stable/v3.0.1.");
        registry.Save();

        // ---- an update: its folder renamed to the new version's before make_game.py starts ----

        Require(registry.FolderOf(two, builds, Versions.Nightly, n201, p) == second, "A rebuild of the same version keeps its folder as it is.");
        var update = registry.FolderOf(two, builds, Versions.Nightly, n205, p);
        Require(update == Path.Combine(builds, "nightly", n205) && OutIs(update), "An update's folder: its new version's name, make_game.py given that.");
        var rename = new InstanceFolders.Move(two, two.Folder, update);
        InstanceFolders.Rename(registry, rename, p, running: None);                         // (BuildStepViewModel.RenameFolder)
        Require(!Directory.Exists(second) && GameFolder.IsGameFolder(update, p) && HasThings(update) && two.Folder == update,
                "Renamed, not copied: its game, save, shader caches, mods and settings are in the new folder, and the row follows.");
        Require(BuildRegistry.Load(listPath).Find(two.Id)?.Folder == update, "The saved list has the new folder before the build runs.");
        registry.Replace(two.Id, update, n205, Versions.Nightly);                          // (the build finished: BuildStepViewModel.Complete)
        Require(two.Folder == update && two.Version == n205 && two.Name == n205, "Finished: the row has the new version, its name and its folder.");

        // ---- a go-back: renamed down; a build that fails: back to its old name ----

        var down = registry.FolderOf(two, builds, Versions.Nightly, n199, p);
        Require(down == Path.Combine(builds, "nightly", n199), "Going back: the older version's name.");
        var goBack = new InstanceFolders.Move(two, two.Folder, down);
        InstanceFolders.Rename(registry, goBack, p, running: None);
        Require(two.Folder == down && HasThings(down), "Going back renames it too.");
        Require(InstanceFolders.MoveBack(registry, goBack, TimeSpan.Zero), "The build failed or was stopped: it goes back.");
        Require(two.Folder == update && Directory.Exists(update) && !Directory.Exists(down) && HasThings(update) && two.Version == n205,
                "Back to the name of the version the row still has, everything in it.");
        Require(BuildRegistry.Load(listPath).Find(two.Id)?.Folder == update, "And the saved list says so.");
        Require(one.Folder == first && Directory.Exists(first), "The other instance is untouched.");

        // ---- a rename that cannot happen changes nothing ----

        var blockedTo = registry.FolderOf(two, builds, Versions.Nightly, n201, p);
        Throws<IOException>(() => InstanceFolders.Rename(registry, new InstanceFolders.Move(two, two.Folder, blockedTo), p, running: () => new[] { "Longshot" }),
                            "Not while Longshot runs.");
        Directory.CreateDirectory(blockedTo);
        Throws<IOException>(() => InstanceFolders.Rename(registry, new InstanceFolders.Move(two, two.Folder, blockedTo), p, running: None),
                            "Not onto a folder that is there.");
        Directory.Delete(blockedTo);
        Require(two.Folder == update && Directory.Exists(update) && HasThings(update), "Refused: nothing changed.");
        if (OperatingSystem.IsWindows())
        {
            // (a file in it held open, as a File Explorer window or an editor holds one: Windows refuses the rename)
            var held = new FileStream(Path.Combine(update, "longshot_options.ini"), FileMode.Open, FileAccess.Read, FileShare.None);
            try
            {
                Throws<IOException>(() => InstanceFolders.Rename(registry, new InstanceFolders.Move(two, two.Folder, blockedTo), p, running: None),
                                    "A file held open in it refuses the rename.");
                Require(two.Folder == update && Directory.Exists(update) && !Directory.Exists(blockedTo), "And nothing changed.");
            }
            finally { held.Dispose(); }
        }
        // (MoveBack waits for a file a stopped build still holds a moment: released at its first wait, it goes back)
        InstanceFolders.Rename(registry, goBack, p, running: None);
        var waits = 0;
        FileStream? holding = OperatingSystem.IsWindows()
            ? new FileStream(Path.Combine(down, "longshot_options.ini"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        Require(InstanceFolders.MoveBack(registry, goBack with { From = update }, TimeSpan.FromSeconds(5), _ => { waits++; holding?.Dispose(); }),
                "A file the stopped build held a moment: it goes back once it is let go.");
        holding?.Dispose();
        Require(two.Folder == update && Directory.Exists(update) && (!OperatingSystem.IsWindows() || waits > 0), "Back, after waiting for it on Windows.");

        // ---- a folder the player chose before: never renamed ----

        var mine = registry.Add(Game(Path.Combine(root, "My games", "Longshot"), "v3.0.0"), "v3.0.0", Versions.Stable);
        Require(registry.FolderOf(mine, builds, Versions.Stable, "v3.0.1", p) == mine.Folder, "An instance in a folder the player chose keeps it.");
        Require(!InstanceFolders.InLayout(mine.Folder, builds) && InstanceFolders.InLayout(two.Folder, builds)
                && !InstanceFolders.InLayout(Path.Combine(builds, "nightly"), builds),
                "In the layout: builds/<track>/<name> only.");

        // ---- the folders the leftovers and the gather look through ----

        var looked = InstanceFolders.Folders(builds, p);
        Require(looked.Contains(first) && looked.Contains(update) && looked.Contains(Path.Combine(builds, "nightly")),
                "They look in the tracks' folders too.");
        // (a game in the layout whose row went is listed again: UnlistedChecks)

        // ---- the instances from before this layout: what the home offers to move ----

        builds = Path.Combine(root, "OldBuilds");                                       // (a builds folder of its own)
        Directory.CreateDirectory(builds);
        var oldList = Path.Combine(root, "old.json");
        var old = BuildRegistry.Load(oldList);
        var rc5 = old.Add(Game(Path.Combine(builds, "v3.0.0-rc.5"), "v3.0.0"), "v3.0.0", Versions.Stable, name: "v3.0.0", makeDefault: true);
        Things(rc5.Folder);
        var night = old.Add(Game(Path.Combine(builds, "nightly"), "nightly-180-eeeeeeee"), "nightly-180-eeeeeeee", Versions.Nightly);
        Things(night.Folder);
        var night2 = old.Add(Game(Path.Combine(builds, "nightly (2)"), "nightly-181-ffffffff"), "nightly-181-ffffffff", Versions.Nightly);
        Game(Path.Combine(builds, "rc", "v3.1.0-rc.1"), "v3.1.0-rc.1");                   // (an unlisted game folder: its name is taken)
        var rc1 = old.Add(Game(Path.Combine(builds, "v3.1.0-rc.1-old"), "v3.1.0-rc.1"), "v3.1.0-rc.1", Versions.Candidate);
        var kept = old.Add(Game(Path.Combine(builds, "v3.0.0-rc.2"), "v3.0.0-rc.2"), "v3.0.0-rc.2", Versions.Candidate);
        var elsewhere = old.Add(Game(Path.Combine(root, "Elsewhere", "v3.0.0"), "v3.0.0"), "v3.0.0", Versions.Stable);
        var gone = old.Add(Path.Combine(builds, "v3.0.0-rc.1"), "v3.0.0-rc.1", Versions.Candidate);
        old.Save();

        var plan = InstanceFolders.Plan(old, builds, p, kept: new[] { kept.Folder });
        string Shown(IEnumerable<InstanceFolders.Move> moves) => string.Join(", ", moves.Select(m => m.FromName + " -> " + m.ToName));
        var s = Path.DirectorySeparatorChar;
        Require(Shown(plan) == $"nightly -> nightly{s}nightly-180-eeeeeeee, nightly (2) -> nightly{s}nightly-181-ffffffff, "
                + $"v3.0.0-rc.5 -> stable{s}v3.0.0, v3.1.0-rc.1-old -> rc{s}v3.1.0-rc.1 (2)",
                "Offered: both old shapes, the track-named first, each to builds/<track>/<version> past every taken name (got: " + Shown(plan) + ").");
        Require(plan.All(m => m.Build != kept && m.Build != elsewhere && m.Build != gone),
                "Not offered: one the player kept, one in a folder of their own choosing, one whose folder is gone.");
        Require(InstanceFolders.Plan(old, builds, p).Any(m => m.Build == kept), "A kept one is offered again only once it is no longer kept.");

        // (moving them, in the plan's order; one that would go inside another instance's folder waits for it)
        Throws<IOException>(() => InstanceFolders.Rename(old, plan[1], p, running: None),
                            "nightly (2) cannot go into nightly while nightly is still an instance's own folder.");
        Require(night2.Folder == plan[1].From && Directory.Exists(plan[1].From), "Refused: nothing changed.");
        var data = Path.Combine(rc5.Folder, "share");
        Throws<IOException>(() => InstanceFolders.Rename(old, plan[2], p, new SaveEnvironment(p, k => k == "XDG_DATA_HOME" ? data : null), None),
                            "Not a folder that holds Longshot's shared saves.");
        Throws<IOException>(() => InstanceFolders.Rename(old, plan[2], p, running: None, getenv: k => k == "HOME" ? Path.Combine(rc5.Folder, "home") : null),
                            "Not a folder that holds the home folder.");
        Throws<IOException>(() => InstanceFolders.Rename(old, plan[2] with { From = night2.Folder }, p, running: None),
                            "Not when the row's folder is not the one planned.");
        foreach (var m in plan) InstanceFolders.Rename(old, m, p, running: None);
        Require(GameFolder.IsGameFolder(Path.Combine(builds, "nightly", "nightly-180-eeeeeeee"), p)
                && HasThings(Path.Combine(builds, "nightly", "nightly-180-eeeeeeee"))
                && !GameFolder.IsGameFolder(Path.Combine(builds, "nightly"), p) && night.Folder == Path.Combine(builds, "nightly", "nightly-180-eeeeeeee"),
                "builds/nightly became the nightly's folder: the instance that was it is in it, everything kept.");
        Require(!Directory.Exists(Path.Combine(builds, "nightly (2)")) && night2.Folder == Path.Combine(builds, "nightly", "nightly-181-ffffffff")
                && rc5.Folder == Path.Combine(builds, "stable", "v3.0.0") && HasThings(rc5.Folder) && rc1.Folder == Path.Combine(builds, "rc", "v3.1.0-rc.1 (2)"),
                "Each in its track's folder, named after its version.");
        var reloaded = BuildRegistry.Load(oldList).Find(rc5.Id);
        Require(reloaded is { Default: true, Version: "v3.0.0", Name: "v3.0.0" } && reloaded.Folder == rc5.Folder,
                "The saved list has the new folders; the rows are otherwise as they were (default, version, name).");
        Require(InstanceFolders.Plan(old, builds, p, kept: new[] { kept.Folder }).Count == 0, "Not offered again once moved.");
        Require(old.FolderOf(rc5, builds, Versions.Stable, "v3.0.1", p) == Path.Combine(builds, "stable", "v3.0.1"),
                "Moved, it is in the layout: its next update renames it to its new version.");

        // (a move into its own folder, undone: builds/nightly back as it was, an instance's own folder again)
        var undoBuilds = Path.Combine(root, "UndoBuilds");
        var undoList = BuildRegistry.Load(Path.Combine(root, "undo.json"));
        var lone = undoList.Add(Game(Path.Combine(undoBuilds, "nightly"), "nightly-170-12345678"), "nightly-170-12345678", Versions.Nightly);
        Things(lone.Folder);
        var into = InstanceFolders.Plan(undoList, undoBuilds, p).Single();
        InstanceFolders.Rename(undoList, into, p, running: None);
        Require(lone.Folder == Path.Combine(undoBuilds, "nightly", "nightly-170-12345678") && HasThings(lone.Folder), "Moved into its own folder.");
        Require(InstanceFolders.MoveBack(undoList, into, TimeSpan.Zero) && lone.Folder == Path.Combine(undoBuilds, "nightly")
                && GameFolder.IsGameFolder(lone.Folder, p) && HasThings(lone.Folder)
                && !Directory.Exists(Path.Combine(undoBuilds, "nightly", "nightly-170-12345678")),
                "And back: the track's folder is the instance's own again, everything in it.");

        // (the list cannot be saved: the folder goes back, and the row)
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "a file where the list's folder would be");
        var stuck = BuildRegistry.Load(Path.Combine(blocked, "builds.json"));
        var row = stuck.Add(Game(Path.Combine(builds, "v3.2.0"), "v3.2.0"), "v3.2.0", Versions.Stable);
        var move = InstanceFolders.Plan(stuck, builds, p).Single();
        Throws<IOException>(() => InstanceFolders.Rename(stuck, move, p, running: None), "A list that cannot be saved refuses the move.");
        Require(Directory.Exists(move.From) && !Directory.Exists(move.To) && row.Folder == move.From
                && !Directory.Exists(Path.Combine(builds, "stable", "v3.2.0")),
                "And the folder and the row are back as they were.");
    }
}
