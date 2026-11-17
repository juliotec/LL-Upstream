using LongshotLauncher.Core;

/// The backups the save editor (Progress) makes, edit-<first 16 characters of the profile id>-<yyyyMMdd-HHmmss>[-n] in the backups
/// folder, holding the profile folder itself: Restore from a backup lists them as what they are, not as a folder of an unknown name
static partial class Program
{
    static void EditBackupsChecks()
    {
        var root = Folder("edit-backups");
        var env = new SaveEnvironment(Platform.Linux, k => k switch { "HOME" => Path.Combine(root, "home"), "XDG_DATA_HOME" => Path.Combine(root, "data"), _ => null });
        void Profile(string folder, string tag)
        {
            Directory.CreateDirectory(Path.Combine(folder, "slot_00"));
            File.WriteAllText(Path.Combine(folder, "account.sav"), "account-" + tag);
            File.WriteAllText(Path.Combine(folder, "slot_00", "progress.sav"), "progress-" + tag);
        }
        var id16 = Saves.UserId[..16];
        Profile(Path.Combine(env.BackupRoot, $"edit-{id16}-20261117-153000"), "BEFORE");
        Profile(Path.Combine(env.BackupRoot, $"edit-{id16}-20261117-153000-2"), "BEFORE-2");
        var live = Saves.ImportTarget(env);
        Profile(live, "AFTER");

        var list = SaveBackups.List(env);
        Require(list.Count == 2 && list.All(b => b.Kind.ToString() == "Edit" && b.Name == id16 && b.When == new DateTime(2026, 11, 17, 15, 30, 0)),
                "A backup the save editor made is listed as an edit's, with its time from its name, twice in the same second: "
                + string.Join(", ", list.Select(b => b.Kind + " " + Path.GetFileName(b.Folder))));
        var done = SaveBackups.Restore(list.Single(b => b.Folder.EndsWith("153000")), env, running: () => Array.Empty<string>(), now: () => new DateTime(2026, 11, 17, 16, 0, 0));
        Require(File.ReadAllText(Path.Combine(live, "slot_00", "progress.sav")) == "progress-BEFORE" && done.Backup != null
                && File.ReadAllText(Path.Combine(done.Backup, "slot_00", "progress.sav")) == "progress-AFTER",
                "Restoring it puts the save as it was before the edit back, with the save now backed up first.");
    }
}
