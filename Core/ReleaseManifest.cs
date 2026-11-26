using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace LongshotLauncher.Core;

/// A file a release has: where it is, and its SHA-256 (from the release's signed SHA256SUMS) and size, which the download is
/// checked against
public sealed record FileRef(string Url, string Sha256, long Size);

/// What a release belongs to: Longshot (its builders, the releases of MagicBeanDev/Longshot) or the launcher itself (the releases
/// of MagicBeanDev/LongshotLauncher). Each has its own versions and its own three channels (docs/releases.md)
public sealed record Product(string Name, string Display, string Repo, int FirstMajor)
{
    public const string Server = "https://git.magicbean.dev";
    public static readonly Product Longshot = new("longshot", "Longshot", "MagicBeanDev/Longshot", Versions.FirstMajor);
    public static readonly Product Launcher = new("launcher", "Longshot Launcher", "MagicBeanDev/LongshotLauncher", Versions.LauncherFirstMajor);

    public string Api => Server + "/api/v1/repos/" + Repo;
}

/// One version's release, its files checked against its signed SHA256SUMS: the launcher's own program for each system (Launcher:
/// "windows-x64", "windows-x64-portable", the portable zip, and "linux-x64"). Notes: the release's page. BuilderManifests: a
/// Longshot release of longshot-builder's kind, by system ("windows-x64", "linux-x64"), its manifest (LongshotBuildRelease) signed
/// in SHA256SUMS; empty for a release built with make_game.py, which this launcher does not build (OldVersions). Assets: every
/// file the release lists, by name (the manifest names its full file and deltas, with their SHA-256s)
public sealed record ReleaseInfo(string Product, string Version, string Commit, DateTimeOffset? Published, string? Notes,
                                 IReadOnlyDictionary<string, FileRef> Launcher,
                                 IReadOnlyDictionary<string, FileRef>? BuilderManifests = null,
                                 IReadOnlyDictionary<string, (string Url, long Size)>? Assets = null);

/// A release as Forgejo's API lists it (GET /repos/{repo}/releases): its tag, whether it is a draft or a pre-release, its
/// files (name, download link, size), its page, its commit, and its page's text (Body: the markdown the release workflow and
/// the What's new above it wrote; ReleaseNotes reads it)
public sealed record ListedRelease(string Tag, bool Draft, bool Prerelease, DateTimeOffset? Published, string? Page, string Commit,
                                   IReadOnlyList<(string Name, string Url, long Size)> Assets, string? Body = null);

/// Reading Forgejo's release list, and the channels from it (scripts/release.py newest has the same rules):
///   stable   the newest vX.Y.Z that is not a draft nor a pre-release
///   nightly  the newest nightly-&lt;count&gt;-&lt;commit&gt; that is not a draft, by its count
/// A draft is a release CI is still making; a version pulled back to a draft, or deleted, is gone from its channel, which then
/// offers the one before. Anything else in the list (a 2.x tag) is on no channel. (The build tools are generic packages, not
/// releases.)
public static class ReleaseManifest
{
    public const long MaxFile = 16L << 30;
    public const string Sums = "SHA256SUMS", Sig = "SHA256SUMS.sig";
    public const int MaxBody = 64 << 10;

    public static string PlatformKey(Platform platform) => platform == Platform.Windows ? "windows-x64" : "linux-x64";

    /// The launcher's files, by key: the Windows program as its setup (the program itself, which offers to install itself when it
    /// is first started), the same program in a zip with its "portable" file, and the AppImage (scripts/package.sh)
    public const string WindowsPortable = "windows-x64-portable";
    public static readonly IReadOnlyList<string> LauncherKeys = new[] { "windows-x64", WindowsPortable, "linux-x64" };

    /// The file names a release of product has, per system (scripts/package.sh, Longshot's release workflow)
    public static string LauncherFile(string version, string key) => key switch
    {
        "windows-x64" => $"LongshotLauncher-{version}-windows-x64-setup.exe",
        WindowsPortable => $"LongshotLauncher-{version}-windows-x64-portable.zip",
        _ => $"LongshotLauncher-{version}-x86_64.AppImage",
    };

