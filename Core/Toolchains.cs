using System.Text.Json;

namespace LongshotLauncher.Core;

/// One installed version's builder: Run, its program's name in Folder (a version folder's Longshot.exe or Longshot.x86_64)
public sealed record Toolchain(string Tag, string Folder, string? Run)
{
    /// The builder's program
    public string BuilderExe => Path.Combine(Folder, Run ?? LongshotBuilder.Name);
}

/// The builder of each version the launcher builds with: the program in its version folder, &lt;launcher data&gt;/versions/&lt;tag&gt;/
/// (BuilderVersions), put there by a builder's own fetch (FetchAsync: `longshot-builder fetch &lt;tag&gt; --out &lt;version folder&gt;
/// --from &lt;every installed version folder&gt; --output json`, Longshot's build.md "Fetch a version"), which checks the release's
/// signature, applies a delta or a chain of them from a kept version, or downloads the full file, and writes its record,
/// longshot_release.json, beside it. A version built from source has source.json instead (SourceBuilds). The launcher keeps no
/// manifest, delta or chain of its own: only the first builder on a PC that has none is downloaded by it (BootstrapAsync), whole
/// and checked against the release's signed manifest, and placed as fetch places it. An older launcher kept its builders in
/// &lt;launcher data&gt;/toolchains/&lt;tag&gt;/ (Legacy): read as a delta's base and as the builder of a version folder made from them,
/// never installed into any more, and pruned once no row uses them (Prune)
public sealed class Toolchains
{
    /// The record fetch writes beside the program (Longshot's builder/crates/release install.rs RECORD)
    public const string ReleaseRecord = "longshot_release.json";
    /// The record from-source writes beside the program (SourceBuilds)
    public const string SourceRecord = "source.json";
    /// An older launcher's record of a builder in toolchains/&lt;tag&gt;/ (fetch reads it as a delta's base too)
    public const string LegacyRecord = "toolchain.json";
    /// The kind an older launcher's record names for longshot-builder's one file (its make_game.py tools' are Setup or Linux)
    public const string LegacyKind = "LongshotBuild";
    /// Where fetch stages what it makes, inside the version folder (install.rs STAGING; deleted whatever happens)
    public const string Staging = ".fetch";

    readonly string root;
    readonly string? legacy;
    readonly Platform platform;

    /// root: the version folders' (BuilderVersions.Root); legacy: an older launcher's toolchains folder (LauncherHome.Toolchains)
    public Toolchains(string root, Platform platform, string? legacy = null)
    {
        this.root = root;
        this.platform = platform;
        this.legacy = legacy;
    }

    public string Root => root;
    /// An older launcher's toolchains folder; null when not given
    public string? LegacyRoot => legacy;
    public Platform Platform => platform;

    public string PathOf(string tag) =>
        Versions.IsTagName(tag) ? Path.Combine(root, tag) : throw new ArgumentException("Not a version name: " + tag, nameof(tag));

    /// An installed version's builder: its version folder's program, there, with a record that names this version (fetch's or
    /// from-source's). Null otherwise: not installed, or taken (an antivirus's quarantine), so the next build fetches it again
    public Toolchain? Find(string tag)
    {
        if (!Versions.IsTagName(tag)) return null;
        var folder = Path.Combine(root, tag);
        var exe = BuilderVersions.ExeName(platform);
        return File.Exists(Path.Combine(folder, exe)) && RecordedVersion(folder) == tag ? new Toolchain(tag, folder, exe) : null;
    }

    /// Whether a version's builder is installed (Find)
    public bool IsInstalled(string tag) => Find(tag) != null;

    /// The versions installed: their version folders holding a builder whose record names them
    public IReadOnlyList<string> Installed()
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>()
            .Where(n => Versions.IsTagName(n) && Find(n) != null)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// The version a version folder's record names: fetch's longshot_release.json, else from-source's source.json ("version");
    /// null for neither
    public static string? RecordedVersion(string folder) =>
        Field(Path.Combine(folder, ReleaseRecord), "version") ?? Field(Path.Combine(folder, SourceRecord), "version");

