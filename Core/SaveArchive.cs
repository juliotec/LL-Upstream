using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// What a .zip of saves holds, read without unpacking it: its slots (and when each was saved, from its progress.sav's time in the
/// zip), whether it is a whole save folder (Whole: SaveArchive.ExportSave's layout) or save files alone, and what keeps it from
/// being imported (empty: it can be)
public sealed record SaveArchiveContents(string Path, IReadOnlyList<SaveSlot> Slots, IReadOnlyList<string> Problems, bool Whole)
{
    public bool Valid => Problems.Count == 0;
}

/// What a .zip written holds: how many files, the slots of Longshot's save in it, and what was left out for being a link
public sealed record ZipWritten(string Zip, int Files, IReadOnlyList<SaveSlot> Slots, IReadOnlyList<string> Skipped);

/// A .zip unpacked for an import, in a temporary folder of its own (Root, deleted with Dispose). Whole: Folder is the save folder
/// it held (save\, its profiles and everything in them, as they were); else Folder is one profile's save files. Problems: what
/// keeps it from being imported (empty: it can be)
public sealed class UnpackedSaves(string root, string folder, bool whole, IReadOnlyList<string> problems) : IDisposable
{
    public string Root { get; } = root;
    public string Folder { get; } = folder;
    public bool Whole { get; } = whole;
    public IReadOnlyList<string> Problems { get; } = problems;
    public bool Valid => Problems.Count == 0;

    public void Dispose() => SaveArchive.DeleteQuietly(Root);
}

/// The game's saves in one file (docs/saves.md, As a .zip file). ExportSave zips Longshot's save folder whole, as it is on the disk:
/// save/ as the zip's top folder, every profile, slot, file and folder in it, with their times; only links are left out (never
/// followed out of the folder). What made it, when and its slots are the zip's comment, so nothing is added among the files.
/// ExportSlot zips one slot's folder whole the same way (slot_NN/ on top).
///
/// Importing one, two rules, by what the zip holds:
///   - a whole save folder (ExportSave's: every file under save/, and save/&lt;Longshot's user id&gt;/ holding a save): unpacked as it
///     is, checked for safety only (no name out of the zip, no link, the caps below), and put in place of Longshot's save folder
///     whole (ImportWhole);
///   - anything else (a zip made by hand of a save folder, Hookshot's or an emulator's, a slot's): only save files, as a Hookshot
///     save is checked (HookshotSaves.Validate), copied into Longshot's save as an import from a folder is (Saves.Copy)
public static class SaveArchive
{
    /// The zip's top folder in ExportSave's layout: the save folder's own name
    public const string Top = "save";

    /// The largest zip read, the most it may unpack to, the most entries, and the largest one file of a whole save folder: far
    /// above a save folder's (a few hundred kilobytes for each profile, a few profiles), so only a bomb stops at them. ExportSave
    /// refuses a folder above them, so every zip it writes can be imported again. (Settable for the tests only)
    public static long MaxZip { get; internal set; } = 512L << 20;
    public static long MaxUnpacked { get; internal set; } = 512L << 20;
    public static int MaxEntries { get; internal set; } = 10000;
    public static long MaxFile { get; internal set; } = 64L << 20;

    /// The note the launcher's first zips held among the files (format 1): read by nothing, left out of an import
    public const string OldManifest = "longshot-save.json";

    static readonly Regex SaveName = new(@"^(account\.sav|option\.sav|slot_[0-9]{2,3}/(progress|caption)\.sav)$", RegexOptions.CultureInvariant);
    static readonly Regex SlotFolder = new("^slot_[0-9]{2,3}$", RegexOptions.CultureInvariant);

    /// What a file manager adds to a zip it makes, never a save: left out, not a reason to refuse it
    static bool Junk(string name)
    {
        var parts = name.TrimEnd('/').Split('/');
        var last = parts[^1];
        return parts.Contains("__MACOSX") || last is ".DS_Store" or "Thumbs.db" or "desktop.ini";
    }