    public static IReadOnlyList<ListedRelease> ParseList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Select(Listed).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The release list cannot be read: " + e.Message, e);
        }
    }

    public static ListedRelease ParseOne(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Listed(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The release cannot be read: " + e.Message, e);
        }
    }

    static string? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static ListedRelease Listed(JsonElement r)
    {
        var assets = new List<(string, string, long)>();
        if (r.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
                if (Opt(a, "name") is { } name && Opt(a, "browser_download_url") is { } url && url.StartsWith("https://", StringComparison.Ordinal))
                    assets.Add((name, url, a.GetProperty("size").GetInt64()));
        DateTimeOffset? published = Opt(r, "published_at") is { } p
            && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
        var page = Opt(r, "html_url") is { } h && h.StartsWith("https://", StringComparison.Ordinal) ? h : null;
        // (a page's text is a few KB; one far larger is cut, so a list of them stays small in memory)
        var body = Opt(r, "body") is { } b ? b.Length > MaxBody ? b[..MaxBody] : b : null;
        return new ListedRelease(Opt(r, "tag_name") ?? "", Flag(r, "draft"), Flag(r, "prerelease"), published, page,
                                 Opt(r, "target_commitish") ?? "", assets, body);
    }

    /// What a channel offers from the list, null when nothing: its newest release this launcher supports on platform (Supported),
    /// an older one when the newest are make_game.py's
    public static string? Newest(IEnumerable<ListedRelease> list, string channel, Product product, Platform platform) =>
        OnChannel(list, channel, product, platform).FirstOrDefault()?.Tag;

    /// Whether this launcher offers a release on platform: a Longshot release only when it is of longshot-builder's kind for this
    /// system (its manifest, LongshotBuildRelease.ManifestName, is one of its files); one built with make_game.py, or with no
    /// builder for this system, is never offered (OldVersions). Every launcher release is
    public static bool Supported(ListedRelease r, Product product, Platform platform) =>
        product.Name != Product.Longshot.Name
        || r.Assets.Any(a => a.Name == LongshotBuildRelease.ManifestName(r.Tag, PlatformKey(platform)));

    /// Every published release a channel offers from the list on platform (Supported), newest first (Versions.Newer: one
    /// channel's versions are all of one order): its newest, then the ones before it that the server still has (the Version
    /// dropdown on Home, Add version)
    public static IReadOnlyList<ListedRelease> OnChannel(IEnumerable<ListedRelease> list, string channel, Product product, Platform platform)
    {
        var published = list.Where(r => !r.Draft && Versions.IsTagName(r.Tag) && Supported(r, product, platform));
        var on = channel switch
        {
            Versions.Nightly => published.Where(r => Versions.TrackOf(r.Tag) == Versions.Nightly),
            Versions.Stable => published.Where(r => !r.Prerelease && Versions.IsRelease(r.Tag, product.FirstMajor)),
            _ => throw new ArgumentException("Not a channel: " + channel, nameof(channel)),
        };
        // (a tag listed twice counts once; OrderBy is stable: ties keep the list's order, the server's newest first)
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var order = Comparer<string>.Create((a, b) => Versions.Newer(a, b, product.FirstMajor) ? 1 : Versions.Newer(b, a, product.FirstMajor) ? -1 : 0);
        return on.Where(r => tags.Add(r.Tag)).OrderByDescending(r => r.Tag, order).ToList();
    }

    /// The release's files, each with the SHA-256 its signed SHA256SUMS gives; a file the list does not name is not taken
    public static ReleaseInfo Build(ListedRelease r, IReadOnlyDictionary<string, string> sums, Product product)
    {
        var assets = r.Assets.ToDictionary(a => a.Name, a => a, StringComparer.Ordinal);
        Dictionary<string, FileRef> Pick(Func<string, string> name, IEnumerable<string>? keys = null)
        {
            var files = new Dictionary<string, FileRef>(StringComparer.Ordinal);
            foreach (var platform in keys ?? new[] { "windows-x64", "linux-x64" })
            {
                var n = name(platform);
                if (assets.TryGetValue(n, out var a) && sums.TryGetValue(n, out var sha))
                {
                    if (a.Size < 0 || a.Size > MaxFile) throw new InvalidDataException($"{n}: a size out of range.");
                    files[platform] = new FileRef(a.Url, sha, a.Size);
                }
            }
            return files;
        }
        var empty = new Dictionary<string, FileRef>();
        if (product.Name == Product.Launcher.Name)
            return new ReleaseInfo(product.Name, r.Tag, r.Commit, r.Published, r.Page, Pick(p => LauncherFile(r.Tag, p), LauncherKeys));
        return new ReleaseInfo(product.Name, r.Tag, r.Commit, r.Published, r.Page, empty,
                               // (by the manifest's exact name, never by a name holding "Builder": the old Windows app is LongshotBuilder)
                               Pick(p => LongshotBuildRelease.ManifestName(r.Tag, p)),
                               r.Assets.GroupBy(a => a.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (g.First().Url, g.First().Size), StringComparer.Ordinal));
    }

    /// A small file a release has, into memory, checked for its size and SHA-256
    public static async Task<byte[]> FetchAsync(IFetcher fetcher, FileRef file, long max, CancellationToken cancel)
    {
        if (file.Size > max) throw new InvalidDataException($"{file.Url} is larger than expected.");
        var data = await FetchUncheckedAsync(fetcher, file.Url, file.Size, cancel).ConfigureAwait(false);
        if (data.LongLength != file.Size || Convert.ToHexStringLower(SHA256.HashData(data)) != file.Sha256)
            throw new InvalidDataException($"{file.Url} is not the file its release names (size or SHA-256).");
        return data;
    }

    /// A small file into memory, no larger than max (SHA256SUMS and its signature, which the signature itself checks)
    public static async Task<byte[]> FetchUncheckedAsync(IFetcher fetcher, string url, long max, CancellationToken cancel)
    {
        using var bytes = new MemoryStream();
        await fetcher.DownloadAsync(url, bytes, have =>
        {
            if (have > max) throw new InvalidDataException($"{url} is larger than expected.");
        }, cancel).ConfigureAwait(false);
        return bytes.ToArray();
    }
}
