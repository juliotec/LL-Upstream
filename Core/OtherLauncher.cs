namespace LongshotLauncher.Core;

/// A build another launcher on this PC lists (OtherLauncher): its folder, version and channel, and whether it is that launcher's
/// default
public sealed record OtherBuild(string Folder, string? Version, string? Channel, bool Default);

/// What OtherLauncher.TakeInto did: the rows it added, whether it copied the other launcher's saves in, and why it did not when
/// it had some to copy (null: nothing to say). Busy: another held the gather lock, so asking again later may copy them
public sealed record OtherTaken(IReadOnlyList<BuildEntry> Added, bool SavesCopied, string? SavesLeft, bool Busy = false);

/// Another Longshot Launcher's builds and saves on this PC. Each launcher keeps its own list (builds.json in its home) and, on
/// Windows, its own save folder: an installed one's %APPDATA%\Longshot, a portable one's user\ in its LongshotLauncher-data. So a
/// player who went from one to the other found neither their builds nor their saves. A portable launcher that starts with an
/// empty list offers the installed one's (InstalledOne); the home's "Add a build folder…" takes a portable one's data folder
/// (At). Its builds are listed where they are, nothing moved or copied; its saves are copied in only where this launcher has
/// none yet (Windows; on Linux every launcher's builds share one save folder already)
public sealed record OtherLauncher(string Home, bool Installed, IReadOnlyList<OtherBuild> Builds, string? User)
{
    /// Its save folder (Windows: &lt;User&gt;\save); null on Linux
    public string? Save => User == null ? null : Path.Combine(User, "save");

