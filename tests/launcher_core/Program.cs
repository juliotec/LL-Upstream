using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LongshotLauncher.Core;

/// The launcher's core (launcher/Core) over authored inputs: no game, no network, no window.
///     LauncherCoreTests <vectors.json> <tests/fixtures> <scratch folder>
/// vectors.json: tests/test_launcher_core.py's TRACKS, ORDER and UNORDERED. The signed files are tests/fixtures/release's.
static partial class Program
{
    static int checks;
    static string scratch = "";

    static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++;
    }

    static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception(message);
    }

    static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { checks++; return; }
        throw new Exception(message);
    }

    static string Folder(string name)
    {
        var path = Path.Combine(scratch, name);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);
        return path;
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--atomic-", StringComparison.Ordinal)) return AtomicChild(args);
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: LauncherCoreTests <vectors.json> <fixtures folder> <scratch folder>");
            return 2;
        }
        using var vectors = JsonDocument.Parse(File.ReadAllText(args[0]));
        var fixtures = args[1];
        scratch = args[2];
        Directory.CreateDirectory(scratch);

        RequirementsChecks();
        GameFolderChecks();
        AtomicChecks();
        KeepAwakeChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        VersionChecks(vectors.RootElement);
        await ReleaseChecks(Path.Combine(fixtures, "release"));
        ReleaseNotesChecks();
        VersionPickChecks();
        await SelfUpdateChecks();
        await InstallChecks();
        ToolchainChecks();
        await CdnChecks();
        await ReportChecks();
        PrivacyChecks();
        await PrefetchChecks();
        RegistryChecks();
        LeftoverChecks();
        UnlistedChecks();
        InstanceFolderChecks();
        OtherLauncherChecks();
        DeleteSaveChecks();
        SettingsChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        ProgressChecks();
        LogChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        DataLogChecks();
        LogFileChecks();
        CommandChecks();
        GamescopeChecks();
        ShaderCacheChecks();
        StorageChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        UpdatePolicyChecks();
        await PullRequestChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        CommandLineChecks();
        SavesChecks.Run(Folder("saves"));
        SaveArchiveChecks.Run(Folder("save-archive"));
        SaveBackupsChecks.Run(Folder("save-backups"));
        ProfileModelChecks.Run(Folder("profiles"), Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        HookshotChecks(fixtures);
        SaveEditorChecks(fixtures);
        HookshotSettingsChecks();
        SettingsCarryChecks();
        ShownProfileChecks();
        DriverSettingsChecks();
        NvidiaCacheChecks();
        EditBackupsChecks();
        NfcChecks();
        NfcReaderChecks();
        PadChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        Utf8Checks();
        SteamChecks(Path.GetFullPath(Path.Combine(fixtures)));
        RouteChecks(fixtures);
        await ScreenChecks();
        await BusyChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        IconChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        ProblemChecks();
        LongshotBuildChecks(fixtures);
        BuilderStorageChecks();
        await LongshotBuildReleaseChecks();
        await BuilderInfoChecks();
        await BuilderStorageCommandChecks();
        PerfChecks();
        CrashReportChecks();
        await BuildReportChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        TranslationChecks();
        RemoveEverythingChecks();
        OldVersionChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        StableSwitchChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        VersionListChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        checks += SavesChecks.Count;
        checks += SaveArchiveChecks.Count;
        checks += SaveBackupsChecks.Count;
        checks += ProfileModelChecks.Count;
        Console.WriteLine($"launcher core: {checks} checks passed");
        return 0;
    }

    // ---- Versions: the shared vectors ----

    static void VersionChecks(JsonElement v)
    {
        var tracks = v.GetProperty("tracks").EnumerateArray().Select(t => (t[0].GetString()!, t[1].GetString()!)).ToList();
        Require(tracks.Count >= 8, "The track vectors did not come through.");
        foreach (var (version, track) in tracks.Concat(new[] { ("nightly", "stable"), ("rc", "stable"), ("dev", "stable") }))
            Require(Versions.TrackOf(version) == track, $"TrackOf({version}) is not {track}.");
        Require(Versions.TrackOf(null) == Versions.Stable, "No version is a release's track.");
        var order = v.GetProperty("order").EnumerateArray().Select(o => (o[0].GetString()!, o[1].GetString()!)).ToList();
        Require(order.Count >= 6, "The order vectors did not come through.");
        foreach (var (newer, older) in order)
        {
            Require(Versions.Newer(newer, older), $"{newer} is not newer than {older}.");
            Require(!Versions.Newer(older, newer), $"{older} is newer than {newer}.");
            Require(!Versions.Newer(newer, newer), $"{newer} is newer than itself.");
            Require(Versions.Compare(newer, older) == 1 && Versions.Compare(older, newer) == -1, "Compare disagrees with Newer.");
        }
        var unordered = v.GetProperty("unordered").EnumerateArray().Select(o => (o[0].GetString()!, o[1].GetString()!)).ToList();
        Require(unordered.Count >= 6, "The unordered vectors did not come through.");
        foreach (var (a, b) in unordered)
            Require(!Versions.Newer(a, b) && !Versions.Newer(b, a) && Versions.Compare(a, b) == 0, $"{a} and {b} were ordered.");
        Require(Versions.Shown("nightly-2029-c544828f") == "Nightly 2029 \u00b7 c544828f"
                && Versions.Shown("nightly-2029-c544828f-dirty") == "Nightly 2029 \u00b7 c544828f-dirty"
                && Versions.Shown("v3.1.0-rc.3") == "v3.1.0-rc.3" && Versions.Shown("nightly-2029-c544828") == "nightly-2029-c544828"
                && Versions.Shown(null) == "", "A nightly as players see it: its number and its commit.");
        Require(!Versions.IsRelease("v3.0.0-rc.5") && !Versions.IsVersion("v3.0.0-rc.5") && !Versions.IsChannel("rc")
                && Versions.Channels.SequenceEqual(new[] { Versions.Stable, Versions.Nightly }),
                "An old release candidate is no version, and rc no channel: stable and nightly are the two.");
        Require(!Versions.Newer("v3.1.0", "dev") && !Versions.Newer("dev", "v3.1.0") && !Versions.Newer("v99999999999999999999.0.0", "v1.0.0"),
                "A name that is no version is never newer, nor older.");
        Require(Versions.IsTagName("v3.1.0-rc.1-3-g1a2b3c4") && Versions.IsTagName("nightly-2029-c544828f-dirty") && !Versions.IsTagName("..") && !Versions.IsTagName("a/b")
                && !Versions.IsTagName(new string('a', 129)), "The tag name rule.");
    }

    // ---- Releases, over a fake server ----

    sealed class FakeFetcher : IFetcher
    {
        public readonly Dictionary<string, Func<string>> Texts = new();
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<string> Asked = new();

        public Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel)
        {
            Asked.Add(url);
            return Texts.TryGetValue(url, out var f) ? Task.FromResult(f()) : throw new FetchException("not found: " + url, 404);
        }

        public async Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel)
        {
            Asked.Add(url);
            if (!Files.TryGetValue(url, out var bytes)) throw new FetchException("not found: " + url, 404);
            for (var at = 0; at < bytes.Length; at += 7)
            {
                cancel.ThrowIfCancellationRequested();
                var n = Math.Min(7, bytes.Length - at);
                await to.WriteAsync(bytes.AsMemory(at, n), cancel);
                progress?.Invoke(at + n);
            }
        }
    }

    static Func<string> Fail(int? status) => () => throw new FetchException("authored failure", status);

    // ---- releases (Releases, ReleaseManifest, SignedSums) ----

    const string Commit = "0123456789abcdef0123456789abcdef01234567";
    static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    static (ECDsa Key, ReleaseKey Trusted) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, ReleaseKey.FromSpki(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    }

    static string ShaOf(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// A Forgejo server with releases: each (tag, draft, prerelease, files) published with SHA256SUMS signed by key (null: none)
    sealed class FakeForgejo
    {
        public readonly FakeFetcher Server = new();
        readonly List<JsonObject> list = new();
        readonly Product product;
        public FakeForgejo(Product product) => this.product = product;

        public void Add(string tag, ECDsa? key, bool draft = false, bool? prerelease = null, Dictionary<string, byte[]>? files = null,
                        Func<string, string>? sums = null)
        {
            files ??= new Dictionary<string, byte[]>();
            var assets = new JsonArray();
            void Asset(string name, byte[] data)
            {
                var url = $"https://git.magicbean.dev/{product.Repo}/releases/download/{tag}/{name}";
                Server.Files[url] = data;
                assets.Add(new JsonObject { ["name"] = name, ["browser_download_url"] = url, ["size"] = data.Length });
            }
            foreach (var (name, data) in files) Asset(name, data);
            if (key != null)
            {
                var text = string.Concat(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{ShaOf(f.Value)}  {f.Key}\n"));
                var sumBytes = Encoding.UTF8.GetBytes(sums?.Invoke(text) ?? text);
                Asset("SHA256SUMS", sumBytes);
                Asset("SHA256SUMS.sig", Encoding.ASCII.GetBytes(Convert.ToBase64String(
                    key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n"));
            }
            var rel = new JsonObject
            {
                ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = prerelease ?? !System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$"),
                ["published_at"] = "2026-11-12T10:00:00Z", ["html_url"] = $"https://git.magicbean.dev/{product.Repo}/releases/tag/{tag}",
                ["target_commitish"] = Commit, ["assets"] = assets,
            };
            list.RemoveAll(r => (string?)r["tag_name"] == tag);
            list.Add(rel);
            Publish();
        }

        public void Remove(string tag)
        {
            list.RemoveAll(r => (string?)r["tag_name"] == tag);
            Publish();
        }

        void Publish()
        {
            var json = new JsonArray(list.Select(r => (JsonNode)JsonNode.Parse(r.ToJsonString())!).ToArray()).ToJsonString();
            Server.Texts[$"{product.Api}/releases?limit=50&page=1"] = () => json;
            foreach (var r in list)
            {
                var one = r.ToJsonString();
                Server.Texts[$"{product.Api}/releases/tags/{(string?)r["tag_name"]}"] = () => one;
            }
        }
    }

    static Dictionary<string, byte[]> LauncherFiles(string v) => new()
    {
        [ReleaseManifest.LauncherFile(v, "linux-x64")] = Encoding.UTF8.GetBytes("launcher " + v),
        [ReleaseManifest.LauncherFile(v, "windows-x64")] = Encoding.UTF8.GetBytes("MZ launcher " + v),
        [ReleaseManifest.LauncherFile(v, ReleaseManifest.WindowsPortable)] = Zip((LauncherLayout.ProgramName, "MZ launcher " + v, null, 0),
                                                                                  (LauncherLayout.PortableMarker, "", null, 0)),
    };

    static async Task ReleaseChecks(string releaseFixtures)
    {
        var (key, trusted) = NewKey();
        var (stranger, _) = NewKey();
        var keys = new[] { trusted };

        // (the channels, from the release list: drafts are not there yet, pre-releases are not stable)
        var forge = new FakeForgejo(Product.Longshot);
        var longshot = new Releases(forge.Server, Platform.Linux, keys, NoWait, Product.Longshot);
        Require(longshot.ListUrl(1) == "https://git.magicbean.dev/api/v1/repos/MagicBeanDev/Longshot/releases?limit=50&page=1"
                && longshot.VersionUrl("v3.0.0") == "https://git.magicbean.dev/api/v1/repos/MagicBeanDev/Longshot/releases/tags/v3.0.0",
                "Where Longshot's releases are.");
        var manifest = Encoding.UTF8.GetBytes("{\"v\":1}");
        Dictionary<string, byte[]> Builder(string v) => new()
        {
            [LongshotBuildRelease.ManifestName(v, "linux-x64")] = manifest,
            [LongshotBuildRelease.ManifestName(v, "windows-x64")] = manifest,
        };
        forge.Add("v3.0.0", key, files: Builder("v3.0.0"));
        forge.Add("v3.0.1", key, prerelease: false, files: Builder("v3.0.1"));
        forge.Add("v3.1.0-rc.1", key, files: Builder("v3.1.0-rc.1"));   // (an old release candidate: on no channel)
        forge.Add("v3.2.0", key, prerelease: false, draft: true, files: Builder("v3.2.0"));
        forge.Add("nightly-9-aaaaaaaa", key, files: Builder("nightly-9-aaaaaaaa"));
        forge.Add("nightly-12-bbbbbbbb", key, files: Builder("nightly-12-bbbbbbbb"));
        forge.Add("tools", null);
        forge.Add("v2.9.0", key, prerelease: false, files: Builder("v2.9.0"));
        Require(await longshot.NewestAsync("stable") == "v3.0.1" && await longshot.NewestAsync("nightly") == "nightly-12-bbbbbbbb",
                "Each channel's newest: the stable (not a draft, not an old release candidate), the nightly by its count; never 2.x or tools.");
        await ThrowsAsync<ArgumentException>(() => longshot.NewestAsync("rc"), "rc was taken for a channel.");
        Require(longshot.Listed is { } listed && listed.Any(r => r.Tag == "v3.0.0") && !listed.Any(r => r.Draft),
                "The list the check read is kept, without its drafts, for What's new.");
        var st = (await longshot.ChannelAsync("stable"))!;
        Require(st.BuilderManifests!["linux-x64"].Size == manifest.Length && st.BuilderManifests["linux-x64"].Sha256 == ShaOf(manifest)
                && st.BuilderManifests.ContainsKey("windows-x64")
                && st.Commit == Commit && st.Notes == "https://git.magicbean.dev/MagicBeanDev/Longshot/releases/tag/v3.0.1",
                "A release's files, with the SHA-256 its signed SHA256SUMS gives.");
        forge.Add("v3.1.0", key, prerelease: false, files: Builder("v3.1.0"));
        Require(await longshot.NewestAsync("stable") == "v3.1.0", "A new stable is its channel's newest.");
        // (pulling a bad version: deleted, or back to a draft, its channel falls back by itself)
        forge.Remove("v3.1.0");
        forge.Remove("v3.0.1");
        Require(await longshot.NewestAsync("stable") == "v3.0.0", "A deleted stable is gone from its channel.");
        forge.Add("v3.0.0", key, draft: true, files: Builder("v3.0.0"));
        Require(await longshot.NewestAsync("stable") == null, "A stable set back to a draft is gone from its channel.");
        forge.Add("v3.0.0", key, files: Builder("v3.0.0"));

        // (releases built with make_game.py, no longshot-builder manifest, or one for another system only, are never offered:
        // a channel whose newest are such offers its newest supported release)
        Dictionary<string, byte[]> OldKind(string v) => new() { ["Longshot-" + v + ".zip"] = manifest };
        forge.Add("v3.4.0", key, prerelease: false, files: OldKind("v3.4.0"));
        forge.Add("v3.4.1", key, prerelease: false, files: new() { [LongshotBuildRelease.ManifestName("v3.4.1", "windows-x64")] = manifest });
        forge.Add("nightly-13-cccccccc", key, files: OldKind("nightly-13-cccccccc"));
        var mixed = new Releases(forge.Server, Platform.Linux, keys, NoWait, Product.Longshot);
        var both = await mixed.ChannelsAsync(Versions.Channels);
        Require(both["stable"]?.Version == "v3.0.0" && both["nightly"]?.Version == "nightly-12-bbbbbbbb",
                "The update check skips the newer make_game.py releases and the Windows-only one on Linux: each channel's newest builder release.");
        Require(await new Releases(forge.Server, Platform.Windows, keys, NoWait, Product.Longshot).NewestAsync("stable") == "v3.4.1",
                "On Windows, the release with a Windows manifest is the newest.");
        Require(VersionPick.Of("stable", Array.Empty<BuildEntry>(), mixed.Listed, Product.Longshot, Platform.Linux).All(v => v.Tag == "v3.0.0"),
                "The Version list holds only the supported releases.");
        forge.Remove("v3.4.0");
        forge.Remove("v3.4.1");
        forge.Remove("nightly-13-cccccccc");

        // (refused: no signature, another key, a list changed after it was signed, a release of the other product's numbers)
        async Task Refused(string tag, ECDsa? signer, string why, Func<string, string>? sums = null)
        {
            forge.Add(tag, signer, files: Builder(tag), sums: sums);
            var r = new Releases(forge.Server, Platform.Linux, keys, NoWait, Product.Longshot);
            await ThrowsAsync<InvalidDataException>(() => r.VersionAsync(tag), why);
            forge.Remove(tag);
        }
        await Refused("v3.2.0", null, "A release with no SHA256SUMS was taken.");
        await Refused("v3.2.0", stranger, "A release signed by a key this launcher does not trust was taken.");
        await Refused("v3.2.0", key, "A SHA256SUMS changed after it was signed was taken.", s => s.Replace("v3.2.0", "v3.9.9"));
        await ThrowsAsync<InvalidDataException>(() => new Releases(forge.Server, Platform.Linux, Array.Empty<ReleaseKey>(), NoWait).VersionAsync("v3.0.0"),
                                                "A launcher with no key took a release.");
        await ThrowsAsync<FetchException>(() => longshot.VersionAsync("v3.5.0"), "A version with no release.");
        forge.Add("v3.3.0", stranger, files: Builder("v3.3.0"));
        var fresh = new Releases(forge.Server, Platform.Linux, keys, NoWait, Product.Longshot);
        Require(await fresh.ChannelAsync("stable") == null && fresh.Problem!.Contains("not signed by a key"),
                "The newest stable, refused, offers nothing, and says why.");
        forge.Remove("v3.3.0");
        // (a file of the release that SHA256SUMS does not name is not taken, nor one that is not this system's)
        forge.Add("v3.4.0", key, files: Builder("v3.4.0"), sums: s => string.Join("", s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.EndsWith("linux-x64.json", StringComparison.Ordinal)).Select(l => l + "\n")));
        await ThrowsAsync<InvalidDataException>(() => new Releases(forge.Server, Platform.Linux, keys, NoWait).VersionAsync("v3.4.0"),
                                                "A file the signed SHA256SUMS does not name was taken.");
        // (the server: tried twice, then nothing; the window says the check failed, quietly)
        var tries = 0;
        var flaky = new FakeFetcher();
        flaky.Texts[longshot.ListUrl(1)] = () => ++tries < 2 ? throw new FetchException("authored", 502) : "[]";
        Require(await new Releases(flaky, Platform.Linux, keys, NoWait).NewestAsync("stable") == null && tries == 2, "A server error is tried again.");
        Require(await new Releases(new FakeFetcher(), Platform.Linux, keys, NoWait).ChannelAsync("stable") == null, "No answer, no channel.");
        // (and the window says why: the fetch's words, until a check gets an answer)
        var down = new FakeFetcher();
        var answers = false;
        down.Texts[longshot.ListUrl(1)] = () => answers ? "[]" : throw new FetchException("This PC could not look up git.magicbean.dev's address.", null);
        var unreachable = new Releases(down, Platform.Linux, keys, NoWait);
        Require(await unreachable.ChannelAsync("stable") == null && unreachable.Unreachable == "This PC could not look up git.magicbean.dev's address.",
                "No answer says why.");
        answers = true;
        Require(await unreachable.ChannelAsync("stable") == null && unreachable.Unreachable == null && unreachable.Problem == null,
                "An answer clears the last check's why.");
        NetFailureChecks();

        // (the launcher's own releases: its own versions, from v1.0.0)
        var own = new FakeForgejo(Product.Launcher);
        var launcher = new Releases(own.Server, Platform.Linux, keys, NoWait, Product.Launcher);
        own.Add("v1.0.0", key, files: LauncherFiles("v1.0.0"));
        own.Add("v1.1.0-rc.1", key, files: LauncherFiles("v1.1.0-rc.1"));   // (an old release candidate: on no channel)
        own.Add("nightly-30-cccccccc", key, files: LauncherFiles("nightly-30-cccccccc"));
        var stable = (await launcher.ChannelAsync("stable"))!;
        Require(stable.Version == "v1.0.0" && stable.Launcher["linux-x64"].Url.EndsWith("/LongshotLauncher-v1.0.0-x86_64.AppImage", StringComparison.Ordinal)
                && stable.Launcher["windows-x64"].Url.EndsWith("/LongshotLauncher-v1.0.0-windows-x64-setup.exe", StringComparison.Ordinal)
                && stable.Launcher[ReleaseManifest.WindowsPortable].Url.EndsWith("/LongshotLauncher-v1.0.0-windows-x64-portable.zip", StringComparison.Ordinal)
                && stable.BuilderManifests is not { Count: > 0 } && await launcher.NewestAsync("nightly") == "nightly-30-cccccccc",
                "The launcher's own two channels, and its three files.");
        // (an update check reads each product's release list once, whatever the number of channels)
        own.Server.Asked.Clear();
        var two = await new Releases(own.Server, Platform.Linux, keys, NoWait, Product.Launcher).ChannelsAsync(Versions.Channels);
        Require(two["stable"]?.Version == "v1.0.0" && two["nightly"]?.Version == "nightly-30-cccccccc"
                && own.Server.Asked.Count(u => u.Contains("/releases?", StringComparison.Ordinal)) == 1, "Both channels from one fetch of the list.");
        Require(launcher.ListUrl(1).StartsWith("https://git.magicbean.dev/api/v1/repos/MagicBeanDev/LongshotLauncher/", StringComparison.Ordinal),
                "The launcher's releases are its own repository's.");

        // (scripts/release.py's SHA256SUMS and signature, made with the test key: the same format on both sides)
        var testKey = ReleaseKey.FromPem(File.ReadAllText(Path.Combine(releaseFixtures, "test-key.pub.pem")));
        var sums = SignedSums.Open(File.ReadAllBytes(Path.Combine(releaseFixtures, "SHA256SUMS")),
                                   File.ReadAllText(Path.Combine(releaseFixtures, "SHA256SUMS.sig")), new[] { testKey });
        Require(sums["LongshotLauncher-v1.0.0-x86_64.AppImage"] == ShaOf(Encoding.UTF8.GetBytes("not a launcher\n")) && sums.Count == 2,
                "scripts/release.py's SHA256SUMS, signed.");
        Throws<InvalidDataException>(() => SignedSums.Open(File.ReadAllBytes(Path.Combine(releaseFixtures, "SHA256SUMS")),
                                                           File.ReadAllText(Path.Combine(releaseFixtures, "SHA256SUMS.sig")), keys),
                                     "scripts/release.py's signature by the test key was taken without it.");
        Require(ReleaseKeys.Current(_ => null) == ReleaseKeys.BuiltIn
                && ReleaseKeys.Current(k => k == ReleaseKeys.TrustVariable ? Path.Combine(releaseFixtures, "test-key.pub.pem") : null)
                    .Any(k => k.Id == testKey.Id), "LONGSHOT_LAUNCHER_TRUST adds a test key.");
        Require(!ReleaseKeys.BuiltIn.Any(k => k.Id == testKey.Id), "The test key is built in.");
    }

    // ---- the launcher's own update (SelfUpdate) ----

    static async Task SelfUpdateChecks()
    {
        var file = (string url, byte[] bytes) => new FileRef(url, ShaOf(bytes), bytes.Length);
        var both = new Dictionary<string, FileRef> { ["linux-x64"] = file("https://x/l", new byte[1]), ["windows-x64"] = file("https://x/w", new byte[1]) };
        var none = new Dictionary<string, FileRef>();
        ReleaseInfo R(string v, IReadOnlyDictionary<string, FileRef>? l = null) => new("launcher", v, Commit, null, null, l ?? both);
        Require(SelfUpdate.UpdateTo("v1.0.0", R("v1.0.1"), Platform.Linux) == "v1.0.1"
                && SelfUpdate.UpdateTo("nightly-2041-aaaaaaaa", R("nightly-2042-bbbbbbbb"), Platform.Windows) == "nightly-2042-bbbbbbbb"
                && SelfUpdate.UpdateTo("v1.0.0", R("v1.1.0"), Platform.Linux, "stable") == "v1.1.0",
                "A newer launcher on its channel.");
        Require(SelfUpdate.UpdateTo("v1.0.1", R("v1.0.0"), Platform.Linux) == null && SelfUpdate.UpdateTo("v1.0.0", R("v1.0.0"), Platform.Linux) == null
                && SelfUpdate.UpdateTo("dev", R("v1.0.1"), Platform.Linux, "stable") == null
                && SelfUpdate.UpdateTo("nightly-2041-aaaaaaaa", R("v1.0.1"), Platform.Linux) == null
                && SelfUpdate.UpdateTo("v1.0.0", R("v1.0.1", new Dictionary<string, FileRef> { ["windows-x64"] = both["windows-x64"] }), Platform.Linux) == null
                && SelfUpdate.UpdateTo("v1.0.0", null, Platform.Linux) == null && SelfUpdate.UpdateTo("v1.1.0-rc.2", R("v1.1.0"), Platform.Linux) == null,
                "Never an older or the same launcher on its channel, never a build from a checkout, never one for the other system.");
        Require(SelfUpdate.UpdateTo("v1.0.0", R("nightly-2042-bbbbbbbb"), Platform.Linux, "nightly") == "nightly-2042-bbbbbbbb"
                && SelfUpdate.UpdateTo("nightly-2041-aaaaaaaa", R("v1.0.0"), Platform.Linux, "stable") == "v1.0.0",
                "The channel the player chose: its newest, across the nightly and the stables.");
        Require(SelfUpdate.ChannelOf("v1.2.0-rc.1", null) == "stable" && SelfUpdate.ChannelOf("dev", null) == "stable"
                && SelfUpdate.ChannelOf("v1.0.0", "nightly") == "nightly" && SelfUpdate.ChannelOf("v1.0.0", "bogus") == "stable"
                && SelfUpdate.ChannelOf("v1.0.0", "rc") == "stable",
                "The launcher's channel: the one chosen, else its own track.");
        Require(SelfUpdate.OwnTag(typeof(Program).Assembly) == "dev", "A build with no LongshotTag is dev.");
        Require(SelfUpdate.CurrentFile(Platform.Linux, k => k == "APPIMAGE" ? "/apps/Longshot.AppImage" : null, "/tmp/.mount/LongshotLauncher") == "/apps/Longshot.AppImage"
                && SelfUpdate.CurrentFile(Platform.Linux, _ => null, "/x/LongshotLauncher") == null
                && SelfUpdate.CurrentFile(Platform.Windows, _ => null, @"C:\L\LongshotLauncher.exe") == @"C:\L\LongshotLauncher.exe"
                && SelfUpdate.CurrentFile(Platform.Windows, _ => null, @"C:\Users\a\Downloads\LongshotLauncher-v1.0.0-windows-x64-setup.exe")
                    == @"C:\Users\a\Downloads\LongshotLauncher-v1.0.0-windows-x64-setup.exe"
                && SelfUpdate.CurrentFile(Platform.Windows, _ => null, @"D:\Games\my launcher (2).EXE") == @"D:\Games\my launcher (2).EXE"
                && SelfUpdate.CurrentFile(Platform.Windows, _ => null, @"C:\dotnet\dotnet.exe") == null
                && SelfUpdate.CurrentFile(Platform.Windows, _ => null, @"C:\L\LongshotLauncher.dll") == null,
                "The launcher's own file: the AppImage, or its .exe whatever it is named (not dotnet.exe).");
        Require(SelfUpdate.StartPath(k => k == "LONGSHOT_LAUNCHER_EXE" ? "/h/.var/app/x/data/longshot-launcher" : k == "APPIMAGE" ? "/a" : null, "/app/bin/LongshotLauncher")
                    == "/h/.var/app/x/data/longshot-launcher"
                && SelfUpdate.StartPath(k => k == "APPIMAGE" ? "/apps/Longshot.AppImage" : null, "/tmp/.mount/LongshotLauncher") == "/apps/Longshot.AppImage"
                && SelfUpdate.StartPath(_ => null, @"C:\L\LongshotLauncher.exe") == @"C:\L\LongshotLauncher.exe",
                "The path the shortcuts start: a package's LONGSHOT_LAUNCHER_EXE, else the AppImage, else the program.");
        foreach (var path in new[] { "Longshot.AppImage", "./Longshot.AppImage", @"C:\Apps\Longshot.AppImage", @"\Apps\Longshot.AppImage", "" })
            Require(SelfUpdate.CurrentFile(Platform.Linux, k => k == "APPIMAGE" ? path : null, "/tmp/LongshotLauncher") == null,
                    "Linux AppImage validation uses Linux absolute paths on either test host: " + path);
        Require(SelfUpdate.Compare("v1.1.0", "v1.0.0") == 1 && SelfUpdate.Compare("v1.0.0", "v1.1.0") == -1
                && SelfUpdate.Compare("nightly-3-aaaaaaaa", "v1.0.0") == 0, "Newer, older, or no order (the banner's words).");
        var kinds = new Dictionary<string, FileRef> { ["windows-x64"] = both["windows-x64"], [ReleaseManifest.WindowsPortable] = both["windows-x64"] };
        Require(SelfUpdate.UpdateTo("v1.0.0", R("v1.0.1", kinds), Platform.Windows, null, ReleaseManifest.WindowsPortable) == "v1.0.1"
                && SelfUpdate.UpdateTo("v1.0.0", R("v1.0.1", both), Platform.Windows, null, ReleaseManifest.WindowsPortable) == null,
                "A portable launcher is offered an update only with a portable zip.");

        var root = Folder("self-update");
        var current = Path.Combine(root, "Longshot Launcher ゲーム.AppImage");
        File.WriteAllText(current, "old");
        var server = new FakeFetcher();
        var newBytes = Encoding.UTF8.GetBytes("new launcher");
        server.Files["https://x/new"] = newBytes;
        await ThrowsAsync<InvalidDataException>(() => SelfUpdate.DownloadAsync(server, new FileRef("https://x/new", new string('0', 64), newBytes.Length), current),
                                                "A launcher with another SHA-256 was kept.");
        await ThrowsAsync<InvalidDataException>(() => SelfUpdate.DownloadAsync(server, new FileRef("https://x/new", ShaOf(newBytes), 3), current),
                                                "A launcher larger than its release says was kept.");
        Require(!File.Exists(SelfUpdate.Staged(current)) && !File.Exists(SelfUpdate.Staged(current) + ".part"), "A refused download left a file.");
        var staged = await SelfUpdate.DownloadAsync(server, file("https://x/new", newBytes), current);
        Require(File.ReadAllBytes(staged).SequenceEqual(newBytes) && (OperatingSystem.IsWindows()
                || File.GetUnixFileMode(staged).HasFlag(UnixFileMode.UserExecute)), "The new launcher, checked and executable.");
        foreach (var platform in new[] { Platform.Linux, Platform.Windows })
        {
            File.WriteAllText(current, "old");
            File.WriteAllBytes(staged, newBytes);
            SelfUpdate.Swap(current, staged, platform);
            Require(File.ReadAllText(current) == "new launcher" && File.ReadAllText(SelfUpdate.Backup(current)) == "old" && !File.Exists(staged),
                    $"{platform}: the swap, the old one kept.");
            Require(SelfUpdate.Rollback(current, platform) && File.ReadAllText(current) == "old", $"{platform}: --rollback.");
            Require(!SelfUpdate.Rollback(current, platform), $"{platform}: nothing more to roll back to.");
        }
        // (#223: the new launcher still held a moment after its --self-check, by an antivirus: the move is tried again; Windows' errors
        // as File.Move throws them, so the test runs the same on Linux)
        IOException Held(uint hr) => new("held", unchecked((int)hr));
        var waited = new List<TimeSpan>();
        var tries = 0;
        SelfUpdate.WhileHeld(() => { if (++tries < 4) throw Held(0x80070020); }, waited.Add);
        Require(tries == 4 && waited.SequenceEqual(SelfUpdate.HeldWaits.Take(3)), "A file held a moment: moved once it is let go.");
        waited.Clear();
        tries = 0;
        IOException? still = null;
        try { SelfUpdate.WhileHeld(() => { tries++; throw Held(0x80070021); }, waited.Add); }
        catch (IOException e) { still = e; }
        Require(tries == SelfUpdate.HeldWaits.Length + 1 && waited.Count == SelfUpdate.HeldWaits.Length
                && waited.Aggregate(TimeSpan.Zero, (a, b) => a + b) is { TotalSeconds: >= 5 and <= 15 } && still?.HResult == unchecked((int)0x80070021),
                "A file held for good: tried for some seconds, then its own error.");
        tries = 0;
        Throws<IOException>(() => SelfUpdate.WhileHeld(() => { tries++; throw new IOException("disk full", unchecked((int)0x80070070)); }, waited.Add),
                            "Another failure was hidden.");
        Throws<UnauthorizedAccessException>(() => SelfUpdate.WhileHeld(() => { tries++; throw new UnauthorizedAccessException(); }, waited.Add),
                                            "A file the player may not change was hidden.");
        Require(tries == 2 && SelfUpdate.IsHeld(Held(0x80070005)) && !SelfUpdate.IsHeld(new IOException("x")), "Any other failure is thrown at once.");
        File.WriteAllText(current, "old");
        File.WriteAllBytes(staged, newBytes);
        SelfUpdate.Swap(current, staged, Platform.Windows, _ => throw new InvalidOperationException("waited for a file nothing holds"));
        Require(File.ReadAllText(current) == "new launcher" && !File.Exists(staged), "Windows: a swap with nothing held does not wait.");
        Require(SelfUpdate.Rollback(current, Platform.Windows), "...and rolls back.");
        File.WriteAllBytes(staged, newBytes);
        Require(SelfUpdate.Discard(staged) && !File.Exists(staged) && SelfUpdate.Discard(staged), "The staged launcher discarded (and nothing to discard).");
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            // (one that cannot be deleted yet is left for the next start's Recover: never an error that hides why the update stopped)
            File.WriteAllBytes(staged, newBytes);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { Require(!SelfUpdate.Discard(staged) && File.Exists(staged), "A staged launcher that cannot be deleted: left, not thrown."); }
            finally { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            File.Delete(staged);
        }
        if (!OperatingSystem.IsWindows())
        {
            var script = Path.Combine(root, "launcher.sh");
            File.WriteAllText(script, "#!/bin/sh\n[ \"$1\" = --self-check ] && echo 'LongshotLauncher v3.0.1' && exit 0\nexit 3\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
            Require(await SelfUpdate.SelfCheckAsync(script, "v3.0.1", TimeSpan.FromSeconds(30)), "A launcher that answers its version.");
            Require(!await SelfUpdate.SelfCheckAsync(script, "v3.0.2", TimeSpan.FromSeconds(30)), "A launcher that is another version.");
            File.WriteAllText(script, "#!/bin/sh\necho 'LongshotLauncher v3.0.1'\nexit 1\n");
            Require(!await SelfUpdate.SelfCheckAsync(script, "v3.0.1", TimeSpan.FromSeconds(30)), "A launcher that fails.");
            File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
            Require(!await SelfUpdate.SelfCheckAsync(script, "v3.0.1", TimeSpan.FromSeconds(1)), "A launcher that hangs.");
            Require(!await SelfUpdate.SelfCheckAsync(Path.Combine(root, "none"), "v3.0.1", TimeSpan.FromSeconds(5)), "No launcher at all.");
        }
    }

    // ---- Toolchains ----

    static byte[] Zip(params (string Name, string? Text, string? Link, int Mode)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text, link, mode) in entries)
            {
                var e = zip.CreateEntry(name);
                e.ExternalAttributes = (link != null ? 0xA000 | 0x1FF : 0x8000 | mode) << 16;
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(link ?? text);
            }
        return ms.ToArray();
    }


    static void ToolchainChecks()
    {
        var root = Folder("toolchains");
        // (AppImageLauncher's binfmt-bypass sets TARGET_APPIMAGE to this launcher: the new launcher's runtime would run this one instead)
        var env = new Dictionary<string, string?> { ["APPIMAGE"] = "/a", ["APPDIR"] = "/m", ["TARGET_APPIMAGE"] = "/a", ["HOME"] = "/h" };
        SelfUpdate.WithoutOurAppImage(env);
        Require(env.Count == 1 && env["HOME"] == "/h", "Another AppImage starts without this launcher's APPIMAGE, APPDIR and TARGET_APPIMAGE.");

        // (an older launcher's toolchains/: its make_game.py tools, a Windows builder with its tools.lock or a Linux AppImage, are no
        // version here, never built with; its longshot-builder is read (a base, a version folder's builder put back). Pruned like
        // any folder no build needs)
        var versionsRoot = Folder("toolchains-versions");
        var chains = new Toolchains(versionsRoot, Platform.Linux, root);
        foreach (var (tag, kind) in new[] { ("v3.3.0", "Setup"), ("v3.3.1", "Linux") })
        {
            Directory.CreateDirectory(Path.Combine(root, tag, "LongshotBuilder"));
            File.WriteAllText(Path.Combine(root, tag, Toolchains.LegacyRecord), $"{{\"format\":1,\"tag\":\"{tag}\",\"kind\":\"{kind}\",\"run\":\"LongshotBuilder/LongshotBuilder.exe\"}}");
        }
        Directory.CreateDirectory(Path.Combine(root, "v4.0.0"));
        File.WriteAllText(Path.Combine(root, "v4.0.0", "longshot-builder"), "builder");
        File.WriteAllText(Path.Combine(root, "v4.0.0", Toolchains.LegacyRecord),
                          "{\"format\":1,\"tag\":\"v4.0.0\",\"kind\":\"LongshotBuild\",\"run\":\"longshot-builder\",\"size\":7}");
        Require(chains.Legacy("v3.3.0") == null && chains.Legacy("v3.3.1") == null && !chains.IsInstalled("v4.0.0") && chains.Installed().Count == 0
                && chains.Legacy("v4.0.0")?.BuilderExe == Path.Combine(root, "v4.0.0", "longshot-builder") && chains.Fetcher()?.Tag == "v4.0.0"
                && chains.Bases().SequenceEqual(new[] { Path.Combine(root, "v4.0.0") }),
                "make_game.py's tools are no version here; an older launcher's longshot-builder is read, fetches and is a base, never installed.");
        Directory.CreateDirectory(Path.Combine(root, "v9.part"));
        InstalledVersion(versionsRoot, "v5.0.0");
        var removed = chains.Prune(new[] { "v3.3.1", "v4.0.0" });
        Require(chains.Legacy("v4.0.0") != null && removed.Contains("v3.3.0") && removed.Contains("v9.part")
                && Directory.Exists(Path.Combine(root, "v3.3.1")) && chains.Installed().SequenceEqual(new[] { "v5.0.0" }),
                "Prune keeps the older launcher's tools builds use (an old version's too, while a row names it), removes the rest, and never a version folder.");
    }

    // ---- Game folders: the game in bin/ behind the setup, or in the folder itself beside its renderer ----

    static void GameFolderChecks()
    {
        foreach (var platform in new[] { Platform.Windows, Platform.Linux })
        {
            var folder = Folder("game-folder-" + platform);
            var exe = Platforms.GameExe(platform);
            var inBin = Path.Combine(folder, "bin", exe);
            var atRoot = Path.Combine(folder, exe);
            var renderer = Path.Combine(folder, platform == Platform.Windows ? "longshot_vk.dll" : "longshot_vk.so");
            var other = platform == Platform.Windows ? Platform.Linux : Platform.Windows;
            File.WriteAllText(Path.Combine(folder, Platforms.GameExe(other)), "authored other-platform file");
            Require(!GameFolder.IsBuilt(folder, platform) && !GameFolder.IsGameFolder(folder, platform)
                    && GameFolder.ExePath(folder, platform) == inBin,
                    $"{platform}: another platform's executable does not make a built game.");

            var stamp = Path.Combine(folder, GameFolder.StampName);
            File.WriteAllText(stamp, "version=v3.0.0-rc.3\n");
            Require(GameFolder.IsGameFolder(folder, platform) && !GameFolder.IsBuilt(folder, platform),
                    $"{platform}: a stamp identifies a game folder, but cannot enable Play without its executable.");
            File.Delete(stamp);

            // (a packaged build: the setup in the folder, the game in bin/, no renderer in the folder)
            File.WriteAllText(atRoot, "authored setup");
            Require(!GameFolder.IsBuilt(folder, platform) && GameFolder.ExePath(folder, platform) == inBin,
                    $"{platform}: the setup alone cannot enable Play when bin's game is missing.");
            Directory.CreateDirectory(Path.GetDirectoryName(inBin)!);
            File.WriteAllText(inBin, "authored packaged game");
            Require(GameFolder.ExePath(folder, platform) == inBin && GameFolder.IsBuilt(folder, platform),
                    $"{platform}: a packaged build plays bin's game, not the setup.");
            var user = Path.Combine(folder, "separate user");
            var play = GamePlay.StartInfo(folder, platform, new Dictionary<string, string>(), user);
            Require(play.FileName == inBin && play.WorkingDirectory == folder,
                    $"{platform}: packaged Play starts bin's game from the game folder.");

            // (built again without the setup: the new game and its renderer in the folder, the older game still in bin/)
            File.WriteAllText(atRoot, "authored newer game");
            File.WriteAllText(renderer, "authored renderer beside the game");
            Require(GameFolder.ExePath(folder, platform) == atRoot && GameFolder.IsBuilt(folder, platform),
                    $"{platform}: the game the last build left in the folder is played, not bin's older one.");
            play = GamePlay.StartInfo(folder, platform, new Dictionary<string, string>(), user);
            Require(play.FileName == atRoot && play.WorkingDirectory == folder && !play.UseShellExecute,
                    $"{platform}: Play starts the folder's own game with the game folder as its working directory.");
            Require(play.ArgumentList.SequenceEqual(new[] { "--user", user }),
                    $"{platform}: the folder's own game gets the same save-folder arguments.");

            // (and packaged again: make_game.py takes the renderer out of the folder, and bin's game is the new one)
            File.Delete(renderer);
            Require(GameFolder.ExePath(folder, platform) == inBin && GameFolder.IsBuilt(folder, platform),
                    $"{platform}: once the renderer is gone from the folder, bin's game is played again.");
            File.Delete(inBin);
            Require(!GameFolder.IsBuilt(folder, platform) && GameFolder.ExePath(folder, platform) == inBin,
                    $"{platform}: a packaged build whose bin game disappears does not fall back to its setup.");

            // (no bin/ at all: the game and its renderer in the folder)
            File.WriteAllText(renderer, "authored renderer beside the game");
            Require(GameFolder.IsBuilt(folder, platform) && GameFolder.IsGameFolder(folder, platform)
                    && GameFolder.ExePath(folder, platform) == atRoot,
                    $"{platform}: a game in the folder itself, beside its renderer, is playable.");
            File.Delete(atRoot);
            Require(!GameFolder.IsBuilt(folder, platform) && GameFolder.ExePath(folder, platform) == inBin,
                    $"{platform}: no executable keeps the expected bin path and disables Play.");
        }
    }

    // ---- BuildRegistry ----

    static void Game(string folder, string? version, bool built = true, string? language = null)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        if (version != null) File.WriteAllText(Path.Combine(folder, "longshot_version.txt"), $"version={version}\nbuilder=/authored/builder\n");
        if (built) File.WriteAllText(GameFolder.ExePath(folder, Platforms.Current), "authored");
        if (language != null) File.WriteAllText(Path.Combine(folder, "language.txt"), language + "\n");
    }

    static void RegistryChecks()
    {
        var home = Folder("registry");
        var path = Path.Combine(home, "builds.json");
        var clock = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var r = BuildRegistry.Load(path, () => clock);
        Require(!r.Existed && r.Builds.Count == 0 && r.Default == null, "No builds.json: an empty list, and a first start.");
        var a = r.Add(Path.Combine(home, "a"), "v3.3.0", null);
        var b = r.Add(Path.Combine(home, "b"), "v3.3.1-rc.1", "rc");   // (an old rc row: rc is no channel now)
        var c = r.Add(Path.Combine(home, "c"), "nightly-2031-abcdef01", null, name: "Nightly");
        Require(r.Default == a && r.Builds.Count(x => x.Default) == 1, "The first build is the default.");
        Require(a.Channel == "stable" && b.Channel == "stable" && c.Channel == "nightly" && c.Name == "Nightly" && a.Name == "a",
                "A channel defaults to the version's track, as one that is no channel does.");
        Require(r.Add(Path.Combine(home, "a") + Path.DirectorySeparatorChar, "v3.3.2", null) == a && r.Builds.Count == 3 && a.Version == "v3.3.2",
                "One entry per folder.");
        r.SetDefault(c.Id);
        Require(r.Default == c && r.Builds.Count(x => x.Default) == 1, "Set default.");
        Require(r.Remove(c.Id) && r.Default == a && r.Builds.Count == 2, "The default removed: the first one left is it.");
        Require(!r.Remove("nope"), "Removing what is not there.");
        Throws<KeyNotFoundException>(() => r.SetDefault("nope"), "A default that is no build.");
        r.SetChannel(b.Id, "nightly");
        r.Save();
        Require(!Directory.EnumerateFiles(home).Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)), "The atomic write left its temporary file.");
        // (two writers at once, as two launchers would: each its own temporary file, the file always whole, nothing left over)
        Parallel.For(0, 16, i => AtomicFile.Write(Path.Combine(home, "both.json"), new string((char)('a' + i), 4096)));
        var both = File.ReadAllText(Path.Combine(home, "both.json"));
        Require(both.Length == 4096 && both.All(ch => ch == both[0]) && !Directory.EnumerateFiles(home).Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)),
                "Two writers at once: one whole file, no temporary file left.");
        var again = BuildRegistry.Load(path);
        Require(again.Existed && again.Builds.Count == 2 && again.Default?.Id == a.Id && again.Find(b.Id)?.Channel == "nightly"
                && again.Find(a.Id)?.Created == clock, "builds.json round trip.");
        var text = File.ReadAllText(path);
        Require(text.Contains("\"format\": 1") && text.Contains("\"folder\"") && text.Contains("\"default\": true"), "builds.json's keys.");
        r.Remove(a.Id);
        r.Remove(b.Id);
        Require(r.Default == null && r.Builds.Count == 0, "An empty list has no default.");

        // (two marked defaults on disk: one is kept)
        File.WriteAllText(path, """{"format":1,"builds":[{"id":"x","folder":"/x","default":true,"channel":"weird"},{"id":"y","folder":"/y","default":true}]}""");
        var two = BuildRegistry.Load(path);
        Require(two.Builds.Count(x => x.Default) == 1 && two.Default!.Id == "x" && two.Find("x")!.Channel == "stable", "Exactly one default after load.");
        File.WriteAllText(path, "{ not json");
        var broken = BuildRegistry.Load(path);
        Require(broken.Problem != null && broken.Builds.Count == 0 && File.Exists(path + ".broken"), "A broken builds.json is kept aside.");

        // (deleting files: only a game folder)
        var game = Path.Combine(home, "game");
        Game(game, "v3.3.0");
        var other = Path.Combine(home, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep.txt"), "x");
        Throws<IOException>(() => BuildRegistry.DeleteFolder(other, Platforms.Current), "A folder that is no game was deleted.");
        Require(File.Exists(Path.Combine(other, "keep.txt")), "The other folder's file is gone.");
        Throws<IOException>(() => BuildRegistry.DeleteFolder(Path.GetPathRoot(home)!, Platforms.Current), "A drive's root.");
        BuildRegistry.DeleteFolder(game, Platforms.Current);
        Require(!Directory.Exists(game), "The game folder was not deleted.");
        var unbuilt = Path.Combine(home, "unbuilt");
        Game(unbuilt, "v3.3.0", built: false);
        Require(GameFolder.IsGameFolder(unbuilt, Platforms.Current) && !GameFolder.IsBuilt(unbuilt, Platforms.Current), "A stamped folder not built.");

        // (a build's folder deleted outside the launcher: its row goes; one whose drive or share is not there keeps it, as does
        // one the launcher is deleting itself, and one whose game is missing but whose folder is there)
        var lost = BuildRegistry.Load(Path.Combine(home, "lost.json"));
        var kept = lost.Add(unbuilt, "v3.3.0", null);
        var deleted = lost.Add(game, "v3.3.1", null, makeDefault: true);
        var unplugged = lost.Add(Path.Combine(home, "no-drive", "stable"), "v3.3.2", null);
        var busy = lost.Add(Path.Combine(home, "busy"), "v3.3.3", null);
        Require(BuildRegistry.Gone(game) && !BuildRegistry.Gone(unbuilt) && !BuildRegistry.Gone(unplugged.Folder),
                "Gone: the folder is not there while the one that held it is.");
        var forgot = lost.ForgetGone(new[] { busy.Id });
        Require(forgot.SequenceEqual(new[] { deleted }) && lost.Builds.SequenceEqual(new[] { kept, unplugged, busy }) && lost.Default == kept,
                "A folder deleted by hand leaves the list (the default passes on); an unplugged drive's and one being deleted stay.");
        Require(lost.ForgetGone().SequenceEqual(new[] { busy }) && lost.ForgetGone().Count == 0, "Nothing more to forget.");

        // (issue 140: a portable launcher's folder renamed, or one above it: its builds, in its folder, follow it; one elsewhere,
        // and one whose old path is still there (a copy of the folder), keep theirs. A list saved before Home: its home's name)
        var games = Folder("portable");
        var data = Path.Combine(games, "Zelda OOT", LauncherLayout.PortableData);
        var portable = BuildRegistry.Load(Path.Combine(data, "builds.json"));
        var inside = Path.Combine(data, "builds", "stable");
        Game(inside, "v3.3.0");
        var gameFile = Path.Combine(games, "Zelda OOT", "game.nup");
        File.WriteAllText(gameFile, "authored");
        var outside = Path.Combine(home, "unbuilt");
        portable.Add(inside, "v3.3.0", null, makeDefault: true).Nup = gameFile;
        portable.Add(outside, "v3.3.0", null);
        portable.Save();
        Require(!BuildRegistry.Load(Path.Combine(data, "builds.json")).FollowHome(), "A launcher where it was: nothing moves.");
        var renamed = Path.Combine(games, "Zelda");
        Directory.Move(Path.Combine(games, "Zelda OOT"), renamed);
        var moved = BuildRegistry.Load(Path.Combine(renamed, LauncherLayout.PortableData, "builds.json"));
        var movedHome = Path.Combine(renamed, LauncherLayout.PortableData);
        Require(moved.FollowHome() && BuildRegistry.SameFolder(moved.Default!.Folder, Path.Combine(movedHome, "builds", "stable"))
                && BuildRegistry.SameFolder(moved.Default.Nup!, Path.Combine(renamed, "game.nup"))
                && moved.Builds.Any(x => BuildRegistry.SameFolder(x.Folder, outside)) && !moved.FollowHome(),
                "A renamed portable folder: its build and game file follow it, a build elsewhere stays.");
        File.WriteAllText(Path.Combine(movedHome, "builds.json"),
            "{\"format\":1,\"builds\":[{\"id\":\"o\",\"folder\":" + JsonSerializer.Serialize(inside) + ",\"default\":true}]}");
        var older = BuildRegistry.Load(Path.Combine(movedHome, "builds.json"));
        Require(older.FollowHome() && BuildRegistry.SameFolder(older.Default!.Folder, Path.Combine(movedHome, "builds", "stable")),
                "A list saved before Home: the old home is found by its name.");
        Game(inside, "v3.3.0");
        var copy = BuildRegistry.Load(Path.Combine(movedHome, "builds.json"));
        Require(!copy.FollowHome() && BuildRegistry.SameFolder(copy.Default!.Folder, inside), "A copy whose first is still there keeps its paths.");
    }

    static void SettingsChecks(string repo)
    {
        StepTwoFolderSourceChecks(repo);
        HomeLayoutSourceChecks(repo);
        RelistSourceChecks(repo);
        BuilderUiThreadSourceChecks(repo);
        var path = Path.Combine(Folder("settings"), "settings.json");
        var d = LauncherSettings.Load(path);
        Require(!d.Dlss && d.DefaultChannel == null && d.MemoryGb == null, "The defaults.");
        Require(!d.Nx2naviSavesAsked && !d.HookshotSavesAsked, "Save import offers default to unanswered.");
        var s = new LauncherSettings
        {
            Nup = "/n.nup", Language = "de", Dlss = true, DlssLicense = true, Fsr = true, FsrLicense = true,
            Xess = true, XessLicense = true, MemoryGb = 16, BuildWorkers = 6, Work = "/w", DefaultChannel = "nightly",
        };
        s.Save(path);
        var t = LauncherSettings.Load(path);
        Require(t.Nup == "/n.nup" && t.Language == "de" && t.Dlss && t.DlssLicense && t.Fsr && t.FsrLicense && t.Xess && t.XessLicense
                && t.MemoryGb == 16 && t.BuildWorkers == 6 && t.Work == "/w" && t.DefaultChannel == "nightly", "settings.json round trip.");
        new LauncherSettings { Nx2naviSavesAsked = true }.Save(path);
        Require(LauncherSettings.Load(path) is { Nx2naviSavesAsked: true, HookshotSavesAsked: false }, "NX2navi's import answer is saved independently of Hookshot's.");
        new LauncherSettings { HookshotSavesAsked = true }.Save(path);
        Require(LauncherSettings.Load(path) is { Nx2naviSavesAsked: false, HookshotSavesAsked: true }, "Hookshot's import answer leaves NX2navi's offer unanswered.");
        // (AMD FSR 4: the Linux notice keeps the switch on without AMD's licence, and is not that licence)
        new LauncherSettings { Fsr = true, FsrLinuxNotice = true }.Save(path);
        var n = LauncherSettings.Load(path);
        Require(n.Fsr && n.FsrLinuxNotice && !n.FsrLicense, "FSR 4 with the Linux notice read: on, and AMD's licence still not accepted.");
        new LauncherSettings { Fsr = true }.Save(path);
        Require(!LauncherSettings.Load(path).Fsr, "FSR 4 with neither AMD's licence nor the Linux notice: off.");
        new LauncherSettings { Xess = true }.Save(path);
        Require(!LauncherSettings.Load(path).Xess, "Intel XeSS without Intel's licence: off.");
        // (Build workers' Every thread is 0, kept; a number below it is no setting: Automatic)
        new LauncherSettings { BuildWorkers = LauncherSettings.EveryThread }.Save(path);
        Require(LauncherSettings.Load(path).BuildWorkers == LauncherSettings.EveryThread, "Build workers' Every thread is kept.");
        new LauncherSettings { BuildWorkers = -3 }.Save(path);
        Require(LauncherSettings.Load(path).BuildWorkers == null, "Build workers below zero: Automatic.");
        File.WriteAllText(path, """{"dlss":true,"dlssLicense":false,"defaultChannel":"beta","memoryGb":0}""");
        var u = LauncherSettings.Load(path);
        Require(!u.Dlss && u.DefaultChannel == null && u.MemoryGb == null, "Saved values outside their range are not taken.");
        // (the "Prepare shaders now" switch is gone, every build prepares them: a settings.json from before still loads)
        File.WriteAllText(path, """{"prepareShaders":false,"language":"fr"}""");
        Require(LauncherSettings.Load(path) is { Language: "fr", Problem: null } && !File.Exists(path + ".broken"),
                "A settings.json with the old prepareShaders is read, the field ignored, and not set aside.");
        // (Intel shader compiles: Auto by default; a launcher from before Auto saved Side by side as intelSideBySide true, and its
        // One at a time, which set nothing, as false: Side by side and Auto. The old field is not written again)
        Require(d.IntelCompiles == GamePlay.IntelCompilesAuto, "Intel shader compiles defaults to Auto.");
        File.WriteAllText(path, """{"intelSideBySide":true}""");
        Require(LauncherSettings.Load(path).IntelCompiles == GamePlay.IntelCompilesSideBySide, "An old Side by side is still Side by side.");
        File.WriteAllText(path, """{"intelSideBySide":false}""");
        Require(LauncherSettings.Load(path).IntelCompiles == GamePlay.IntelCompilesAuto, "An old One at a time, which set nothing, is Auto.");
        File.WriteAllText(path, """{"intelCompiles":9}""");
        Require(LauncherSettings.Load(path).IntelCompiles == GamePlay.IntelCompilesAuto, "An Intel shader compiles value outside the three: Auto.");
        new LauncherSettings { IntelCompiles = GamePlay.IntelCompilesOneAtATime }.Save(path);
        Require(LauncherSettings.Load(path).IntelCompiles == GamePlay.IntelCompilesOneAtATime && !File.ReadAllText(path).Contains("intelSideBySide"),
                "One at a time round trips, and the old intelSideBySide is not written.");
        File.WriteAllText(path, "nope");
        Require(LauncherSettings.Load(path) is { CheckUpdates: true, Language: null }, "Unreadable settings are the defaults.");
        // (a settings.json that cannot be read is kept beside it as settings.json.broken, as a broken builds.json is: the next save
        // writes the defaults over it, and the builds folder, the build files' folder and the kept folders were lost with it)
        var unread = Encoding.UTF8.GetBytes("{\"buildsFolder\": \"D:\\\\Games\\\\Longshot\", \"work\": \"E:\\\\work\", \"keptFolders\": [\"D:\\\\old");
        File.WriteAllBytes(path, unread);
        var fresh = LauncherSettings.Load(path);
        Require(fresh is { CheckUpdates: true, BuildsFolder: null, Work: null } && fresh.KeptFolders.Count == 0, "A cut-off settings.json loads as the defaults.");
        Require(fresh.Problem is { Length: > 0 }, "A cut-off settings.json says why it was not read, for the home's notice.");
        Require(File.Exists(path + ".broken") && File.ReadAllBytes(path + ".broken").SequenceEqual(unread),
                "An unreadable settings.json is kept as settings.json.broken, byte for byte.");
        fresh.Save(path);
        Require(File.ReadAllBytes(path + ".broken").SequenceEqual(unread) && LauncherSettings.Load(path) is { BuildsFolder: null, Problem: null },
                "The save after it writes the defaults and leaves the kept copy as it was.");
        Require(!File.ReadAllText(path).Contains("problem", StringComparison.OrdinalIgnoreCase), "Problem is not saved.");
        File.WriteAllText(path, "null");
        Require(LauncherSettings.Load(path).Problem != null, "A settings.json of null is unreadable too, as an empty builds.json is.");
        var absent = Path.Combine(Folder("settings-absent"), "settings.json");
        Require(LauncherSettings.Load(absent).Problem == null && !File.Exists(absent + ".broken"), "No settings.json yet is the first start, not a broken one.");
        var homeSource = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "HomeViewModel.cs"));
        Require(homeSource.Contains("if (Service.Settings.Problem is { } unreadable) notes.Add(Tr.F(\"The launcher's settings could not be read (kept as settings.json.broken): {0}\", unreadable));"),
                "The home says the settings were set aside, beside the list of builds' notice.");

        // (the channel new builds start on: the launcher's own until the player picks one)
        var none = new LauncherSettings();
        foreach (var (own, track) in new[] { ("v1.2.0", "stable"), ("v1.3.0-rc.1", "stable"), ("nightly-8-3b243d73", "nightly"), ("dev", "stable") })
            Require(none.BuildChannel(own) == track, $"A launcher {own} does not start new builds on {track}.");
        Require(new LauncherSettings { LauncherChannel = "nightly" }.BuildChannel("v1.2.0") == "nightly",
                "New builds do not follow the launcher's chosen channel.");
        new LauncherSettings { DefaultChannel = "stable" }.Save(path);
        var picked = LauncherSettings.Load(path);
        Require(picked.DefaultChannel == "stable" && picked.BuildChannel("nightly-8-3b243d73") == "stable"
                && picked.Format == LauncherSettings.CurrentFormat, "A channel the player picked does not stick.");
        new LauncherSettings().Save(path);
        Require(File.ReadAllText(path).Contains("\"defaultChannel\": null") && LauncherSettings.Load(path).BuildChannel("nightly-8-3b243d73") == "nightly",
                "No pick is saved as none.");
        // (format 1 wrote its own default, stable, for everyone: read once as no pick; a pick of another channel stays)
        File.WriteAllText(path, """{"format":1,"defaultChannel":"stable","launcherChannel":null,"checkUpdates":true}""");
        var old = LauncherSettings.Load(path);
        Require(old.DefaultChannel == null && old.Format == LauncherSettings.CurrentFormat && old.BuildChannel("nightly-8-3b243d73") == "nightly",
                "An old settings.json's stable is not read as no pick.");
        old.Save(path);
        Require(LauncherSettings.Load(path).BuildChannel("nightly-8-3b243d73") == "nightly", "The migrated settings.json changes on its next load.");
        File.WriteAllText(path, """{"format":1,"defaultChannel":"rc","launcherChannel":"rc"}""");
        var rcPick = LauncherSettings.Load(path);
        Require(rcPick.DefaultChannel == null && rcPick.LauncherChannel == null && rcPick.BuildChannel("v1.2.0") == "stable",
                "An old settings.json's pick of rc, a channel no more, is read as no pick.");
    }

    // ---- longshot-builder's events and the overall bar ----

    /// An event line as longshot-builder writes it (--output json): its type and fields
    static string Event(string type, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["v"] = 1, ["type"] = type };
        foreach (var (k, v) in fields) o[k] = v is null ? null : JsonValue.Create(v);
        return o.ToJsonString();
    }

    static void Feed(BuildProgress p, string line)
    {
        if (LongshotBuilder.Read(line) is { } read) p.Apply(read);
    }

    static string StageEvent(string id) => Event("stage", ("stage", id), ("text", id));
    static string ProgressEvent(string id, double fraction) => Event("progress", ("stage", id), ("fraction", fraction), ("overall", 0.0));

    static void ProgressChecks()
    {
        var transcript = new[]
        {
            StageEvent("read"), ProgressEvent("read", 0.5), ProgressEvent("read", 1.0), StageEvent("lift"),
            ProgressEvent("lift", 0.3), ProgressEvent("lift", 0.2), ProgressEvent("lift", 1), StageEvent("compile"),
            Event("compile_plan", ("changed", 3), ("total", 10)), Event("eta", ("seconds", 120)),
            Event("unit", ("name", "a.c"), ("seconds", 1.0), ("result", "compiled")), ProgressEvent("compile", 0.4),
            Event("unit", ("name", "b.c"), ("seconds", 1.0), ("result", "cached")), Event("unit", ("name", "c.c"), ("seconds", 2.0), ("result", "retried")),
            ProgressEvent("compile", 0.9), StageEvent("lift"), StageEvent("compile"), ProgressEvent("compile", 1),
            StageEvent("finish"), StageEvent("shaders"), ProgressEvent("shaders", 0.5),
            Event("done", ("folder", "/authored/game"), ("seconds", 600.0), ("compile_seconds", 400.0)),
        };
        var p = new BuildProgress(download: true, shaders: true);
        Require(p.Stages.Select(s => s.Id).SequenceEqual(new[] { "download", "read", "lift", "compile", "finish", "shaders" }), "The stages of a first build.");
        var bars = new List<double> { p.Overall };
        for (var i = 0; i <= 10; i++)
        {
            p.Download(i / 10.0);
            bars.Add(p.Overall);
        }
        Require(Math.Abs(p.Overall - 10.0 / 825) < 1e-9, "The tools' download fills its weight: its forecast 10 s of the 825.");
        foreach (var line in transcript)
        {
            Feed(p, line);
            bars.Add(p.Overall);
            if (line.Contains("\"eta\"")) Require(p.Eta == 120 && p.EtaLines == 1, "The ETA passes through, counted.");
            if (line.Contains("compile_plan")) Require(p.Stages.First(s => s.Id == "compile").Kept, "A compile of 3 files of 10 kept the rest.");
            if (line.Contains("\"c.c\"")) Require(p.Stages.First(s => s.Id == "compile").Note == "2 of 3 changed files", "The compile's note.");
        }
        Require(bars.Zip(bars.Skip(1)).All(x => x.Second >= x.First), "The bar went back: " + string.Join(" ", bars.Select(b => b.ToString("0.000"))));
        Require(bars[^1] == 1 && p.Finished && p.Folder == "/authored/game" && p.Stages.All(s => s.State == StageState.Done), "The bar ends full.");
        Require(Math.Abs(bars[bars.Count - 2] - (825 - 311 / 2.0) / 825) < 1e-9, "The shaders' half: their forecast 311 s of the 825, halfway.");
        Require(p.Stages.Where(s => s.Id != "compile").All(s => !s.Kept), "Nothing else said it kept an earlier build's work.");
        // (a progress event of another stage than the one running moves nothing)
        var other = new BuildProgress(download: false, shaders: true);
        Feed(other, StageEvent("read"));
        Feed(other, ProgressEvent("lift", 0.9));
        Require(other.Overall == 0, "Another stage's progress moves nothing.");

        var q = new BuildProgress(download: false, shaders: true);
        Feed(q, StageEvent("read"));
        Require(q.Overall == 0 && q.Current == "read", "With no download, the read starts at 0.");
        Feed(q, StageEvent("compile"));
        Require(Math.Abs(q.Overall - (76 + 57) / 815.0) < 1e-9, "The weights scale to the stages a build has.");
        Feed(q, Event("warning", ("code", BuildEvents.ShadersNotPrepared), ("text", "The shaders were not prepared now (\"Prepare shaders now\" is unticked): the game prepares them at its first start.")));
        Feed(q, Event("done", ("folder", "/g")));
        var shaders = q.Stages.First(s => s.Id == "shaders");
        Require(q.Overall == 1 && shaders.State == StageState.Skipped && shaders.Note!.Contains("unticked"), "Shaders left to the first start.");
        var r = new BuildProgress(download: false, shaders: true);
        Feed(r, StageEvent("finish"));
        Feed(r, Event("done", ("folder", "/g")));
        Require(r.Stages.First(s => s.Id == "shaders").State == StageState.Skipped, "No shaders stage is no check mark.");
        // (the bar's shares are the forecast at the build's threads: fewer threads, more of it for the compile and the shaders)
        var slow = new BuildProgress(download: false, shaders: true, threads: 8);
        var fast = new BuildProgress(download: false, shaders: true, threads: 24);
        double Share(BuildProgress b, string id) => b.Stages.First(s => s.Id == id).Weight;
        Require(Math.Abs(slow.Stages.Sum(s => s.Weight) - 1) < 1e-9 && Share(slow, "compile") > Share(fast, "compile") + 0.03
                && Share(slow, "read") < Share(fast, "read") && Math.Abs(Share(slow, "compile") / Share(slow, "read") - BuildForecast.Seconds("compile", 8) / 76) < 1e-9,
                "The bar's shares follow this PC's forecast.");
        var kept = new BuildProgress(download: false, shaders: false);
        Feed(kept, StageEvent("compile"));
        Feed(kept, Event("compile_plan", ("changed", 10), ("total", 10)));
        Require(!kept.Stages.First(s => s.Id == "compile").Kept, "A compile of every file kept nothing.");
        var e = new BuildProgress(download: false, shaders: false, update: true);
        Feed(e, StageEvent("read"));
        Feed(e, Event("error", ("code", "disk_full"), ("text", "No space left")));
        Require(e.Error == "No space left" && e.ErrorCode == "disk_full" && !e.Finished && e.Overall < 1 && e.Stages[0].Title.Contains("kept") && e.Stages[0].Kept,
                "A failed update.");
    }

    // ---- the game's command line and environment at Play ----

    static void CommandChecks()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin", ["LONGSHOT_TOOLS"] = "/t", ["PYTHONHOME"] = "/p", ["APPDIR"] = "/a", ["LD_LIBRARY_PATH"] = "/l", ["HOME"] = "/h",
        };
        var game = GamePlay.Environment(Platform.Linux, env);
        Require(game.Keys.OrderBy(k => k).SequenceEqual(new[] { "HOME", "LONGSHOT_NO_UPDATE_CHECK", "PATH" }) && game["LONGSHOT_NO_UPDATE_CHECK"] == "1",
                "Linux Play drops the AppImage's and the tools' environment.");
        Require(GamePlay.Environment(Platform.Windows, env).ContainsKey("LONGSHOT_TOOLS"), "Windows Play keeps the environment, as the Builder's.");
        // (Auto must set nothing: while RX_VK_NO_INTEL_SERIAL is set the game shows its own Intel shader compiles row fixed)
        Require(GamePlay.Environment(Platform.Windows, env, intelCompiles: GamePlay.IntelCompilesSideBySide)["RX_VK_NO_INTEL_SERIAL"] == "1"
                && GamePlay.Environment(Platform.Windows, env, intelCompiles: GamePlay.IntelCompilesOneAtATime)["RX_VK_NO_INTEL_SERIAL"] == "0"
                && !GamePlay.Environment(Platform.Windows, env, intelCompiles: GamePlay.IntelCompilesAuto).ContainsKey("RX_VK_NO_INTEL_SERIAL")
                && !GamePlay.Environment(Platform.Windows, env).ContainsKey("RX_VK_NO_INTEL_SERIAL")
                && !GamePlay.Environment(Platform.Linux, env, intelCompiles: GamePlay.IntelCompilesSideBySide).ContainsKey("RX_VK_NO_INTEL_SERIAL"),
                "Play with Intel shader compiles: RX_VK_NO_INTEL_SERIAL 1 or 0 on Windows for Side by side or One at a time, nothing on Auto.");
        var playLonger = GamePlay.Environment(Platform.Windows, env, intelLongerLimits: true);
        Require(GamePlay.IntelLongerLimits.All(v => playLonger[v.Name] == v.Value) && !playLonger.ContainsKey("RX_VK_NO_INTEL_SERIAL")
                && GamePlay.IntelLongerLimits.All(v => !GamePlay.Environment(Platform.Windows, env).ContainsKey(v.Name)
                                                       && !GamePlay.Environment(Platform.Linux, env, intelLongerLimits: true).ContainsKey(v.Name)),
                "Play with Shader time limit on Longer: the three longer waits on Windows only.");
        Require(SystemInfo.IntelCompilesApply(Platform.Windows, new[] { new Gpu(SystemInfo.Intel, "Intel(R) UHD Graphics") })
                && SystemInfo.IntelCompilesApply(Platform.Windows, new[] { new Gpu(SystemInfo.Nvidia, "RTX 4060 Laptop"), new Gpu(SystemInfo.Intel) })
                && !SystemInfo.IntelCompilesApply(Platform.Windows, new[] { new Gpu(SystemInfo.Amd, "AMD Radeon RX 9070 XT") })
                && !SystemInfo.IntelCompilesApply(Platform.Windows, Array.Empty<Gpu>())
                && !SystemInfo.IntelCompilesApply(Platform.Linux, new[] { new Gpu(SystemInfo.Intel) }),
                "Intel shader compiles is offered on Windows with Intel graphics only.");
        var play = GamePlay.StartInfo("/g", Platform.Linux, env);
        Require(play.FileName == Path.Combine("/g", "bin", "longshot") && play.WorkingDirectory == "/g" && !play.Environment.ContainsKey("APPDIR")
                && play.ArgumentList.Count == 0, "Play starts bin/longshot in its folder.");
        Require(GamePlay.StartInfo("/g", Platform.Linux, env, user: "/s").ArgumentList.SequenceEqual(new[] { "--user", "/s" })
                && GamePlay.StartInfo("/g", Platform.Windows, env, user: @"C:\Users\Zoë\AppData\Roaming\Longshot").ArgumentList
                    .SequenceEqual(new[] { "--user", @"C:\Users\Zoë\AppData\Roaming\Longshot" })
                && GamePlay.StartInfo("/g", Platform.Windows, env).ArgumentList.Count == 0
                && GamePlay.StartInfo("/g", Platform.Linux, env).ArgumentList.Count == 0,
                "Play gives the game the user folder it is given (--user), on Linux too; none, no argument.");
        // (a settings.json of before, with moveSaves: still read)
        var old = Path.Combine(Folder("settings-old"), "settings.json");
        File.WriteAllText(old, "{\"format\":2,\"moveSaves\":false,\"language\":\"fr\"}");
        Require(LauncherSettings.Load(old).Language == "fr", "A settings.json from before shared saves is still read.");
    }

    static void UpdatePolicyChecks()
    {
        Require(UpdatePolicy.UpdateTo("v3.3.0", "stable", "v3.3.1") == "v3.3.1", "release: the newer release.");
        Require(UpdatePolicy.UpdateTo("v3.3.1", "stable", "v3.3.1") == null, "release: up to date.");
        Require(UpdatePolicy.UpdateTo("v3.4.0", "stable", "v3.3.1") == null, "release: a release newer than the channel's is not taken back.");
        Require(UpdatePolicy.UpdateTo("v3.4.0-rc.1", "stable", "v3.3.1") == "v3.3.1",
                "An old release candidate is on no track: its channel's newest, as dev is.");
        Require(UpdatePolicy.UpdateTo("nightly-2029-aaaaaaaa", "nightly", "nightly-2031-bbbbbbbb") == "nightly-2031-bbbbbbbb", "nightly: a later one.");
        Require(UpdatePolicy.UpdateTo("nightly-2031-bbbbbbbb", "nightly", "nightly-2029-aaaaaaaa") == null, "nightly: not an older one.");
        Require(UpdatePolicy.UpdateTo("nightly-2029-aaaaaaaa-dirty", "nightly", "nightly-2029-aaaaaaaa") == null,
                "nightly: a changed build of the nightly's own commit is not taken back to it.");
        Require(UpdatePolicy.UpdateTo("v3.4.0-rc.1-3-gaaaaaaa", "nightly", "nightly-2029-aaaaaaaa") == "nightly-2029-aaaaaaaa",
                "nightly: one named the old way (git describe) takes the first new one.");
        Require(UpdatePolicy.UpdateTo("v3.5.0", "nightly", "nightly-2029-aaaaaaaa") == null
                && UpdatePolicy.UpdateTo("nightly-2029-aaaaaaaa", "stable", "v3.5.0") == null,
                "A nightly and a release are not ordered: neither is offered for the other.");
        Require(UpdatePolicy.UpdateTo(null, "stable", "v3.3.1") == null && UpdatePolicy.UpdateTo("v3.3.0", "stable", null) == null,
                "No version on either side: no offer.");
        Require(UpdatePolicy.UpdateTo("dev", "stable", "v3.3.1") == "v3.3.1" && UpdatePolicy.UpdateTo("1a2b3c4d", "nightly", "nightly-2029-aaaaaaaa")
                == "nightly-2029-aaaaaaaa", "A version on no track (a hash, dev): its channel's newest (docs/versioning.md).");
        Require(UpdatePolicy.ChannelOf(null, "v3.4.0-rc.1") == "stable" && UpdatePolicy.ChannelOf("rc", "v3.4.0") == "stable"
                && UpdatePolicy.ChannelOf("nightly", "v3.4.0") == "nightly", "A build's channel (rc is none now: its version's track).");
        var b = new BuildEntry { Version = "v3.4.0", Channel = "nightly" };
        Require(UpdatePolicy.UpdateTo(b, new Dictionary<string, string?> { ["stable"] = "v3.5.0", ["nightly"] = "nightly-2031-bbbbbbbb" }) == null
                && UpdatePolicy.UpdateTo(b, new Dictionary<string, string?> { ["stable"] = "v3.5.0" }) == null, "Each build asks its own channel.");
        var oldRc = new BuildEntry { Version = "v3.4.0-rc.1", Channel = "rc" };
        Require(UpdatePolicy.UpdateTo(oldRc, new Dictionary<string, string?> { ["stable"] = "v3.5.0", ["nightly"] = "nightly-2031-bbbbbbbb" }) == "v3.5.0",
                "A build left on the old rc channel is offered the stable.");
    }

    /// Why git.magicbean.dev was not reached, in words a player can act on, and .NET's own in brackets
    static void NetFailureChecks()
    {
        const string host = "git.magicbean.dev";
        string Why(System.Net.Http.HttpRequestError error, Exception? inner = null) =>
            NetFailure.Why(new HttpRequestException(error, "outer words", inner), host);
        var dns = Why(System.Net.Http.HttpRequestError.NameResolutionError, new System.Net.Sockets.SocketException(11001));
        Require(dns.Contains("could not look up git.magicbean.dev's address") && dns.EndsWith(")", StringComparison.Ordinal) && !dns.Contains("outer words"),
                "No address: says so, with the innermost words. (said: " + dns + ")");
        Require(Why(System.Net.Http.HttpRequestError.ConnectionError, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))
                    .Contains("refused the connection"), "A refused connection");
        Require(Why(System.Net.Http.HttpRequestError.ConnectionError, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut))
                    .Contains("did not answer in time"), "A connection that timed out");
        Require(Why(System.Net.Http.HttpRequestError.ConnectionError, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable))
                    .Contains("no route"), "No network");
        Require(Why(System.Net.Http.HttpRequestError.SecureConnectionError, new System.Security.Authentication.AuthenticationException("bad certificate"))
                    .Contains("date and time") && Why(System.Net.Http.HttpRequestError.SecureConnectionError, new System.Security.Authentication.AuthenticationException("bad certificate")).Contains("(bad certificate)"),
                "A failed secure connection: check the clock; the certificate's words");
        Require(Why(System.Net.Http.HttpRequestError.Unknown, new System.Security.Authentication.AuthenticationException("x")).Contains("date and time"),
                "An unknown error over a failed TLS handshake is still the secure connection.");
        Require(Why(System.Net.Http.HttpRequestError.ProxyTunnelError).Contains("proxy") && Why(System.Net.Http.HttpRequestError.ProxyTunnelError).EndsWith("(outer words)", StringComparison.Ordinal),
                "A proxy that did not connect, with the only words there are");
        Require(Why(System.Net.Http.HttpRequestError.Unknown).Contains("Could not connect to git.magicbean.dev"), "Anything else: no connection, and what to check");
        var proxied = NetFailure.Why(new HttpRequestException(System.Net.Http.HttpRequestError.ConnectionError, "x",
                                         new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)), host, new Uri("http://proxy.lan:3128"));
        Require(proxied.Contains("the proxy proxy.lan:3128, which did not answer") && !proxied.Contains("refused the connection"),
                "Through a proxy, a refused connection is the proxy's, not the server's. (said: " + proxied + ")");
        Require(NetFailure.Status(503, host).Contains("having trouble (HTTP 503 (ServiceUnavailable))") && NetFailure.Status(429, host).Contains("too many requests")
                && NetFailure.Status(403, host).Contains("refused the request") && NetFailure.Status(418, host) == "git.magicbean.dev answered HTTP 418.",
                "An HTTP error, by what it means: " + NetFailure.Status(418, host));
        Require(NetFailure.TimedOut(host).StartsWith("git.magicbean.dev did not answer in time.", StringComparison.Ordinal), "A timeout");
    }

    static void CommandLineChecks()
    {
        StartRequest P(params string[] a) => StartRequest.Parse(a);
        Require(P() is { Kind: StartKind.Window, Folder: null, Problem: null }, "No arguments: the window.");
        Require(P("--launch", "/g") is { Kind: StartKind.Launch, Folder: "/g" }, "--launch");
        Require(P("--play") is { Kind: StartKind.Play, Folder: null }, "--play: the default build");
        Require(P("--play", "/g") is { Kind: StartKind.Play, Folder: "/g" }, "--play <folder>");
        Require(P("--launch") is { Kind: StartKind.Window, Problem: not null }, "--launch with no folder");
        Require(P("--play", "/g", "--launch", "/h") is { Problem: not null }, "Two at once");
        Require(P("--update", "/g") is { Kind: StartKind.Window, Folder: null, Problem: null } u && u.Ignored.SequenceEqual(new[] { "--update", "/g" }),
                "--update is no start mode: an unknown argument, the window opens.");
        Require(P("--some-avalonia-flag", "--launch", "/g").Ignored.SequenceEqual(new[] { "--some-avalonia-flag" }), "Unknown arguments are kept aside.");
    }

    // ---- a folder named outside any one code page ----

    static void Utf8Checks()
    {
        Require(scratch.Contains("ゲーム Zélda 🎮"), "The scratch folder is not the UTF-8 one: " + scratch);
        var home = new LauncherHome(Path.Combine(scratch, "home ゲーム"));
        var game = Path.Combine(scratch, "ゲーム Zélda 🎮 game");
        Game(game, "v3.3.0", language: "ja");
        Require(GameFolder.Version(game) == "v3.3.0" && GameFolder.Language(game) == "ja" && GameFolder.IsBuilt(game, Platforms.Current), "The stamp read.");
        var r = BuildRegistry.Load(home.Builds);
        r.Add(game, GameFolder.Version(game), null, name: "Zélda 🎮");
        r.Save();
        var back = BuildRegistry.Load(home.Builds);
        Require(back.Default is { Name: "Zélda 🎮" } d && d.Folder == game && back.FindByFolder(game) != null, "builds.json keeps the UTF-8 folder.");
        Require(!File.ReadAllBytes(home.Builds).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "builds.json has no BOM.");
        Require(StartRequest.Parse(new[] { "--launch", game }).Folder == game, "The command line keeps it.");
        var s = new LauncherSettings { Nup = Path.Combine(game, "ゲーム.nup") };
        s.Save(home.Settings);
        Require(LauncherSettings.Load(home.Settings).Nup == Path.Combine(game, "ゲーム.nup"), "settings.json keeps it.");
    }
}
