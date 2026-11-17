using System.IO.Compression;
using LongshotLauncher.Core;

/// Core/SaveArchive.cs: Longshot's save folder in one .zip, whole, and back; and the other zips an import takes (save files alone,
/// made by hand or by another program). Fake saves made here only, and zips written here to be what a careless or a hostile one
/// would be: a path out of the zip, a drive, a link, a bomb, a file that is no save.
static class SaveArchiveChecks
{
    public static int Count;
    static readonly IReadOnlyList<string> None = Array.Empty<string>();

    static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("save archive: " + message);
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
        throw new Exception("save archive: not refused: " + message);
    }

    static string P(params string[] parts) => Path.Combine(parts);

    static void MakeSave(string folder, string tag, DateTime saved, params string[] slots)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(P(folder, "account.sav"), "account-" + tag);
        File.WriteAllText(P(folder, "option.sav"), "option-" + tag);
        foreach (var s in slots)
        {
            Directory.CreateDirectory(P(folder, s));
            File.WriteAllText(P(folder, s, "progress.sav"), "progress-" + tag + s);
            File.WriteAllText(P(folder, s, "caption.sav"), "caption-" + tag + s);
            File.SetLastWriteTime(P(folder, s, "progress.sav"), saved);
        }
    }

    /// Every file under folder, its bytes, and every empty folder: two trees the same, byte for byte
    static string Tree(string folder) =>
        !Directory.Exists(folder) ? "" : string.Join("\n",
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/') + "=" + Convert.ToHexString(File.ReadAllBytes(f)))
                .Concat(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Where(d => !Directory.EnumerateFileSystemEntries(d).Any())
                    .Select(d => Path.GetRelativePath(folder, d).Replace('\\', '/') + "/"))
                .Order(StringComparer.Ordinal));

    /// A zip written by hand: each (name, text) an entry; a null text is a folder entry; unixMode sets the entry's Unix mode
    static string Zip(string path, params (string Name, string? Text, int UnixMode)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (name, text, mode) in entries)
        {
            var e = archive.CreateEntry(name);
            if (mode != 0) e.ExternalAttributes = mode << 16;
            if (text == null) continue;
            using var w = new StreamWriter(e.Open());
            w.Write(text);
        }
        return path;
    }

    static (string, string?, int) F(string name, string? text = "x") => (name, text, 0);

    static void Refused(string zip, string contains, string message)
    {
        var read = SaveArchive.Read(zip);
        Require(!read.Valid && read.Problems.Any(p => p.Contains(contains)), $"Read: {message} (said: {string.Join("; ", read.Problems)})");
        using var unpacked = SaveArchive.Unpack(zip);
        Require(!unpacked.Valid && unpacked.Problems.Any(p => p.Contains(contains)) && !Directory.Exists(unpacked.Root),
                $"Unpack: {message}, and nothing of it is left (said: {string.Join("; ", unpacked.Problems)})");
    }

    public static void Run(string root)
    {
        var temp = Directory.CreateDirectory(P(root, "tmp")).FullName;
        var was = (SaveArchive.TempRoot, SaveArchive.MaxEntries, SaveArchive.MaxFile, SaveArchive.MaxUnpacked);
        SaveArchive.TempRoot = temp;
        try { Checks(root, temp); }
        finally
        {
            (SaveArchive.TempRoot, SaveArchive.MaxEntries, SaveArchive.MaxFile, SaveArchive.MaxUnpacked) = was;
            Saves.Fault = null;
        }
    }

    static void Checks(string root, string temp)
    {
        var saved = new DateTime(2026, 11, 14, 21, 30, 10, DateTimeKind.Local);
        var made = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Local);
        var data = P(root, "home", ".local", "share");
        var env = new SaveEnvironment(Platform.Linux, k => k switch { "HOME" => P(root, "home"), "XDG_DATA_HOME" => data, _ => null });
        var saveFolder = P(env.LongshotUser, SaveArchive.Top);
        Require(Saves.ImportTarget(env) == P(saveFolder, Saves.UserId), "(the save folder is the one the game's profile is in)");

        // (Longshot's save folder as a player's may be: its profile with slots, a file and a folder nobody knows, a nested one, an
        // empty one, another profile, and a binary file)
        var mine = P(saveFolder, Saves.UserId);
        MakeSave(mine, "HS", saved, "slot_00", "slot_02");
        File.WriteAllText(P(mine, "notes.txt"), "a file nobody knows");
        Directory.CreateDirectory(P(mine, "slot_00", "extra", "deeper"));
        File.WriteAllBytes(P(mine, "slot_00", "extra", "deeper", "blob.bin"), Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        Directory.CreateDirectory(P(mine, "empty folder"));
        MakeSave(P(saveFolder, "0123456789abcdef0123456789abcdef"), "OTHER", saved, "slot_04");
        var before = Tree(saveFolder);
        // (beside the save folder, in the user folder: the settings, which stay out)
        File.WriteAllText(P(env.LongshotUser, "longshot_options.ini"), "settings");
        File.WriteAllText(P(env.LongshotUser, "mouse_sens.txt"), "1.0");

        // ---- export: the folder whole ----
        var zip = P(root, "out", "Longshot saves 2026-11-17.zip");
        var written = SaveArchive.ExportSave(saveFolder, zip, "v1.2.0", () => None, () => made);
        Require(written.Slots.Select(s => s.Name).SequenceEqual(new[] { "slot_00", "slot_02" }) && written.Skipped.Count == 0 && File.Exists(zip)
                && !File.Exists(zip + ".longshot-part"), "ExportSave writes the folder, made beside the zip and renamed into place.");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Require(names.All(n => n.StartsWith("save/", StringComparison.Ordinal)) && names.Contains($"save/{Saves.UserId}/slot_00/extra/deeper/blob.bin")
                    && names.Contains($"save/{Saves.UserId}/notes.txt") && names.Contains($"save/{Saves.UserId}/empty folder/")
                    && names.Contains("save/0123456789abcdef0123456789abcdef/slot_04/progress.sav") && names.Count == written.Files + 1,
                    "The save folder is the zip's top folder, every file and folder in it, profiles, nested and empty ones too. " + string.Join(", ", names));
            Require(!names.Any(n => n.Contains("longshot_options.ini") || n.Contains("mouse_sens.txt") || n.Contains(SaveArchive.OldManifest)),
                    "Nothing from beside the save folder, and no file added among the saves.");
            Require(archive.Comment.Contains("v1.2.0") && archive.Comment.Contains("slot_00") && archive.Comment.Contains("2026-11-17"),
                    "What made it, when and its slots are the zip's comment. " + archive.Comment);
        }
        Require(SaveArchive.DefaultName(made) == "Longshot saves 2026-11-17.zip", "The name a zip starts with: the day it is made.");
        var read = SaveArchive.Read(zip);
        Require(read.Valid && read.Whole && read.Slots.Select(s => s.Name).SequenceEqual(new[] { "slot_00", "slot_02" })
                && read.Slots.All(s => s.Saved is { } t && Math.Abs((t - saved).TotalSeconds) <= 2),
                "Read: a whole save folder, its Longshot profile's slots and when each was saved. " + string.Join(", ", read.Problems));

        Fails(() => SaveArchive.ExportSave(saveFolder, P(root, "out", "busy.zip"), "v1", () => new[] { "Longshot" }), "Close Longshot first",
              "Export is refused while the game runs.", SaveRefusal.Running);
        Require(!File.Exists(P(root, "out", "busy.zip")), "A refused export writes nothing.");
        Fails(() => SaveArchive.ExportSave(saveFolder, P(mine, "inside.zip"), "v1", () => None), "outside the save folder", "A zip inside the save folder is refused.");
        Fails(() => SaveArchive.ExportSave(P(root, "no-save"), P(root, "out", "none.zip"), "v1", () => None), "holds no save", "No save folder: nothing to export.");
        var bytes = File.ReadAllBytes(zip);
        Saves.Fault = s => { if (s == "swap") throw new IOException("disk full"); };
        Fails(() => SaveArchive.ExportSave(saveFolder, zip, "v9", () => None), "could not be written", "A failed export says so.");
        Saves.Fault = null;
        Require(File.ReadAllBytes(zip).SequenceEqual(bytes) && !File.Exists(zip + ".longshot-part"),
                "A failed export leaves the zip that was there as it was, and no part file.");
        SaveArchive.MaxFile = 200;
        Fails(() => SaveArchive.ExportSave(saveFolder, P(root, "out", "big.zip"), "v1", () => None), "larger than a .zip of saves may hold",
              "A file above the cap: refused, so every zip written can be imported again.");
        SaveArchive.MaxFile = 64L << 20;

        // ---- links: left out, never followed ----
        if (TestLinks.Available)
        {
            var outside = P(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(P(outside, "secret.txt"), "not the save's");
            File.CreateSymbolicLink(P(mine, "link.txt"), P(outside, "secret.txt"));
            Directory.CreateSymbolicLink(P(mine, "linked folder"), outside);
            var linked = SaveArchive.ExportSave(saveFolder, P(root, "out", "linked.zip"), "v1", () => None);
            using var archive = ZipFile.OpenRead(P(root, "out", "linked.zip"));
            Require(linked.Skipped.Count == 2 && linked.Skipped.All(s => s.EndsWith("link.txt") || s.EndsWith("linked folder"))
                    && !archive.Entries.Any(e => e.FullName.Contains("link") || e.FullName.Contains("secret")),
                    "A link in the save folder is left out and said, and what it points to never goes in.");
            File.Delete(P(mine, "link.txt"));
            Directory.Delete(P(mine, "linked folder"));
        }

        // ---- unpack: the folder as it was, byte for byte ----
        using (var unpacked = SaveArchive.Unpack(zip))
        {
            Require(unpacked.Valid && unpacked.Whole && Path.GetFileName(unpacked.Folder) == SaveArchive.Top
                    && Saves.Inside(Path.GetFullPath(temp), Path.GetFullPath(unpacked.Root)),
                    "Unpack: the whole save folder, into a folder of its own in the temporary folder. " + string.Join("; ", unpacked.Problems));
            Require(Tree(unpacked.Folder) == before, "Every file and folder comes out as it went in, byte for byte: the unknown file, the nested and empty folders, the other profile.");
            Require(Saves.Describe(P(unpacked.Folder, Saves.UserId)).All(s => s.Saved is { } t && Math.Abs((t - saved).TotalSeconds) <= 2), "Each slot keeps when it was saved.");
            var rootFolder = unpacked.Root;
            unpacked.Dispose();
            Require(!Directory.Exists(rootFolder), "Dispose deletes the temporary folder.");
        }

        // ---- import a whole folder: in place of Longshot's, all of it ----
        File.WriteAllText(P(mine, "option.sav"), "option-CHANGED");
        File.WriteAllText(P(mine, "only-here.txt"), "goes with the old folder");
        var old = Tree(saveFolder);
        var backups = P(root, "backups");
        using (var unpacked = SaveArchive.Unpack(zip))
        {
            var dry = SaveArchive.ImportWhole(unpacked, env, dryRun: true, backupRoot: backups, now: () => made);
            Require(dry.Backup != null && Path.GetFileName(dry.Backup) == "import-zip-20261117-100000" && !Directory.Exists(backups),
                    "The confirm page's dry run names the backup, and makes nothing.");
            Fails(() => SaveArchive.ImportWhole(unpacked, env, backupRoot: backups, running: () => new[] { "Longshot" }), "Close Longshot first",
                  "An import is refused while the game runs.", SaveRefusal.Running);
            Saves.Fault = s => { if (s == "swap") throw new IOException("in use"); };
            Fails(() => SaveArchive.ImportWhole(unpacked, env, backupRoot: backups, running: () => None, now: () => made.AddSeconds(1)),
                  "Nothing was changed", "A failed import says so.");
            Saves.Fault = null;
            Require(Tree(saveFolder) == old && !Directory.EnumerateDirectories(env.LongshotUser).Any(d => Path.GetFileName(d).StartsWith('.')),
                    "A failed import leaves the save folder as it was, and nothing beside it.");
            var done = SaveArchive.ImportWhole(unpacked, env, backupRoot: backups, running: () => None, now: () => made.AddSeconds(2));
            Require(Tree(saveFolder) == before, "The zip's save folder is Longshot's now, as it was: every profile, file and folder, the ones it lacked gone.");
            Require(done.Backup != null && Tree(done.Backup) == old, "Longshot's save folder is backed up whole first.");
            Require(File.ReadAllText(P(env.LongshotUser, "longshot_options.ini")) == "settings", "The settings beside the save folder stay as they were.");
        }
        File.WriteAllText(P(mine, "option.sav"), "option-MINE");
        using (var unpacked = SaveArchive.Unpack(zip))
            SaveArchive.ImportWhole(unpacked, env, keepSettings: true, backupRoot: backups, running: () => None, now: () => made.AddSeconds(3));
        Require(File.ReadAllText(P(mine, "option.sav")) == "option-MINE", "Keep Longshot's game settings: its option.sav stays.");

        // ---- whole folders that are refused: only for safety ----
        Refused(Zip(P(root, "in", "w-up.zip"), F($"save/{Saves.UserId}/slot_00/progress.sav"), F("save/../escaped.sav")), "is not a save file's name",
                "A whole folder with a path out of the zip");
        Refused(Zip(P(root, "in", "w-link.zip"), F($"save/{Saves.UserId}/slot_00/progress.sav"), ($"save/{Saves.UserId}/x", "/etc/passwd", 0xA1FF)),
                "is a link", "A whole folder with a link");
        Refused(Zip(P(root, "in", "w-twice.zip"), F($"save/{Saves.UserId}/slot_00/progress.sav"), F($"save/{Saves.UserId}/a.txt"), F($"save/{Saves.UserId}/A.TXT")),
                "twice", "A whole folder with a file in it twice");
        Refused(Zip(P(root, "in", "w-nosave.zip"), F($"save/{Saves.UserId}/account.sav"), F($"save/{Saves.UserId}/slot_00/caption.sav")),
                "holds no save", "A whole folder whose Longshot profile holds no save");
        SaveArchive.MaxFile = 3;
        Refused(Zip(P(root, "in", "w-big.zip"), F($"save/{Saves.UserId}/slot_00/progress.sav", "four")), "larger than a save folder's files are",
                "A whole folder with a file above the cap");
        SaveArchive.MaxFile = 64L << 20;
        SaveArchive.MaxEntries = 10;
        var many = Enumerable.Range(0, 11).Select(i => F($"save/{Saves.UserId}/f{i}.txt")).Prepend(F($"save/{Saves.UserId}/slot_00/progress.sav")).ToArray();
        Refused(Zip(P(root, "in", "w-many.zip"), many), "far more than a save folder", "A whole folder above the entry cap");
        SaveArchive.MaxEntries = 10000;
        // (an unknown file, empty or not, is fine in a whole folder: only safety is checked there)
        using (var unpacked = SaveArchive.Unpack(Zip(P(root, "in", "w-unknown.zip"), F($"save/{Saves.UserId}/slot_00/progress.sav"),
                                                     F($"save/{Saves.UserId}/whatever.dat", ""), F("save/elsewhere/readme.txt"))))
            Require(unpacked.Valid && unpacked.Whole && Tree(unpacked.Folder).Contains("whatever.dat=") && Tree(unpacked.Folder).Contains("elsewhere/readme.txt"),
                    "A whole folder keeps what it holds, an empty file and another folder too. " + string.Join("; ", unpacked.Problems));

        // ---- other zips: save files only (made by hand, Hookshot's, an emulator's, the launcher's first ones) ----
        var wrapped = Zip(P(root, "in", "wrapped.zip"),
            F("My saves/", null), F("My saves/5a3c/", null), F("My saves/5a3c/account.sav"), F("My saves/5a3c/slot_01/", null),
            F("My saves/5a3c/slot_01/progress.sav"), F("My saves/5a3c/slot_01/caption.sav"), F("__MACOSX/My saves/._account.sav"),
            F("My saves/.DS_Store"), F("My saves/5a3c/Thumbs.db"));
        using (var unpacked = SaveArchive.Unpack(wrapped))
            Require(unpacked.Valid && !unpacked.Whole && Tree(unpacked.Folder) == "account.sav=78\nslot_01/caption.sav=78\nslot_01/progress.sav=78",
                    "The folders every save file sits in are skipped, and a file manager's leftovers left out. " + string.Join("; ", unpacked.Problems));
        using (var unpacked = SaveArchive.Unpack(Zip(P(root, "in", "hookshot.zip"), F("save/fedcba9876543210fedcba9876543210/account.sav"),
                                                     F("save/fedcba9876543210fedcba9876543210/slot_00/progress.sav"))))
            Require(unpacked.Valid && !unpacked.Whole, "Hookshot's save folder zipped (another profile id, not Longshot's): save files only, into Longshot's profile.");
        Refused(Zip(P(root, "in", "hookshot-extra.zip"), F("save/fedcba9876543210fedcba9876543210/slot_00/progress.sav"),
                    F("save/fedcba9876543210fedcba9876543210/readme.txt")), "readme.txt is not a save file", "Not Longshot's: a file that is no save refuses it");
        using (var unpacked = SaveArchive.Unpack(Zip(P(root, "in", "first.zip"), F("account.sav"), F("slot_00/progress.sav"), F(SaveArchive.OldManifest, "{}"))))
            Require(unpacked.Valid && !unpacked.Whole && !File.Exists(P(unpacked.Folder, SaveArchive.OldManifest)), "The launcher's first zips (their note left out).");

        var escapes = P(root, "escaped.sav");
        Refused(Zip(P(root, "in", "up.zip"), F("slot_00/progress.sav"), F("../escaped.sav")), "is not a save file's name", "A path out of the zip");
        Refused(Zip(P(root, "in", "up2.zip"), F("slot_00/progress.sav"), F("slot_00/../../escaped.sav")), "is not a save file's name", "A path out of the zip, further in");
        Require(!File.Exists(escapes) && !File.Exists(P(temp, "escaped.sav")), "Nothing is written outside the folder it unpacks to.");
        Refused(Zip(P(root, "in", "abs.zip"), F("/slot_00/progress.sav")), "is not a save file's name", "An absolute path");
        Refused(Zip(P(root, "in", "drive.zip"), F("C:/slot_00/progress.sav")), "is not a save file's name", "A drive");
        Refused(Zip(P(root, "in", "back.zip"), F(@"slot_00\..\..\escaped.sav")), "is not a save file's name", "A path out of it with backslashes");
        Refused(Zip(P(root, "in", "other.zip"), F("slot_00/progress.sav"), F("readme.txt")), "readme.txt is not a save file", "A file that is no save's");
        Refused(Zip(P(root, "in", "two-places.zip"), F("a/slot_00/progress.sav"), F("b/slot_01/progress.sav")), "is not a save file", "Save files in two places");
        Refused(Zip(P(root, "in", "empty.zip"), F("slot_00/progress.sav", "")), "is empty", "An empty save file");
        Refused(Zip(P(root, "in", "twice.zip"), F("slot_00/progress.sav"), F("slot_00/progress.sav")), "twice", "A file in it twice");
        Refused(Zip(P(root, "in", "noslot.zip"), F("account.sav"), F("option.sav")), "There is no save slot", "No slot");
        Refused(Zip(P(root, "in", "noprogress.zip"), F("slot_00/caption.sav")), "has no progress.sav", "A slot with no progress.sav");
        Refused(Zip(P(root, "in", "link.zip"), F("slot_00/progress.sav"), ("account.sav", "/etc/passwd", 0xA1FF)), "is a link", "A link");
        Refused(Zip(P(root, "in", "bomb.zip"), F("slot_00/progress.sav", new string('\0', (int)HookshotSaves.MaxSaveFile + 1))),
                "too large to be a save", "A save file larger than a save");
        Require(new FileInfo(P(root, "in", "bomb.zip")).Length < 64 << 10, "(The bomb is small.)");
        File.WriteAllText(P(root, "in", "not.zip"), "this is no zip");
        Refused(P(root, "in", "not.zip"), "not a .zip that can be read", "A file that is no zip");
        Refused(P(root, "in", "missing.zip"), "There is no file", "No file");

        // (save files alone: imported into Longshot's profile, as an import from a folder is)
        var other = P(root, "theirs");
        MakeSave(other, "NEW", saved, "slot_01");
        Zip(P(root, "in", "theirs.zip"), F("account.sav", "account-NEW"), F("option.sav", "option-NEW"), F("slot_01/progress.sav", "progress-NEW"));
        using (var unpacked = SaveArchive.Unpack(P(root, "in", "theirs.zip")))
        {
            var done = Saves.ImportExport(true, Saves.Zip, unpacked.Folder, env, backupRoot: backups, running: () => None, now: () => made.AddMinutes(5));
            Require(Saves.SlotNames(mine).SequenceEqual(new[] { "slot_01" }) && File.ReadAllText(P(mine, "account.sav")) == "account-NEW"
                    && File.Exists(P(mine, "notes.txt")) && Path.GetFileName(done.Backup!) == "import-zip-20261117-100500",
                    "Save files alone replace the slots of Longshot's profile; its other files stay (Saves.Copy).");
        }

        // (a temporary folder an import never finished, the launcher closed in between: gone once a day old)
        var stale = Directory.CreateDirectory(P(temp, "longshot-saves-zip-stale")).FullName;
        Directory.SetLastWriteTime(stale, DateTime.Now.AddDays(-2));
        var fresh = Directory.CreateDirectory(P(temp, "longshot-saves-zip-fresh")).FullName;
        using (SaveArchive.Unpack(zip)) { }
        Require(!Directory.Exists(stale) && Directory.Exists(fresh), "An unpack deletes what one cut short left a day ago, and nothing newer.");
    }
}
