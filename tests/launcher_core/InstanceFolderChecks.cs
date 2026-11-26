using LongshotLauncher.Core;

/// Instances' folders (Core/InstanceFolders.cs, BuildRegistry.FolderOf, LauncherSettings.FolderFor), through the path a build
/// takes: a new instance goes in builds/<track>/<version> and the builder is given that folder (--out); a second instance of
/// the same version is "(2)"; an update or a go-back renames its folder to the new version's name before the builder starts,
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

    /// The home's layout in its source: it fits the window, its list of saves scrolling inside it. Home only plays: the brand's band, a strip, and the card at the foot,
    /// Play (the go button) beside the version (the versions on this PC, then Manage versions…), the profile (mods and settings)
    /// and the save to start on, an update on the band when there is one; no track, no list of versions. The first build's card is one view, Home's and the Versions page's
    /// empty state. Every top card is one style (Border.offer: an icon in a circle, a title, its whole text, a quiet link and one
    /// strong button), nothing cut to a line; the old-layout offer's list of folders alone is under a toggle (not a tooltip only:
    /// a touch screen reaches it)
    static void HomeLayoutSourceChecks(string repo)
    {
        var view = File.ReadAllText(Path.Combine(repo, "App", "Views", "HomeView.axaml"));
        var versions = File.ReadAllText(Path.Combine(repo, "App", "Views", "VersionsView.axaml"));
        // (Home is a front page, not a dashboard: the band, the Version and Save pickers and one button, centred; no list of saves,
        // no profile card, no tiles, no update card: the profile is the title bar's, an update is the button's)
        Require(view.Contains("<Border Classes=\"homepage\">") && view.Contains("<StackPanel Classes=\"front\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Center\">")
                && !view.Contains("Name=\"HomeScroll\"") && !view.Contains("ScrollViewer")
                && !view.Contains("{Binding SaveRows}") && !view.Contains("{Binding ProfileMods}") && !view.Contains("Classes=\"infotile\"")
                && !view.Contains("{Binding BandKicker}") && !view.Contains("{Binding BandNote}") && !view.Contains("Classes=\"updatecard\""),
                "Home is the band, the two pickers and the button, centred, with nothing else to read.");
        Require(view.Contains("<Button Classes=\"go bigplay\" Command=\"{Binding HeroPlayCommand}\" IsVisible=\"{Binding HeroIsPlayOnly}\" c:Pad.First=\"True\">")
                && view.Contains("<Button Classes=\"go bigplay\" Command=\"{Binding BuildCommand}\" IsVisible=\"{Binding NoBuilds}\" c:Pad.First=\"True\">")
                && view.Contains("<Button Classes=\"primary bigplay\" Command=\"{Binding HeroPlayCommand}\" IsVisible=\"{Binding HeroIsRebuild}\" c:Pad.First=\"True\">")
                && view.Contains("<Button Classes=\"second bigplay\" Command=\"{Binding UpdateDefaultCommand}\" IsVisible=\"{Binding HeroIsPlayAndUpdate}\">")
                && !view.Contains("Classes=\"link\" Command=\"{Binding HeroPlayCommand}\"")
                && view.Contains("ItemsSource=\"{Binding HeroChoices}\"") && view.Contains("ItemsSource=\"{Binding BuildChoices}\"")
                && !view.Contains("ItemsSource=\"{Binding ProfileChoices}\"")
                && view.Contains("ItemsSource=\"{Binding SaveChoices}\"") && view.Contains("IsEnabled=\"{Binding HasBuilds}\""),
                "Home's button: Build, Build it again, or Play, and Update as big beside Play; the Version picker (the version a build makes with none yet) and the Save picker (off with none).");
        // (the Save picker is the profile's slots, the newest picked, then Manage saves…: no Choose in game; New game alone with
        // none; a version that cannot start on a slot hides it, one quiet line in its place)
        var homeVm = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "HomeViewModel.cs"));
        Require(!homeVm.Contains("Tr.T(\"Choose in game\")") && homeVm.Contains("new SaveStartChoice(null, Tr.T(\"New game\")")
                && homeVm.Contains("public bool ShowSavePicker => NoBuilds || HeroStartsOnSlot;")
                && view.Contains("IsVisible=\"{Binding ShowSavePicker}\"") && view.Contains("IsVisible=\"{Binding ShowSaveTitleNote}\""),
                "Home's Save picker lists the profile's slots (New game with none), hidden for a version that cannot start on one.");
        Require(!view.Contains("{Binding Tracks}") && !view.Contains("{Binding Builds}") && !view.Contains("{Binding TrackVersions}"),
                "Home has no track and no list of versions: they are the Versions page's.");
        Require(!view.Contains("BuildCardView") && versions.Contains("<views:BuildCardView IsVisible=\"{Binding NoBuilds}\"/>"),
                "The first build is Home's own state (its button says Build); the card is the Versions page's empty state.");
        Require(view.Contains("IsVisible=\"{Binding HasStatusLine}\"") && view.Contains("Text=\"{Binding StatusLine}\""),
                "One status line under the button, only when it must be said.");
        foreach (var has in new[] { "HasLauncherUpdate", "HasHookshotOffer", "HasNx2naviOffer", "HasInstalledOffer", "HasLeftoverOffer", "HasRenameOffer" })
            Require(view.Contains($"<Border DockPanel.Dock=\"Top\" Classes=\"offer\" IsVisible=\"{{Binding {has}}}\">"),
                    $"A home card has the one card style ({has}).");
        Require(view.Contains("<Border DockPanel.Dock=\"Top\" Classes=\"offer warn\" IsVisible=\"{Binding HasNotice}\">"),
                "A problem is the same card, in the warning's colour.");
        Require(!view.Contains("MaxLines=\"1\" TextTrimming=\"CharacterEllipsis\""), "No card's text is cut to one line.");
        Require(view.Contains("x:Name=\"RenameMore\" Classes=\"details\"") && view.Contains("IsVisible=\"{Binding #RenameMore.IsChecked}\""),
                "The old-layout offer's folders are under a toggle.");
        Require(System.Text.RegularExpressions.Regex.Matches(view, "<Button Classes=\"small primary\"").Count >= 6,
                "Each offer has one strong button.");
        HomeUpdateSourceChecks(repo, view, versions);
    }

    /// A newer version shows once in each place: on Home, for the version shown, one line (its newest, What's new, Update); on the
    /// Versions page, on its card (Update available, Update to vX, What's new) and in the Version list of Build another version
    /// (Update available in place of the note; on the caption's row, so the version is never cut). The track's newest opposite
    /// the title of neither page
    static void HomeUpdateSourceChecks(string repo, string view, string versions)
    {
        var home = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "HomeViewModel.cs"));
        var app = File.ReadAllText(Path.Combine(repo, "App", "App.axaml"));
        Require(view.Contains("Command=\"{Binding UpdateDefaultCommand}\"")
                && home.Contains("UpdateDefaultCommand = new Command(() => { if (HeroItem is { } d) UpdateBuild(d); }")
                && home.Contains("public bool HeroIsUpdate => HeroItem is { IsBuilt: true, HasUpdate: true, IsDeleting: false };"),
                "Home's update is its button for the version shown: Update, with Play beside it.");
        Require(versions.Contains("Command=\"{Binding UpdateToCommand}\" IsVisible=\"{Binding ShowUpdateTo}\"")
                && versions.Contains("Command=\"{Binding WhatsNewCommand}\" IsVisible=\"{Binding ShowUpdateTo}\"")
                && versions.Contains("Command=\"{Binding RebuildCommand}\" IsVisible=\"{Binding ShowRebuild}\""),
                "A version's card has its own Update to, What's new, and Build it again when its game is missing.");
        var add = File.ReadAllText(Path.Combine(repo, "App", "Views", "AddVersionView.axaml"));
        var addVm = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "AddVersionViewModel.cs"));
        Require(versions.Contains("Command=\"{Binding AddVersionCommand}\"") && !versions.Contains("TrackVersions")
                && add.Contains("ItemsSource=\"{Binding Channels}\"") && add.Contains("ItemsSource=\"{Binding ChannelVersions}\"")
                && add.Contains("IsVisible=\"{Binding Installed}\"") && add.Contains("Text=\"{Binding Note}\"")
                && addVm.Contains("Channels = ChannelPickerViewModel.All.Select(") && app.Contains("<Style Selector=\"Border.chip.update\">"),
                "Add version: Stable and Nightly, each with its newest, and the channel's versions with a check beside an installed one.");
        Require(add.Contains("IsVisible=\"{Binding AdvancedOpen}\"") && add.Contains("IsChecked=\"{Binding IsMain}\"") && add.Contains("IsChecked=\"{Binding IsPull}\"")
                && add.Contains("IsVisible=\"{Binding IsDraft}\"") && add.Contains("<TextBlock Text=\"WIP\"/>") && add.Contains("IsVisible=\"{Binding ShowsPullWarning}\"")
                && add.Contains("IsVisible=\"{Binding ShowsMainNote}\"") && addVm.Contains("public bool AdvancedClosed => !advanced;")
                && !addVm.Contains("advanced = true") && !home.Contains("InstalledOn("),
                "Its Advanced, closed until opened: main and the pull requests (WIP ones marked), each with its warning.");
        Require(home.Contains("NotesDefaultCommand = new Command(() => { if (TrackUpdateItem is { } d) ShowNotes(d); });")
                && !home.Contains("is out on the {1} channel"),
                "What's new is for the build that has the update, by its shown name.");
        // (what it says of their sizes: InstancesUseChecks)
        var disk = File.ReadAllText(Path.Combine(repo, "Core", "Disk.cs"));
        Require(home.Contains("public string Footer => Disk.InstancesUse(")
                && disk.Contains("Tr.P(n, \"1 version uses {1} in {2}.\", \"{0} versions use {1} in {2}.\"")
                && disk.Contains("Tr.P(n, \"1 version in {1}.\", \"{0} versions in {1}.\"")
                && versions.Contains("Text=\"{Binding Footer}\""),
                "The Versions page's footer counts versions.");
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
        var lin = new Toolchain("v3.3.0", Path.Combine(home.Toolchains, "v3.3.0"), LongshotBuilder.Name);
        string[] Out(string folder) => new LongshotBuilder(lin, lin.BuilderExe, p, home, "/L/Longshot.AppImage", _ => null)
            .Build(new BuildRequest(new BuildSource.Nup("/d/game.nup"), folder)).Command.Arguments.ToArray();
        bool OutIs(string folder) => Out(folder).SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() == folder;
        const string n200 = "nightly-200-aaaaaaaa", n201 = "nightly-201-bbbbbbbb", n205 = "nightly-205-cccccccc", n199 = "nightly-199-dddddddd";

        var listPath = Path.Combine(root, "builds.json");
        var registry = BuildRegistry.Load(listPath);
        var first = registry.FolderOf(null, builds, Versions.Nightly, n200, p);              // (the first nightly: Build)
        Require(first == Path.Combine(builds, "nightly", n200) && OutIs(first),
                "A first nightly is made in Builds/nightly/<its version>, and the builder is given that folder as it is (--out).");
        Game(first, n200);                                                                 // (the builder writes there: --out)
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

        // ---- an update: its folder renamed to the new version's before the builder starts ----

        Require(registry.FolderOf(two, builds, Versions.Nightly, n201, p) == second, "A rebuild of the same version keeps its folder as it is.");
        var update = registry.FolderOf(two, builds, Versions.Nightly, n205, p);
        Require(update == Path.Combine(builds, "nightly", n205) && OutIs(update), "An update's folder: its new version's name, the builder given that.");
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
        // (the game files picked in the folder being renamed, its own romfs and .longshot_exefs: they follow it. Before, the rename
        // left the builder the old path, and it stopped: "does not hold the game's files")
        string In(string folder, string name) => Path.Combine(folder, name);
        var dump = Path.Combine(root, "dump");
        two.Romfs = In(update, "romfs");
        two.Exefs = In(update, ".longshot_exefs");
        one.Romfs = In(update, "romfs");                                                   // (another row made from the same files)
        one.Exefs = In(dump, "exefs");                                                     // (and from files elsewhere)
        var remembered = new LauncherSettings { Romfs = In(update, "romfs"), Exefs = In(update, ".longshot_exefs") };
        var picked = new FoundGameFile(update, "Last used", 0, DateTime.UtcNow, true, In(update, "romfs"), In(update, ".longshot_exefs"));
        InstanceFolders.Rename(registry, goBack, p, running: None);
        Require(two.Folder == down && HasThings(down), "Going back renames it too.");
        Require(two.Romfs == In(down, "romfs") && two.Exefs == In(down, ".longshot_exefs") && one.Romfs == In(down, "romfs")
                && one.Exefs == In(dump, "exefs"),
                "Game files a row was made from that were in the renamed folder follow it; files elsewhere stay as they were.");
        Require(BuildRegistry.Load(listPath).Find(two.Id)?.Romfs == In(down, "romfs"), "And the saved list says so.");
        Require(remembered.Follow(update, down) && remembered.Romfs == In(down, "romfs") && remembered.Exefs == In(down, ".longshot_exefs")
                && !remembered.Follow(update, down),
                "The game files last picked (the next build's Last used) follow it too, once.");
        Require(picked.Follow(update, down) is { Source: BuildSource.Folders { Romfs: var r, Exefs: var x } } moved && moved.Path == down
                && r == In(down, "romfs") && x == In(down, ".longshot_exefs") && picked.Follow(second, down) == picked,
                "The build's own pick is read from the renamed folder (its dump folder); one elsewhere as it was.");
        Require(InstanceFolders.Follow(In(update + "-other", "romfs"), update, down) == In(update + "-other", "romfs")
                && InstanceFolders.Follow(update, update, down) == down && InstanceFolders.Follow(null, update, down) == null,
                "Only paths in the folder follow it (not a folder whose name starts the same); the folder itself does.");
        Require(InstanceFolders.MoveBack(registry, goBack, TimeSpan.Zero), "The build failed or was stopped: it goes back.");
        Require(two.Folder == update && Directory.Exists(update) && !Directory.Exists(down) && HasThings(update) && two.Version == n205,
                "Back to the name of the version the row still has, everything in it.");
        Require(two.Romfs == In(update, "romfs") && one.Romfs == In(update, "romfs"), "Its game files go back with it.");
        Require(BuildRegistry.Load(listPath).Find(two.Id)?.Folder == update, "And the saved list says so.");
        one.Romfs = one.Exefs = two.Romfs = two.Exefs = null;
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
        Game(Path.Combine(builds, "stable", "v3.1.0"), "v3.1.0");                         // (an unlisted game folder: its name is taken)
        var rc1 = old.Add(Game(Path.Combine(builds, "v3.1.0-old"), "v3.1.0"), "v3.1.0", Versions.Stable);
        var kept = old.Add(Game(Path.Combine(builds, "v3.0.2"), "v3.0.2"), "v3.0.2", Versions.Stable);
        var elsewhere = old.Add(Game(Path.Combine(root, "Elsewhere", "v3.0.0"), "v3.0.0"), "v3.0.0", Versions.Stable);
        var gone = old.Add(Path.Combine(builds, "v3.0.1"), "v3.0.1", Versions.Stable);
        old.Save();

        var plan = InstanceFolders.Plan(old, builds, p, kept: new[] { kept.Folder });
        string Shown(IEnumerable<InstanceFolders.Move> moves) => string.Join(", ", moves.Select(m => m.FromName + " -> " + m.ToName));
        var s = Path.DirectorySeparatorChar;
        Require(Shown(plan) == $"nightly -> nightly{s}nightly-180-eeeeeeee, nightly (2) -> nightly{s}nightly-181-ffffffff, "
                + $"v3.0.0-rc.5 -> stable{s}v3.0.0, v3.1.0-old -> stable{s}v3.1.0 (2)",
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
                && rc5.Folder == Path.Combine(builds, "stable", "v3.0.0") && HasThings(rc5.Folder) && rc1.Folder == Path.Combine(builds, "stable", "v3.1.0 (2)"),
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
