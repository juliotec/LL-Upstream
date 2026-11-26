using System.Text.Json.Serialization;

namespace LongshotLauncher.Core;

/// A profile of the launcher: a player or a setup (LauncherProfiles), as its profile.json holds it, read when asked for: nothing of
/// it is kept in the launcher's settings. Color: one of LauncherProfiles.Colors; StartSlot: the save Play starts on; LastPlayed and
/// LastPlayedAt: the version Play last started for it, and when. Problem: why its profile.json cannot be read (it is shown as
/// damaged), or that it is of a newer launcher's format (read for what it can be, never written); either way it is not changed here
public sealed class LauncherProfile
{
    public string Id { get; set; } = LauncherProfiles.DefaultId;
    public string Name { get; set; } = "";
    public string Color { get; set; } = LauncherProfiles.Colors[0];
    /// The save Play starts this profile's game on (a slot's folder name); null: its newest save, else the title screen's
    public string? StartSlot { get; set; }
    /// The version Play last started for this profile (its BuildEntry.Id), and when
    public string? LastPlayed { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    /// The profile it was made as a copy of
    public string? CopyOf { get; set; }
    [JsonIgnore]
    public string? Problem { get; set; }
    [JsonIgnore]
    public bool Damaged => Problem != null;
}

/// What a change to the profiles could not do, in the player's words
public sealed class ProfileFailure(string message) : Exception(message);

/// The launcher's profiles: the folders under profiles/ are the only list of them, and a profile is its folder (ProfileStore:
/// profile.json, settings.ini, saves/), which every version plays (through its builder's --profile). Everything about a profile lives in its profile.json (name, colour, version, game,
/// upscalers, start slot, last played, copy_of, and the keys this launcher does not know, kept); every call reads the folders, so two
/// launchers on the same profiles never see a stale list. The launcher's settings keep only which profile is current
/// (LauncherSettings.CurrentProfile), per PC. A profile's id is its folder's name, and stays when the profile is renamed
public static class LauncherProfiles
{
    /// The default profile's id: the profile there always is, which cannot be deleted
    public const string DefaultId = "default";
    public static string DefaultName => Tr.T("Default");
    public const int MaxProfiles = 16;
    /// The colours a profile's avatar takes
    public static readonly IReadOnlyList<string> Colors = new[] { "#7CC242", "#F3C24F", "#D9783A", "#A8472B", "#3D63E6", "#C2ED83" };

    /// A profile's id: "default", or "p-" and 8 lowercase hex digits (its folder's name)
    public static bool IsId(string? id) =>
        id == DefaultId || (id is { Length: 10 } && id.StartsWith("p-", StringComparison.Ordinal) && id[2..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));

    // ---- list: the folders of profiles/, read each time

    static LauncherProfile Make(string id, ProfileFile? file, string? problem)
    {
        var p = new LauncherProfile { Id = id, Name = id == DefaultId ? DefaultName : id, Problem = problem };
        if (file == null) return p;
        if (file.Name.Length > 0) p.Name = file.Name;
        p.Color = file.Color is { } c && Colors.Contains(c) ? c : Colors[0];
        p.StartSlot = file.StartSlot;
        p.LastPlayed = file.Version;
        p.LastPlayedAt = file.LastPlayed;
        p.CopyOf = file.CopyOf;
        return p;
    }

    /// The profiles, the default first: one per folder of profiles/ named as a profile's id. A folder whose profile.json is missing
    /// or cannot be read is listed as damaged, never left out. The default is listed even before its folder is made
    public static List<LauncherProfile> All(SaveEnvironment env)
    {
        var list = env.ProfilesRoot is { } root
            ? ProfileStore.All(root).Select(x => Make(x.Id, x.File, x.Problem)).ToList()
            : new List<LauncherProfile>();
        if (list.All(p => p.Id != DefaultId)) list.Insert(0, Make(DefaultId, null, null));
        return list;
    }

    /// One profile read from its folder; null when there is none
    public static LauncherProfile? Find(SaveEnvironment env, string id)
    {
        if (!IsId(id)) return null;
        if (env.ProfilesRoot is not { } root) return id == DefaultId ? Make(DefaultId, null, null) : null;
        var dir = ProfileStore.Dir(root, id);
        if (!Directory.Exists(dir)) return id == DefaultId ? Make(DefaultId, null, null) : null;
        var (file, problem) = ProfileStore.Read(dir);
        return Make(id, file, problem);
    }

    /// The profile the launcher is on (its setting), the default when that one is gone
    public static LauncherProfile Current(SaveEnvironment env, LauncherSettings s) =>
        (s.CurrentProfile != null ? Find(env, s.CurrentProfile) : null) ?? Find(env, DefaultId)!;

    public static LauncherProfile Of(SaveEnvironment env, string id) =>
        Find(env, id) ?? throw new ProfileFailure(Tr.T("That profile is gone."));

