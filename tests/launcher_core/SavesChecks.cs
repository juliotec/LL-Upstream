using System.Text;
using LongshotLauncher.Core;

/// launcher/Core/Saves.cs over tests/test_longshot_saves.py's vectors (the same folders, the same expectations), so the launcher and
/// scripts/longshot_saves.py agree. Fake saves made here only: HOME, XDG_DATA_HOME, APPDATA and LOCALAPPDATA all point into one folder.
static class SavesChecks
{
    public static int Count;
    const string Uid = "0123456789abcdef0123456789abcdef";
    const string Profile = "FEDCBA9876543210FEDCBA9876543210";
    const string Account = "00112233445566778899aabbccddeeff";

    static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("saves: " + message);
        Count++;
    }

    static void Fails(Action action, string contains, string message)
    {
        try { action(); }
        catch (SaveFailure e)
        {
            Require(e.Message.Contains(contains), $"{message} (said: {e.Message})");
            return;
        }
        throw new Exception("saves: not refused: " + message);
    }

    static string P(params string[] parts) => Path.Combine(parts);

    static void MakeSave(string folder, string tag, params string[] slots)
    {
        if (slots.Length == 0) slots = new[] { "slot_00", "slot_01" };
        Directory.CreateDirectory(folder);
        File.WriteAllText(P(folder, "account.sav"), "account-" + tag);
        File.WriteAllText(P(folder, "option.sav"), "option-" + tag);
        foreach (var s in slots.Where(s => s != "-"))
        {
            Directory.CreateDirectory(P(folder, s));
            File.WriteAllText(P(folder, s, "progress.sav"), "progress-" + tag + s);
            File.WriteAllText(P(folder, s, "caption.sav"), "caption-" + tag + s);
        }
    }

    static string Tree(string folder) =>
        !Directory.Exists(folder) ? "" : string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f) + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal));

    static string EdenSave(string data, string profile = Profile) => P(data, "nand", "user", "save", Saves.NoAccount, profile, Saves.Title);

    sealed class Case
    {
        public readonly string Tmp, Home, Data, AppData, LocalAppData, Hs, Li, Backups;
        public readonly SaveEnvironment Linux, Windows;
        public IReadOnlyList<string> Busy = Array.Empty<string>();

        public Case(string root, string name)
        {
            Tmp = P(root, name);
            Home = P(Tmp, "home");
            Data = P(Home, ".local", "share");
            AppData = P(Home, "AppData", "Roaming");
            LocalAppData = P(Home, "AppData", "Local");
            Hs = P(Data, "longshot", "save", Uid);
            Li = EdenSave(P(Data, "eden"));
            Backups = P(Tmp, "backups");
            MakeSave(Hs, "HS");
            MakeSave(Li, "LI", "slot_00");
            string? Env(string k) => k switch
            {
                "HOME" or "USERPROFILE" => Home, "XDG_DATA_HOME" => Data, "APPDATA" => AppData, "LOCALAPPDATA" => LocalAppData, _ => null,
            };
            Linux = new SaveEnvironment(Platform.Linux, Env);
            Windows = new SaveEnvironment(Platform.Windows, Env);
        }

        public IReadOnlyList<string> BackupDirs() =>
            Directory.Exists(Backups) ? Directory.EnumerateDirectories(Backups).Order(StringComparer.Ordinal).ToList() : Array.Empty<string>();

        /// longshot_saves.main import|export with the name or folder: Longshot's side on Linux
        public SaveCopy Run(bool import, string? name, string? folder = null, string? profile = null, bool keep = false, bool dry = false, bool force = false)
        {
            var other = name == null ? Saves.Folder : Saves.Named(name);
            var hs = Saves.Pick(Saves.Locate(Saves.Longshot, null, Linux).Profiles, "Longshot");
            var em = Saves.Pick(Saves.Locate(other, folder, Linux).Profiles, other.Display, profile);
            var (src, dst) = import ? (em.Path, hs.Path) : (hs.Path, em.Path);
            return Saves.Copy(src, dst, Backups, import ? "import" : "export", other.Name, keep, dry, force, () => Busy);
        }
    }

    public static void Run(string root)
    {
        Discovery(root);
        Copies(root);
        FirstImports(root);
        Nx2navi(root);
        Guard(root);
        Builds(root);
    }

    static void Discovery(string root)
    {
        // (test_every_entry_on_linux)
        var c = new Case(root, "linux");
        MakeSave(EdenSave(P(c.Data, "eglantine")), "EG");
        var nx = P(c.Home, "Games", "Ocarina", "profile", "save", Account);
        MakeSave(nx, "NX");
        var expect = new Dictionary<string, string>
        {
            ["lichii"] = c.Li, ["frizti"] = c.Li, ["orchid"] = c.Li, ["pokedinner"] = c.Li, ["eden"] = c.Li,
            ["eglantine"] = EdenSave(P(c.Data, "eglantine")), ["nx2navi"] = nx,
        };
        Require(expect.Keys.Order().SequenceEqual(Saves.Emulators.Select(p => p.Name).Order()), "Every emulator of the script.");
        foreach (var program in Saves.Emulators)
            Require(Saves.Locate(program, null, c.Linux).Profiles.Select(p => p.Path).SequenceEqual(new[] { expect[program.Name] }), program.Name + " on Linux");
        Require(Saves.Locate(Saves.Longshot, null, c.Linux).Profiles.SequenceEqual(new[] { new SaveProfile(Uid, c.Hs) }), "Longshot on Linux");

        // (test_every_entry_on_windows)
        var w = new Case(root, "windows");
        var winLi = EdenSave(P(w.AppData, "eden"));
        var winEg = EdenSave(P(w.AppData, "eglantine"));
        var winNx = P(w.LocalAppData, "Programs", "NX2navi", "game", "profile", "save", Account);
        foreach (var f in new[] { winLi, winEg, winNx }) MakeSave(f, "W");
        var wexpect = new Dictionary<string, string>
        {
            ["lichii"] = winLi, ["frizti"] = winLi, ["orchid"] = winLi, ["pokedinner"] = winLi, ["eden"] = winLi, ["eglantine"] = winEg, ["nx2navi"] = winNx,
        };
        foreach (var program in Saves.Emulators)
            Require(Saves.Locate(program, null, w.Windows).Profiles.Select(p => p.Path).SequenceEqual(new[] { wexpect[program.Name] }), program.Name + " on Windows");
        Fails(() => Saves.Locate(Saves.Longshot, null, w.Windows), "No Longshot save found", "Windows: no shared save yet");
        Require(!Saves.Shared(w.Windows).Found && Saves.Shared(w.Windows).Problem != null, "Windows: no shared save yet, and why.");
        var game = P(w.Tmp, "Longshot");
        MakeSave(P(game, "user", "save", Uid), "W");
        Require(Saves.Locate(Saves.Longshot, game, w.Windows).Profiles.SequenceEqual(new[] { new SaveProfile(Uid, P(game, "user", "save", Uid)) }),
                "A Windows game folder of before, chosen.");
        var winShared = P(w.AppData, "Longshot", "save", Uid);
        MakeSave(winShared, "S");
        Require(w.Windows.LongshotUser == P(w.AppData, "Longshot") && Saves.Locate(Saves.Longshot, null, w.Windows).Profiles.Single().Path == winShared
                && Saves.Shared(w.Windows).Profiles.Single().Path == winShared && Saves.SharedSavePath(w.Windows, Uid) == winShared,
                "Windows: the save every build shares, in %APPDATA%\\Longshot.");
        Require((w.Windows with { UserFolder = P(w.Tmp, "usb", "user") }).LongshotUser == P(w.Tmp, "usb", "user")
                && w.Linux.LongshotUser == P(w.Data, "longshot") && (w.Linux with { UserFolder = P(w.Tmp, "usb", "user") }).LongshotUser == P(w.Data, "longshot")
                && Saves.Shared(w.Linux).Profiles.Single().Path == w.Hs, "A portable launcher's own user folder (Windows); on Linux the game's own.");

        // (Linux: a Longshot that keeps its user files apart moves them into longshot/user at its first start, given --user
        // longshot as before; the launcher reads them there from then on, and still passes the same folder to every build)
        var u = new Case(root, "linux-user");
        var linuxRoot = P(u.Data, "longshot");
        Require(u.Linux.LongshotRoot == linuxRoot && u.Linux.LongshotUser == linuxRoot && u.Linux.LinuxUserFiles == P(linuxRoot, "user")
                && GamePlay.UserFolder(u.Linux, P(u.Tmp, "game")) == linuxRoot && !Directory.Exists(u.Linux.LinuxUserFiles),
                "Linux, before a newer Longshot ran: the folder itself, and no user/ made by the launcher.");
        Directory.CreateDirectory(P(linuxRoot, "user"));
        Directory.Move(P(linuxRoot, "save"), P(linuxRoot, "user", "save"));
        var moved = P(linuxRoot, "user", "save", Uid);
        Require(u.Linux.LongshotUser == P(linuxRoot, "user") && u.Linux.LongshotRoot == linuxRoot && u.Linux.SaveFolder == P(linuxRoot, "user", "save")
                && Saves.Shared(u.Linux).Profiles.Single().Path == moved && Saves.ShownProfile(u.Linux) == moved
                && Saves.ImportTarget(u.Linux) == moved && Saves.Locate(Saves.Longshot, null, u.Linux).Profiles.Single().Path == moved,
                "Linux, after the game moved its files into user/: the save found, shown and imported into there.");
        Require(GamePlay.UserFolder(u.Linux, P(u.Tmp, "game")) == linuxRoot && GamePlay.UserFolder(u.Windows, P(u.Tmp, "game")) == P(u.AppData, "Longshot")
                && u.Windows.LongshotUser == P(u.AppData, "Longshot"),
                "Every build is still started with --user longshot (an old one keeps its files there, a newer one in its user/); Windows unchanged.");

        // (test_lichii_s_own_folder_first)
        var l = new Case(root, "lichii-own");
        var own = EdenSave(P(l.Data, "lichii"));
        MakeSave(own, "L2", "slot_02");
        var located = Saves.Locate(Saves.Named("lichii"), null, l.Linux);
        Require(located.Folder == P(l.Data, "lichii") && located.Profiles.SequenceEqual(new[] { new SaveProfile(Profile, own) }), "Lichii's own folder first.");
        foreach (var name in new[] { "frizti", "orchid", "pokedinner", "eden" })
            Require(Saves.Locate(Saves.Named(name), null, l.Linux).Profiles.Single().Path == l.Li, name + " stays in Eden's folder");
        var winOwn = EdenSave(P(l.AppData, "lichii"));
        var winEden = EdenSave(P(l.AppData, "eden"));
        MakeSave(winOwn, "W2");
        MakeSave(winEden, "W1");
        Require(Saves.Locate(Saves.Named("lichii"), null, l.Windows).Profiles.Single().Path == winOwn
                && Saves.Locate(Saves.Named("orchid"), null, l.Windows).Profiles.Single().Path == winEden, "The same on Windows.");
        var groups = Saves.Emulator(l.Linux);
        Require(groups.Any(g => g.Display == "Lichii" && g.Folder == P(l.Data, "lichii"))
                && groups.Any(g => g.Display == "Frizti (Fritchii) / Orchid / PokeDinner / Eden" && g.Folder == P(l.Data, "eden"))
                && groups.Any(g => g.Display == "Eglantine" && !g.Found && g.Problem!.StartsWith("Nothing in")),
                "The list's groups with Lichii's own folder (test_list_with_lichii_s_own_folder).");
        var shared = Saves.Emulator(c.Linux);
        Require(shared.Any(g => g.Display == "Lichii / Frizti (Fritchii) / Orchid / PokeDinner / Eden" && g.Found)
                && shared.Any(g => g.Display == "NX2navi" && g.Profiles.Single().Id == Account), "The list's groups (test_list).");

        // (test_lichii_s_own_folder_without_a_save_leaves_the_older_one)
        var e = new Case(root, "lichii-empty");
        Directory.CreateDirectory(P(e.Data, "lichii", "nand", "user", "save"));
        Require(Saves.Locate(Saves.Named("lichii"), null, e.Linux).Profiles.Single().Path == e.Li, "An empty Lichii folder leaves Eden's.");

        // (test_aliases_of_names)
        Require(Saves.Named("Fritchii") == Saves.Named("frizti"), "Fritchii is Frizti.");
        Fails(() => Saves.Named("ryujinx"), "Unknown emulator", "An unknown emulator.");

        // (test_deeper_and_shallower_folders)
        var eden = P(c.Data, "eden");
        foreach (var r in new[] { eden, P(eden, "nand"), P(eden, "nand", "user", "save"), Path.GetDirectoryName(Path.GetDirectoryName(c.Li))!, Path.GetDirectoryName(c.Li)!, c.Li })
            Require(Saves.Find(r).SequenceEqual(new[] { new SaveProfile(Profile, c.Li) }), "Found from " + r);
        foreach (var r in new[] { P(c.Data, "longshot"), Path.GetDirectoryName(c.Hs)!, c.Hs })
            Require(Saves.Find(r).SequenceEqual(new[] { new SaveProfile(Uid, c.Hs) }), "Found from " + r);

        // (test_empty_title_folder_is_a_target)
        var empty = EdenSave(P(c.Tmp, "portable", "user"), "11111111111111111111111111111111");
        Directory.CreateDirectory(empty);
        Require(Saves.Find(P(c.Tmp, "portable", "user")).SequenceEqual(new[] { new SaveProfile("11111111111111111111111111111111", empty) })
                && Saves.Find(empty).SequenceEqual(new[] { new SaveProfile("11111111111111111111111111111111", empty) }), "An empty title folder is a target.");

        // (test_eden_newer_layout)
        var newer = P(c.Tmp, "newer");
        const string uuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var save = P(newer, "nand", "user", "save", "account", uuid, Saves.Title, "0");
        MakeSave(save, "NEW");
        foreach (var r in new[] { newer, Path.GetDirectoryName(save)!, save })
            Require(Saves.Find(r).SequenceEqual(new[] { new SaveProfile(uuid, save) }), "Eden's newer layout from " + r);

        // (test_unrelated_folders_are_not_saves, test_missing_folders)
        Directory.CreateDirectory(P(c.Tmp, "junk", "a", "0"));
        Directory.CreateDirectory(P(c.Tmp, "junk", "b", "save", Uid));
        Require(Saves.Find(P(c.Tmp, "junk")).Count == 0, "Unrelated folders are not saves.");
        Fails(() => Saves.Locate(Saves.Folder, P(c.Tmp, "nothing"), c.Linux), "No folder", "A missing folder.");
        Directory.CreateDirectory(P(c.Tmp, "empty"));
        Fails(() => Saves.Locate(Saves.Folder, P(c.Tmp, "empty"), c.Linux), "No save of the game", "An empty folder.");
        var noEg = new Case(root, "no-eglantine");
        Fails(() => Saves.Locate(Saves.Named("eglantine"), null, noEg.Linux), P(noEg.Data, "eglantine"), "Where Eglantine was looked for.");

        // (test_ambiguous_profiles_listed)
        var a = new Case(root, "ambiguous");
        const string other = "11111111111111111111111111111111";
        MakeSave(EdenSave(P(a.Data, "eden"), other), "O");
        var before = Tree(a.Li);
        try
        {
            a.Run(false, "lichii");
            throw new Exception("saves: two profiles were not refused");
        }
        catch (SaveFailure f)
        {
            Require(f.Message.Contains(Profile) && f.Message.Contains(other) && Tree(a.Li) == before && a.BackupDirs().Count == 0, "Two profiles: listed, nothing copied.");
        }
        a.Run(false, "lichii", profile: other.ToLowerInvariant());
        Require(Tree(EdenSave(P(a.Data, "eden"), other)) == Tree(a.Hs), "The profile asked for.");

        // (describe: the slots and when each was saved)
        File.SetLastWriteTime(P(c.Hs, "slot_01", "progress.sav"), new DateTime(2026, 11, 1, 12, 30, 0));
        var slots = Saves.Describe(c.Hs);
        Require(slots.Select(s => s.Name).SequenceEqual(new[] { "slot_00", "slot_01" }) && slots[1].Saved == new DateTime(2026, 11, 1, 12, 30, 0), "The slots and their times.");
    }

    static void Copies(string root)
    {
        // (test_export)
        var c = new Case(root, "export");
        var before = Tree(c.Li);
        var done = c.Run(false, "lichii");
        var backup = c.BackupDirs().Single();
        Require(Tree(c.Li) == Tree(c.Hs) && Path.GetFileName(backup).StartsWith("export-lichii-") && Tree(backup) == before && done.Backup == backup,
                "Export: the copy and its backup, whose path is said.");
        // (test_import)
        var i = new Case(root, "import");
        before = Tree(i.Hs);
        i.Run(true, "orchid");
        Require(Tree(i.Hs) == Tree(i.Li) && !Tree(i.Hs).Contains("slot_01") && Path.GetFileName(i.BackupDirs().Single()).StartsWith("import-orchid-")
                && Tree(i.BackupDirs().Single()) == before, "Import: the target's own slots go.");
        // (test_lichii_s_copy_goes_to_its_own_folder)
        var o = new Case(root, "own");
        var own = EdenSave(P(o.Data, "lichii"));
        MakeSave(own, "L2", "slot_02");
        before = Tree(o.Li);
        o.Run(false, "lichii");
        Require(Tree(own) == Tree(o.Hs) && Tree(o.Li) == before, "Lichii's copy goes to its own folder.");
        // (test_from_a_save_folder_and_a_data_folder)
        var f = new Case(root, "from");
        var otherSave = P(f.Tmp, "backup of a save");
        MakeSave(otherSave, "F", "slot_03");
        f.Run(true, null, otherSave);
        Require(Tree(f.Hs) == Tree(otherSave), "From a save folder.");
        var portable = P(f.Tmp, "Lichii", "user");
        MakeSave(EdenSave(portable), "P", "slot_04");
        f.Run(true, null, portable);
        Require(Tree(f.Hs) == Tree(EdenSave(portable)) && f.BackupDirs().All(b => Path.GetFileName(b).StartsWith("import-folder-")), "From a portable user folder.");
        // (test_to_a_folder)
        var t = new Case(root, "to");
        var target = EdenSave(P(t.Tmp, "Eglantine", "user"));
        Directory.CreateDirectory(target);
        t.Run(false, "eglantine", P(t.Tmp, "Eglantine", "user"));
        Require(Tree(target) == Tree(t.Hs), "To a portable folder whose game never saved.");

        // (the launcher's rule, which the script does not have: the folder a player picks for an export may hold no save yet.
        // The export makes it the save folder, and only a folder that already had a save is backed up)
        var ne = new Case(root, "to-new");
        var picked = P(ne.Tmp, "a folder with no save");
        Directory.CreateDirectory(picked);
        var previewNew = Saves.ImportExport(false, Saves.Folder, picked, ne.Linux, dryRun: true, backupRoot: ne.Backups, running: () => ne.Busy);
        Require(previewNew.Target == picked && previewNew.Backup == null && Tree(picked) == "",
                "A folder with no save: the preview names it and changes nothing.");
        var made = Saves.ImportExport(false, Saves.Folder, picked, ne.Linux, backupRoot: ne.Backups, running: () => ne.Busy);
        Require(made.Backup == null && Tree(picked) == Tree(ne.Hs) && ne.BackupDirs().Count == 0,
                "A folder with no save: the save is made in it, and nothing was backed up.");
        Require(Saves.Locate(Saves.Folder, picked, ne.Linux).Profiles.SequenceEqual(new[] { new SaveProfile("a folder with no save", picked) }),
                "A folder with no save: it is the save folder afterwards.");
        var again = Saves.ImportExport(false, Saves.Folder, picked, ne.Linux, backupRoot: ne.Backups, running: () => ne.Busy);
        Require(again.Backup != null && Directory.Exists(again.Backup) && Tree(picked) == Tree(ne.Hs),
                "A folder that has the save: it is backed up and replaced.");
        var filled = P(ne.Tmp, "a folder with other files");
        Directory.CreateDirectory(filled);
        File.WriteAllText(P(filled, "notes.txt"), "keep me");
        Saves.ImportExport(false, Saves.Folder, filled, ne.Linux, backupRoot: ne.Backups, running: () => ne.Busy);
        Require(File.ReadAllText(P(filled, "notes.txt")) == "keep me"
                && Tree(filled).Split('\n').Where(l => !l.StartsWith("notes.txt=", StringComparison.Ordinal)).SequenceEqual(Tree(ne.Hs).Split('\n')),
                "A folder with other files: the save joins them, and they stay.");
        Fails(() => Saves.ExportTarget(Saves.Folder, P(ne.Tmp, "not there"), ne.Linux), "No folder", "A folder that is not there.");
        var emulatorFolder = P(ne.Tmp, "an emulator folder with no save");
        Directory.CreateDirectory(emulatorFolder);
        Fails(() => Saves.ExportTarget(Saves.Named("lichii"), emulatorFolder, ne.Linux), "No save of the game",
              "An emulator's folder with no save is still refused.");
        Fails(() => Saves.ExportTarget(Saves.Folder, Path.GetPathRoot(Path.GetFullPath(ne.Tmp))!, ne.Linux), "top of a drive",
              "The top of a drive has no place beside it for the copy.");
        // (test_links_in_a_save_refused)
        if (!OperatingSystem.IsWindows())
        {
            var k = new Case(root, "links");
            var secret = P(k.Tmp, "secret.txt");
            File.WriteAllText(secret, "not a save");
            before = Tree(k.Hs);
            var makers = new Action<string>[]
            {
                d => { File.Delete(P(d, "slot_00", "progress.sav")); File.CreateSymbolicLink(P(d, "slot_00", "progress.sav"), secret); },
                d => { File.Delete(P(d, "account.sav")); File.CreateSymbolicLink(P(d, "account.sav"), secret); },
                d => Directory.CreateSymbolicLink(P(d, "slot_00", "deep"), k.Tmp),
            };
            for (var n = 0; n < makers.Length; n++)
            {
                var linked = P(k.Tmp, "linked-" + n);
                MakeSave(linked, "L", "slot_00");
                makers[n](linked);
                Fails(() => k.Run(true, null, linked), "is a link", "A link in a save.");
                Require(Tree(k.Hs) == before, "A refused link changed nothing.");
            }
            Require(File.ReadAllText(secret) == "not a save", "The link's target is untouched.");
        }
        // (test_same_folder_refused, test_backup_inside_target_refused_before_copy)
        var s = new Case(root, "same");
        Fails(() => s.Run(true, null, s.Hs), "both the source and the target", "One folder both ways.");
        before = Tree(s.Hs);
        foreach (var dry in new[] { false, true })
        {
            Fails(() => Saves.Copy(s.Li, s.Hs, P(s.Hs, "backups"), "import", "folder", dryRun: dry, running: () => s.Busy),
                  "outside the target save folder", "A backup inside the target.");
            Require(!Directory.Exists(P(s.Hs, "backups")) && Tree(s.Hs) == before, "Nothing copied for a backup inside the target.");
        }
        // (test_keep_settings)
        var ks = new Case(root, "keep");
        ks.Run(false, "lichii", keep: true);
        Require(File.ReadAllText(P(ks.Li, "option.sav")) == "option-LI" && File.ReadAllText(P(ks.Li, "account.sav")) == "account-HS"
                && File.ReadAllText(P(ks.Li, "slot_01", "progress.sav")) == "progress-HSslot_01", "Keep settings: option.sav stays.");
        // (test_dry_run_changes_nothing)
        var d = new Case(root, "dry");
        var both = Tree(d.Hs) + Tree(d.Li);
        d.Busy = new[] { "Lichii" };
        var dry2 = d.Run(false, "lichii", dry: true);
        Require(dry2.Dry && Tree(d.Hs) + Tree(d.Li) == both && d.BackupDirs().Count == 0 && dry2.Slots.SequenceEqual(new[] { "slot_00", "slot_01" }),
                "A dry run says and changes nothing.");
        // (test_source_without_slots)
        var n2 = new Case(root, "noslots");
        foreach (var slot in new[] { "slot_00", "slot_01" }) Directory.Delete(P(n2.Hs, slot), true);
        before = Tree(n2.Li);
        Fails(() => n2.Run(false, "lichii"), "no slot_* folders", "A source with no slots.");
        Require(Tree(n2.Li) == before, "Nothing copied from no slots.");
    }

    static void Nx2navi(string root)
    {
        var c = new Case(root, "nx2navi");
        var game = P(c.Home, "Games", "Ocarina");
        var nx = P(game, "profile", "save", Account);
        MakeSave(nx, "NX", "slot_05");
        File.WriteAllBytes(P(game, "profile", "account.uid"), new byte[16]);
        Directory.CreateDirectory(P(game, "profile", "save-tx"));
        // (what NX2navi leaves in save-tx that is not a save in waiting, runtime/fs_host.c: the working copy a quit leaves,
        // an unfinished commit, the save a commit replaced, and names that are not a finished commit's)
        var tx = P(game, "profile", "save-tx");
        MakeSave(P(tx, "work"), "WORK", "slot_05");
        MakeSave(P(tx, "new"), "NEW", "slot_05");
        MakeSave(P(tx, "old"), "OLD", "slot_05");
        MakeSave(P(game, "profile", "save-import.part"), "PART", "slot_05");
        foreach (var name in new[] { "ready-0", "ready-", "ready-1x", "Ready-1", "ready-99999999999" }) MakeSave(P(tx, name), "NOT", "slot_05");
        File.WriteAllText(P(tx, "ready-2"), "a file, not a folder");
        Require(Saves.PendingCommit(nx) == null, "A working copy, an unfinished commit or an old save in save-tx is no save waiting.");
        // (test_both_ways)
        c.Run(true, "nx2navi");
        Require(Tree(c.Hs) == Tree(nx), "From NX2navi.");
        MakeSave(P(c.Hs, "slot_00"), "Z", "-");
        c.Run(false, "nx2navi", game);
        Require(Tree(nx) == Tree(c.Hs) && File.ReadAllBytes(P(game, "profile", "account.uid")).Length == 16, "To NX2navi's game folder.");
        // (test_waiting_save_refused)
        MakeSave(P(tx, "ready-0000000003"), "READY", "slot_05");
        var before = Tree(c.Hs) + Tree(nx);
        var backups = c.BackupDirs().Count;
        foreach (var import in new[] { true, false })
            Fails(() => c.Run(import, "nx2navi"), "save-tx", "A save waiting in save-tx.");
        Require(Tree(c.Hs) + Tree(nx) == before && c.BackupDirs().Count == backups && Saves.PendingCommit(nx) == P(game, "profile", "save-tx"),
                "Nothing copied while NX2navi has a save waiting.");
        c.Run(true, "nx2navi", force: true);
        Require(Tree(c.Hs) == Tree(nx), "Forced.");
        Directory.Delete(P(tx, "ready-0000000003"), recursive: true);
        var pendingImport = P(game, "profile", "save-import");
        MakeSave(pendingImport, "IMPORT", "slot_04");
        before = Tree(c.Hs) + Tree(game);
        backups = c.BackupDirs().Count;
        Require(Saves.PendingCommit(nx) == pendingImport, "NX2navi's completed menu import waits for the next start.");
        foreach (var import in new[] { true, false })
            Fails(() => c.Run(import, "nx2navi"), "save-import", "A menu import waiting in NX2navi.");
        Require(Tree(c.Hs) + Tree(game) == before && c.BackupDirs().Count == backups,
                "Nothing copied while NX2navi has a menu import waiting.");
    }

    static void Guard(string root)
    {
        // (test_refuses_while_running, test_force)
        var c = new Case(root, "guard");
        c.Busy = new[] { "Longshot" };
        var before = Tree(c.Hs) + Tree(c.Li);
        Fails(() => c.Run(false, "lichii"), "Longshot", "A running game.");
        Require(Tree(c.Hs) + Tree(c.Li) == before && c.BackupDirs().Count == 0, "Nothing copied while a game runs.");
        c.Busy = new[] { "Lichii" };
        c.Run(false, "lichii", force: true);
        Require(Tree(c.Li) == Tree(c.Hs), "Forced while running.");

        // (test_every_process_name_on_windows)
        var names = Saves.ProcessNames();
        foreach (var program in Saves.Emulators.Prepend(Saves.Longshot))
            foreach (var p in program.Processes) Require(names[p].Contains(program.Display), "The process names of " + program.Name);
        var images = names.Keys.Select((p, i) => i % 2 == 1 ? p.ToUpperInvariant() + ".exe" : p + ".exe").Append("notepad.exe");
        Require(Saves.RunningFrom(Platform.Windows, images).SequenceEqual(names.Values.Distinct().Order(StringComparer.Ordinal)), "Every program's process on Windows.");
        // (test_linux_processes_and_appimages: each process's comm, and its argv[0]'s file name)
        var linux = new[] { "orchid", "orchid", "AppRun", "eglantine-linux-x86_64.AppImage", "ocarina", "ocarina", "bash", "bash" };
        Require(Saves.RunningFrom(Platform.Linux, linux).SequenceEqual(new[] { "NX2navi", "Orchid", "PokeDinner or Eglantine" }), "Linux processes and AppImages.");
        Require(Saves.RunningFrom(Platform.Linux, new[] { "Longshot.AppImage", "LongshotLauncher" }).Count == 0, "The launcher itself is not the game.");
        // (#118: Gear Lever renames the launcher's AppImage after its menu entry's name ("Longshot"), and a deduped name like
        //  longshot-2.appimage walks into the game-AppImage regex: the launcher read its own runtime as the game and refused
        //  its own update. The runtime's argv[0] is the AppImage file path, and the runtime names it in APPIMAGE for the
        //  launcher: a full-path match is what says "this process is the launcher's own runtime", a file name can never tell
        //  the launcher's longshot-2.appimage from a game's.)
        Require(Saves.IsOwnRuntime("/home/deck/Applications/longshot-2.appimage", "/home/deck/Applications/longshot-2.appimage"),
                "The launcher's own AppImage runtime is found by the exact path the runtime runs and the runtime names.");
        Require(!Saves.IsOwnRuntime("/games/longshot-2.appimage", "/home/deck/Applications/longshot-2.appimage"),
                "A different AppImage of the same name is not the launcher's own: the path decides, never the file name.");
        Require(!Saves.IsOwnRuntime("/games/longshot.appimage", null) && !Saves.IsOwnRuntime("", "/home/deck/a.appimage")
                && !Saves.IsOwnRuntime("/home/deck/a.appimage", ""),
                "No AppImage env (the launcher run unpacked) or no process: nothing is the launcher's own.");
        Require(Saves.IsOwnRuntime("/home/deck/LongshotLauncher-route-x86_64.AppImage", "/home/deck/LongshotLauncher-route-x86_64.AppImage"),
                "The normal (never renamed) launcher AppImage is its own runtime too.");
    }

    static void FirstImports(string root)
    {
        foreach (var platform in new[] { Platform.Linux, Platform.Windows })
        foreach (var custom in new[] { false, true })
        {
            var c = new Case(root, $"first-nx2navi-{platform}-{custom}");
            Directory.Delete(c.Hs, recursive: true);
            var env = platform == Platform.Linux ? c.Linux : c.Windows;
            var game = custom ? P(c.Tmp, "portable game") : platform == Platform.Linux
                ? P(c.Home, "Games", "Ocarina") : P(c.LocalAppData, "Programs", "NX2navi", "game");
            var folder = custom ? game : null;
            var source = P(game, "profile", "save", Profile);
            MakeSave(source, "FIRST", "slot_03");
            File.WriteAllBytes(P(game, "profile", "account.uid"), new byte[16]);
            var progress = P(source, "slot_03", "progress.sav");
            var bytes = new byte[] { 0, 255, 128, 42, 13, 10 };
            File.WriteAllBytes(progress, bytes);
            var sourceBefore = Tree(game);
            var target = Saves.ImportTarget(env);
            Require(target == P(env.LongshotUser, "save", "5a3c9e1f0b7d24680000000000000001")
                    && !Directory.Exists(target), "First import: the fixed user Longshot reads, without writing a folder.");
            Require(Saves.Locate(Saves.Named("nx2navi"), folder, env).Profiles.Single().Path == source,
                    "First import: NX2navi's default or custom game folder on either platform.");

            SaveCopy Copy(bool dry = false) => Saves.ImportExport(true, Saves.Named("nx2navi"), folder, env,
                dryRun: dry, backupRoot: c.Backups, running: () => c.Busy);
            c.Busy = new[] { "NX2navi" };
            Fails(() => Copy(), "Close NX2navi", "First import refuses a running NX2navi.");
            Require(!Directory.Exists(target) && c.BackupDirs().Count == 0 && Tree(game) == sourceBefore,
                    "A refused first import writes nothing.");
            var preview = Copy(dry: true);
            Require(preview.Target == target && preview.Backup == null && !Directory.Exists(target) && c.BackupDirs().Count == 0,
                    "A first import's preview names the target without making it or a backup.");
            c.Busy = Array.Empty<string>();
            Fails(() => Saves.ImportExport(false, Saves.Named("nx2navi"), folder, env, backupRoot: c.Backups, running: () => c.Busy),
                  "Start Longshot once", "Export still requires a Longshot save.");
            foreach (var pending in new[] { P(game, "profile", "save-tx", "ready-1"), P(game, "profile", "save-import") })
            {
                MakeSave(pending, "WAITING", "slot_02");
                var before = Tree(game);
                try
                {
                    Copy();
                    throw new Exception("First import ignored NX2navi's pending save.");
                }
                catch (SaveFailure e)
                {
                    Require(e.Reason == SaveRefusal.Waiting && e.Who.SequenceEqual(new[] { "NX2navi" })
                            && e.Message.Contains(Path.GetFileName(pending) == "save-import" ? pending : Path.GetDirectoryName(pending)!),
                            "First import names NX2navi's pending commit or menu import.");
                }
                Require(!Directory.Exists(target) && c.BackupDirs().Count == 0 && Tree(game) == before,
                        "A pending NX2navi save refuses first import without changing either side.");
                Directory.Delete(pending, recursive: true);
            }

            var first = Copy();
            Require(first.Backup == null && Tree(target) == Tree(source)
                    && File.ReadAllBytes(P(target, "slot_03", "progress.sav")).SequenceEqual(bytes)
                    && File.ReadAllBytes(progress).SequenceEqual(bytes) && Tree(game) == sourceBefore,
                    "First import: every file copied, source bytes untouched, no backup of an absent target.");
            MakeSave(target, "PLAYED", "slot_08");
            var targetBefore = Tree(target);
            var second = Copy();
            Require(second.Backup != null && Tree(second.Backup) == targetBefore && Tree(target) == Tree(source)
                    && !Directory.Exists(P(target, "slot_08")) && Tree(game) == sourceBefore,
                    "A later import backs up the target whole and replaces its slots, leaving the source alone.");

            var secondSource = P(game, "profile", "save", Account);
            MakeSave(secondSource, "SECOND");
            var bothBefore = Tree(game) + Tree(target);
            var backups = c.BackupDirs().Count;
            Fails(() => Copy(), "pick one", "Several NX2navi profiles require a choice.");
            Require(Tree(game) + Tree(target) == bothBefore && c.BackupDirs().Count == backups,
                    "An ambiguous NX2navi profile writes nothing.");
            Saves.ImportExport(true, Saves.Named("nx2navi"), folder, env, profile: Account,
                               backupRoot: c.Backups, running: () => c.Busy);
            Require(Tree(target) == Tree(secondSource), "The NX2navi profile the player chose is imported.");

            // (a second Longshot profile folder, one the game never opens: the import still goes into the fixed user, and leaves it)
            var stray = P(env.LongshotUser, "save", Uid);
            MakeSave(stray, "STRAY");
            var strayBefore = Tree(stray);
            Require(Saves.ImportTarget(env) == target, "Another Longshot profile moved the import away from the user the game opens.");
            var beside = Saves.ImportExport(true, Saves.Named("nx2navi"), folder, env, profile: Account,
                                            backupRoot: c.Backups, running: () => c.Busy);
            Require(beside.Target == target && Tree(target) == Tree(secondSource) && Tree(stray) == strayBefore,
                    "An import beside a stray Longshot profile goes into the fixed user and leaves the stray one alone.");
        }
    }

    static void Builds(string root)
    {
        // (import and export: the save every build shares, on Windows as on Linux)
        var c = new Case(root, "builds");
        MakeSave(EdenSave(P(c.AppData, "eden")), "WE", "slot_07");
        Fails(() => Saves.ImportExport(false, Saves.Named("lichii"), null, c.Windows, backupRoot: c.Backups, running: () => c.Busy),
              "Start Longshot once", "Windows: Longshot never saved yet.");
        var shared = Saves.SharedSavePath(c.Windows, Saves.UserId);
        MakeSave(shared, "S");
        Saves.ImportExport(true, Saves.Named("lichii"), null, c.Windows, backupRoot: c.Backups, running: () => c.Busy);
        Require(Tree(shared) == Tree(EdenSave(P(c.AppData, "eden"))), "Import into Windows' shared save.");
        Saves.ImportExport(false, Saves.Named("eden"), null, c.Linux, backupRoot: c.Backups, running: () => c.Busy);
        Require(Tree(c.Li) == Tree(c.Hs), "Export from Linux's shared save.");
        Gather(root);
        GatherSlots(root);
        GatherLink(root);
        LinkSavesChecks(root);
    }

    /// Every file of a folder written at that time
    static void Age(string folder, DateTime when)
    {
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetLastWriteTime(f, when);
    }

    /// The merge checks below put a plain user\save in again after each start, as a build started on its own did before Gather left a
    /// link there (issue 146's slots, merged): for them the link fails, as on a disk with no junctions, and the folder stays a plain
    /// one. then is the check's own fault, if it has one
    static Action<string> NoLink(Action<string>? then = null) => s =>
    {
        if (s == "link") throw new IOException("no junction on this disk");
        then?.Invoke(s);
    };

    static void Gather(string root)
    {
        // (Windows: the saves each build kept in its own folder, and the 2.x Builder's, into the one every build shares)
        Saves.Fault = NoLink();
        var c = new Case(root, "gather");
        var builds = P(c.Home, "Games", "Longshot");   // (the 2.x Builder's game folder, the launcher's builds folder in it)
        var a = P(builds, "v3.0.0-rc.1");
        var b = P(builds, "v3.0.0-rc.2");
        var old = Saves.OldUserFolders(c.Windows, new[] { a, b, a + Path.DirectorySeparatorChar }, builds);
        Require(old.SequenceEqual(new[] { P(a, "user"), P(b, "user"), P(builds, "user") }), "The old user folders: each build's once, then the 2.x Builder's.");
        Require(Saves.OldUserFolders(c.Linux, new[] { a }, builds).Count == 0, "None on Linux: the game has always shared them there.");
        Require(Saves.OldUserFolders(c.Windows with { UserFolder = P(a, "user") }, new[] { a, b }, null).SequenceEqual(new[] { P(b, "user") }),
                "Never the shared folder itself; no 2.x Builder's for a portable launcher.");
        Require(Saves.Gather(c.Windows, old, () => c.Busy) == null && Saves.Gather(c.Linux, old, () => c.Busy) == null, "Nothing to gather.");

        var when = new DateTime(2026, 11, 13, 9, 0, 0);
        var sharedSave = P(c.Windows.LongshotUser, "save");
        MakeSave(P(builds, "user", "save", Uid), "OLD");
        MakeSave(P(a, "user", "save", Uid), "A");
        MakeSave(P(b, "user", "save", Uid), "B");
        File.WriteAllText(P(a, "user", "mouse_sens.txt"), "2.5");
        Age(P(builds, "user", "save"), when.AddDays(-30));
        Age(P(a, "user", "save"), when.AddDays(-1));
        Age(P(b, "user", "save"), when.AddDays(-2));
        var treeA = Tree(P(a, "user", "save"));
        Require(Saves.Gather(c.Windows, old, () => new[] { "Longshot" }) == null && Directory.Exists(P(a, "user", "save")) && !Directory.Exists(sharedSave),
                "Not while Longshot runs: the next start tries again.");
        var g = Saves.Gather(c.Windows, old, () => new[] { "Lichii" }, () => when)!;
        Require(LauncherLog.Tail(3).Any(l => l.Contains(" saves gather: backed up ") && l.Contains(Privacy.Scrub(g.Backup))
                                             && l.Contains($"the save of {Privacy.Scrub(P(a, "user"))} copied into")),
                "The gather is in the launcher's log, with its backup and the save kept: " + string.Join(" | ", LauncherLog.Tail(3)));
        Require(g.From == P(a, "user") && !g.WasShared && Tree(sharedSave) == treeA, "The newest save becomes the shared one (an emulator may run).");
        Require(File.ReadAllText(P(c.Windows.LongshotUser, "mouse_sens.txt")) == "2.5", "The kept save's own settings come along (mouse_sens.txt).");
        Require(g.Moved.SequenceEqual(old) && old.All(u => !Directory.Exists(P(u, "save")) && Directory.Exists(P(u, "save.moved-20261113-090000"))),
                "Each old save renamed aside: the game no longer finds it, nothing is deleted.");
        Require(Path.GetFileName(g.Backup) == "gather-20261113-090000" && Path.GetDirectoryName(g.Backup) == c.Windows.BackupRoot
                && Tree(P(g.Backup, "v3.0.0-rc.1")) == treeA && Tree(P(g.Backup, "Longshot")) != "" && Tree(P(g.Backup, "v3.0.0-rc.2")) != "",
                "Every one backed up first, each named for its game folder.");
        Require(SaveText.Gathered(g).Contains(a) && SaveText.Gathered(g).Contains(g.Backup), "What the home says: where the save came from, and the backup.");
        Require(Saves.Gather(c.Windows, old, () => c.Busy) == null, "Done once: nothing left to gather.");

        // (an old build started on its own, without --user, saves in its folder again: a new game in the same slots, say. Its newer
        // slots go to the free ones, never over the shared save's; the shared one is backed up)
        MakeSave(P(b, "user", "save", Uid), "B2");
        Age(sharedSave, when.AddDays(-1));
        Age(P(b, "user", "save"), when);
        var g2 = Saves.Gather(c.Windows, old, () => c.Busy, () => when.AddHours(1))!;
        var b2 = P(b, "user", "save.moved-20261113-100000", Uid);
        Require(g2.WasShared && g2.From == c.Windows.LongshotUser && Slot(P(sharedSave, Uid), "slot_00") == Slot(P(a, "user", "save.moved-20261113-090000", Uid), "slot_00")
                && Slot(P(sharedSave, Uid), "slot_02") == Slot(b2, "slot_00") && Slot(P(sharedSave, Uid), "slot_03") == Slot(b2, "slot_01")
                && Tree(P(g2.Backup, "shared")) == treeA && SaveText.Gathered(g2).Contains("slot 1 from " + b + " as slot 3", StringComparison.Ordinal),
                "A newer save in a build's own folder goes to the free slots, the shared one's kept and backed up.");

        // (an older one: the shared save stays, the old one goes aside)
        var kept = Tree(sharedSave);
        MakeSave(P(a, "user", "save", Uid), "A3");
        Age(P(a, "user", "save"), when.AddDays(-5));
        Age(sharedSave, when);
        var g3 = Saves.Gather(c.Windows, old, () => c.Busy, () => when.AddHours(2))!;
        Require(g3.WasShared && g3.From == c.Windows.LongshotUser && Tree(sharedSave) == kept && !Directory.Exists(P(a, "user", "save"))
                && SaveText.Gathered(g3).Contains("shared"), "An older save: the shared one kept, the old one moved aside.");

        // (a swap that fails: the shared save and the old one as they were, the backup kept)
        MakeSave(P(a, "user", "save", Uid), "A4");
        Age(P(a, "user", "save"), when.AddDays(1));
        Saves.Fault = NoLink(s => { if (s == "swap") throw new IOException("disk full"); });
        try { Fails(() => Saves.Gather(c.Windows, old, () => c.Busy, () => when.AddHours(3)), "Nothing was changed", "A gather that fails half way."); }
        finally { Saves.Fault = NoLink(); }
        Require(Tree(sharedSave) == kept && Directory.Exists(P(a, "user", "save")) && !Directory.Exists(P(c.Windows.LongshotUser, ".save.longshot-old")),
                "After a failed gather: the shared save and the old one as they were.");

        // (a shared save a gather cut short left aside, while a new one was made since: backed up before it goes)
        var aside = P(c.Windows.LongshotUser, ".save.longshot-old");
        MakeSave(P(aside, Uid), "ASIDE");
        var asideTree = Tree(aside);
        var g4 = Saves.Gather(c.Windows, old, () => c.Busy, () => when.AddHours(4))!;
        Require(g4.WasShared && g4.Slots.Count(s => s.MovedTo != null) == 2 && Tree(P(g4.Backup, "shared (set aside)")) == asideTree && !Directory.Exists(aside),
                "What a gather cut short left aside is backed up too.");

        // (an old build started on its own makes a save with nothing played: it never replaces the shared one)
        var keptNow = Tree(sharedSave);
        Directory.CreateDirectory(P(b, "user", "save", Uid));
        File.WriteAllText(P(b, "user", "save", Uid, "account.sav"), "fresh");
        File.WriteAllText(P(b, "user", "save", Uid, "option.sav"), "fresh");
        var g5 = Saves.Gather(c.Windows, old, () => c.Busy, () => when.AddHours(5))!;
        Require(g5.WasShared && Tree(sharedSave) == keptNow && !Directory.Exists(P(b, "user", "save")),
                "A save with nothing played yet never replaces the shared one; it goes aside.");

        // (a user\save that is a link, the player's own arrangement or the shared folder itself, is left alone)
        if (TestLinks.Available)
        {
            var linked = P(c.Tmp, "linked");
            Directory.CreateDirectory(P(linked, "user"));
            Directory.CreateSymbolicLink(P(linked, "user", "save"), sharedSave);
            Require(Saves.Gather(c.Windows, new[] { P(linked, "user") }, () => c.Busy, () => when.AddHours(6)) == null
                    && Tree(sharedSave) == keptNow && Directory.Exists(P(linked, "user", "save")), "A linked save folder is never gathered nor renamed.");
        }
        Saves.Fault = null;
    }

    /// One slot's files, as Tree gives them ("" when the slot is not there)
    static string Slot(string profile, string slot) => Tree(P(profile, slot));

    static void GatherSlots(string root)
    {
        Saves.Fault = NoLink();
        // (issue #146: a gather merges slot by slot, and never over a slot of the shared save. The shared save has slots 1-3. An old
        // build's folder has a slot 2 saved later (an old build started on its own: a new game begun in that slot, or the same one
        // played on), a slot 5 the shared one lacks, and a slot 1 saved at the same time as the shared one's. The shared slots stay;
        // the newer slot 2 goes to the first free slot, 4; slot 5 comes over; nothing of either side is lost)
        var c = new Case(root, "gather-slots");
        var a = P(c.Home, "Games", "Longshot", "v3.0.0-rc.1");
        var sharedSave = P(c.Windows.LongshotUser, "save");
        var shared = P(sharedSave, Saves.UserId);
        var oldSave = P(a, "user", "save");
        var old = P(oldSave, Saves.UserId);
        var when = new DateTime(2026, 11, 17, 9, 0, 0);
        MakeSave(shared, "S", "slot_00", "slot_01", "slot_02");
        MakeSave(old, "O", "slot_00", "slot_01", "slot_04");
        Age(sharedSave, when.AddDays(-1));
        Age(oldSave, when.AddDays(-3));
        File.SetLastWriteTime(P(old, "slot_00", "progress.sav"), when.AddDays(-1));   // (a tie: the shared one's stays)
        File.SetLastWriteTime(P(old, "slot_01", "progress.sav"), when);               // (newer, and another save)
        var (s0, s1, s2, o1, o4) = (Slot(shared, "slot_00"), Slot(shared, "slot_01"), Slot(shared, "slot_02"), Slot(old, "slot_01"), Slot(old, "slot_04"));
        var (sharedBefore, oldBefore) = (Tree(sharedSave), Tree(oldSave));

        var g = Saves.Gather(c.Windows, Saves.OldUserFolders(c.Windows, new[] { a }, null), () => c.Busy, () => when)!;
        Require(Saves.SlotNames(shared).SequenceEqual(new[] { "slot_00", "slot_01", "slot_02", "slot_03", "slot_04" }),
                "Merged slot by slot: every slot of both sides is in the shared save (has: " + string.Join(" ", Saves.SlotNames(shared)) + ").");
        Require(Slot(shared, "slot_00") == s0 && Slot(shared, "slot_01") == s1 && Slot(shared, "slot_02") == s2,
                "The shared save's slots are never written over, by a tie or by a newer copy.");
        Require(Slot(shared, "slot_03") == o1 && Slot(shared, "slot_04") == o4, "The newer copy goes to the first free slot; the new slot comes over.");
        Require(File.ReadAllText(P(shared, "account.sav")) == "account-S" && File.ReadAllText(P(shared, "option.sav")) == "option-S",
                "account.sav and option.sav stay the shared save's.");
        Require(Tree(P(g.Backup, "shared")) == sharedBefore && Tree(P(g.Backup, "v3.0.0-rc.1")) == oldBefore, "Both sides backed up whole first.");
        Require(!Directory.Exists(oldSave) && Tree(P(a, "user", "save.moved-20261117-090000")) == oldBefore, "The old save renamed aside, as it was.");
        var said = SaveText.Gathered(g);
        Require(said.Contains("slot 2 from " + a + " as slot 4", StringComparison.Ordinal) && said.Contains("slot 5 from " + a, StringComparison.Ordinal)
                && said.Contains("only in that backup: slot 1 from " + a, StringComparison.Ordinal) && !said.Contains("slot 3 from", StringComparison.Ordinal)
                && said.Contains(g.Backup, StringComparison.Ordinal), "What the home says: which slots came from where, what stays in the backup only, and the backup (said: " + said + ").");
        Require(Saves.Gather(c.Windows, Saves.OldUserFolders(c.Windows, new[] { a }, null), () => c.Busy) == null, "Done once: nothing left to gather.");

        // (no shared save yet, the first gather after an update: the 2.x Builder's folder has slot 1 of one adventure, saved after
        // rc.1's slot 1 of another; rc.1 was played last (its slot 2). Neither slot 1 is lost: the later one goes to a free slot)
        var fc = new Case(root, "gather-slots-first");
        var builds = P(fc.Home, "Games", "Longshot");
        var rc1 = P(builds, "v3.0.0-rc.1");
        var adventureA = P(builds, "user", "save", Saves.UserId);
        var adventureB = P(rc1, "user", "save", Saves.UserId);
        MakeSave(adventureA, "A", "slot_00");
        MakeSave(adventureB, "B", "slot_00", "slot_01");
        Age(P(builds, "user", "save"), when.AddDays(1));
        Age(P(rc1, "user", "save"), when);
        File.SetLastWriteTime(P(adventureB, "slot_01", "progress.sav"), when.AddDays(2));
        var (a0, b0, b1) = (Slot(adventureA, "slot_00"), Slot(adventureB, "slot_00"), Slot(adventureB, "slot_01"));
        var first = Saves.Gather(fc.Windows, Saves.OldUserFolders(fc.Windows, new[] { rc1 }, builds), () => fc.Busy, () => when)!;
        var firstShared = P(fc.Windows.LongshotUser, "save", Saves.UserId);
        Require(Slot(firstShared, "slot_00") == b0 && Slot(firstShared, "slot_01") == b1 && Slot(firstShared, "slot_02") == a0,
                "No shared save yet: a later slot 1 of another adventure goes to a free slot, never over the kept one's (has: "
                + string.Join(" ", Saves.SlotNames(firstShared)) + ").");
        Require(SaveText.Gathered(first).Contains("slot 1 from " + builds + " as slot 3", StringComparison.Ordinal), "And the home says where it went.");
        string Did(SaveGathered r) => string.Join(" ", r.Slots.Select(s => $"{s.Slot}<{(s.Shared ? "shared" : Path.GetFileName(Path.GetDirectoryName(s.From)))}"
                                                                             + (s.MovedTo is { } to ? ">" + to : "") + (s.Left ? ">left" : "")));
        Require(Did(g) == "slot_00<shared slot_01<shared slot_02<shared slot_04<v3.0.0-rc.1 slot_01<v3.0.0-rc.1>slot_03 slot_00<v3.0.0-rc.1>left"
                && g.Slots.Single(s => s.Left).Older
                && g.Slots.All(s => s.Profile == Saves.UserId) && g.WasShared && g.From == c.Windows.LongshotUser, "SaveGathered says what became of each slot (did: " + Did(g) + ").");

        // (the shared save played last of all, while an old folder has a slot it lacks and a newer copy of one it has: both come in)
        var c3 = new Case(root, "gather-slots-3");
        var shared3 = P(c3.Windows.LongshotUser, "save", Saves.UserId);
        var e = P(c3.Home, "Games", "Longshot", "v3.0.0-rc.2");
        var eOld = P(e, "user", "save", Saves.UserId);
        MakeSave(shared3, "S", "slot_00", "slot_01");
        MakeSave(eOld, "E", "slot_01", "slot_03");
        Age(P(c3.Windows.LongshotUser, "save"), when.AddDays(-2));
        File.SetLastWriteTime(P(shared3, "slot_00", "progress.sav"), when.AddDays(5));   // (the newest of all)
        Age(P(e, "user", "save"), when);
        var (k0, k1, e1, e3) = (Slot(shared3, "slot_00"), Slot(shared3, "slot_01"), Slot(eOld, "slot_01"), Slot(eOld, "slot_03"));
        Saves.Gather(c3.Windows, Saves.OldUserFolders(c3.Windows, new[] { e }, null), () => c3.Busy, () => when);
        Require(Saves.SlotNames(shared3).SequenceEqual(new[] { "slot_00", "slot_01", "slot_02", "slot_03" }) && Slot(shared3, "slot_00") == k0
                && Slot(shared3, "slot_01") == k1 && Slot(shared3, "slot_02") == e1 && Slot(shared3, "slot_03") == e3,
                "A shared save played last of all still takes in a slot it lacks and a newer copy of one it has, on the disk.");

        // (two old folders: for a slot the shared save lacks, the newer copy; a tie, the first folder's; a slot with no progress.sav
        // or an empty one never the newest; a profile only one of them has comes whole. A newer, different copy of a shared slot
        // goes to a free slot, the newest first; with none left, it stays in the backup, and the home says so)
        var c2 = new Case(root, "gather-slots-2");
        var shared2 = P(c2.Windows.LongshotUser, "save", Saves.UserId);
        var b = P(c2.Home, "Games", "Longshot", "v3.0.0-rc.2");
        var d = P(c2.Home, "Games", "Longshot", "nightly-200-aaaaaaaa");
        var bs = P(b, "user", "save", Saves.UserId);
        var ds = P(d, "user", "save", Saves.UserId);
        MakeSave(shared2, "S", "slot_00");
        MakeSave(bs, "B", "slot_00", "slot_01", "slot_02", "slot_04");
        MakeSave(ds, "D", "slot_00", "slot_01", "slot_02", "slot_03", "slot_04");
        Directory.CreateDirectory(P(bs, "slot_03"));
        File.WriteAllText(P(bs, "slot_03", "caption.sav"), "caption-B-no-progress");   // (no progress.sav)
        MakeSave(P(d, "user", "save", Uid), "DX", "slot_00");
        Age(P(c2.Windows.LongshotUser, "save"), when.AddDays(-1));
        Age(P(b, "user", "save"), when.AddDays(1));
        Age(P(d, "user", "save"), when.AddDays(1));
        File.SetLastWriteTime(P(ds, "slot_00", "progress.sav"), when.AddDays(3));    // (D's slot 1 newer than B's: both newer than the shared one's)
        File.SetLastWriteTime(P(ds, "slot_01", "progress.sav"), when.AddDays(2));    // (D's slot 2 newer than B's)
        File.SetLastWriteTime(P(ds, "slot_03", "progress.sav"), when.AddDays(-10));  // (old, but B's slot 4 has no progress at all)
        File.WriteAllText(P(bs, "slot_04", "progress.sav"), "");                      // (B's slot 5 is empty: never the newest)
        File.SetLastWriteTime(P(bs, "slot_04", "progress.sav"), when.AddDays(9));
        File.SetLastWriteTime(P(ds, "slot_04", "progress.sav"), when.AddDays(-5));
        var sideTrees = new[] { Tree(P(c2.Windows.LongshotUser, "save")), Tree(P(b, "user", "save")), Tree(P(d, "user", "save")) };
        var want = new Dictionary<string, string>
        {
            ["slot_00"] = Slot(shared2, "slot_00"), ["slot_01"] = Slot(ds, "slot_01"), ["slot_02"] = Slot(bs, "slot_02"),
            ["slot_03"] = Slot(ds, "slot_03"), ["slot_04"] = Slot(ds, "slot_04"), ["slot_05"] = Slot(ds, "slot_00"),
        };
        var two = Saves.Gather(c2.Windows, Saves.OldUserFolders(c2.Windows, new[] { b, d }, null), () => c2.Busy, () => when)!;
        Require(want.All(w => Slot(shared2, w.Key) == w.Value) && Saves.SlotNames(shared2).Count == 6,
                "Two old folders: each slot the shared save lacks from the newer copy (a tie: the first folder's; no or an empty progress.sav: never), "
                + "a newer copy of a shared slot in the free slot (did: " + Did(two) + ").");
        Require(Tree(P(c2.Windows.LongshotUser, "save", Uid)) == Tree(P(d, "user", "save.moved-20261117-090000", Uid)), "A profile only one old folder has comes whole.");
        Require(two.Slots.Where(s => s.Left && !s.Older).Select(s => $"{s.Slot}<{Path.GetFileName(Path.GetDirectoryName(s.From))}").SequenceEqual(new[]
                {
                    "slot_00<v3.0.0-rc.2", "slot_01<v3.0.0-rc.2", "slot_02<nightly-200-aaaaaaaa", "slot_03<v3.0.0-rc.2", "slot_04<v3.0.0-rc.2",
                }) && File.ReadAllText(P(shared2, "account.sav")) == "account-S",
                "Every other different copy, the losers of a slot the shared save lacks too, wanted a free slot; with none left it stays in "
                + "the backup only, said so; the shared account.sav stays.");
        Require(Tree(P(two.Backup, "shared")) == sideTrees[0] && Tree(P(two.Backup, "v3.0.0-rc.2")) == sideTrees[1]
                && Tree(P(two.Backup, "nightly-200-aaaaaaaa")) == sideTrees[2], "Every side backed up whole: nothing is lost.");
        var saidTwo = SaveText.Gathered(two);
        Require(saidTwo.Contains("slot 1 from " + d + " as slot 6", StringComparison.Ordinal) && saidTwo.Contains("No slot was free for slot 1 from " + b, StringComparison.Ordinal)
                && saidTwo.Contains("(profile " + Uid + ")", StringComparison.Ordinal) && saidTwo.Contains(two.Backup, StringComparison.Ordinal),
                "What the home says: where each slot went, what stayed in the backup, each profile named (said: " + saidTwo + ").");

        // (a copy the same as a slot the shared save has, wherever, even saved later: nothing to do; what a cut-short gather left
        // aside is backed up and goes)
        var f = P(c2.Home, "Games", "Longshot", "v3.0.0-rc.3");
        var fs = P(f, "user", "save", Saves.UserId);
        Directory.CreateDirectory(fs);
        foreach (var file in Directory.EnumerateFiles(shared2)) File.Copy(file, P(fs, Path.GetFileName(file)));
        Directory.CreateDirectory(P(fs, "slot_00"));
        foreach (var file in Directory.EnumerateFiles(P(shared2, "slot_05"))) File.Copy(file, P(fs, "slot_00", Path.GetFileName(file)));
        Age(P(f, "user", "save"), when.AddDays(30));
        var aside = P(c2.Windows.LongshotUser, ".save.longshot-old");
        MakeSave(P(aside, Saves.UserId), "ASIDE");
        var (asideTree, unchanged) = (Tree(aside), Tree(P(c2.Windows.LongshotUser, "save")));
        var same = Saves.Gather(c2.Windows, Saves.OldUserFolders(c2.Windows, new[] { f }, null), () => c2.Busy, () => when.AddHours(1))!;
        Require(Tree(P(c2.Windows.LongshotUser, "save")) == unchanged && same.Slots.All(s => s.Shared) && SaveText.Gathered(same).Contains("shared", StringComparison.Ordinal),
                "A copy the same as one of the shared save's slots changes nothing.");
        Require(!Directory.Exists(aside) && Tree(P(same.Backup, "shared (set aside)")) == asideTree, "What a cut-short gather left aside is backed up, then goes.");

        // (three old folders with a later slot 1, two saved at the same time, the third the same as the first, byte for byte; one
        // free slot: the first folder's goes there, the second's stays in the backup, the third is the first's save already)
        var c4 = new Case(root, "gather-slots-4");
        var shared4 = P(c4.Windows.LongshotUser, "save", Saves.UserId);
        MakeSave(shared4, "S", "slot_00", "slot_01", "slot_02", "slot_03", "slot_04");
        var (x, y, z) = (P(c4.Home, "Games", "Longshot", "x"), P(c4.Home, "Games", "Longshot", "y"), P(c4.Home, "Games", "Longshot", "z"));
        MakeSave(P(x, "user", "save", Saves.UserId), "X", "slot_00");
        MakeSave(P(y, "user", "save", Saves.UserId), "Y", "slot_00");
        MakeSave(P(z, "user", "save", Saves.UserId), "X", "slot_00");
        Age(P(c4.Windows.LongshotUser, "save"), when);
        foreach (var u in new[] { x, y, z }) Age(P(u, "user", "save"), when.AddDays(1));
        var x0 = Slot(P(x, "user", "save", Saves.UserId), "slot_00");
        var four = Saves.Gather(c4.Windows, Saves.OldUserFolders(c4.Windows, new[] { x, y, z }, null), () => c4.Busy, () => when)!;
        Require(Did(four) == "slot_00<shared slot_01<shared slot_02<shared slot_03<shared slot_04<shared slot_00<x>slot_05 slot_00<y>left"
                && Slot(shared4, "slot_05") == x0, "The same copy twice takes one slot; on a tie the first folder's goes first (did: " + Did(four) + ").");

        // (a copy older than a kept slot's own, but the same as one that fills another slot: kept there, and never said to be in
        // the backup only; nor placed twice)
        var c5 = new Case(root, "gather-slots-5");
        var shared5 = P(c5.Windows.LongshotUser, "save", Saves.UserId);
        var (fb, fcf) = (P(c5.Home, "Games", "Longshot", "b"), P(c5.Home, "Games", "Longshot", "c"));
        MakeSave(shared5, "Y", "slot_00");
        MakeSave(P(fb, "user", "save", Saves.UserId), "X", "slot_00");
        Directory.CreateDirectory(P(fcf, "user", "save", Saves.UserId, "slot_03"));
        foreach (var file in Directory.EnumerateFiles(P(fb, "user", "save", Saves.UserId, "slot_00")))
            File.Copy(file, P(fcf, "user", "save", Saves.UserId, "slot_03", Path.GetFileName(file)));
        Age(P(c5.Windows.LongshotUser, "save"), when);
        Age(P(fb, "user", "save"), when.AddDays(-1));
        Age(P(fcf, "user", "save"), when.AddDays(-1));
        var xCopy = Slot(P(fb, "user", "save", Saves.UserId), "slot_00");
        var five = Saves.Gather(c5.Windows, Saves.OldUserFolders(c5.Windows, new[] { fb, fcf }, null), () => c5.Busy, () => when)!;
        Require(Did(five) == "slot_00<shared slot_03<c" && Slot(shared5, "slot_03") == xCopy && Saves.SlotNames(shared5).Count == 2
                && !SaveText.Gathered(five).Contains("only in that backup", StringComparison.Ordinal),
                "A copy the same as one kept in another slot is kept there, and not said to be in the backup only (did: " + Did(five) + ").");

        // (a merge that fails while it copies the slots: the shared save as it was, the old one still there)
        var h = P(c.Home, "Games", "Longshot", "v3.0.0-rc.4");
        MakeSave(P(h, "user", "save", Saves.UserId), "H", "slot_01");
        Age(sharedSave, when);
        Age(P(h, "user", "save"), when.AddDays(1));
        var (keptShared, backedUp) = (Tree(sharedSave), Directory.EnumerateFiles(sharedSave, "*", SearchOption.AllDirectories).Count()
                                                        + Directory.EnumerateFiles(P(h, "user", "save"), "*", SearchOption.AllDirectories).Count());
        var copied = 0;
        Saves.Fault = NoLink(s => { if (s == "file" && ++copied > backedUp) throw new IOException("disk full"); });
        try { Fails(() => Saves.Gather(c.Windows, Saves.OldUserFolders(c.Windows, new[] { h }, null), () => c.Busy, () => when.AddHours(2)), "Nothing was changed", "A merge that fails while it copies."); }
        finally { Saves.Fault = NoLink(); }
        Require(copied > backedUp && Tree(sharedSave) == keptShared && Directory.Exists(P(h, "user", "save"))
                && !Directory.Exists(P(c.Windows.LongshotUser, ".save.longshot-staging")), "After a merge that failed while copying: everything as it was.");
        Saves.Fault = null;
    }

    static void GatherLink(string root)
    {
        // (Longshot#639: a build started on its own, without --user, keeps its saves in its own folder's user\save. The launcher's start
        // took them in and renamed the folder aside, so that game found no save, began a new one there, and the next start of the launcher
        // took that in and moved it again: every start moved the folder. The folder taken in is now a link to the shared save, so the
        // game started on its own plays the same adventure, and the next start finds nothing to take in)
        var c = new Case(root, "gather-link");
        var build = P(c.Home, "Games", "Longshot", "nightly-170-6c9edffe");
        var user = P(build, "user");
        var ownSave = P(user, "save");
        var sharedSave = P(c.Windows.LongshotUser, "save");
        var when = new DateTime(2026, 11, 17, 12, 0, 0);
        MakeSave(P(ownSave, Uid), "OUT", "slot_00", "slot_01");
        Age(ownSave, when.AddDays(-1));
        var played = Tree(ownSave);
        var old = Saves.OldUserFolders(c.Windows, new[] { build }, null);

        var g = Saves.Gather(c.Windows, old, () => c.Busy, () => when)!;
        var aside = P(user, "save.moved-20261117-120000");
        Require(g.Moved.SequenceEqual(old) && Tree(aside) == played && Tree(sharedSave) == played,
                "Taken in as before: the shared save has it, the folder is renamed aside, nothing is deleted.");
        Require(new DirectoryInfo(ownSave).LinkTarget != null && Tree(ownSave) == played,
                "user\\save is then a link to the shared save: a game started on its own finds the saves there.");

        Require(LauncherLog.Tail(6).Any(l => l.Contains(" saves link: ", StringComparison.Ordinal) && l.Contains("is now a link to", StringComparison.Ordinal)
                                             && l.Contains(Privacy.Scrub(sharedSave), StringComparison.Ordinal)),
                "The link is in the launcher's log: " + string.Join(" | ", LauncherLog.Tail(6)));

        // (that game plays on and saves, through the link)
        var progress = P(sharedSave, Uid, "slot_00", "progress.sav");
        File.WriteAllText(P(ownSave, Uid, "slot_00", "progress.sav"), "played on");
        Require(File.ReadAllText(progress) == "played on", "What the game started on its own saves is the shared save.");

        // (the launcher's next starts: nothing to take in, nothing moved, whatever the game saved meanwhile)
        var entries = Directory.EnumerateFileSystemEntries(user).Order(StringComparer.Ordinal).ToList();
        for (var start = 1; start <= 3; start++)
            Require(Saves.Gather(c.Windows, old, () => c.Busy, () => when.AddHours(start)) == null
                    && Directory.EnumerateFileSystemEntries(user).Order(StringComparer.Ordinal).SequenceEqual(entries)
                    && Tree(aside) == played && File.ReadAllText(progress) == "played on" && Directory.GetDirectories(c.Windows.BackupRoot).Length == 1,
                    $"Start {start} of the launcher moves nothing: the game's folder as it was, one backup only, the saves as the game left them.");

        Require(Saves.SaveIn(build) == null && Saves.LinksToShared(c.Windows, ownSave) && !Saves.LinksToShared(c.Windows, aside),
                "A linked user\\save is not a save the folder holds itself (so Delete backs nothing up), and is the launcher's own link to the shared save.");

        // (a link that cannot be made, a drive with no junctions: what a gather always left, the folder renamed aside)
        var d = new Case(root, "gather-nolink");
        var plain = P(d.Home, "Games", "Longshot", "nightly-170-6c9edffe", "user");
        MakeSave(P(plain, "save", Uid), "OUT");
        var plainTree = Tree(P(plain, "save"));
        Saves.Fault = s => { if (s == "link") throw new IOException("no junction on this drive"); };
        SaveGathered? plainGather;
        try { plainGather = Saves.Gather(d.Windows, Saves.OldUserFolders(d.Windows, new[] { Path.GetDirectoryName(plain)! }, null), () => d.Busy, () => when); }
        finally { Saves.Fault = null; }
        Require(plainGather is { Moved.Count: 1 } && !Directory.Exists(P(plain, "save")) && Tree(P(plain, "save.moved-20261117-120000")) == plainTree
                && Tree(P(d.Windows.LongshotUser, "save")) == plainTree, "A link that cannot be made leaves the gather as it was: taken in, renamed aside, nothing failed.");
    }

    static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget != null;

    static void LinkSavesChecks(string root)
    {
        // (Saves.LinkSaves: the link a gather leaves where it took a save in, made too where a game folder has no user\save, or one with no
        // save in it, so that a build started on its own reads the shared saves from its first start)
        var c = new Case(root, "link-saves");
        var when = new DateTime(2026, 11, 17, 13, 0, 0);
        var sharedSave = P(c.Windows.LongshotUser, "save");
        string Game(string name)
        {
            var dir = P(c.Home, "Games", "Longshot", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(P(dir, GameFolder.StampName), $"version={name}\n");
            return dir;
        }
        IReadOnlyList<string> Link(SaveEnvironment env, params string[] folders) => Saves.LinkSaves(env, folders, () => c.Busy, () => when);

        // (no user\save: the link is made, and the shared save folder with it; what the game writes there is the shared save)
        var fresh = Game("nightly-170-6c9edffe");
        var link = P(fresh, "user", "save");
        Require(Link(c.Windows, fresh).SequenceEqual(new[] { fresh }) && IsLink(link) && Saves.LinksToShared(c.Windows, link) && Directory.Exists(sharedSave),
                "A game folder with no user\\save is given the link, and the shared save folder is there.");
        MakeSave(P(link, Uid), "NEW", "slot_00");
        Require(Tree(sharedSave) == Tree(P(link)) && File.Exists(P(sharedSave, Uid, "slot_00", "progress.sav")),
                "A save the game makes through the link is in the shared save.");
        Require(Link(c.Windows, fresh).Count == 0 && IsLink(link), "Done once: the second start makes nothing.");

        // (a folder that is no game folder, and a folder that is not there: untouched)
        var photos = P(c.Home, "Games", "Longshot", "photos");
        Directory.CreateDirectory(photos);
        Require(Link(c.Windows, photos, P(c.Home, "nowhere")).Count == 0 && !Directory.Exists(P(photos, "user")) && !Directory.Exists(P(c.Home, "nowhere")),
                "A folder that is not a game folder, or not there, is left alone.");

        // (only on Windows)
        var onLinux = Game("v3.1.0");
        Require(Link(c.Linux, onLinux).Count == 0 && !Directory.Exists(P(onLinux, "user")), "Nothing on Linux, where the game shares the saves itself.");

        // (a folder with the game's own options and no save: set aside, nothing deleted, then the link)
        var options = Game("nightly-171-7a7a7a7a");
        Directory.CreateDirectory(P(options, "user", "save", Uid));
        File.WriteAllText(P(options, "user", "save", Uid, "option.sav"), "options only");
        Require(Link(c.Windows, options).Count == 1 && IsLink(P(options, "user", "save"))
                && File.ReadAllText(P(options, "user", "save.moved-20261117-130000", Uid, "option.sav")) == "options only"
                && File.ReadAllText(P(sharedSave, Uid, "option.sav")) == "option-NEW",
                "A folder with no save in it, only the game's options, is renamed aside and given the link; the shared save's own options stay.");

        Require(LauncherLog.Tail(4).Any(l => l.Contains(" saves link: ", StringComparison.Ordinal) && l.Contains("what was there is renamed", StringComparison.Ordinal)
                                             && l.Contains("save.moved-20261117-130000", StringComparison.Ordinal)),
                "What was set aside is in the launcher's log: " + string.Join(" | ", LauncherLog.Tail(4)));

        // (an empty one: replaced, nothing to set aside)
        var empty = Game("nightly-172-8b8b8b8b");
        Directory.CreateDirectory(P(empty, "user", "save"));
        Require(Link(c.Windows, empty).Count == 1 && IsLink(P(empty, "user", "save")) && Directory.GetDirectories(P(empty, "user"), "save.moved-*").Length == 0,
                "An empty user\\save is replaced by the link.");

        // (a save in it: the gather's to take in, not the link's; the player's own links stay)
        var held = Game("nightly-173-9c9c9c9c");
        MakeSave(P(held, "user", "save", Uid), "HELD");
        var heldTree = Tree(P(held, "user", "save"));
        var synced = P(c.Tmp, "synced-saves");
        Directory.CreateDirectory(synced);
        var theirs = Game("nightly-174-adadadad");
        Directory.CreateDirectory(P(theirs, "user"));
        Directory.CreateSymbolicLink(P(theirs, "user", "save"), synced);
        var userLink = Game("nightly-175-bebebebe");
        Directory.CreateSymbolicLink(P(userLink, "user"), synced);
        Require(Link(c.Windows, held, theirs, userLink).Count == 0 && Tree(P(held, "user", "save")) == heldTree && !IsLink(P(held, "user", "save"))
                && Path.GetFullPath(new DirectoryInfo(P(theirs, "user", "save")).LinkTarget!) == Path.GetFullPath(synced) && !Directory.Exists(P(userLink, "user", "save.moved-20261117-130000")),
                "A folder with a save in it, a link of the player's own, and a user\\ that is a link are left alone.");
        Require(!Saves.LinksToShared(c.Windows, P(theirs, "user", "save")), "The player's own link is not the launcher's.");

        // (a game folder whose user\save is the shared save itself, a portable launcher put beside its game: never made a link to itself)
        var inside = Game("nightly-181-b4b4b4b4");
        var itself = c.Windows with { UserFolder = P(inside, "user") };
        Directory.CreateDirectory(P(inside, "user", "save", Uid));
        File.WriteAllText(P(inside, "user", "save", Uid, "option.sav"), "options only");
        var insideTree = Tree(P(inside, "user", "save"));
        Require(Link(itself, inside).Count == 0 && !IsLink(P(inside, "user", "save")) && Tree(P(inside, "user", "save")) == insideTree
                && Directory.GetDirectories(P(inside, "user"), "save.moved-*").Length == 0,
                "A game folder whose user\\save is the shared save itself is left alone: never set aside, never a link to itself.");
        Directory.Delete(P(inside, "user", "save"), true);
        Require(Link(itself, inside).Count == 0 && !IsLink(P(inside, "user", "save")), "Nor made where the shared save is yet to be.");

        // (the shared save gone: the link names nothing now; its folder is made again, the link stays)
        Directory.Delete(sharedSave, true);
        Require(!Directory.Exists(link) && Link(c.Windows, fresh).Count == 0 && Directory.Exists(sharedSave) && Directory.Exists(link) && IsLink(link),
                "A link whose shared save folder is gone: the folder is made again, the link kept.");

        // (never while Longshot runs, nor against another launcher's gather; no error)
        var busy = Game("nightly-176-cfcfcfcf");
        Require(Saves.LinkSaves(c.Windows, new[] { busy }, () => new[] { "Longshot" }, () => when).Count == 0 && !Directory.Exists(P(busy, "user")),
                "Not while Longshot runs: the next start tries again.");
        Saves.GatherWait = TimeSpan.FromMilliseconds(100);
        try
        {
            using (Saves.HoldGatherLock(c.Windows))
                Require(Link(c.Windows, busy).Count == 0 && !Directory.Exists(P(busy, "user")), "Another gather holds the lock: nothing is changed, nothing fails.");
        }
        finally { Saves.GatherWait = TimeSpan.FromSeconds(10); }
        Require(Link(c.Windows, busy).Count == 1 && IsLink(P(busy, "user", "save")), "And the next start makes it.");

        // (a link that cannot be made: what was there stays, no error; a folder set aside for it stays set aside)
        var failing = Game("nightly-177-d0d0d0d0");
        Directory.CreateDirectory(P(failing, "user", "save", Uid));
        File.WriteAllText(P(failing, "user", "save", Uid, "option.sav"), "options only");
        Saves.Fault = NoLink();
        try { Require(Link(c.Windows, failing).Count == 0 && !Directory.Exists(P(failing, "user", "save")), "A link that cannot be made is not an error."); }
        finally { Saves.Fault = null; }
        Require(LauncherLog.Tail(3).Any(l => l.Contains(" saves link: no link could be made at ", StringComparison.Ordinal) && l.Contains("stays renamed", StringComparison.Ordinal)),
                "A link that could not be made is in the launcher's log: " + string.Join(" | ", LauncherLog.Tail(3)));
        Require(File.ReadAllText(P(failing, "user", "save.moved-20261117-130000", Uid, "option.sav")) == "options only", "What was set aside is still there.");
        Require(Link(c.Windows, failing).Count == 1 && IsLink(P(failing, "user", "save")), "The next start makes the link where the folder was set aside.");

        // (a portable launcher's link: its folder moved, so the link names nothing; made again at the new place. A link of the player's own that
        // names nothing, such as a synced folder on a drive that is out, is not touched)
        var before = c.Windows with { UserFolder = P(c.Tmp, "portable-before", LauncherLayout.PortableData, "user") };
        var portable = Game("nightly-178-e1e1e1e1");
        Require(Link(before, portable).Count == 1 && Saves.LinksToShared(before, P(portable, "user", "save")), "A portable launcher's link names its own user folder.");
        Directory.Delete(P(c.Tmp, "portable-before"), true);
        var after = c.Windows with { UserFolder = P(c.Tmp, "portable-after", LauncherLayout.PortableData, "user") };
        var away = Game("nightly-179-f2f2f2f2");
        Directory.CreateDirectory(P(away, "user"));
        Directory.CreateSymbolicLink(P(away, "user", "save"), P(c.Tmp, "drive-out", "Longshot-saves"));
        Require(!Directory.Exists(P(portable, "user", "save")) && Link(after, portable, away).SequenceEqual(new[] { portable })
                && Saves.LinksToShared(after, P(portable, "user", "save")) && Directory.Exists(P(portable, "user", "save"))
                && !Directory.Exists(P(away, "user", "save")) && Path.GetFullPath(new DirectoryInfo(P(away, "user", "save")).LinkTarget!) == Path.GetFullPath(P(c.Tmp, "drive-out", "Longshot-saves")),
                "A portable launcher's link whose folder moved is made again at the new place; a dangling link of the player's own is left.");

        // (what a delete does with it: the link goes, never the save it names)
        var gone = Game("nightly-180-a3a3a3a3");
        MakeSave(P(sharedSave, Uid), "KEPT");
        Link(c.Windows, gone);
        var kept = Tree(sharedSave);
        BuildRegistry.DeleteFolder(gone, Platform.Windows, null, null, c.Windows);
        Require(!Directory.Exists(gone) && Tree(sharedSave) == kept && kept != "", "Deleting a folder that holds the link takes the link and never the save it names.");

        // (a link that names something other than the shared save, as a junction made wrong would: taken away again, the folder as it was)
        var wrong = Game("nightly-182-c5c5c5c5");
        var elsewhere = P(c.Tmp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Saves.LinkMaker = (l, _) => FileLinks.TryDirectoryLink(l, elsewhere);
        try
        {
            Require(Link(c.Windows, wrong).Count == 0 && !Directory.Exists(P(wrong, "user", "save")) && !IsLink(P(wrong, "user", "save")),
                    "A link that does not name the shared save is taken away again.");
        }
        finally { Saves.LinkMaker = null; }
        Require(Directory.Exists(elsewhere) && Link(c.Windows, wrong).Count == 1 && Saves.LinksToShared(c.Windows, P(wrong, "user", "save")),
                "What it named stays, and the next start makes the right link.");

        // (the junction's data, as FSCTL_SET_REPARSE_POINT takes it: the tag, the length after the header, the two names with their zeros)
        var data = FileLinks.JunctionData(P(c.Tmp, "target", "save") + Path.DirectorySeparatorChar);
        var shown = Path.GetFullPath(P(c.Tmp, "target", "save"));
        var system = Encoding.Unicode.GetBytes(@"\??\" + shown);
        var print = Encoding.Unicode.GetBytes(shown);
        Require(BitConverter.ToUInt32(data, 0) == 0xA0000003 && BitConverter.ToUInt16(data, 4) == data.Length - 8 && BitConverter.ToUInt16(data, 6) == 0
                && BitConverter.ToUInt16(data, 8) == 0 && BitConverter.ToUInt16(data, 10) == system.Length
                && BitConverter.ToUInt16(data, 12) == system.Length + 2 && BitConverter.ToUInt16(data, 14) == print.Length
                && data.Length == 16 + system.Length + 2 + print.Length + 2
                && data.AsSpan(16, system.Length).SequenceEqual(system) && data[16 + system.Length] == 0 && data[17 + system.Length] == 0
                && data.AsSpan(16 + system.Length + 2, print.Length).SequenceEqual(print) && data[^1] == 0 && data[^2] == 0,
                "The junction's reparse data: a mount point, the target as the system and the player name it, each ended by a zero.");
    }
}