    /// A string at the top of a record; null when the file is not there or not a JSON object with it
    static string? Field(string path, string name)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, name) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// An older launcher's builder of tag (toolchains/&lt;tag&gt;/, its record of longshot-builder's kind and its file there); null
    /// when there is none. Read only: the builder of a version folder made from its row (LauncherService), and a delta's base
    public Toolchain? Legacy(string tag)
    {
        if (legacy == null || !Versions.IsTagName(tag)) return null;
        var folder = Path.Combine(legacy, tag);
        var record = Path.Combine(folder, LegacyRecord);
        if (Field(record, "kind") != LegacyKind || Field(record, "run") is not { Length: > 0 } run || run != Path.GetFileName(run)
            || !File.Exists(Path.Combine(folder, run)))
            return null;
        return new Toolchain(tag, folder, run);
    }

    /// The folders a fetch may take a delta's base from (its --from): every version folder with a builder in it, whatever its
    /// record (fetch hashes a program with none), and an older launcher's toolchains
    public IReadOnlyList<string> Bases()
    {
        var bases = new List<string>();
        if (Directory.Exists(root))
            bases.AddRange(Directory.EnumerateDirectories(root)
                                    .Where(d => Versions.IsTagName(Path.GetFileName(d)) && File.Exists(BuilderVersions.ExeOf(d, platform))));
        if (legacy != null && Directory.Exists(legacy))
            bases.AddRange(Directory.EnumerateDirectories(legacy).Where(d => Legacy(Path.GetFileName(d)) != null));
        return bases.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    /// The builder that runs a fetch: the newest installed version's (by Versions' order, then the newest program), else an older
    /// launcher's. Null on a PC with none (BootstrapAsync)
    public Toolchain? Fetcher()
    {
        var newest = Comparer<string>.Create((a, b) => Versions.Newer(a, b) ? -1 : Versions.Newer(b, a) ? 1 : 0);
        IEnumerable<Toolchain> mine = Installed().Select(t => Find(t)).OfType<Toolchain>();
        if (!mine.Any() && legacy != null && Directory.Exists(legacy))
            mine = Directory.EnumerateDirectories(legacy).Select(d => Legacy(Path.GetFileName(d))).OfType<Toolchain>();
        return mine.OrderBy(t => t.Tag, newest).ThenByDescending(t => File.GetLastWriteTimeUtc(t.BuilderExe)).FirstOrDefault();
    }

    /// An older launcher's toolchains that keep does not name, deleted (with its staging and downloads); the names removed. The
    /// version folders are never pruned here: BuilderVersions.Prunable
    public IReadOnlyList<string> Prune(IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.Ordinal);
        var removed = new List<string>();
        if (legacy == null || !Directory.Exists(legacy)) return removed;
        foreach (var dir in Directory.EnumerateDirectories(legacy))
        {
            var name = Path.GetFileName(dir);
            var stale = name == ".downloads" || name.EndsWith(".part", StringComparison.Ordinal);
            if (!stale && (!Versions.IsTagName(name) || kept.Contains(name))) continue;
            DeleteTree(dir);
            removed.Add(name);
        }
        return removed;
    }

    /// A version's builder installed into its version folder by a builder's own fetch: progress as it says it (fetch_progress),
    /// its log lines (log). On a PC with no builder at all, or whose builder has no fetch (an older one: a command line it does
    /// not take), BootstrapAsync. Throws InvalidDataException with the builder's text when it stopped (its error event), and
    /// OperationCanceledException when cancelled (the process ended: the next fetch clears what it left)
    public Task<Toolchain> FetchAsync(Releases releases, string tag, Action<ToolsDownloadProgress>? progress = null, Action<string>? log = null,
                                      CancellationToken cancel = default, Func<string, string?>? getenv = null) =>
        FetchAsync(c => releases.VersionAsync(tag, c), releases.Fetcher, tag, progress, log, cancel, getenv);

    /// FetchAsync with the release read by release (its files checked against its signed SHA256SUMS), downloaded by fetcher
    public async Task<Toolchain> FetchAsync(Func<CancellationToken, Task<ReleaseInfo>> release, IFetcher fetcher, string tag,
                                            Action<ToolsDownloadProgress>? progress = null, Action<string>? log = null,
                                            CancellationToken cancel = default, Func<string, string?>? getenv = null)
    {
        if (Fetcher() is { } runner)
        {
            if (await RunFetchAsync(runner, tag, progress, log, cancel, getenv).ConfigureAwait(false) is { } fetched) return fetched;
            log?.Invoke($"longshot-builder {tag}: {runner.Tag}'s builder has no fetch, or does not start: downloaded by the launcher");
        }
        return await BootstrapAsync(await release(cancel).ConfigureAwait(false), fetcher, progress, log, cancel).ConfigureAwait(false);
    }

    /// The fetch's command line: fetch &lt;tag&gt; --out &lt;its version folder&gt; --from &lt;each base&gt; --output json
    public IReadOnlyList<string> FetchArguments(string tag)
    {
        var args = new List<string> { "fetch", tag, "--out", PathOf(tag) };
        foreach (var b in Bases().Where(b => !BuildRegistry.SameFolder(b, PathOf(tag)))) args.AddRange(["--from", b]);
        args.AddRange(LongshotBuilder.OutputJson);
        return args;
    }

    /// fetch run by runner: the version installed, or null when the runner does not take the command (exit status 2 with no
    /// error event: a builder from before fetch) or does not start at all (damaged, or another system's)
    async Task<Toolchain?> RunFetchAsync(Toolchain runner, string tag, Action<ToolsDownloadProgress>? progress, Action<string>? log,
                                         CancellationToken cancel, Func<string, string?>? getenv)
    {
        var answer = await LongshotBuilder.AskAsync(runner.BuilderExe, FetchArguments(tag), e =>
        {
            switch (e)
            {
                case BuildEvent.FetchProgress p:
                    progress?.Invoke(new ToolsDownloadProgress(p.Total > 0 ? Math.Clamp(p.Bytes / (double)p.Total, 0, 1) : 0, p.Total));
                    break;
                case BuildEvent.Log l:
                    log?.Invoke($"longshot-builder {tag}: {l.Text}");
                    break;
            }
        }, cancel, getenv).ConfigureAwait(false);
        // (exit 2 first: a builder with no fetch command says so as a usage error event too, and falls back all the same)
        if (answer.Exit is 2 or -1)
        {
            log?.Invoke($"longshot-builder {tag}: {runner.Tag}'s builder did not fetch: {answer.Why}");
            return null;
        }
        if (answer.Error is { } error)
            throw new InvalidDataException(error.Code == "no_builder_in_release" ? OldVersions.NotBuiltHere(tag) : error.Text);
        if (answer.Exit != 0 || answer.Events.OfType<BuildEvent.Fetched>().LastOrDefault() is not { } fetched)
            throw new InvalidDataException(answer.Why ?? Tr.F("The build tools stopped without saying why (exit code {0}).", answer.Exit));
        log?.Invoke($"longshot-builder {tag}: {fetched.How}" + (fetched.From is { } from ? $" from {from}" : "")
                    + $", {fetched.Downloaded >> 20} MB downloaded, by {runner.Tag}'s builder");
        progress?.Invoke(new ToolsDownloadProgress(1, 0));
        return Find(tag) ?? throw new InvalidDataException(Tr.F("{0}'s builder was fetched, but its version folder does not hold it.", Versions.Shown(tag)));
    }

    /// The first builder on a PC that has none: the release's full file for this system, downloaded and checked against its
    /// manifest (its size and SHA-256), the manifest against the release's signed SHA256SUMS (as Releases.VersionAsync read it), and placed
    /// in the version folder as fetch places it, with the record fetch writes, so the next fetch takes it as one of its own. A
    /// release with no builder for this system was made for make_game.py, which this launcher does not run: OldVersions' words
    public async Task<Toolchain> BootstrapAsync(ReleaseInfo release, IFetcher fetcher, Action<ToolsDownloadProgress>? progress = null,
                                                Action<string>? log = null, CancellationToken cancel = default)
    {
        var tag = release.Version;
        var key = ReleaseManifest.PlatformKey(platform);
        if (release.BuilderManifests?.TryGetValue(key, out var manifestFile) != true)
            throw new InvalidDataException(OldVersions.NotBuiltHere(tag));
        var json = System.Text.Encoding.UTF8.GetString(
            await ReleaseManifest.FetchAsync(fetcher, manifestFile!, LongshotBuildRelease.MaxManifest, cancel).ConfigureAwait(false));
        var manifest = LongshotBuildRelease.Parse(json, tag, key);
        if (release.Assets?.TryGetValue(manifest.Full.Name, out var full) != true) throw new InvalidDataException($"{tag} has no {manifest.Full.Name}.");
        var dest = PathOf(tag);
        var staging = Path.Combine(dest, Staging);
        if (new DirectoryInfo(staging).LinkTarget != null) throw new IOException("The version folder's staging folder is a symbolic link: " + staging);
        DeleteTreeIfPresent(staging);
        Directory.CreateDirectory(staging);
        var exe = BuilderVersions.ExeName(platform);
        var made = Path.Combine(staging, exe + ".part");
        try
        {
            progress?.Invoke(new ToolsDownloadProgress(0, manifest.Full.Size));
            await LongshotBuildRelease.DownloadAsync(fetcher, full.Url, manifest.Full, made,
                have => progress?.Invoke(new ToolsDownloadProgress(Math.Min(1, have / (double)manifest.Full.Size), manifest.Full.Size)), cancel)
                .ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(made, File.GetUnixFileMode(made) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            cancel.ThrowIfCancellationRequested();
            // (Windows: an antivirus scans the program just written, holding it: tried again while it is held)
            SelfUpdate.WhileHeld(() => File.Move(made, Path.Combine(dest, exe), overwrite: true));
            File.WriteAllText(Path.Combine(dest, ReleaseRecord), manifest.Record(exe));
        }
        finally { DeleteTreeIfPresent(staging); }
        log?.Invoke($"longshot-builder {tag}: downloaded whole by the launcher ({manifest.Full.Size >> 20} MB): no builder on this PC to fetch it");
        progress?.Invoke(new ToolsDownloadProgress(1, 0));
        return Find(tag) ?? throw new InvalidDataException(Tr.F("{0}'s builder was fetched, but its version folder does not hold it.", Versions.Shown(tag)));
    }

    /// A version's stamps from its record (ShaderCaches.Stamp), translator then compiler; null when its record has none
    public static string? BuilderStamp(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ReleaseRecord);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("stamps", out var s)
                   && s.ValueKind == JsonValueKind.Object && Str(s, "translator") is { Length: > 0 } t && Str(s, "compiler") is { Length: > 0 } c
                ? t + "-" + c : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// A folder and everything in it; a link in it is removed, never followed
    public static void DeleteTree(string dir)
    {
        DeleteTreeIfPresent(dir);
    }

    /// Delete a tree, reporting whether its root was present when deletion began.
    public static bool DeleteTreeIfPresent(string dir)
    {
        var info = new DirectoryInfo(dir);
        try { File.GetAttributes(dir); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        if (info.LinkTarget != null) { info.Delete(); return true; }
        foreach (var child in info.EnumerateFileSystemInfos())
        {
            if (child is DirectoryInfo d && d.LinkTarget == null) DeleteTree(d.FullName);
            else
            {
                if (!OperatingSystem.IsWindows() || child.LinkTarget != null) { child.Delete(); continue; }
                child.Attributes = FileAttributes.Normal;
                child.Delete();
            }
        }
        info.Delete();
        return true;
    }
}