    public static string? BadName(SaveEnvironment env, string name, string? except = null)
    {
        var n = name.Trim();
        if (n.Length == 0) return Tr.T("Give the profile a name.");
        if (n.Length > 40) return Tr.T("A profile's name is at most 40 characters.");
        if (n.Any(char.IsControl)) return Tr.T("A profile's name has no control characters.");
        if (All(env).Any(p => p.Id != except && string.Equals(LauncherProfilesText.Name(p), n, StringComparison.CurrentCultureIgnoreCase)))
            return Tr.F("There is a profile named {0} already.", n);
        return null;
    }

    static string Checked(SaveEnvironment env, string name, string? except = null) =>
        BadName(env, name, except) is { } bad ? throw new ProfileFailure(bad) : name.Trim();

    static string Root(SaveEnvironment env) => env.ProfilesRoot ?? throw new InvalidOperationException("The launcher has no profiles folder.");

    static string CannotChange(string problem) => Tr.F("This profile's profile.json cannot be used ({0}). Fix it, or delete the profile.", problem);

    // ---- create, change, copy, delete: each writes its folder at once

    /// The default profile's folder made when profiles/ has none for it (its game copy the launcher's to name, knownGame). Not
    /// when it is there damaged
    public static void Ensure(SaveEnvironment env, string? knownGame = null)
    {
        if (env.ProfilesRoot is not { } root) return;
        var dir = ProfileStore.Dir(root, DefaultId);
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(ProfileStore.SavesOf(dir));
        ProfileStore.Save(dir, new ProfileFile { Name = DefaultName, Color = Colors[0], Game = knownGame });
    }

    /// A new profile: its saves empty, playing the game copy the launcher knows (knownGame), its own settings when it first plays
    public static LauncherProfile Create(SaveEnvironment env, string name, string? color = null, string? copyOf = null, string? knownGame = null)
    {
        var root = Root(env);
        var all = All(env);
        if (all.Count >= MaxProfiles) throw new ProfileFailure(Tr.F("The launcher keeps at most {0} profiles.", MaxProfiles));
        var file = new ProfileFile { Name = Checked(env, name), Color = color ?? Colors[all.Count % Colors.Count], CopyOf = copyOf, Game = knownGame };
        var id = NewId(root);
        var dir = ProfileStore.Dir(root, id);
        Directory.CreateDirectory(ProfileStore.SavesOf(dir));
        ProfileStore.Save(dir, file);
        return Of(env, id);
    }

    static string NewId(string root)
    {
        string id;
        do id = "p-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        while (Directory.Exists(ProfileStore.Dir(root, id)));
        return id;
    }

    /// The profile's profile.json read, changed by change and written, every key it does not know kept. Refused for a profile that
    /// is gone, damaged, or of a newer launcher's format. Nothing without a profiles folder. The default, when its folder is not
    /// there yet, is made first
    public static LauncherProfile Update(SaveEnvironment env, string id, Action<ProfileFile> change, string? knownGame = null)
    {
        if (env.ProfilesRoot is not { } root) return Of(env, id);
        if (Find(env, id) == null) throw new ProfileFailure(Tr.T("That profile is gone."));
        var dir = ProfileStore.Dir(root, id);
        if (!Directory.Exists(dir)) Ensure(env, knownGame);
        var (file, problem) = ProfileStore.Read(dir);
        if (file == null || problem != null) throw new ProfileFailure(CannotChange(problem ?? "?"));
        var before = file.Write();
        change(file);
        if (file.Write() != before) ProfileStore.Save(dir, file);
        Directory.CreateDirectory(ProfileStore.SavesOf(dir));
        return Of(env, id);
    }

    /// The name changed: the folder (the profile's id) stays
    public static LauncherProfile Rename(SaveEnvironment env, string id, string name) =>
        Update(env, id, f => f.Name = Checked(env, name, id));

    public static LauncherProfile SetColor(SaveEnvironment env, string id, string color) => Update(env, id, f => f.Color = color);

    /// The save Play starts this profile on; null: its newest
    public static LauncherProfile SetStartSlot(SaveEnvironment env, string id, string? slot) => Update(env, id, f => f.StartSlot = slot);

    /// The version it played (a BuildEntry.Id) and when
    public static LauncherProfile MarkPlayed(SaveEnvironment env, string id, string version, DateTime at) =>
        Update(env, id, f => { f.Version = version; f.LastPlayed = at; });

    /// The game copy the launcher knows put in a profile that names none yet (what the builder plays)
    public static void EnsureGame(SaveEnvironment env, string id, string? knownGame)
    {
        if (knownGame != null) Update(env, id, f => f.Game ??= knownGame, knownGame);
    }