    /// Its save's profiles that hold a played slot or more (none: nothing to copy)
    public IReadOnlyList<SaveProfile> Profiles
    {
        get
        {
            try { return User != null && Directory.Exists(User) ? Saves.Find(User).Where(p => Saves.Describe(p.Path).Count > 0).ToList() : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// Builds or saves to take in
    public bool HasAnything => Builds.Count > 0 || Profiles.Count > 0;

    /// Its saves could be copied into env's (Windows): it has some, they are not env's own folder, and env's save folder holds
    /// no file yet, nor does what a gather cut short set aside beside it (Saves.Beside)
    public bool SavesFitIn(SaveEnvironment env) =>
        Save is { } from && Profiles.Count > 0 && !BuildRegistry.SameFolder(from, Path.Combine(env.LongshotUser, "save"))
        && !HoldsSave(Path.Combine(env.LongshotUser, "save"));

    /// A save folder that holds a file, or one a gather cut short set aside beside it, or that cannot be read or is a link (the
    /// player's own arrangement): a save to keep, never one to copy over
    static bool HoldsSave(string save)
    {
        try
        {
            if (Directory.Exists(save) && new DirectoryInfo(save).LinkTarget != null) return true;
            return HasFiles(save) || HasFiles(Saves.Beside(save).Aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// The installed launcher's, as a portable one (its home: ownHome) sees it: the default home of this PC's user
    /// (LauncherHome.Resolve with no LONGSHOT_LAUNCHER_HOME) and, on Windows, %APPDATA%\Longshot. Null when that home is ownHome
    public static OtherLauncher? InstalledOne(Platform platform, Func<string, string?> getenv, string ownHome)
    {
        Func<string, string?> plain = n => n == LauncherHome.Variable ? null : getenv(n);
        var home = LauncherHome.Resolve(platform, plain).Root;
        if (BuildRegistry.SameFolder(home, ownHome)) return null;
        return Read(home, true, platform == Platform.Windows ? new SaveEnvironment(platform, plain).LongshotUser : null, platform);
    }

    /// The launcher whose data folder is folder, or is in it (LongshotLauncher-data beside a portable launcher), picked on the
    /// home; null when neither holds a builds.json. Its saves: a portable one's user\ there, an installed one's %APPDATA%\Longshot
    public static OtherLauncher? At(string folder, Platform platform, Func<string, string?> getenv)
    {
        var full = Path.GetFullPath(folder);
        var home = new[] { full, Path.Combine(full, LauncherLayout.PortableData) }.FirstOrDefault(h => File.Exists(Path.Combine(h, "builds.json")));
        if (home == null) return null;
        Func<string, string?> plain = n => n == LauncherHome.Variable ? null : getenv(n);
        var installed = BuildRegistry.SameFolder(home, LauncherHome.Resolve(platform, plain).Root);
        var user = platform != Platform.Windows ? null : installed ? new SaveEnvironment(platform, plain).LongshotUser : Path.Combine(home, "user");
        return Read(home, installed, user, platform);
    }

    /// Its list's builds whose game is there (BuildRegistry.Peek: its file is never written), then the games in a portable one's
    /// own builds folder its list lost (a folder moved since)
    static OtherLauncher Read(string home, bool installed, string? user, Platform platform)
    {
        var builds = new List<OtherBuild>();
        foreach (var b in BuildRegistry.Peek(Path.Combine(home, "builds.json")))
        {
            try
            {
                if (GameFolder.IsBuilt(b.Folder, platform) && !builds.Any(o => BuildRegistry.SameFolder(o.Folder, b.Folder)))
                    builds.Add(new OtherBuild(Path.GetFullPath(b.Folder), b.Version, b.Channel, b.Default));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }   // (a path that is none)
        }
        foreach (var dir in GamesIn(Path.Combine(home, "builds"), platform))
            if (!builds.Any(o => BuildRegistry.SameFolder(o.Folder, dir)))
                builds.Add(new OtherBuild(dir, GameFolder.Version(dir), ChannelNamed(dir, GameFolder.Version(dir)), false));
        return new OtherLauncher(home, installed, builds, user);
    }

    /// The Longshot games in folder that play: folder itself when it is one, else each game folder in it as a builds folder holds
    /// them: right in it (before builds/&lt;track&gt;/&lt;version&gt;) and in its tracks' folders (InstanceFolders.Folders). A track's folder
    /// picked (builds/nightly) gives the versions in it. Only a release's or a nightly's version, as Leftovers.Relist takes: not a Hookshot 2.x game (a clean break), nor
    /// one with no version. Links are left out; a folder that cannot be read has none
    public static IReadOnlyList<string> GamesIn(string folder, Platform platform)
    {
        bool Plays(string d) => GameFolder.IsBuilt(d, platform) && GameFolder.Version(d) is { } v && (Versions.IsRelease(v) || Versions.TrackOf(v) == Versions.Nightly);
        try
        {
            var full = Path.GetFullPath(folder);
            if (!Directory.Exists(full)) return [];
            if (GameFolder.IsGameFolder(full, platform)) return Plays(full) ? [full] : [];
            return InstanceFolders.Folders(full, platform).Where(d => new DirectoryInfo(d).LinkTarget == null && Plays(d)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }

    /// The channel a game folder is named after (stable, rc, nightly: LauncherSettings.FolderFor), else null (its version's)
    /// The channel a game folder's track folder is named after (builds/nightly/&lt;version&gt;, nightly (2) too), or before that layout
    /// its own name (rc, stable, nightly: launchers v1.0.0-rc.7 to #184), when version is one that channel follows (the rc
    /// channel follows stable too); else null, its version's
    public static string? ChannelNamed(string folder, string? version)
    {
        var self = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (var name in new[] { Path.GetFileName(Path.GetDirectoryName(self) ?? ""), Path.GetFileName(self) })
        {
            var channel = Suffix.Replace(name, "");
            if (!Versions.IsChannel(channel)) continue;
            var track = Versions.TrackOf(version);
            return version != null && (track == channel || channel == Versions.Candidate && track == Versions.Stable) ? channel : null;
        }
        return null;
    }

    static readonly System.Text.RegularExpressions.Regex Suffix = new(@" \(\d+\)$");

    /// Its builds that registry does not list yet, listed (the other's default is this one's when this list had none); then, on
    /// Windows, its save folder copied whole into env's when env's holds no file yet: never over a save (Saves > Import does
    /// that, with a backup), nor while Longshot runs. running: the programs running now (Saves.Running). registry is not saved
    public OtherTaken TakeInto(BuildRegistry registry, SaveEnvironment env, Func<IReadOnlyList<string>> running)
    {
        var wasEmpty = registry.Builds.Count == 0;
        var added = new List<BuildEntry>();
        foreach (var b in Builds)
        {
            if (registry.FindByFolder(b.Folder) != null) continue;
            added.Add(registry.Add(b.Folder, b.Version, b.Channel, makeDefault: wasEmpty && b.Default));
        }
        if (Save is not { } from || Profiles.Count == 0) return new OtherTaken(added, false, null);
        var to = Path.Combine(env.LongshotUser, "save");
        if (BuildRegistry.SameFolder(from, to)) return new OtherTaken(added, false, null);
        // (the gather lock, as a gather takes it (the start's, Play's, a Delete's, another launcher's --play), whose staging and
        // set-aside folders beside the save folder this copy uses too)
        FileStream held;
        try { held = Saves.HoldGatherLock(env); }
        catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException)
        {
            return new OtherTaken(added, false, Tr.F("The saves in {0} could not be copied: {1}", from, e.Message), Busy: true);
        }
        using (held)
        {
            // (a gather cut short between its two renames left the save aside: put back first, as Gather and Saves.Copy do, so
            // Manage saves shows it)
            var aside = Saves.Beside(to).Aside;
            try
            {
                if (!Directory.Exists(to) && Directory.Exists(aside))
                {
                    Directory.Move(aside, to);
                    LauncherLog.Add($"the save a gather cut short set aside in {aside} put back in {to}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // (HoldsSave still sees it aside)
            if (HoldsSave(to))
                return new OtherTaken(added, false, Saves.CopiedWhole(from, to) ? null   // (the same saves: copied before)
                    : Tr.F("This launcher has saves of its own already, so the ones in {0} were not copied over them: Manage saves imports them, after a backup.", from));
            if (running().Any(n => n.Contains(Saves.Longshot.Display, StringComparison.Ordinal)))
                return new OtherTaken(added, false, Tr.F("Longshot is running, so the saves in {0} were not copied: close it, then import them from Manage saves.", from));
            var (staging, _) = Saves.Beside(to);
            try
            {
                Saves.DeleteTree(staging);
                Saves.CopyTree(from, staging);
                // (read while another launcher may write it: the copy must be what is there, file for file)
                if (!Saves.CopiedWhole(from, staging)) throw new IOException("the copy differs from the saves; they changed while it was made");
                RemoveEmpty(to);
                Directory.Move(staging, to);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Saves.DeleteTree(staging);
                LauncherLog.Add($"saves of the launcher in {Home} not copied from {from} into {to}: {e.GetType().Name}: {e.Message}");
                return new OtherTaken(added, false, Tr.F("The saves in {0} could not be copied: {1}", from, e.Message));
            }
            foreach (var f in Saves.PlayerFiles)
            {
                try
                {
                    var a = Path.Combine(User!, f);
                    var b = Path.Combine(env.LongshotUser, f);
                    if (File.Exists(a) && !File.Exists(b)) File.Copy(a, b);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // (the game's own default then)
            }
            LauncherLog.Add($"saves of the launcher in {Home} copied from {from} into {to}");
            return new OtherTaken(added, true, null);
        }
    }

    /// A save folder with no file in it taken away, folder by folder from the deepest (Directory.Delete without recursive: one
    /// that holds a file stops it, nothing of a save is ever deleted here); none there is fine
    static void RemoveEmpty(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var d in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            Directory.Delete(d);
        Directory.Delete(folder);
    }

    static bool HasFiles(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }   // (cannot tell: never over it)
    }
}
