using System.Collections.Concurrent;
using System.Text.Json;

namespace LongshotLauncher.Core;

/// What a version folder's builder says of itself and of a profile's game folder: `longshot-builder info --profile &lt;dir&gt; --output
/// json`'s one info event (Longshot's build.md "Info"), read instead of the game folder's files. Tag: the release it carries (null:
/// a developer's build). Builder: what the builder can do (play, fetch, from_source, steam_shortcut, storage, ...). Game: what the
/// profile's game can do (save_dir, profile, start_slot: the build record's features, or an older folder's features= line, as
/// the builder reads them). Build: the profile's game folder's state
public sealed record BuilderInfo(string? Tag, string? Channel, IReadOnlySet<string> Builder, IReadOnlySet<string> Game, BuilderInfo.BuildState Build)
{
    /// The profile's game folder: there (Present), and whether play would start it as it is (UpToDate: the fast check's
    /// answer) or why not (Stale, in the builder's words); the release that built it (BuiltBy) and the upscalers it built in
    /// (dlss, fsr4, xess)
    public sealed record BuildState(string? Folder, bool Present, bool UpToDate, string? Stale, string? BuiltBy, IReadOnlyList<string> Upscalers)
    {
        public bool Equals(BuildState? o) => o != null && Folder == o.Folder && Present == o.Present && UpToDate == o.UpToDate && Stale == o.Stale
                                            && BuiltBy == o.BuiltBy && Upscalers.SequenceEqual(o.Upscalers);
        public override int GetHashCode() => HashCode.Combine(Folder, Present, UpToDate, Stale, BuiltBy);
    }

    /// Whether the builder can do it (a capability's name: never asked by version)
    public bool Can(string capability) => Builder.Contains(capability);

    /// The info event's fields; null when it is not one this launcher reads (no capabilities)
    public static BuilderInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object) return null;
        static IReadOnlySet<string> Names(JsonElement o, string name) =>
            (o.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!) : [])
            .ToHashSet(StringComparer.Ordinal);
        static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        static bool Flag(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
        var version = e.TryGetProperty("version", out var v) ? v : default;
        var build = e.TryGetProperty("build", out var b) ? b : default;
        var upscalers = build.ValueKind == JsonValueKind.Object ? Names(build, "upscalers").Order(StringComparer.Ordinal).ToList() : [];
        return new BuilderInfo(Str(version, "tag"), Str(version, "channel"), Names(caps, "builder"), Names(caps, "game"),
                               new BuildState(Str(build, "folder"), Flag(build, "present"), Flag(build, "up_to_date"), Str(build, "stale"), Str(build, "builder"),
                                              upscalers));
    }

    /// The command line: info [--profile &lt;the profile's folder&gt;] --output json
    public static IReadOnlyList<string> Arguments(string? profile) =>
        profile == null ? ["info", .. LongshotBuilder.OutputJson] : ["info", "--profile", Path.GetFullPath(profile), .. LongshotBuilder.OutputJson];
}

/// The last info each version folder's builder gave for the current profile (LauncherService.RefreshInfos), which what the launcher
/// shows of a version reads in place of its game folder's files (GameFeatures, LauncherService.IsBuilt). Forgotten when the
/// version is built or deleted, or the profile changes, until its builder is asked again
public static class BuilderInfos
{
    static readonly ConcurrentDictionary<string, BuilderInfo> Known =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    static string Key(string versionFolder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionFolder));

    /// A version folder's info; false when it did not change
    public static bool Set(string versionFolder, BuilderInfo info)
    {
        var key = Key(versionFolder);
        var changed = !Known.TryGetValue(key, out var was) || !Same(was, info);
        Known[key] = info;
        return changed;
    }

    static bool Same(BuilderInfo a, BuilderInfo b) =>
        a.Tag == b.Tag && a.Build == b.Build && a.Builder.SetEquals(b.Builder) && a.Game.SetEquals(b.Game);

    public static void Forget(string versionFolder) => Known.TryRemove(Key(versionFolder), out _);

    public static void Clear() => Known.Clear();

    /// A version folder's last info; null when its builder was not asked yet
    public static BuilderInfo? OfVersion(string versionFolder) => Known.TryGetValue(Key(versionFolder), out var i) ? i : null;

    /// The info of the version folder a game folder is in (BuilderVersions.VersionOfGame); null for another game folder, or one
    /// whose builder was not asked yet
    public static BuilderInfo? Of(string gameFolder, Platform platform) =>
        BuilderVersions.VersionOfGame(gameFolder, platform) is { } version ? OfVersion(version) : null;
}