    /// The profile's own folder (profile.json, settings.ini, saves/); null before the profiles' folders (no ProfilesRoot)
    public static string? FolderOf(SaveEnvironment env, string id) => env.ProfilesRoot is { } root ? ProfileStore.Dir(root, id) : null;

    /// The folder that holds a profile's save\ and save-work\: its folder's saves/; null without the profiles' folders (the default
    /// profile's saves are then the --user folder's, SaveEnvironment.LongshotUser)
    public static string? SavesHome(SaveEnvironment env, string id) =>
        FolderOf(env, id) is { } dir ? ProfileStore.SavesOf(dir) : null;

    /// A copy of a profile: its folder copied whole (its saves, game settings, game copy, upscalers and the keys it holds that this
    /// launcher does not know), named name, copy_of naming where from; the copy has not played yet
    public static LauncherProfile Duplicate(SaveEnvironment env, string id, string name)
    {
        var root = Root(env);
        var from = Of(env, id);
        if (from.Problem != null) throw new ProfileFailure(CannotChange(from.Problem));
        var all = All(env);
        if (all.Count >= MaxProfiles) throw new ProfileFailure(Tr.F("The launcher keeps at most {0} profiles.", MaxProfiles));
        var newName = Checked(env, name);
        var newId = NewId(root);
        var fromDir = ProfileStore.Dir(root, id);
        var toDir = ProfileStore.Dir(root, newId);
        if (Directory.Exists(fromDir)) CopyTree(fromDir, toDir);
        Directory.CreateDirectory(ProfileStore.SavesOf(toDir));
        var file = ProfileStore.Load(toDir) ?? new ProfileFile();
        file.Name = newName;
        file.Color = Colors[all.Count % Colors.Count];
        file.CopyOf = id;
        file.LastPlayed = null;
        ProfileStore.Save(toDir, file);
        return Of(env, newId);
    }

    /// A profile deleted: its whole folder (its saves, settings and profile.json) moved into the backups
    /// (BackupRoot/profile-&lt;id&gt;-&lt;time&gt;), never thrown away, a damaged one too; the default profile cannot be deleted. Where it
    /// was kept. s: the launcher's settings, whose current profile goes back to the default when it was this one
    public static string? Delete(LauncherSettings s, SaveEnvironment env, string id)
    {
        if (id == DefaultId) throw new ProfileFailure(Tr.T("The default profile cannot be deleted."));
        Of(env, id);
        string? kept = null;
        if (FolderOf(env, id) is { } home && Directory.Exists(home))
        {
            kept = Path.Combine(env.BackupRoot, $"profile-{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(env.BackupRoot);
            CopyTree(home, kept);
            Directory.Delete(home, true);
        }
        if (s.CurrentProfile == id) s.CurrentProfile = null;
        return kept;
    }

    /// The folder of a profile's slots: &lt;its saves&gt;/save/&lt;user id&gt; (the --user folder's for the default without the profiles' folders)
    public static string SlotsFolder(SaveEnvironment env, string id) =>
        Path.Combine(SavesHome(env, id) ?? env.LongshotUser, "save", Saves.UserId);

    /// A slot copied (or moved) to another profile, into the same slot number when it is free there, else the first free one; the
    /// slot's name there. Refused while a game runs (the caller checks) and when the other profile has no free slot
    public static string CopySlot(SaveEnvironment env, string from, string to, string slot, bool move)
    {
        if (from == to) throw new ProfileFailure(Tr.T("That save is this profile's already."));
        var source = Path.Combine(SlotsFolder(env, from), slot);
        if (!Directory.Exists(source)) throw new ProfileFailure(Tr.T("That save is gone."));
        var folder = SlotsFolder(env, to);
        Directory.CreateDirectory(folder);
        var name = Directory.Exists(Path.Combine(folder, slot)) ? FreeSlot(folder) : slot;
        if (name == null) throw new ProfileFailure(Tr.T("The other profile has no free save slot."));
        CopyTree(source, Path.Combine(folder, name));
        if (move) Directory.Delete(source, true);
        return name;
    }

    static string? FreeSlot(string folder)
    {
        for (var n = 0; n < 100; n++)
        {
            var name = $"slot_{n:00}";
            if (!Directory.Exists(Path.Combine(folder, name))) return name;
        }
        return null;
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(from))
            if (new DirectoryInfo(d).LinkTarget == null) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

/// A profile's name as the window shows it: the default's in the language in use until it is renamed
public static class LauncherProfilesText
{
    public static string Name(LauncherProfile p) =>
        p.Id == LauncherProfiles.DefaultId && (p.Name.Length == 0 || p.Name == "Default") ? LauncherProfiles.DefaultName : p.Name;

    /// Its avatar's letter
    public static string Initial(LauncherProfile p) => Name(p) is { Length: > 0 } n ? n[..1].ToUpper(System.Globalization.CultureInfo.CurrentCulture) : "?";
}
