using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LongshotLauncher.Core;

/// A Longshot release of longshot-builder's kind (Longshot #2286): one self-contained file per system, longshot-builder-&lt;version&gt;-&lt;system&gt;
/// (.exe on windows-x64), and a manifest, longshot-builder-&lt;version&gt;-&lt;system&gt;.json, signed in the release's SHA256SUMS:
///     {"v":1, "version", "channel", "platform", "full":{"name","size","sha256"}, "deltas":[...], "stamps":{"compiler","translator"}}
/// A release has one when it is of this kind (ReleaseInfo.BuilderManifests): that, not its version, decides. Any other was built
/// with make_game.py, which this launcher does not run (OldVersions). The builder installs every release itself (its fetch: the
/// deltas, the chains, the checks: Toolchains.FetchAsync); the launcher reads a manifest only for the first builder on a PC that
/// has none (Toolchains' bootstrap), and only its full file and stamps
public sealed record LongshotBuildRelease(string Version, string Channel, string Platform, LongshotBuildRelease.FileEntry Full,
                                          IReadOnlyDictionary<string, string> Stamps)
{
    public sealed record FileEntry(string Name, long Size, string Sha256);

    /// The manifest's format this launcher reads
    public const int Format = 1;

    /// The largest manifest read
    public const long MaxManifest = 1 << 20;

    /// The manifest's name (LongshotBuilder.Name)
    public static string ManifestName(string version, string platform) => $"{LongshotBuilder.Name}-{version}-{platform}.json";

    /// The manifest, checked: its format, that it is this version's, of its track's channel (nightly or stable: Versions.TrackOf)
    /// and this system's, its full file a plain name with a size and a SHA-256. Its deltas are the builder's to read. Throws
    /// InvalidDataException
    public static LongshotBuildRelease Parse(string json, string version, string platform)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not an object");
            if (!r.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != Format)
                throw new InvalidDataException($"its format is not {Format}");
            if (Str(r, "version") != version) throw new InvalidDataException($"it is not {version}'s");
            if (Str(r, "platform") != platform) throw new InvalidDataException($"it is not {platform}'s");
            var channel = Str(r, "channel");
            if (channel != Versions.TrackOf(version)) throw new InvalidDataException($"its channel, {channel ?? "none"}, is not {version}'s");
            var full = Entry(r.GetProperty("full"));
            var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
            if (r.TryGetProperty("stamps", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } value) stamps[p.Name] = value;
            return new LongshotBuildRelease(version, Versions.TrackOf(version), platform, full, stamps);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The manifest of longshot-builder {version} cannot be read: {e.Message}", e);
        }
        catch (InvalidDataException e) when (!e.Message.StartsWith("The manifest", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The manifest of longshot-builder {version} is refused: {e.Message}.", e);
        }
    }

    /// The record fetch writes beside the program it places (Toolchains.ReleaseRecord; Longshot's install.rs Record::of), for the
    /// program named program: what the next fetch takes it as (its version and full file's SHA-256, with no hashing)
    public string Record(string program) => new JsonObject
    {
        ["v"] = 1, ["version"] = Version, ["channel"] = Channel, ["platform"] = Platform,
        ["full"] = new JsonObject { ["name"] = Full.Name, ["size"] = Full.Size, ["sha256"] = Full.Sha256 },
        ["program"] = program,
        ["stamps"] = new JsonObject(Stamps.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
    }.ToJsonString();

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static string Sha(string? s) =>
        s is { Length: 64 } && s.All(Uri.IsHexDigit) ? s.ToLowerInvariant() : throw new InvalidDataException("a SHA-256 that is not one");

    static FileEntry Entry(JsonElement e)
    {
        var name = Str(e, "name");
        if (name is not { Length: > 0 } || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException("a file's name that is not a plain name");
        var size = e.GetProperty("size").GetInt64();
        if (size <= 0 || size > ReleaseManifest.MaxFile) throw new InvalidDataException("a size out of range");
        return new FileEntry(name, size, Sha(Str(e, "sha256")));
    }

    /// The full file downloaded to path and checked by its size and SHA-256; InvalidDataException otherwise (path deleted)
    public static async Task DownloadAsync(IFetcher fetcher, string url, FileEntry want, string path, Action<long> progress, CancellationToken cancel)
    {
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                await fetcher.DownloadAsync(url, file, have =>
                {
                    if (have > want.Size) throw new InvalidDataException("The builder's download is larger than its release says.");
                    progress(have);
                }, cancel).ConfigureAwait(false);
            if (new FileInfo(path).Length != want.Size || Sha256Of(path) != want.Sha256)
                throw new InvalidDataException($"{want.Name} did not download correctly (its size or checksum differs).");
        }
        catch
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// A file's SHA-256, lowercase hex
    public static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }
}
