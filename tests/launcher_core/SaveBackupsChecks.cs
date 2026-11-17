using LongshotLauncher.Core;

/// Core/SaveBackups.cs: the backups every copy makes, listed and put back. The backups are made here the way the copies make
/// them (Saves.Copy, Saves.BackUpForDelete, and a gather's folder laid out as Saves.Gather lays it out), of fake saves only.
static class SaveBackupsChecks
{
    public static int Count;
    const string Uid = "5a3c9e1f0b7d24680000000000000001";

    static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("save backups: " + message);
        Count++;
    }

    static void Fails(Action action, string contains, string message, SaveRefusal? reason = null)
    {
        try { action(); }
        catch (SaveFailure e)
        {
            Require(e.Message.Contains(contains) && (reason == null || e.Reason == reason), $"{message} (said: {e.Message})");
            return;
        }
        throw new Exception("save backups: not refused: " + message);
    }

    static string P(params string[] parts) => Path.Combine(parts);

    static void MakeSave(string folder, string tag, params string[] slots)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(P(folder, "account.sav"), "account-" + tag);
        File.WriteAllText(P(folder, "option.sav"), "option-" + tag);
        foreach (var s in slots)
        {
            Directory.CreateDirectory(P(folder, s));
            File.WriteAllText(P(folder, s, "progress.sav"), "progress-" + tag + s);
            File.WriteAllText(P(folder, s, "caption.sav"), "caption-" + tag + s);
        }
    }

    static string Tree(string folder) =>
        !Directory.Exists(folder) ? "" : string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/') + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal));

    public static void Run(string root)
    {
        var home = P(root, "home");
        var data = P(home, ".local", "share");
        var env = new SaveEnvironment(Platform.Linux, k => k switch { "HOME" => home, "XDG_DATA_HOME" => data, _ => null });
        var backups = env.BackupRoot;
        var none = (IReadOnlyList<string>)Array.Empty<string>();
        Require(SaveBackups.List(env).Count == 0, "No backups folder yet: no backups.");

        var shared = Saves.ImportTarget(env);
        MakeSave(shared, "FIRST", "slot_00", "slot_01");
        var lichii = P(root, "lichii-save");
        MakeSave(lichii, "LI", "slot_02");
        var t0 = new DateTime(2026, 11, 10, 9, 0, 0);

        // (an import: Longshot's save as it was is the backup)
        Saves.Copy(lichii, shared, backups, "import", "lichii", running: () => none, now: () => t0);
        // (an export to a folder: that folder's save is the backup)
        var folder = P(root, "portable");
        MakeSave(folder, "PORT", "slot_05");
        Saves.Copy(shared, folder, backups, "export", "folder", running: () => none, now: () => t0.AddDays(1));
        // (a gather: each version folder's save\ under its name, profile folders inside)
        var gather = P(backups, "gather-20261112-080000");
        MakeSave(P(gather, "shared", Uid), "GSHARED", "slot_00");
        MakeSave(P(gather, "v3.0.0-rc.1", Uid), "GRC1", "slot_00", "slot_03");
        // (a version deleted: its own save copied whole first)
        var game = P(root, "games", "nightly");
        MakeSave(P(game, "user", "save", Uid), "NIGHT", "slot_04");
        Saves.BackUpForDelete(env, game, P(game, "user", "save"), () => t0.AddDays(3));
        // (what is no backup: a copy's staging, a folder with no save, a file)
        MakeSave(P(backups, ".save.longshot-staging"), "STAGING", "slot_00");
        Directory.CreateDirectory(P(backups, "empty-20261101-000000"));
        File.WriteAllText(P(backups, "notes.txt"), "x");
        // (a folder the launcher did not name, with a save: listed by its name, at its folder's time)
        MakeSave(P(backups, "my copy"), "MINE", "slot_01");
        Directory.SetLastWriteTime(P(backups, "my copy"), t0.AddDays(-30));

        var list = SaveBackups.List(env);
        string Show(BackupSave b) => $"{b.Kind}/{b.Name}/{b.Part}/{b.When:yyyyMMdd-HHmmss}/{string.Join("+", b.Slots.Select(s => s.Name))}";
        var shown = list.Select(Show).ToList();
        Require(shown.SequenceEqual(new[]
                {
                    "Delete///20261113-090000/slot_04".Replace("///", "//nightly/"),
                    "Gather///20261112-080000/slot_00".Replace("///", "//shared/"),
                    "Gather///20261112-080000/slot_00+slot_03".Replace("///", "//v3.0.0-rc.1/"),
                    "Export/folder//20261111-090000/slot_05",
                    "Import/lichii//20261110-090000/slot_00+slot_01",
                    $"Other///{t0.AddDays(-30):yyyyMMdd-HHmmss}/slot_01",
                }), "Every backup's saves, newest first, with what made each, the other side, and the version folder: " + string.Join(" | ", shown));
        Require(list.All(b => b.Bytes > 0) && list[0].Path == P(backups, "delete-20261113-090000", "nightly", Uid)
                && list[4].Path == P(backups, "import-lichii-20261110-090000"),
                "Each names its save folder and its size.");

        // ---- restore ----
        var importBackup = list.First(b => b.Kind == BackupKind.Import);
        var before = Tree(importBackup.Path);
        var at = t0.AddDays(5);
        var dry = SaveBackups.Restore(importBackup, env, dryRun: true, now: () => at);
        Require(dry.Backup == P(backups, "restore-backup-20261115-090000") && !Directory.Exists(dry.Backup),
                "The confirm page's dry run names the backup a restore makes of Longshot's save, and makes nothing.");
        Fails(() => SaveBackups.Restore(importBackup, env, running: () => new[] { "Longshot" }), "Close Longshot first",
              "A restore is refused while the game runs.", SaveRefusal.Running);
        var now = Tree(shared);
        var done = SaveBackups.Restore(importBackup, env, running: () => none, now: () => at);
        Require(Tree(shared) == before, "The backup's save is Longshot's again: its slots, account.sav and option.sav.");
        Require(Tree(importBackup.Path) == before, "The backup itself is left as it was.");
        Require(done.Backup != null && Tree(done.Backup) == now, "Longshot's save from before the restore is backed up first, whole.");
        var after = SaveBackups.List(env);
        Require(after[0].Kind == BackupKind.Restore && after[0].Name == "backup" && after.Count == list.Count + 1,
                "The restore's own backup is the newest in the list.");

        // (a gather's: the version folder's save; keep Longshot's game settings)
        var rc1 = list.First(b => b.Part == "v3.0.0-rc.1");
        var options = File.ReadAllText(P(shared, "option.sav"));
        SaveBackups.Restore(rc1, env, keepSettings: true, running: () => none, now: () => at.AddMinutes(1));
        Require(Saves.SlotNames(shared).SequenceEqual(new[] { "slot_00", "slot_03" }) && File.ReadAllText(P(shared, "account.sav")) == "account-GRC1"
                && File.ReadAllText(P(shared, "option.sav")) == options,
                "A gather's version folder's save comes back, Longshot's game settings kept when asked.");

        // (a whole save folder imported from a .zip: its backup is the save folder, Longshot's profile in it restorable)
        var zipped = P(root, "zipped", SaveArchive.Top);
        MakeSave(P(zipped, Uid), "ZIPPED", "slot_01");
        SaveArchive.ExportSave(zipped, P(root, "whole.zip"), "v1", () => none);
        var withRc1 = Tree(shared);
        using (var unpacked = SaveArchive.Unpack(P(root, "whole.zip")))
            SaveArchive.ImportWhole(unpacked, env, running: () => none, now: () => at.AddMinutes(2));
        var fromZip = SaveBackups.List(env).First();
        Require(fromZip.Kind == BackupKind.Import && fromZip.Name == "zip" && fromZip.Path == P(backups, "import-zip-20261115-090200", Uid)
                && Tree(fromZip.Path) == withRc1, "A whole folder's import: its backup listed, Longshot's profile in it, as it was.");
        SaveBackups.Restore(fromZip, env, running: () => none, now: () => at.AddMinutes(3));
        Require(Tree(shared) == withRc1, "And restored.");

        Directory.Delete(rc1.Path, true);
        Fails(() => SaveBackups.Restore(rc1, env, running: () => none), "not there any more", "A backup deleted since it was listed is refused.");

        // ---- several profiles in one folder (review of #182): the game's alone when it is there, else each named ----
        const string Stray = "0123456789abcdef0123456789abcdef", Other = "fedcba9876543210fedcba9876543210";
        var wholeTwo = P(backups, "import-zip-20261101-070000");
        MakeSave(P(wholeTwo, Uid), "MINE", "slot_00");
        MakeSave(P(wholeTwo, Stray), "STRAY", "slot_03");
        var gatherTwo = P(backups, "gather-20261101-060000");
        MakeSave(P(gatherTwo, "v3.0.0", Stray), "A", "slot_01");
        MakeSave(P(gatherTwo, "v3.0.0", Other), "B", "slot_02");
        MakeSave(P(gatherTwo, "nightly", Uid), "N", "slot_00");
        MakeSave(P(gatherTwo, "nightly", Other), "NO", "slot_04");
        var several = SaveBackups.List(env);
        var fromWhole = several.Where(b => b.Folder == wholeTwo).ToList();
        Require(fromWhole.Count == 1 && fromWhole[0].Path == P(wholeTwo, Uid) && fromWhole[0].Profile == null,
                "A whole save folder backed up with a stray profile beside the game's: only the game's is listed.");
        var fromGather = several.Where(b => b.Folder == gatherTwo).Select(b => $"{b.Part}/{b.Profile}/{Path.GetFileName(b.Path)}").ToList();
        Require(fromGather.SequenceEqual(new[] { $"nightly//{Uid}", $"v3.0.0/{Stray}/{Stray}", $"v3.0.0/{Other}/{Other}" }),
                "A version folder with the game's profile lists it alone; one with two others lists both, each named. " + string.Join(", ", fromGather));
        SaveBackups.Restore(fromWhole[0], env, running: () => none, now: () => at.AddMinutes(10));
        Require(File.ReadAllText(P(shared, "account.sav")) == "account-MINE" && Saves.SlotNames(shared).SequenceEqual(new[] { "slot_00" }),
                "And the one restored is the game's profile, not the stray one.");

        // (an export's backup: the other program's old save, restored into Longshot)
        var exported = several.First(b => b.Kind == BackupKind.Export);
        SaveBackups.Restore(exported, env, running: () => none, now: () => at.AddMinutes(11));
        Require(Saves.SlotNames(shared).SequenceEqual(new[] { "slot_05" }) && File.ReadAllText(P(shared, "account.sav")) == "account-PORT",
                "An export's backup restores into Longshot (the other program's save as it was before the export).");

        // (a backup with no account.sav or option.sav: Longshot keeps its own, only the slots change)
        var bare = P(backups, "import-folder-20261101-050000");
        MakeSave(bare, "BARE", "slot_01");
        File.Delete(P(bare, "account.sav"));
        File.Delete(P(bare, "option.sav"));
        var keptAccount = File.ReadAllText(P(shared, "account.sav"));
        var keptOptions = File.ReadAllText(P(shared, "option.sav"));
        SaveBackups.Restore(SaveBackups.List(env).First(b => b.Folder == bare), env, running: () => none, now: () => at.AddMinutes(12));
        Require(Saves.SlotNames(shared).SequenceEqual(new[] { "slot_01" }) && File.ReadAllText(P(shared, "account.sav")) == keptAccount
                && File.ReadAllText(P(shared, "option.sav")) == keptOptions,
                "A backup with no account.sav or option.sav: its slots come back, Longshot's own files stay.");
    }
}