    /// The name a zip gives the saves it holds, by default: "Longshot saves 2026-11-17.zip"
    public static string DefaultName(DateTime now) => $"Longshot saves {now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.zip";

    static void NotRunning(Func<IReadOnlyList<string>>? running)
    {
        if ((running ?? (() => Saves.Running(Platforms.Current)))() is { Count: > 0 } busy)
            throw new SaveFailure($"Close {string.Join(" and ", busy)} first: a running game can write its save while it is copied.",
                                  SaveRefusal.Running, busy);
    }

    static bool IsLink(FileSystemInfo f) => f.LinkTarget != null || f.Attributes.HasFlag(FileAttributes.ReparsePoint);

    static string Stamp(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    static string SlotsLine(IReadOnlyList<SaveSlot> slots) =>
        slots.Count == 0 ? "none" : string.Join(", ", slots.Select(s => s.Saved is { } t ? $"{s.Name} (saved {Stamp(t)})" : s.Name));

    /// Longshot's save folder (saveFolder: &lt;its user folder&gt;\save, every profile in it) zipped whole into zip, a new file or one
    /// written over (the window asked first): made beside it and put in its place by a rename, so a failure leaves what was there.
    /// Refused (SaveFailure): a game runs, the zip would go inside the folder, the folder holds no save, it is larger than the caps
    /// let an import take it back. Links in it are left out (Skipped), never followed. Only the save folder: nothing beside it in
    /// the user folder (longshot_options.ini, mouse_sens.txt, present.txt: the settings are separate)
    public static ZipWritten ExportSave(string saveFolder, string zip, string launcher, Func<IReadOnlyList<string>>? running = null,
                                        Func<DateTime>? now = null)
    {
        if (!Directory.Exists(saveFolder) || Saves.Find(saveFolder).Count == 0) throw new SaveFailure($"{saveFolder} holds no save to put in a .zip.");
        var made = (now ?? (() => DateTime.Now))();
        var mine = System.IO.Path.Combine(saveFolder, Saves.UserId);
        var slots = Directory.Exists(mine) ? Saves.Describe(mine) : Saves.Find(saveFolder).SelectMany(p => Saves.Describe(p.Path)).ToList();
        var comment = $"Longshot saves: the whole save folder, made by the Longshot Launcher {launcher} on {Stamp(made)}. " +
                      $"Slots: {SlotsLine(slots)}. Import it with Saves > From an emulator… > A .zip file….";
        return Write(saveFolder, Top, zip, comment, slots, running);
    }

    /// One slot's folder (save\slot_NN, everything in it) zipped whole the same way, slot_NN/ on top
    public static ZipWritten ExportSlot(string save, string slot, string zip, string launcher, Func<IReadOnlyList<string>>? running = null,
                                        Func<DateTime>? now = null)
    {
        if (!SlotFolder.IsMatch(slot)) throw new SaveFailure($"{slot} is not a save slot.");
        var folder = System.IO.Path.Combine(save, slot);
        if (!File.Exists(System.IO.Path.Combine(folder, "progress.sav"))) throw new SaveFailure($"{folder} holds no saved game (no progress.sav).");
        var made = (now ?? (() => DateTime.Now))();
        var slots = Saves.Describe(save).Where(s => s.Name == slot).ToList();
        var comment = $"Longshot saves: one slot's folder, made by the Longshot Launcher {launcher} on {Stamp(made)}. Slots: {SlotsLine(slots)}.";
        return Write(folder, slot, zip, comment, slots, running);
    }

    static ZipWritten Write(string folder, string top, string zip, string comment, IReadOnlyList<SaveSlot> slots, Func<IReadOnlyList<string>>? running)
    {
        NotRunning(running);
        var full = System.IO.Path.GetFullPath(zip);
        var root = Saves.Real(folder);
        if (Saves.Inside(root, Saves.Real(full))) throw new SaveFailure($"Choose a place outside the save folder ({folder}) for the .zip.");
        if (IsLink(new DirectoryInfo(folder))) throw new SaveFailure($"{folder} is a link: a .zip of saves holds only the folder's own files.");

        // (everything in the folder as it is: files, folders, empty ones too; a link is left out, never followed)
        var files = new List<(string Name, string From)>();
        var folders = new List<string>();
        var skipped = new List<string>();
        void Walk(string dir, string name)
        {
            var any = false;
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                any = true;
                var n = name + "/" + e.Name;
                if (IsLink(e)) skipped.Add(n);
                else if (e is DirectoryInfo) Walk(e.FullName, n);
                else
                {
                    if (((FileInfo)e).Length > MaxFile) throw new SaveFailure($"{n} is larger than a .zip of saves may hold ({MaxFile >> 20} MB a file), so nothing was written.");
                    files.Add((n, e.FullName));
                }
            }
            if (!any) folders.Add(name + "/");
        }
        Walk(root, top);
        if (files.Count + folders.Count > MaxEntries)
            throw new SaveFailure($"{folder} holds {files.Count + folders.Count} files and folders, more than a .zip of saves may ({MaxEntries}), so nothing was written.");
        if (files.Sum(f => new FileInfo(f.From).Length) > MaxUnpacked)
            throw new SaveFailure($"{folder} is larger than a .zip of saves may hold ({MaxUnpacked >> 20} MB), so nothing was written.");

        var part = full + ".longshot-part";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                archive.Comment = comment;
                foreach (var d in folders) archive.CreateEntry(d);
                foreach (var (name, from) in files)
                {
                    Saves.Fault?.Invoke("file");
                    // (the entry keeps the file's time: when each slot was saved, after an import too)
                    archive.CreateEntryFromFile(from, name, CompressionLevel.Optimal);
                }
            }
            Saves.Fault?.Invoke("swap");
            File.Move(part, full, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(part);
            throw new SaveFailure($"The .zip could not be written ({e.Message}). Nothing was changed.");
        }
        return new ZipWritten(full, files.Count, slots, skipped);
    }

    /// One entry of a zip to unpack: its name below the folder it goes into (a folder's ends with /)
    sealed record Entry(ZipArchiveEntry Zip, string Name);

    sealed record ZipPlan(List<Entry> Entries, List<string> Problems, bool Whole);

    /// The zip's entries, which rule applies (Whole: ExportSave's layout) and its problems: nothing is unpacked
    static ZipPlan Plan(ZipArchive archive)
    {
        var problems = new List<string>();
        if (archive.Entries.Count > MaxEntries)
            return new ZipPlan(new(), new() { $"It holds {archive.Entries.Count} files, far more than a save folder" }, false);
        var named = new List<(ZipArchiveEntry Entry, string Name)>();
        foreach (var e in archive.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.Length == 0 || Junk(name)) continue;
            var parts = name.TrimEnd('/').Split('/');
            if (name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl) || parts.Any(p => p is "" or "." or ".."))
            {
                problems.Add($"{e.FullName} is not a save file's name");
                continue;
            }
            // (a link, as some zip tools store it: the Unix mode's file type)
            if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                problems.Add($"{e.FullName} is a link");
                continue;
            }
            named.Add((e, name));
        }
        var whole = named.Count > 0 && named.All(n => n.Name.StartsWith(Top + "/", StringComparison.Ordinal))
                    && named.Any(n => n.Name.StartsWith($"{Top}/{Saves.UserId}/", StringComparison.Ordinal) && !n.Name.EndsWith('/')
                                      && (n.Name == $"{Top}/{Saves.UserId}/account.sav" || n.Name.Split('/') is { Length: 4 } p && SlotFolder.IsMatch(p[2])));
        var plan = whole ? Whole(named, problems) : Files(named, problems);
        if (plan.Sum(p => p.Zip.Length) > MaxUnpacked) problems.Add("It unpacks to far more than a save folder");
        return new ZipPlan(plan, problems, whole);
    }

    /// ExportSave's layout: every entry as it is below save/, only safety checked (sizes, a file in it twice)
    static List<Entry> Whole(List<(ZipArchiveEntry Entry, string Name)> named, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<Entry>();
        foreach (var (e, name) in named)
        {
            var rest = name[(Top.Length + 1)..];
            if (rest.Length == 0) continue;
            if (!seen.Add(rest.TrimEnd('/')))
            {
                problems.Add($"{rest} is in it twice");
                continue;
            }
            if (e.Length > MaxFile) problems.Add($"{rest} is larger than a save folder's files are ({MaxFile >> 20} MB)");
            else plan.Add(new Entry(e, rest));
        }
        if (!plan.Any(p => p.Name.Split('/') is { Length: 3 } n && n[0] == Saves.UserId && SlotFolder.IsMatch(n[1]) && n[2] == "progress.sav"))
            problems.Add("Longshot's profile in it holds no save");
        return plan;
    }

    /// Any other layout: save files only, under the folders every one of them sits in
    static List<Entry> Files(List<(ZipArchiveEntry Entry, string Name)> named, List<string> problems)
    {
        // (a zip made of a save folder, or of a folder above it: the folders every file sits in, left out)
        var files = named.Where(n => !n.Name.EndsWith('/')).ToList();
        var prefix = "";
        if (files.Count > 0)
        {
            var folders = files.Select(f => f.Name.Split('/')[..^1]).ToList();
            var depth = 0;
            while (folders.All(f => f.Length > depth) && folders.All(f => f[depth] == folders[0][depth]) && !SlotFolder.IsMatch(folders[0][depth])) depth++;
            prefix = string.Concat(folders[0].Take(depth).Select(p => p + "/"));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<Entry>();
        foreach (var (e, name) in named)
        {
            // (the folders above the save files, as their own entries)
            if (name.EndsWith('/') && prefix.StartsWith(name, StringComparison.Ordinal)) continue;
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                problems.Add($"{e.FullName} is not a save file");
                continue;
            }
            var rest = name[prefix.Length..];
            if (rest.EndsWith('/'))
            {
                if (rest.TrimEnd('/') is var d && d.Length > 0 && !SlotFolder.IsMatch(d)) problems.Add($"{e.FullName} is not a save file");
                continue;
            }
            if (rest == OldManifest) continue;
            if (!SaveName.IsMatch(rest))
            {
                problems.Add($"{rest} is not a save file");
                continue;
            }
            if (!seen.Add(rest))
            {
                problems.Add($"{rest} is in it twice");
                continue;
            }
            if (e.Length == 0) problems.Add($"{rest} is empty");
            else if (e.Length > HookshotSaves.MaxSaveFile) problems.Add($"{rest} is too large to be a save");
            else plan.Add(new Entry(e, rest));
        }
        var slots = plan.Select(p => p.Name.Split('/')).Where(p => p.Length == 2).GroupBy(p => p[0], StringComparer.Ordinal).ToList();
        if (slots.Count == 0) problems.Add("There is no save slot");
        foreach (var s in slots.Where(s => s.All(p => p[1] != "progress.sav"))) problems.Add($"{s.Key} has no progress.sav");
        return plan;
    }

    static ZipArchive? Open(string zip, List<string> problems, out FileStream? stream)
    {
        stream = null;
        try
        {
            var info = new FileInfo(zip);
            if (!info.Exists)
            {
                problems.Add($"There is no file at {zip}");
                return null;
            }
            if (IsLink(info))
            {
                problems.Add("The .zip is a link");
                return null;
            }
            if (info.Length > MaxZip)
            {
                problems.Add("The .zip is far larger than a save folder is");
                return null;
            }
            stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            stream?.Dispose();
            stream = null;
            problems.Add("It is not a .zip that can be read: " + e.Message);
            return null;
        }
    }

    /// What zip holds and whether it can be imported, read without unpacking it: the slots of Longshot's save in a whole save
    /// folder, else the slots of the save files
    public static SaveArchiveContents Read(string zip)
    {
        var problems = new List<string>();
        using var archive = Open(zip, problems, out var stream);
        using var _ = stream;
        if (archive == null) return new SaveArchiveContents(zip, Array.Empty<SaveSlot>(), problems, false);
        var plan = Plan(archive);
        problems.AddRange(plan.Problems);
        var under = plan.Whole ? Saves.UserId + "/" : "";
        var slots = plan.Entries.Where(f => f.Name.StartsWith(under, StringComparison.Ordinal))
                        .Select(f => f.Name[under.Length..].Split('/')).Where(p => p.Length == 2 && SlotFolder.IsMatch(p[0])).Select(p => p[0])
                        .Distinct().Order(StringComparer.Ordinal)
                        .Select(s => new SaveSlot(s, plan.Entries.FirstOrDefault(f => f.Name == under + s + "/progress.sav")?.Zip.LastWriteTime.LocalDateTime))
                        .ToList();
        return new SaveArchiveContents(zip, slots, problems, plan.Whole);
    }

    /// Where an import's zip is unpacked: one folder each, in the system's temporary folder (never in a save folder, nor among the
    /// backups); what one cut short left there a day ago or more is deleted first
    internal static string TempRoot { get; set; } = System.IO.Path.GetTempPath();

    const string TempPrefix = "longshot-saves-zip-";

    /// The zip into a new temporary folder, with its files' times. A whole save folder comes out as it is (save\ and everything in
    /// it), and Longshot's profile in it must hold a save; save files alone are checked again after (HookshotSaves.Validate). Never
    /// throws for a zip that is not a save: Problems says why, and nothing of it is left. Dispose deletes the folder
    public static UnpackedSaves Unpack(string zip)
    {
        CleanTemp();
        var root = System.IO.Path.Combine(TempRoot, TempPrefix + Guid.NewGuid().ToString("N")[..12]);
        var problems = new List<string>();
        bool whole;
        string folder;
        using (var archive = Open(zip, problems, out var stream))
        using (stream)
        {
            if (archive == null) return new UnpackedSaves(root, root, false, problems);
            var plan = Plan(archive);
            whole = plan.Whole;
            folder = whole ? System.IO.Path.Combine(root, Top) : root;
            problems.AddRange(plan.Problems);
            if (problems.Count > 0) return new UnpackedSaves(root, folder, whole, problems);
            var max = whole ? MaxFile : HookshotSaves.MaxSaveFile;
            try
            {
                Directory.CreateDirectory(folder);
                var top = System.IO.Path.GetFullPath(folder);
                long total = 0;
                foreach (var f in plan.Entries)
                {
                    var to = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, f.Name.TrimEnd('/').Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!Saves.Inside(top, to)) throw new InvalidDataException($"{f.Name} is not a save file's name");
                    if (f.Name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(to);
                        continue;
                    }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                    // (the size the zip says is not trusted: at most a file's cap is read, whatever it claims)
                    using (var from = f.Zip.Open())
                    using (var write = new FileStream(to, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[81920];
                        long n = 0;
                        int read;
                        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            n += read;
                            total += read;
                            if (n > max) throw new InvalidDataException($"{f.Name} is larger than a save folder's files are");
                            if (total > MaxUnpacked) throw new InvalidDataException("It unpacks to far more than a save folder");
                            write.Write(buffer, 0, read);
                        }
                    }
                    File.SetLastWriteTime(to, f.Zip.LastWriteTime.LocalDateTime);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                DeleteQuietly(root);
                return new UnpackedSaves(root, folder, whole, new[] { e is InvalidDataException ? e.Message : "It could not be unpacked: " + e.Message });
            }
        }
        var invalid = whole
            ? (Saves.Describe(System.IO.Path.Combine(folder, Saves.UserId)).Any(s => s.Saved != null) ? Array.Empty<string>() : new[] { "Longshot's profile in it holds no save" })
            : HookshotSaves.Validate(folder);
        if (invalid.Count > 0)
        {
            DeleteQuietly(root);
            return new UnpackedSaves(root, folder, whole, invalid);
        }
        return new UnpackedSaves(root, folder, whole, Array.Empty<string>());
    }

    /// A whole save folder unpacked (Unpack, Whole) in place of Longshot's (&lt;its user folder&gt;\save), as it is: every profile, slot,
    /// file and folder. Longshot's is backed up whole first (import-zip-&lt;stamp&gt;); keepSettings keeps each of its profiles'
    /// option.sav. The new folder is made beside it and takes its place by two renames, so a failure leaves it as it was. Refused
    /// (SaveFailure) while a game or an emulator runs (not asked on a dry run, which only says where the backup would go)
    public static SaveCopy ImportWhole(UnpackedSaves unpacked, SaveEnvironment env, bool keepSettings = false, bool dryRun = false,
                                       string? backupRoot = null, Func<IReadOnlyList<string>>? running = null, Func<DateTime>? now = null)
    {
        if (!unpacked.Whole || !unpacked.Valid) throw new SaveFailure("This .zip is not a whole save folder that can be imported.");
        var target = System.IO.Path.Combine(env.SavesUser, Top);
        var had = Directory.Exists(target);
        var stamp = (now ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var root = backupRoot ?? env.BackupRoot;
        var backup = System.IO.Path.Combine(root, $"import-zip-{stamp}");
        for (var n = 2; Directory.Exists(backup) || File.Exists(backup); n++) backup = System.IO.Path.Combine(root, $"import-zip-{stamp}-{n}");
        var slots = Saves.SlotNames(System.IO.Path.Combine(unpacked.Folder, Saves.UserId));
        var result = new SaveCopy(unpacked.Folder, target, had ? backup : null, slots, Array.Empty<string>(), keepSettings, dryRun);
        if (dryRun) return result;
        NotRunning(running);
        if (had && Saves.Inside(Saves.Real(target), Saves.Real(backup))) throw new SaveFailure($"Choose a backup folder outside the save folder ({target}).");

        var real = had ? Saves.Real(target) : System.IO.Path.GetFullPath(target);
        var (staging, aside) = Saves.Beside(real);
        Saves.DeleteTree(staging);
        Saves.DeleteTree(aside);
        if (had)
        {
            try
            {
                Directory.CreateDirectory(root);
                Saves.CopyTree(real, backup);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Saves.DeleteTree(backup);
                throw new SaveFailure($"{target} could not be backed up ({e.Message}), so nothing was copied: it is as it was.");
            }
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(real)!);
            Saves.CopyTree(unpacked.Folder, staging);
            if (keepSettings && had)
                foreach (var profile in Directory.EnumerateDirectories(staging))
                {
                    var kept = System.IO.Path.Combine(real, System.IO.Path.GetFileName(profile), "option.sav");
                    if (File.Exists(kept)) File.Copy(kept, System.IO.Path.Combine(profile, "option.sav"), overwrite: true);
                }
            if (had)
            {
                Directory.Move(real, aside);
                try
                {
                    Saves.Fault?.Invoke("swap");
                    Directory.Move(staging, real);
                }
                catch (Exception) when (!Directory.Exists(real))
                {
                    Directory.Move(aside, real);
                    throw;
                }
                Saves.DeleteTree(aside);
            }
            else Directory.Move(staging, real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Saves.DeleteTree(staging);
            var where = had ? $" A backup of it is in {backup}." : "";
            throw new SaveFailure($"The copy failed ({e.Message}). Nothing was changed: {target} is as it was.{where}", backup: had ? backup : null);
        }
        return result;
    }

    /// The temporary folders of imports that never finished (the launcher closed in between), a day old or more
    static void CleanTemp()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(TempRoot, TempPrefix + "*"))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-1)) DeleteQuietly(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
