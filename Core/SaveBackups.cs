using System.Globalization;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// What made a backup, from its folder's name: a copy's (Saves.Copy: import, export, hookshot, restore and the like, the
/// command first), the saves brought together (Saves.Gather: gather), a version deleted (Saves.BackUpForDelete: delete), a slot
/// edited in Progress (the save editor: edit), or a folder the launcher did not name (Other)
public enum BackupKind { Import, Export, Hookshot, Restore, Gather, Delete, Edit, Other }

/// One save kept in the backups folder, which Restore can put back: the backup folder it is in (one per copy, named for what
/// made it and when), the save folder in it (a gather's or a delete's backup holds one per version folder: Part names it), what
/// made it, the program or folder on the other side of that copy (Name: lichii, zip, folder…; null when none), when, its slots
/// and its size in bytes
public sealed record BackupSave(string Folder, string Path, BackupKind Kind, string? Name, string? Part, DateTime When,
                                IReadOnlyList<SaveSlot> Slots, long Bytes, string? Profile = null)
{
    /// The backups folder it is in (one of SaveEnvironment.BackupRoots)
    public string Root => System.IO.Path.GetDirectoryName(Folder) ?? Folder;
}

/// The backups every copy makes before it replaces a save (docs/saves.md: Restore from a backup): listed newest first, and put
/// back into Longshot's save as an import is, Longshot's save backed up first. Nothing here deletes or changes a backup
public static class SaveBackups
{
    /// The other side of a restore, for its own backup's name (restore-backup-&lt;stamp&gt;) and the copy's report
    public static readonly SaveProgram Program = new("backup", "the backup", new Dictionary<Platform, string[]>(), Array.Empty<string>(), Array.Empty<string>());

    // (<command>-<name>-<yyyyMMdd-HHmmss>[-n], as Saves.Copy names its backups; gather- and delete- have no name)
    static readonly Regex Named = new(@"^(?<kind>[a-z]+)(?:-(?<name>.+?))?-(?<stamp>\d{8}-\d{6})(?:-\d+)?$", RegexOptions.CultureInvariant);

    static BackupKind KindOf(string kind) => kind switch
    {
        "import" => BackupKind.Import,
        "export" => BackupKind.Export,
        "hookshot" => BackupKind.Hookshot,
        "restore" => BackupKind.Restore,
        "gather" => BackupKind.Gather,
        "delete" => BackupKind.Delete,
        "edit" => BackupKind.Edit,
        _ => BackupKind.Other,
    };

    /// Every save in the backups folders (env.BackupRoots: on Linux the old $XDG_DATA_HOME/oot-save-backups too), newest first; a backup that holds several (a gather's) is one entry per
    /// save, in the order its folders are named. Where one folder holds several profiles, only the game's is listed when it is
    /// there (Saves.UserId); else each is, with its profile (Profile) to tell them apart. A folder in it that holds no save, a hidden one (a copy's staging) and one that
    /// cannot be read are left out. None when there is no backups folder yet
    public static IReadOnlyList<BackupSave> List(SaveEnvironment env)
    {
        var list = new List<BackupSave>();
        var folders = new List<string>();
        foreach (var root in env.BackupRoots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            try { if (Directory.Exists(root)) folders.AddRange(Directory.EnumerateDirectories(root)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var folder in folders)
        {
            var name = System.IO.Path.GetFileName(folder);
            if (name.StartsWith('.')) continue;
            var m = Named.Match(name);
            var kind = m.Success ? KindOf(m.Groups["kind"].Value) : BackupKind.Other;
            DateTime when;
            if (!m.Success || !DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out when))
                when = Directory.GetLastWriteTime(folder);
            string? other = m.Success && m.Groups["name"].Success ? m.Groups["name"].Value : null;
            // (a copy's backup is the save folder itself; a gather's or a delete's holds one folder per version folder, named for
            // it, with the save\ folder's profiles in it: Part)
            var found = new List<(SaveProfile Profile, string? Part)>();
            try
            {
                found.AddRange(Saves.Find(folder).Select(p => (p, (string?)null)));
                if (found.Count == 0)
                    foreach (var sub in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
                        found.AddRange(Saves.Find(sub).Select(p => (p, (string?)System.IO.Path.GetFileName(sub))));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SaveFailure) { continue; }
            // (a save folder backed up whole, an import's from a .zip, a gather's or a delete's, may hold more than the game's
            // profile, a stray one, a copy made by hand: where the game's is there, it is the one listed; where it is not, each is
            // listed with its profile named, so two rows never look the same)
            foreach (var group in found.GroupBy(f => f.Part))
            {
                var profiles = group.ToList();
                if (profiles.Count > 1 && profiles.FirstOrDefault(f => string.Equals(f.Profile.Id, Saves.UserId, StringComparison.OrdinalIgnoreCase))
                        is { Profile: not null } mine)
                    profiles = new() { mine };
                foreach (var (p, part) in profiles)
                {
                    var slots = Saves.Describe(p.Path);
                    if (slots.Count == 0) continue;
                    list.Add(new BackupSave(folder, p.Path, kind, other, part, when, slots, Bytes(p.Path), profiles.Count > 1 ? p.Id : null));
                }
            }
        }
        return list.OrderByDescending(b => b.When).ThenBy(b => b.Folder, StringComparer.Ordinal).ThenBy(b => b.Part, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Profile, StringComparer.Ordinal).ToList();
    }

    static long Bytes(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// The backup's save into Longshot's (the save of the player the game opens: Saves.ImportTarget), as an import is: Longshot's
    /// own copied whole to the backups first (restore-backup-&lt;stamp&gt;), its slots replaced by the backup's, its account.sav and
    /// (unless keepSettings) option.sav too. The backup itself is only read. Refused (SaveFailure) as every copy is: a game or
    /// an emulator running, a link in it, no slot
    public static SaveCopy Restore(BackupSave backup, SaveEnvironment env, bool keepSettings = false, bool dryRun = false,
                                   Func<IReadOnlyList<string>>? running = null, Func<DateTime>? now = null)
    {
        if (!Directory.Exists(backup.Path)) throw new SaveFailure($"The backup in {backup.Path} is not there any more.");
        return Saves.Copy(backup.Path, Saves.ImportTarget(env), env.BackupRoot, "restore", Program.Name, keepSettings, dryRun,
                          running: running, now: now);
    }
}
