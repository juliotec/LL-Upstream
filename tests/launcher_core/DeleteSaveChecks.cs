using LongshotLauncher.Core;

/// Deleting a version's folder never takes a save with it (BuildRegistry.DeleteFolder). On Windows a build started outside the
/// launcher (Explorer, the taskbar, Steam) keeps its save in its own &lt;folder&gt;\user\save, and the launcher's gather may not
/// have taken it in yet: skipped while Longshot ran at the launcher's start, or failed. Fake saves of text files only
static partial class Program
{
    static void DeleteSaveChecks()
    {
        var root = Folder("delete-saves");
        var home = Path.Combine(root, "home");
        string? Env(string k) => k switch
        {
            "HOME" or "USERPROFILE" => home,
            "APPDATA" => Path.Combine(home, "AppData", "Roaming"),
            "LOCALAPPDATA" => Path.Combine(home, "AppData", "Local"),
            "XDG_DATA_HOME" => Path.Combine(home, ".local", "share"),
            _ => null,
        };
        string Tree(string folder) =>
            !Directory.Exists(folder) ? "" : string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f) + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal));
        string Version(string name)
        {
            var dir = Path.Combine(root, "Games", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, GameFolder.StampName), $"version={name}\n");
            return dir;
        }
        void MakeSave(string save, string tag)
        {
            Directory.CreateDirectory(Path.Combine(save, "slot_00"));
            File.WriteAllText(Path.Combine(save, "account.sav"), "account-" + tag);
            File.WriteAllText(Path.Combine(save, "slot_00", "progress.sav"), "progress-" + tag);
        }

        // (a Windows build started from Explorer saved in its own folder; the launcher has not taken that save in)
        var game = Version("nightly-200-aaaaaaaa");
        var save = Path.Combine(game, "user", "save", Saves.UserId);
        MakeSave(save, "OWN");
        var own = Tree(save);
        try { BuildRegistry.DeleteFolder(game, Platform.Windows, null, Env); }
        catch (IOException) { }
        Require(Tree(save) == own, "Deleting a version's folder deleted the save in its user\\save, with no copy kept.");

        // (DeleteFolder with nowhere given to back it up: refused, and says where the save is)
        var windows = new SaveEnvironment(Platform.Windows, Env);
        var linux = new SaveEnvironment(Platform.Linux, Env);
        var sharedSave = Path.Combine(windows.LongshotUser, "save");
        // (Open saves folder shows the folder every build shares, the one ImportTarget's profile is in, on both systems)
        Require(windows.SaveFolder == sharedSave && linux.SaveFolder == Path.Combine(linux.LongshotUser, "save")
                && Path.GetDirectoryName(Saves.ImportTarget(windows)) == windows.SaveFolder
                && Path.GetDirectoryName(Saves.ImportTarget(linux)) == linux.SaveFolder,
            "The saves folder Open saves folder shows is LongshotUser's save, the one the game's profile is in.");
        var held = Path.GetFullPath(Path.Combine(game, "user", "save"));
        var ownSave = Tree(held);
        Require(Saves.SaveIn(game) == held, "A version's folder with its own save holds it (SaveIn).");
        var refused = false;
        try { BuildRegistry.DeleteFolder(game, Platform.Windows, null, Env); }
        catch (IOException e) { refused = e.Message.Contains(held, StringComparison.Ordinal); }
        Require(refused && Tree(save) == own, "DeleteFolder with no backups given refuses a folder that holds a save, and says where it is.");

        var when = new DateTime(2026, 11, 17, 9, 0, 0);
        var longshotRuns = () => (IReadOnlyList<string>)new[] { "Longshot" };
        var nothingRuns = () => (IReadOnlyList<string>)Array.Empty<string>();
        DeletedGame Delete(SaveEnvironment env, string folder, Func<IReadOnlyList<string>> running) =>
            BuildRegistry.DeleteGame(env, folder, env.Platform, null, running, () => when, Env);
        string WithSave(string name, string tag)
        {
            var dir = Version(name);
            MakeSave(Path.Combine(dir, "user", "save", Saves.UserId), tag);
            return dir;
        }

        // (the backup cannot be made: the folder and its save stay, and the home says why, naming the folder)
        Saves.Fault = s => { if (s == "backup") throw new IOException("disk full"); };
        DeletedGame noBackup;
        try { noBackup = Delete(windows, game, longshotRuns); }
        finally { Saves.Fault = null; }
        Require(!noBackup.Deleted && noBackup.Backup == null && Tree(save) == own && noBackup.Problem!.Contains("disk full", StringComparison.Ordinal)
                && noBackup.Said()!.Contains(game, StringComparison.Ordinal) && noBackup.Said()!.Contains("could not be backed up", StringComparison.Ordinal),
                "No backup could be made: the folder stays as it was, and the home says why (said: " + noBackup.Said() + ").");
        Require(!Directory.Exists(windows.BackupRoot) || !Directory.EnumerateDirectories(windows.BackupRoot, "delete-*").Any(),
                "A failed backup leaves no half copy.");

        // (the gather skipped, Longshot runs: the save is copied whole and checked, then the folder goes)
        var skipped = Delete(windows, game, longshotRuns);
        Require(skipped.Deleted && !Directory.Exists(game) && skipped.Gathered == null && skipped.Backup is { } b1 && Tree(b1) == ownSave
                && Path.GetFileName(b1) == "nightly-200-aaaaaaaa" && Path.GetFileName(Path.GetDirectoryName(b1)) == "delete-20261117-090000"
                && skipped.Said()!.Contains(b1, StringComparison.Ordinal), "The gather skipped: the save backed up whole, the folder deleted, the home says where.");
        Require(!Directory.Exists(sharedSave), "Nothing was gathered while Longshot ran.");

        // (the home's Delete, the gather working: the save taken into the shared one, backed up, renamed aside; nothing left to copy)
        var taken = WithSave("nightly-201-bbbbbbbb", "TAKEN");
        var takenSave = Tree(Path.Combine(taken, "user", "save"));
        var gathered = Delete(windows, taken, nothingRuns);
        Require(gathered.Deleted && !Directory.Exists(taken) && gathered.Gathered is { WasShared: false } g1 && gathered.Backup == null
                && Tree(sharedSave) == takenSave && Tree(Path.Combine(g1.Backup, "nightly-201-bbbbbbbb")) == takenSave
                && gathered.Said()!.Contains(g1.Backup, StringComparison.Ordinal), "Delete takes the folder's save into the shared one first, then deletes it.");

        // (the shared save is newer: it stays; the folder's own survives in the gather's backup)
        var older = WithSave("nightly-202-cccccccc", "OLDER");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(older, "user", "save"), "*", SearchOption.AllDirectories)) File.SetLastWriteTime(f, when.AddDays(-10));
        var olderSave = Tree(Path.Combine(older, "user", "save"));
        var newer = Delete(windows, older, nothingRuns);
        Require(newer.Deleted && newer.Gathered is { WasShared: true } g2 && Tree(sharedSave) == takenSave
                && Tree(Path.Combine(g2.Backup, "nightly-202-cccccccc")) == olderSave, "A newer shared save: the folder's own is kept in the backup.");

        // (the gather fails: the save is still backed up before the folder goes, and the home says both)
        var failing = WithSave("nightly-203-dddddddd", "FAILING");
        var failingSave = Tree(Path.Combine(failing, "user", "save"));
        Saves.Fault = s => { if (s == "file") throw new IOException("disk full"); };
        DeletedGame afterFail;
        try { afterFail = Delete(windows, failing, nothingRuns); }
        finally { Saves.Fault = null; }
        Require(afterFail.Deleted && afterFail.GatherProblem != null && afterFail.Backup is { } b3 && Tree(b3) == failingSave
                && afterFail.Said()!.Contains(b3, StringComparison.Ordinal) && Tree(sharedSave) == takenSave,
                "A failed gather: the save backed up, then the folder deleted; the home says both.");

        // (a save the gather took in but could not rename aside, a file in use: backed up too, then deleted)
        var inUse = WithSave("nightly-204-eeeeeeee", "IN USE");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(inUse, "user", "save"), "*", SearchOption.AllDirectories)) File.SetLastWriteTime(f, when.AddDays(-5));
        var inUseSave = Tree(Path.Combine(inUse, "user", "save"));
        Saves.Fault = s => { if (s == "rename") throw new IOException("in use"); };
        DeletedGame afterUse;
        try { afterUse = Delete(windows, inUse, nothingRuns); }
        finally { Saves.Fault = null; }
        Require(afterUse.Deleted && afterUse.Gathered != null && afterUse.Backup is { } b4 && Tree(b4) == inUseSave, "A save that could not be renamed: backed up, then deleted.");

        // (a retry: a copy with the same files is used again; one that changed since is copied anew)
        var retry = WithSave("nightly-205-ffffffff", "RETRY");
        var retrySave = Path.GetFullPath(Path.Combine(retry, "user", "save"));
        var first = Saves.BackUpForDelete(windows, retry, retrySave, () => when);
        Require(Saves.BackUpForDelete(windows, retry, retrySave, () => when) == first, "The same save backed up again: the copy before is used.");
        File.WriteAllText(Path.Combine(retrySave, Saves.UserId, "slot_00", "progress.sav"), "progress-RETRX");   // (one byte, the same length)
        var second = Saves.BackUpForDelete(windows, retry, retrySave, () => when);
        Require(second != first && Tree(second) == Tree(retrySave) && Tree(first) != Tree(retrySave), "A save that changed since: a new copy, the first kept.");

        // (Linux: nothing is gathered; the save is backed up and the folder deleted, never left stuck)
        var onLinux = WithSave("v3.1.0", "LINUX");
        var linuxSave = Tree(Path.Combine(onLinux, "user", "save"));
        var linuxDone = Delete(linux, onLinux, nothingRuns);
        Require(linuxDone.Deleted && linuxDone.Gathered == null && linuxDone.Backup is { } b5 && Tree(b5) == linuxSave
                && b5.StartsWith(linux.BackupRoot, StringComparison.Ordinal), "Linux: the save backed up, the folder deleted.");

        // (a folder DeleteFolder refuses, here one holding another build's: refused before its save is gathered or renamed)
        var outer = WithSave("nightly-210-55555555", "GUARDED");
        var innerBuild = Path.Combine(outer, "inner");
        Directory.CreateDirectory(innerBuild);
        var guarded = BuildRegistry.DeleteGame(windows, outer, Platform.Windows, new[] { innerBuild }, nothingRuns, () => when, Env);
        Require(!guarded.Deleted && guarded.Gathered == null && guarded.Backup == null && Directory.Exists(Path.Combine(outer, "user", "save"))
                && Tree(sharedSave) == takenSave, "A folder Delete refuses is left as it was: its save neither gathered nor renamed.");

        // (another gather holds the lock, such as a desktop shortcut's --play in a process of its own: this one waits, then is
        // refused; the delete backs the save up instead)
        var waiting = WithSave("nightly-211-66666666", "WAITING");
        var waitingSave = Tree(Path.Combine(waiting, "user", "save"));
        Saves.GatherWait = TimeSpan.FromMilliseconds(200);
        try
        {
            using (Saves.HoldGatherLock(windows))
            {
                var locked0 = Delete(windows, waiting, nothingRuns);
                Require(locked0.Deleted && locked0.Gathered == null && locked0.GatherProblem!.Contains("Another Longshot Launcher", StringComparison.Ordinal)
                        && locked0.Backup is { } b6 && Tree(b6) == waitingSave && Tree(sharedSave) == takenSave,
                        "A gather that cannot get the lock changes nothing; the save is backed up before the folder goes.");
                Throws<SaveFailure>(() => Saves.GatherAlone(windows, Array.Empty<string>()), "A second gather ran while another held the lock.");
            }
        }
        finally { Saves.GatherWait = TimeSpan.FromSeconds(10); }

        // (a delete that stops half-way, a file in use: the copy of the save is named still, as the folder may have lost it already)
        var stuck = WithSave("nightly-212-77777777", "STUCK");
        var stuckSave = Tree(Path.Combine(stuck, "user", "save"));
        var inUseFile = Path.Combine(stuck, "zz-in-use", "held.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(inUseFile)!);
        File.WriteAllText(inUseFile, "x");
        var release = OperatingSystem.IsWindows() ? HoldOpen(inUseFile) : NoWrites(Path.GetDirectoryName(inUseFile)!);
        if (release == null) Console.WriteLine("delete-saves: a file that cannot be deleted could not be made here (root?); that check is skipped.");
        else
        {
            DeletedGame half;
            try { half = Delete(linux, stuck, nothingRuns); }
            finally { release(); }
            Require(!half.Deleted && half.Backup is { } b7 && Tree(b7) == stuckSave && half.Said()!.Contains(b7, StringComparison.Ordinal)
                    && half.Said()!.Contains(stuck, StringComparison.Ordinal), "A delete stopped half-way still names the copy of the save (said: " + half.Said() + ").");
        }

        // (no save: nothing to say, the folder deleted)
        var plain = Version("nightly-206-11111111");
        Directory.CreateDirectory(Path.Combine(plain, "user", "save", Saves.UserId));
        Require(Saves.SaveIn(plain) == null && Delete(windows, plain, nothingRuns) is { Deleted: true, Backup: null, Gathered: null } d
                && d.Said() == null && !Directory.Exists(plain), "A folder with no save of its own is deleted, with nothing to say.");

        // (a folder that holds Longshot's shared saves is never deleted)
        var holder = Version("nightly-207-22222222");
        var inside = windows with { UserFolder = Path.Combine(holder, "data") };
        Throws<IOException>(() => BuildRegistry.DeleteFolder(holder, Platform.Windows, null, Env, inside), "A folder holding the shared saves was deleted.");

        // (a save that cannot be read: it counts as one, no backup can be made, and the folder stays)
        var locked = WithSave("nightly-208-33333333", "LOCKED");
        var lockedSave = Path.Combine(locked, "user", "save");
        var undo = OperatingSystem.IsWindows() ? DenyListing(lockedSave) : NoPermissions(lockedSave);
        if (undo == null) Console.WriteLine("delete-saves: an unreadable folder could not be made here (root?); that check is skipped.");
        else
        {
            DeletedGame unreadable;
            try
            {
                Require(Saves.SaveIn(locked) == Path.GetFullPath(lockedSave), "A save folder that cannot be read counts as holding a save.");
                unreadable = Delete(linux, locked, nothingRuns);
            }
            finally { undo(); }
            Require(!unreadable.Deleted && Directory.Exists(lockedSave) && unreadable.Said()!.Contains(locked, StringComparison.Ordinal),
                    "An unreadable save: no backup, the folder stays, and the home says why.");
        }

        // (a user\save that is a link, to the shared save here: deleting the folder removes the link and never follows it)
        if (TestLinks.Available)
        {
            var linked = Version("nightly-209-44444444");
            Directory.CreateDirectory(Path.Combine(linked, "user"));
            Directory.CreateSymbolicLink(Path.Combine(linked, "user", "save"), sharedSave);
            Require(Saves.SaveIn(linked) == null, "A linked user\\save is not the folder's own save.");
            BuildRegistry.DeleteFolder(linked, Platform.Windows, null, Env, windows);
            Require(!Directory.Exists(linked) && Tree(sharedSave) == takenSave, "Deleting it left the save it linked to.");
        }
    }

    /// The folder made unlistable for this user (a deny rule); what puts it back, or null when it stays readable
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static Action? DenyListing(string dir)
    {
        var info = new DirectoryInfo(dir);
        var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        void Undo()
        {
            var back = info.GetAccessControl();
            back.RemoveAccessRule(rule);
            info.SetAccessControl(back);
        }
        return StillReadable(dir, Undo);
    }

    /// The folder given no permissions; what puts them back, or null when it stays readable (root)
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static Action? NoPermissions(string dir)
    {
        var mode = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.None);
        return StillReadable(dir, () => File.SetUnixFileMode(dir, mode));
    }

    /// The file held open with no sharing, so that it cannot be deleted; what closes it
    static Action HoldOpen(string file)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        return stream.Dispose;
    }

    /// The folder made read-only for its owner, so that nothing in it can be deleted; what puts it back, or null when files can
    /// still be made in it (root)
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static Action? NoWrites(string dir)
    {
        var mode = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var probe = Path.Combine(dir, ".probe");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            File.SetUnixFileMode(dir, mode);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return () => File.SetUnixFileMode(dir, mode); }
    }

    static Action? StillReadable(string dir, Action undo)
    {
        try { _ = Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return undo; }
        undo();
        return null;
    }
}
