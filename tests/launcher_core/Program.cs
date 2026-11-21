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
        await ToolchainChecks();
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
        LogChecks();
        DataLogChecks();
        LogFileChecks();
        CommandChecks();
        ShaderCacheChecks();
        StorageChecks();
        UpdatePolicyChecks();
        await PullRequestChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        CommandLineChecks();
        SavesChecks.Run(Folder("saves"));
        SaveArchiveChecks.Run(Folder("save-archive"));
        SaveBackupsChecks.Run(Folder("save-backups"));
        HookshotChecks(fixtures);
        SaveEditorChecks(fixtures);
        HookshotSettingsChecks();
        SettingsCarryChecks();
        ShownProfileChecks();
        DriverSettingsChecks();
        EditBackupsChecks();
        NfcChecks();
        NfcReaderChecks();
        PadChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        Utf8Checks();
        SteamChecks(Path.GetFullPath(Path.Combine(fixtures)));
        await ScreenChecks();
        await BusyChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        IconChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        ProblemChecks();
        PerfChecks();
        CrashReportChecks();
        await BuildReportChecks(Path.GetFullPath(Path.Combine(fixtures, "..", "..")));
        TranslationChecks();
        checks += SavesChecks.Count;
        checks += SaveArchiveChecks.Count;
        checks += SaveBackupsChecks.Count;
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
        public Action<long>? DuringDownload;

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
                DuringDownload?.Invoke(at + n);
            }
        }
    }

    static Func<string> Fail(int? status) => () => throw new FetchException("authored failure", status);

    // ---- releases (Releases, ReleaseManifest, SignedSums, ToolsLock) ----

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
        var image = Encoding.UTF8.GetBytes("\x7fELF the builder");
        Dictionary<string, byte[]> Builder(string v) => new()
        {
            [ReleaseManifest.BuilderFile(v, "linux-x64")] = image,
            [ReleaseManifest.BuilderFile(v, "windows-x64")] = Encoding.UTF8.GetBytes("PK the builder"),
            ["tools.lock"] = Encoding.UTF8.GetBytes("# no tools\n"),
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
        Require(st.Builder["linux-x64"].Size == image.Length && st.Builder["linux-x64"].Sha256 == ShaOf(image) && st.Tools.ContainsKey("windows-x64")
                && st.Commit == Commit && st.Notes == "https://git.magicbean.dev/MagicBeanDev/Longshot/releases/tag/v3.0.1",
                "A release's files, with the SHA-256 its signed SHA256SUMS gives.");
        Require((await longshot.BuilderAsync("v3.0.1")).Url.EndsWith("/Longshot-v3.0.1-x86_64.AppImage", StringComparison.Ordinal),
                "The Linux builder of a version.");
        forge.Add("v3.1.0", key, prerelease: false, files: Builder("v3.1.0"));
        Require(await longshot.NewestAsync("stable") == "v3.1.0", "A new stable is its channel's newest.");
        // (pulling a bad version: deleted, or back to a draft, its channel falls back by itself)
        forge.Remove("v3.1.0");
        forge.Remove("v3.0.1");
        Require(await longshot.NewestAsync("stable") == "v3.0.0", "A deleted stable is gone from its channel.");
        forge.Add("v3.0.0", key, draft: true, files: Builder("v3.0.0"));
        Require(await longshot.NewestAsync("stable") == null, "A stable set back to a draft is gone from its channel.");
        forge.Add("v3.0.0", key, files: Builder("v3.0.0"));

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
            .Where(l => !l.EndsWith("x86_64.AppImage", StringComparison.Ordinal)).Select(l => l + "\n")));
        await ThrowsAsync<InvalidDataException>(() => new Releases(forge.Server, Platform.Linux, keys, NoWait).BuilderAsync("v3.4.0"),
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
                && stable.Builder.Count == 0 && await launcher.NewestAsync("nightly") == "nightly-30-cccccccc",
                "The launcher's own two channels, and its three files.");
        // (an update check reads each product's release list once, whatever the number of channels)
        own.Server.Asked.Clear();
        var two = await new Releases(own.Server, Platform.Linux, keys, NoWait, Product.Launcher).ChannelsAsync(Versions.Channels);
        Require(two["stable"]?.Version == "v1.0.0" && two["nightly"]?.Version == "nightly-30-cccccccc"
                && own.Server.Asked.Count(u => u.Contains("/releases?", StringComparison.Ordinal)) == 1, "Both channels from one fetch of the list.");
        Require(launcher.ListUrl(1).StartsWith("https://git.magicbean.dev/api/v1/repos/MagicBeanDev/LongshotLauncher/", StringComparison.Ordinal),
                "The launcher's releases are its own repository's.");

        // (tools.lock: the Windows builder's tools; a prefix becomes the dest, mirrors are kept, other systems left out)
        var sha = new string('a', 64);
        var lockParts = ToolsLock.Parse(
            "# a comment\n" +
            $"tool python 3.14 windows-x64 {sha} 10 LongshotBuilder/tools/python HookshotBuilder/tools/python https://up/p.zip https://mirror/p.zip\n" +
            $"tool llvm-mingw 1 windows-x64 {sha} 11 LongshotBuilder/tools/llvm-mingw llvm-mingw-1-ucrt https://up/l.zip\n" +
            $"tool whole 1 windows-x64 {sha} 12 LongshotBuilder/tools/whole - https://up/w.zip\n" +
            $"tool other 1 linux-x64 {sha} 12 x - https://up/o.zip\n", "windows-x64");
        Require(lockParts.Count == 3 && lockParts[0] is { Dest: "LongshotBuilder/tools", From: "HookshotBuilder/tools/python", To: "python" }
                && lockParts[0].Mirrors!.SequenceEqual(new[] { "https://mirror/p.zip" }) && lockParts[2] is { Dest: "LongshotBuilder/tools/whole", From: null },
                "tools.lock's lines as parts.");
        foreach (var bad in new[] { $"tool p 1 windows-x64 {sha} 10 ../x - https://u", $"tool p 1 windows-x64 {sha} 10 a - http://u",
                                    $"tool p 1 windows-x64 {sha.ToUpperInvariant()} 10 a - https://u", $"tool P 1 windows-x64 {sha} 10 a - https://u",
                                    "part x y", $"tool p 1 windows-x64 {sha} 10 a /etc https://u" })
            Throws<FormatException>(() => ToolsLock.Parse(bad, "windows-x64"), "A bad tools.lock line was taken: " + bad);

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
        ReleaseInfo R(string v, IReadOnlyDictionary<string, FileRef>? l = null) => new("launcher", v, Commit, null, null, l ?? both, none, none);
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


    /// A part served by f at url, unpacked whole under dest
    static Part PartAt(FakeFetcher f, string url, byte[] bytes, string dest)
    {
        f.Files[url] = bytes;
        return new Part(ShaOf(bytes), bytes.Length, dest, null, null, url);
    }

    /// A Linux version's parts, as InstallAppImageAsync installs the tools of the tools.lock in an AppImage
    static PartsList Linux(string tag, params Part[] parts) => new(ListKind.Linux, tag, parts, null);

    static async Task ToolchainChecks()
    {
        var root = Folder("toolchains");

        // (a binfmt_misc handler for AppImages, as NixOS's programs.appimage.binfmt registers it: the builder's magic cleared then)
        var binfmt = Folder("binfmt_misc");
        File.WriteAllText(Path.Combine(binfmt, "status"), "enabled\n");
        File.WriteAllText(Path.Combine(binfmt, "register"), "");
        Require(!Toolchains.HasAppImageBinfmt(binfmt) && !Toolchains.HasAppImageBinfmt(Path.Combine(binfmt, "missing")),
                "No AppImage handler: none registered, or no binfmt_misc at all.");
        File.WriteAllText(Path.Combine(binfmt, "appimage_type_2"),
                          "disabled\ninterpreter /run/binfmt/appimage_type_2\nflags: \noffset 0\nmagic 7f454c462e2e2e2e414902\nmask ffffffff00000000ffffff\n");
        Require(!Toolchains.HasAppImageBinfmt(binfmt), "A disabled AppImage handler takes nothing.");
        File.WriteAllText(Path.Combine(binfmt, "appimage_type_2"),
                          "enabled\ninterpreter /run/binfmt/appimage_type_2\nflags: \noffset 0\nmagic 7f454c462e2e2e2e414902\nmask ffffffff00000000ffffff\n");
        Require(Toolchains.HasAppImageBinfmt(binfmt), "NixOS's AppImage handler is seen.");
        File.WriteAllText(Path.Combine(binfmt, "appimage_type_2"), "enabled\ninterpreter /usr/bin/x\nflags: \noffset 8\nmagic 414902\n");
        Require(Toolchains.HasAppImageBinfmt(binfmt), "An AppImage handler matching at offset 8 is seen.");
        var image = Path.Combine(binfmt, "builder.AppImage");
        File.WriteAllBytes(image, [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, (byte)'A', (byte)'I', 2, 0, 9]);
        Toolchains.ClearAppImageMagic(image);
        Require(File.ReadAllBytes(image).SequenceEqual(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 9 }),
                "The AppImage magic cleared, nothing else.");
        // (AppImageLauncher's binfmt-bypass sets TARGET_APPIMAGE to this launcher: the builder's runtime would unpack it instead)
        var env = new Dictionary<string, string?> { ["APPIMAGE"] = "/a", ["APPDIR"] = "/m", ["TARGET_APPIMAGE"] = "/a", ["HOME"] = "/h" };
        Toolchains.WithoutOurAppImage(env);
        Require(env.Count == 1 && env["HOME"] == "/h", "Another AppImage starts without this launcher's APPIMAGE, APPDIR and TARGET_APPIMAGE.");
        var gh = new FakeFetcher();
        var tools = Zip(("nx2/make_game.py", "print('authored')\n", null, 0x1ED), ("nx2/link.py", TestLinks.Available ? null : "authored link placeholder", TestLinks.Available ? "make_game.py" : null, 0),
                        ("README", "authored\n", null, 0x1A4));
        var python = Zip(("bin/python3", "#!/bin/sh\n", null, 0x1ED));
        var python2 = Zip(("bin/python3", "#!/bin/sh\n# 2\n", null, 0x1ED));
        var sysroot = Zip(("lib/libc.so.6", "authored", null, 0x1A4), ("lib64/ld.so", TestLinks.Available ? null : "authored link placeholder", TestLinks.Available ? "/lib/libc.so.6" : null, 0));
        var v1 = Linux("v3.3.0", PartAt(gh, "https://x/tools.zip", tools, "usr/share/longshot"),
                       PartAt(gh, "https://x/python.zip", python, "usr/python"),
                       PartAt(gh, "https://x/sysroot.zip", sysroot, Toolchains.SysrootPart));
        var chains = new Toolchains(root, gh);
        var seen = new List<double>();
        var t1 = await chains.InstallAsync(v1, p => seen.Add(p.Fraction));
        Require(File.ReadAllText(t1.MakeGame) == "print('authored')\n" && t1.Python == Path.Combine(root, "v3.3.0", "usr", "python", "bin", "python3")
                && File.Exists(t1.Python), "The tools are where make_game.py and Python are looked for.");
        Require(seen.Count > 3 && seen.Zip(seen.Skip(1)).All(p => p.Second >= p.First) && Math.Abs(seen[^1] - 1) < 1e-9,
                "The install's progress goes up to 1.");
        if (TestLinks.Available) Require(new FileInfo(Path.Combine(t1.ToolsRoot, "nx2", "link.py")).LinkTarget == "make_game.py", "A part's relative link.");
        var rebased = new FileInfo(Path.Combine(root, "v3.3.0", "usr", "lib", "longshot", "sysroot", "lib64", "ld.so"));
        if (TestLinks.Available) Require(rebased.LinkTarget == "../lib/libc.so.6", "A sysroot's rooted link is rebased inside it: " + rebased.LinkTarget);
        if (!OperatingSystem.IsWindows())
            Require((File.GetUnixFileMode(t1.MakeGame) & UnixFileMode.UserExecute) != 0
                    && (File.GetUnixFileMode(Path.Combine(t1.ToolsRoot, "README")) & (UnixFileMode.UserExecute | UnixFileMode.OtherWrite)) == 0,
                    "The parts' modes: executables stay so, nothing is world-writable.");
        Require(chains.IsInstalled("v3.3.0") && !Directory.Exists(Path.Combine(root, "v3.3.0.part")), "Installed, and no staging left.");

        // (the next version: the unchanged parts come from v3.3.0, only the changed one is downloaded)
        gh.Asked.Clear();
        var v2 = Linux("v3.3.1", PartAt(gh, "https://x/tools.zip", tools, "usr/share/longshot"),
                       PartAt(gh, "https://x/python2.zip", python2, "usr/python"),
                       PartAt(gh, "https://x/sysroot.zip", sysroot, Toolchains.SysrootPart));
        var t2 = await chains.InstallAsync(v2);
        Require(gh.Asked.SequenceEqual(new[] { "https://x/python2.zip" }), "Unchanged parts were downloaded again: " + string.Join(", ", gh.Asked));
        Require(File.ReadAllText(t2.MakeGame) == "print('authored')\n" && File.ReadAllText(t2.Python).Contains("# 2")
                && File.ReadAllText(Path.Combine(t2.ToolsRoot, "README")) == "authored\n",
                "A kept part is the same files.");
        if (TestLinks.Available)
            Require(new FileInfo(Path.Combine(t2.ToolsRoot, "nx2", "link.py")).LinkTarget == "make_game.py"
                    && new FileInfo(Path.Combine(root, "v3.3.1", "usr", "lib", "longshot", "sysroot", "lib64", "ld.so")).LinkTarget == "../lib/libc.so.6",
                    "A kept part keeps both relative and rebased links.");
        Require(File.ReadAllText(t1.Python) == "#!/bin/sh\n", "The older version kept its own Python.");

        // (a part whose bytes differ from its list: nothing is left, the version is not installed)
        var python3 = Zip(("bin/python3", "#!/bin/sh\n# 3\n", null, 0x1ED));
        var tampered = Linux("v3.4.0", PartAt(gh, "https://x/tools.zip", tools, "usr/share/longshot"),
                             new Part(ShaOf(python3), python3.Length, "usr/python", null, null, "https://x/python3.zip"));
        gh.Files["https://x/python3.zip"] = Zip(("bin/python3", "#!/bin/sh\n# 4\n", null, 0x1ED));
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(tampered), "A part with another checksum was installed.");
        Require(!Directory.Exists(Path.Combine(root, "v3.4.0")) && !Directory.Exists(Path.Combine(root, "v3.4.0.part"))
                && !Directory.EnumerateFiles(root, "*.zip.part", SearchOption.AllDirectories).Any(), "A failed install left files.");
        gh.Files["https://x/python3.zip"] = python3;
        var longer = Linux("v3.4.0", new Part(ShaOf(python3), python3.Length - 1, "usr/python", null, null, "https://x/python3.zip"));
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(longer), "A part larger than its list was installed.");
        Require(!Directory.Exists(Path.Combine(root, "v3.4.0.part")), "A part too large left its staging.");
        // (a zip with a name outside its folder)
        var evil = Zip(("../evil", "x", null, 0x1A4));
        var slip = Linux("v3.4.0", PartAt(gh, "https://x/evil.zip", evil, "usr/share/longshot"));
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(slip), "A zip's ../ name was unpacked.");
        Require(!File.Exists(Path.Combine(root, "evil")) && !File.Exists(Path.Combine(root, "v3.4.0.part", "usr", "share", "evil")), "../evil was written.");
        var outward = Zip(("x", null, "../../../../outside", 0));
        var outwardList = Linux("v3.4.0", PartAt(gh, "https://x/out.zip", outward, "usr/share/longshot"));
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(outwardList), "A link out of the version's folder was made.");
        var absolute = Zip(("x", null, "/etc/passwd", 0));
        var absoluteList = Linux("v3.4.0", PartAt(gh, "https://x/abs.zip", absolute, "usr/share/longshot"));
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(absoluteList), "An absolute link outside the sysroot was made.");
        Require(!Directory.Exists(Path.Combine(root, "v3.4.0")) && !Directory.Exists(Path.Combine(root, "v3.4.0.part")), "A refused part left files.");

        // (cancelled half way through a download: nothing is left)
        using (var cancel = new CancellationTokenSource())
        {
            var big = Zip(("nx2/make_game.py", string.Concat(Enumerable.Range(0, 200).Select(_ => Guid.NewGuid().ToString())), null, 0x1A4));
            var cancelled = Linux("v3.5.0", PartAt(gh, "https://x/big.zip", big, "usr/share/longshot"));
            gh.DuringDownload = n => { if (n > 100) cancel.Cancel(); };
            await ThrowsAsync<OperationCanceledException>(() => chains.InstallAsync(cancelled, null, cancel.Token), "The cancel was not heard.");
            gh.DuringDownload = null;
            Require(!Directory.Exists(Path.Combine(root, "v3.5.0")) && !Directory.Exists(Path.Combine(root, "v3.5.0.part"))
                    && !Directory.EnumerateFiles(root, "*.zip.part", SearchOption.AllDirectories).Any(), "A cancelled install left files.");
        }

        // (Windows: the builder's zip under ., with its LongshotBuilder/ folder, and a tool's prefix folder renamed as tools.lock says)
        var app = Zip(("LongshotBuilder/LongshotBuilder.exe", "MZ", null, 0x1A4), ("LongshotBuilder/tools/nx2/make_game.py", "w", null, 0x1A4),
                      ("LongshotBuilder/tools/python/python.exe", "MZ", null, 0x1A4));
        var llvm = Zip(("llvm-mingw-20261104-ucrt-x86_64/bin/clang.exe", "MZ", null, 0x1A4));
        gh.Files["https://x/llvm.zip"] = llvm;
        var setup = new PartsList(ListKind.Setup, "v3.3.0-rc.2", new List<Part>
        {
            PartAt(gh, "https://x/app.zip", app, "."),
            new(ShaOf(llvm), llvm.Length, "LongshotBuilder/tools", "llvm-mingw-20261104-ucrt-x86_64", "llvm-mingw", "https://x/llvm.zip"),
        }, ToolsLock.Run);
        var w = await chains.InstallAsync(setup);
        Require(w.ToolsRoot == Path.Combine(root, "v3.3.0-rc.2", "LongshotBuilder", "tools") && File.Exists(w.MakeGame)
                && w.Python == Path.Combine(w.ToolsRoot, "python", "python.exe")
                && File.Exists(Path.Combine(w.ToolsRoot, "llvm-mingw", "bin", "clang.exe")), "The Windows layout.");
        Require(chains.Find("v3.3.0-rc.2")?.Kind == ListKind.Setup, "The record says which list made it.");

        Require(chains.Installed().SequenceEqual(new[] { "v3.3.0", "v3.3.0-rc.2", "v3.3.1" }), "The installed versions.");
        Directory.CreateDirectory(Path.Combine(root, "v9.part"));
        var removed = chains.Prune(new[] { "v3.3.1" });
        Require(chains.Installed().SequenceEqual(new[] { "v3.3.1" }) && removed.Contains("v3.3.0") && removed.Contains("v9.part"),
                "Prune keeps the versions builds use and removes the rest.");
        Require(File.ReadAllText(Path.Combine(root, "v3.3.1", "usr", "share", "longshot", "nx2", "make_game.py")) == "print('authored')\n",
                "A version that shared files with a removed one keeps them.");
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
        // (the game files a new build of a .nup can link from: a build of that .nup whose folder has what make_game's
        // romfs_from_build checks, romfs/, .longshot_exefs/ and this .nup's stamp; on the new build's drive first, then the newest;
        // never its own folder)
        var nup = Path.Combine(home, "game.nup");
        File.WriteAllBytes(nup, new byte[37]);
        var stamp = BuildRegistry.UnpackStamp(nup);
        Require(stamp == "unpacked by this builder: game.nup 37" && BuildRegistry.UnpackStamp(Path.Combine(home, "gone.nup")) == null,
                "The stamp make_game writes for a .nup's unpack: its name and size.");
        void Unpack(string folder, string? mark = null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "romfs"));
            Directory.CreateDirectory(Path.Combine(folder, ".longshot_exefs"));
            File.WriteAllText(Path.Combine(folder, ".longshot_romfs"), (mark ?? stamp) + "\n");
        }
        var fresh = Path.Combine(home, "new");
        a.Nup = b.Nup = nup;
        b.Updated = a.Updated.AddMinutes(5);
        Require(r.SameGameFiles(nup, fresh) == null, "No folder unpacked yet: none.");
        Unpack(a.Folder);
        Unpack(b.Folder);
        Require(r.SameGameFiles(nup, fresh) == b && r.SameGameFiles(Path.Combine(home, "other.nup"), fresh) == null,
                "The game files of another build of the same .nup: the newest that has them.");
        Require(r.SameGameFiles(nup, b.Folder) == null, "A folder that has them already (an update in its own folder): none.");
        Directory.Delete(Path.Combine(b.Folder, ".longshot_exefs"));
        Require(r.SameGameFiles(nup, fresh) == a && r.SameGameFiles(nup, b.Folder) == a,
                "A folder without its kept exefs is passed over, and an older one used, never the build's own folder.");
        Unpack(b.Folder, mark: "unpacked by this builder: game.nup 36");
        Require(r.SameGameFiles(nup, fresh) == a, "A folder unpacked from another .nup (its stamp) is passed over.");
        File.Delete(Path.Combine(b.Folder, ".longshot_romfs"));
        Require(r.SameGameFiles(nup, fresh) == a, "A folder with no stamp is passed over.");
        Unpack(b.Folder);
        Directory.Delete(Path.Combine(a.Folder, "romfs"));
        Require(r.SameGameFiles(nup, fresh) == b && r.SameGameFiles(nup, b.Folder) == null, "A folder without its romfs is passed over.");
        Unpack(a.Folder);
        Func<string, string> drives = f => BuildRegistry.SameFolder(f, a.Folder) || BuildRegistry.SameFolder(f, fresh) ? "D:" : "C:";
        Require(r.SameGameFiles(nup, fresh, drives) == a && r.SameGameFiles(nup, fresh, _ => "C:") == b,
                "A build on the new build's drive first, as a hard link does not cross drives; then the newest.");
        // (romfs_from_build compares the stamp only, the .nup's name and size: the .nup moved, or a build with no record of it, still shares)
        var movedNup = Path.Combine(home, "moved", "game.nup");
        Directory.CreateDirectory(Path.GetDirectoryName(movedNup)!);
        File.Move(nup, movedNup);
        Require(r.SameGameFiles(movedNup, fresh, drives) == a, "A .nup moved to another folder still shares the game files unpacked from it.");
        a.Nup = null;
        Require(r.SameGameFiles(movedNup, fresh, drives) == a, "So does a build with no record of its .nup (listed again): its stamp says it.");
        File.Move(movedNup, nup);
        a.Nup = nup;
        File.WriteAllBytes(movedNup, new byte[36]);
        Require(r.SameGameFiles(movedNup, fresh) == null, "A .nup of the same name and another size shares nothing.");
        File.Delete(nup);
        Require(r.SameGameFiles(nup, fresh) == null, "No .nup to stamp: none.");
        a.Nup = b.Nup = null;
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

    // ---- make_game.py's lines and the overall bar ----

    static void ProgressChecks()
    {
        Require(MakeGameProtocol.Parse("@@stage compile Compiling") is MakeGameLine.Stage { Id: "compile", Text: "Compiling" }, "@@stage");
        Require(MakeGameProtocol.Parse("@@progress 0.250") is MakeGameLine.Progress { Fraction: 0.25 }, "@@progress");
        Require(MakeGameProtocol.Parse("@@progress 7") is MakeGameLine.Progress { Fraction: 1 }, "@@progress is clamped");
        Require(MakeGameProtocol.Parse("@@progress nan") is MakeGameLine.Plain, "@@progress nan is no progress");
        Require(MakeGameProtocol.Parse("@@plan 12 3400") is MakeGameLine.Plan { Changed: 12, Total: 3400 }, "@@plan");
        Require(MakeGameProtocol.Parse("@@file a.c 1.5 new") is MakeGameLine.File { Name: "a.c", Kind: "new" }, "@@file");
        Require(MakeGameProtocol.Parse("@@eta 95") is MakeGameLine.Eta { Seconds: 95 }, "@@eta");
        Require(MakeGameProtocol.Parse("@@times 600 400 -1") is MakeGameLine.Times { Previous: -1 }, "@@times");
        Require(MakeGameProtocol.Parse("@@check bad Not the supported version") is MakeGameLine.Check { Ok: false, Text: "Not the supported version" }, "@@check");
        Require(MakeGameProtocol.Parse("@@done /g/Longshot é") is MakeGameLine.Done { Folder: "/g/Longshot é" }, "@@done");
        Require(MakeGameProtocol.Parse("@@error Out of space\r") is MakeGameLine.Error { Text: "Out of space" }, "@@error");
        Require(MakeGameProtocol.Parse("the shaders were not prepared now (no usable Vulkan here): the game prepares them at its first start")
                is MakeGameLine.NotPrepared { Reason: "no usable Vulkan here" }, "make_game's not-prepared line");
        Require(MakeGameProtocol.Parse("build: up to 16 compilers") is MakeGameLine.Plain, "A plain line");

        var transcript = new[]
        {
            "@@stage read Reading your game files", "@@progress 0.5", "@@progress 1.0", "@@stage lift Translating the game to PC code",
            "@@progress 0.3", "@@progress 0.2", "@@progress 1", "@@stage compile Compiling", "@@plan 3 10", "@@eta 120",
            "@@file a.c 1.0 new", "@@progress 0.4", "@@file b.c 1.0 cached", "@@file c.c 2.0 again", "@@progress 0.9",
            "@@stage lift Translating the game to PC code", "@@stage compile Compiling", "@@progress 1",
            "@@stage finish Putting the game folder together", "@@stage shaders Preparing shaders", "@@progress 0.5",
            "@@times 600 400 -1", "@@done /authored/game",
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
            p.Feed(line);
            bars.Add(p.Overall);
            if (line.StartsWith("@@eta")) Require(p.Eta == 120 && p.EtaLines == 1, "The ETA passes through, counted.");
            if (line.StartsWith("@@plan")) Require(p.Stages.First(s => s.Id == "compile").Kept, "A compile of 3 files of 10 kept the rest.");
            if (line.StartsWith("@@file c.c")) Require(p.Stages.First(s => s.Id == "compile").Note == "2 of 3 changed files", "The compile's note.");
        }
        Require(bars.Zip(bars.Skip(1)).All(x => x.Second >= x.First), "The bar went back: " + string.Join(" ", bars.Select(b => b.ToString("0.000"))));
        Require(bars[^1] == 1 && p.Finished && p.Folder == "/authored/game" && p.Stages.All(s => s.State == StageState.Done), "The bar ends full.");
        Require(Math.Abs(bars[bars.Count - 3] - (825 - 311 / 2.0) / 825) < 1e-9, "The shaders' half: their forecast 311 s of the 825, halfway.");

        // (the read's unpacking says no @@progress: its "MiB written" lines against the title's size move the read's row)
        var rp = new BuildProgress(download: false, shaders: true);
        var read = rp.Stages.First(st => st.Id == "read");
        rp.Feed("@@stage read Reading your game files");
        rp.Feed("  1.0 MiB written (0 files completed)");   // (before the size: nothing to measure it against)
        Require(rp.Overall == 0, "A MiB line before the title's size moves nothing.");
        rp.Feed("title 0: 0400486024dec000 v131072  exefs 5 files (main 29953289)  romfs 186587 files, 26.24 GB");
        rp.Feed("  12512.0 MiB written (90000 files completed)");
        var half = 12512.0 * (1 << 20) / 26.24e9 * 0.95;
        Require(Math.Abs(rp.Overall - (read.Start + read.Weight * half)) < 1e-9, "Half the title unpacked: the read's row about half way.");
        rp.Feed("  24408.6 MiB written (175854 files completed)");
        rp.Feed("  100.0 MiB written (1 files completed)");   // (a second title's: the bar never goes back)
        Require(rp.Overall > read.Start + read.Weight * 0.9 && rp.Overall <= read.Start + read.Weight * 0.95, "Nearly all unpacked: the checks' share left.");
        rp.Feed("@@stage lift Translating the game to PC code");
        rp.Feed("  9999.0 MiB written (1 files completed)");
        Require(Math.Abs(rp.Overall - rp.Stages.First(st => st.Id == "lift").Start) < 1e-9, "Another stage's MiB lines move nothing.");
        Require(p.Stages.Where(s => s.Id != "compile").All(s => !s.Kept), "Nothing else said it kept an earlier build's work.");

        var q = new BuildProgress(download: false, shaders: true);
        q.Feed("@@stage read x");
        Require(q.Overall == 0 && q.Current == "read", "With no download, the read starts at 0.");
        q.Feed("@@stage compile x");
        Require(Math.Abs(q.Overall - (76 + 57) / 815.0) < 1e-9, "The weights scale to the stages a build has.");
        q.Feed("the shaders were not prepared now (\"Prepare shaders now\" is unticked): the game prepares them at its first start");
        q.Feed("@@done /g");
        var shaders = q.Stages.First(s => s.Id == "shaders");
        Require(q.Overall == 1 && shaders.State == StageState.Skipped && shaders.Note!.Contains("unticked"), "Shaders left to the first start.");
        var r = new BuildProgress(download: false, shaders: true);
        r.Feed("@@stage finish x");
        r.Feed("@@done /g");
        Require(r.Stages.First(s => s.Id == "shaders").State == StageState.Skipped, "No @@stage shaders is no check mark.");
        // (the bar's shares are the forecast at the build's threads: fewer threads, more of it for the compile and the shaders)
        var slow = new BuildProgress(download: false, shaders: true, threads: 8);
        var fast = new BuildProgress(download: false, shaders: true, threads: 24);
        double Share(BuildProgress b, string id) => b.Stages.First(s => s.Id == id).Weight;
        Require(Math.Abs(slow.Stages.Sum(s => s.Weight) - 1) < 1e-9 && Share(slow, "compile") > Share(fast, "compile") + 0.03
                && Share(slow, "read") < Share(fast, "read") && Math.Abs(Share(slow, "compile") / Share(slow, "read") - BuildForecast.Seconds("compile", 8) / 76) < 1e-9,
                "The bar's shares follow this PC's forecast.");
        var kept = new BuildProgress(download: false, shaders: false);
        kept.Feed("@@stage read x");
        kept.Feed("the game was read from this .nup before: reading it again is skipped");
        kept.Feed("@@stage lift x");
        kept.Feed("the game was read from this .nup before: reading it again is skipped");   // (a read's line in the lift: not the lift's)
        Require(kept.Stages[0].Kept && !kept.Stages[1].Kept, "A read skipped kept the read's work.");
        kept.Feed("the game was translated with these tools before (its compiled code kept): translating it again is skipped");
        kept.Feed("@@stage compile x");
        kept.Feed("@@plan 10 10");
        Require(kept.Stages[1].Kept && !kept.Stages[2].Kept, "A translation skipped kept; a compile of every file did not.");
        var e = new BuildProgress(download: false, shaders: false, update: true);
        e.Feed("@@stage read x");
        e.Feed("@@error No space left");
        Require(e.Error == "No space left" && !e.Finished && e.Overall < 1 && e.Stages[0].Title.Contains("kept") && e.Stages[0].Kept, "A failed update.");
    }

    // ---- make_game.py's command line ----

    static void CommandChecks()
    {
        var home = new LauncherHome(Path.Combine(scratch, "cmd-home"));
        var win = new Toolchain("v3.3.0", Path.Combine(home.Toolchains, "v3.3.0"), ListKind.Setup, "LongshotBuilder/LongshotBuilder.exe");
        var request = new BuildRequest(new BuildSource.Nup("C:/g/game.nup"), "C:/Games/Longshot", Language: "fr", Jobs: 6, MemoryGb: 16,
                                       PrepareShaders: false, Dlss: true, DlssLicense: true, Fsr: true, FsrLicense: true);
        var w = MakeGameCommand.For(win, request, Platform.Windows, home, "C:/L/LongshotLauncher.exe", d => d.EndsWith("cpm"));
        Require(w.FileName == Path.Combine(win.ToolsRoot, "python", "python.exe") && w.WorkingDirectory == win.ToolsRoot, "Windows: the tools' Python.");
        Require(w.Arguments.SequenceEqual(new[]
                {
                    "-I", win.MakeGame, "--nup", "C:/g/game.nup", "--out", "C:/Games/Longshot", "--language", "fr", "--work", home.Work,
                    "--no-prepare-shaders", "--fetch-ngx-sdk", "--accept-nvidia-license", "--fsr", "--fetch-fsr-runtime", "--accept-amd-license",
                }), "Windows arguments: " + string.Join(" ", w.Arguments));
        Require(w.Environment["LONGSHOT_JOBS"] == "6" && w.Environment["LONGSHOT_MEM_LIMIT_GB"] == "16" && w.Environment["LONGSHOT_NGX_SDK"] == home.NgxSdk
                && w.Environment["LONGSHOT_FFX_SDK"] == home.FfxSdk && w.Environment["LONGSHOT_BUILDER_EXE"] == "C:/L/LongshotLauncher.exe"
                && w.Environment["RX_CPM"] == Path.Combine(win.ToolsRoot, "cpm") && !w.Environment.ContainsKey("EMSDK")
                && w.Environment["PYTHONIOENCODING"] == "utf-8" && w.Environment["LONGSHOT_SETUP_BASE"] == null, "Windows environment.");
        Require(w.Environment["LONGSHOT_OBJ_STORE"] == Path.Combine(home.Work, Disk.ObjectStore)
                && w.Environment["LONGSHOT_LIFT_STORE"] == Path.Combine(home.Work, Disk.LiftStore)
                && w.Environment["LONGSHOT_OBJ_STORE_GB"] == "4" && w.Environment["LONGSHOT_LIFT_STORE_KEEP"] == "2",
                "Windows: capped stores are kept in the work folder.");
        var licenceless = MakeGameCommand.For(win, request with { DlssLicense = false, FsrLicense = false, Jobs = null, MemoryGb = null, PrepareShaders = true },
                                              Platform.Windows, home, "L", _ => false);
        Require(!licenceless.Arguments.Any(a => a.StartsWith("--fetch") || a.StartsWith("--accept") || a == "--fsr" || a == "--no-prepare-shaders")
                && licenceless.Environment["LONGSHOT_NGX_SDK"] == null && licenceless.Environment["LONGSHOT_FFX_SDK"] == null
                && !licenceless.Environment.ContainsKey("LONGSHOT_JOBS") && licenceless.Environment["LONGSHOT_MEM_LIMIT_GB"] == null,
                "No license, no DLSS or FSR; Automatic sets nothing.");

        var lin = new Toolchain("v3.3.0", Path.Combine(home.Toolchains, "v3.3.0"), ListKind.Linux, null);
        var l = MakeGameCommand.For(lin, request with { Source = new BuildSource.Folders("/d/romfs", "/d/exefs"), Out = "/g", Work = "/w" },
                                    Platform.Linux, home, "/L/Longshot.AppImage", _ => false);
        Require(l.FileName == Path.Combine(lin.Folder, "usr", "python", "bin", "python3") && l.WorkingDirectory == Path.Combine(lin.Folder, "usr", "share", "longshot"),
                "Linux: the AppImage layout's Python.");
        Require(l.Arguments.SequenceEqual(new[]
                {
                    "-I", lin.MakeGame, "--romfs", "/d/romfs", "--exefs", "/d/exefs", "--out", "/g", "--language", "fr", "--work", "/w",
                    "--jobs", "6", "--no-prepare-shaders", "--fetch-ngx-sdk", "--accept-nvidia-license",
                }), "Linux arguments (tools without AMD's FSR API headers: no FSR): " + string.Join(" ", l.Arguments));
        Require(!l.Environment.ContainsKey("LONGSHOT_JOBS") && l.Environment["LONGSHOT_MEM_LIMIT_GB"] == "16"
                && l.Environment["LONGSHOT_VK_PREBUILT"] == null && l.Environment["LONGSHOT_TOOLS"] == lin.ToolsRoot
                && l.Environment["LONGSHOT_SYSROOT"] == Path.Combine(lin.LibRoot, "sysroot") && l.Environment["EMSDK"] == Path.Combine(lin.LibRoot, "emsdk")
                && l.Environment["LONGSHOT_FFX_SDK"] == null, "Linux environment (DLSS builds its own Vulkan back end).");
        Require(l.Environment["LONGSHOT_OBJ_STORE"] == Path.Combine("/w", Disk.ObjectStore)
                && l.Environment["LONGSHOT_LIFT_STORE"] == Path.Combine("/w", Disk.LiftStore)
                && l.Environment["LONGSHOT_OBJ_STORE_GB"] == "4" && l.Environment["LONGSHOT_LIFT_STORE_KEEP"] == "2",
                "Linux: capped stores are kept in the work folder.");
        // (AMD FSR 4 on Linux: --fsr alone, nothing of AMD's fetched, and the Vulkan back end compiled here, not the prebuilt one)
        var fidelityfx = Path.Combine(lin.ToolsRoot, "third_party", "fidelityfx");
        var lf = MakeGameCommand.For(lin, request with { Dlss = false, Source = new BuildSource.Nup("/g/game.nup"), Out = "/g", Work = "/w" },
                                     Platform.Linux, home, "/L", d => d == fidelityfx);
        Require(lf.Arguments.SequenceEqual(new[]
                {
                    "-I", lin.MakeGame, "--nup", "/g/game.nup", "--out", "/g", "--language", "fr", "--work", "/w", "--jobs", "6",
                    "--no-prepare-shaders", "--fsr",
                }), "Linux arguments with FSR 4: " + string.Join(" ", lf.Arguments));
        Require(lf.Environment["LONGSHOT_VK_PREBUILT"] == null && lf.Environment["LONGSHOT_FFX_SDK"] == null && lf.Environment["LONGSHOT_NGX_SDK"] == null,
                "Linux environment with FSR 4: its own Vulkan back end, no AMD runtime folder.");
        Require(MakeGameCommand.FsrBuilds(lin, Platform.Linux, d => d == fidelityfx) && !MakeGameCommand.FsrBuilds(lin, Platform.Linux, _ => false)
                && MakeGameCommand.FsrBuilds(win, Platform.Windows, _ => false), "FSR 4: Windows always, Linux with AMD's FSR API headers.");
        var both = MakeGameCommand.For(lin, request with { Source = new BuildSource.Nup("/g/game.nup"), Out = "/g", Work = "/w" },
                                       Platform.Linux, home, "/L", d => d == fidelityfx);
        Require(both.Arguments.Contains("--fsr") && both.Arguments.Contains("--fetch-ngx-sdk") && !both.Arguments.Contains("--fetch-fsr-runtime")
                && both.Environment["LONGSHOT_VK_PREBUILT"] == null && both.Environment["LONGSHOT_NGX_SDK"] == home.NgxSdk
                && both.Environment["LONGSHOT_FFX_SDK"] == null, "Linux with DLSS and FSR 4: both, one back end compiled here.");
        // (the prebuilt back end only where an AppImage from before 2026-11-14 carries it: current tools compile it in every build)
        var prebuilt = Path.Combine(lin.LibRoot, "longshot_vk.so");
        var lfu = MakeGameCommand.For(lin, request with { Dlss = false, FsrLicense = false }, Platform.Linux, home, "/L", d => d == fidelityfx, f => f == prebuilt);
        Require(!lfu.Arguments.Contains("--fsr") && lfu.Environment["LONGSHOT_VK_PREBUILT"] == prebuilt,
                "Linux, older tools with a prebuilt back end, FSR 4 not acknowledged: the prebuilt back end.");
        var current = MakeGameCommand.For(lin, request with { Dlss = false, FsrLicense = false }, Platform.Linux, home, "/L", d => d == fidelityfx, _ => false);
        Require(current.Environment.ContainsKey("LONGSHOT_VK_PREBUILT") && current.Environment["LONGSHOT_VK_PREBUILT"] == null,
                "Linux, current tools (no prebuilt back end): none given, and one set on the PC is not passed on.");
        // (Intel XeSS, Windows: Intel's library fetched under Intel's licence, by tools that carry scripts/xess_sdk.py; older
        // tools' make_game.py would stop at the flag. Never on Linux, where Intel makes no XeSS library)
        var xessScript = Path.Combine(win.ToolsRoot, "scripts", "xess_sdk.py");
        var xr = request with { Dlss = false, Fsr = false, Xess = true, XessLicense = true };
        var wx = MakeGameCommand.For(win, xr, Platform.Windows, home, "L", _ => false, f => f == xessScript);
        Require(wx.Arguments.SkipWhile(a => a != "--no-prepare-shaders").Skip(1).SequenceEqual(new[] { "--fetch-xess-runtime", "--accept-intel-license" })
                && wx.Environment["LONGSHOT_XESS_SDK"] == home.XessSdk && wx.Environment["LONGSHOT_FFX_SDK"] == null,
                "Windows with XeSS: both flags, and Intel's library kept in the launcher's xess-sdk: " + string.Join(" ", wx.Arguments));
        var oldTools = MakeGameCommand.For(win, xr, Platform.Windows, home, "L", _ => false, _ => false);
        Require(!oldTools.Arguments.Contains("--fetch-xess-runtime") && !oldTools.Arguments.Contains("--accept-intel-license")
                && oldTools.Environment["LONGSHOT_XESS_SDK"] == null, "Windows, tools without scripts/xess_sdk.py: no XeSS flag.");
        var xessUnaccepted = MakeGameCommand.For(win, xr with { XessLicense = false }, Platform.Windows, home, "L", _ => false, f => f == xessScript);
        Require(!xessUnaccepted.Arguments.Contains("--fetch-xess-runtime") && xessUnaccepted.Environment["LONGSHOT_XESS_SDK"] == null,
                "XeSS without Intel's licence: nothing fetched.");
        var lx = MakeGameCommand.For(lin, xr with { Source = new BuildSource.Nup("/g/game.nup"), Out = "/g", Work = "/w" }, Platform.Linux, home, "/L",
                                     _ => false, f => f == Path.Combine(lin.ToolsRoot, "scripts", "xess_sdk.py"));
        Require(!lx.Arguments.Contains("--fetch-xess-runtime") && lx.Environment["LONGSHOT_XESS_SDK"] == null, "Linux: no XeSS.");
        Require(MakeGameCommand.XessBuilds(win, f => f == xessScript) && !MakeGameCommand.XessBuilds(win, _ => false),
                "XeSS: tools that carry scripts/xess_sdk.py.");
        // (Longshot #1802 folded scripts/xess_sdk.py into scripts/fetch.py, which has fetched Intel's library since it was made:
        // tools from nightly-611 on carry fetch.py alone, and make_game.py still takes --fetch-xess-runtime)
        var fetchScript = Path.Combine(win.ToolsRoot, "scripts", "fetch.py");
        var wf = MakeGameCommand.For(win, xr, Platform.Windows, home, "L", _ => false, f => f == fetchScript);
        Require(MakeGameCommand.XessBuilds(win, f => f == fetchScript)
                && wf.Arguments.Contains("--fetch-xess-runtime") && wf.Arguments.Contains("--accept-intel-license")
                && wf.Environment["LONGSHOT_XESS_SDK"] == home.XessSdk,
                "XeSS: tools that carry scripts/fetch.py (Longshot from #1802 on) fetch Intel's library too: " + string.Join(" ", wf.Arguments));
        var allButXess = (string f) => f != xessScript && f != fetchScript;   // (make_game.py, ngx_sdk.py, ffx_sdk.py...: tools from before XeSS)
        Require(!MakeGameCommand.XessBuilds(win, allButXess) && !MakeGameCommand.For(win, xr, Platform.Windows, home, "L", _ => true, allButXess).Arguments.Contains("--fetch-xess-runtime"),
                "XeSS: tools with every other file but neither script (from before XeSS) are no XeSS tools.");
        var u = MakeGameCommand.For(lin, new BuildRequest(new BuildSource.Update("/g"), null), Platform.Linux, home, "/L", fileExists: f => f == prebuilt);
        Require(u.Arguments.SequenceEqual(new[] { "-I", lin.MakeGame, "--update", "/g", "--work", home.Work })
                && u.Environment["LONGSHOT_VK_PREBUILT"] == prebuilt, "An update: the folder, no --out or --language.");
        Throws<ArgumentOutOfRangeException>(() => MakeGameCommand.For(lin, request with { Jobs = 0 }, Platform.Linux, home, "/L"), "Zero workers.");
        // (another game folder of the same .nup gives a new build its game files: the variable for --nup only, and never one from the PC)
        Require(w.Environment.ContainsKey("LONGSHOT_ROMFS_FROM") && w.Environment["LONGSHOT_ROMFS_FROM"] == null
                && MakeGameCommand.For(win, request with { RomfsFrom = "C:/Games/Longshot/v3.0.0" }, Platform.Windows, home, "L", _ => false)
                       .Environment["LONGSHOT_ROMFS_FROM"] == "C:/Games/Longshot/v3.0.0"
                && l.Environment["LONGSHOT_ROMFS_FROM"] == null
                && MakeGameCommand.For(lin, request with { Source = new BuildSource.Folders("/d/romfs", "/d/exefs"), Out = "/g", RomfsFrom = "/o" },
                                       Platform.Linux, home, "/L").Environment["LONGSHOT_ROMFS_FROM"] == null,
                "LONGSHOT_ROMFS_FROM: the folder for a .nup, removed otherwise.");
        // (Intel shader compiles: Longshot's switch for the build's shader preparation, on Windows only, 1 Side by side and 0 One at
        // a time; Auto sets nothing, so the game's own Auto, or one set on the PC, applies)
        Require(MakeGameCommand.For(win, request with { IntelCompiles = GamePlay.IntelCompilesSideBySide }, Platform.Windows, home, "L", _ => false)
                       .Environment["RX_VK_NO_INTEL_SERIAL"] == "1"
                && MakeGameCommand.For(win, request with { IntelCompiles = GamePlay.IntelCompilesOneAtATime }, Platform.Windows, home, "L", _ => false)
                       .Environment["RX_VK_NO_INTEL_SERIAL"] == "0"
                && !w.Environment.ContainsKey("RX_VK_NO_INTEL_SERIAL")
                && !MakeGameCommand.For(lin, request with { IntelCompiles = GamePlay.IntelCompilesSideBySide }, Platform.Linux, home, "/L")
                       .Environment.ContainsKey("RX_VK_NO_INTEL_SERIAL"),
                "Intel shader compiles: RX_VK_NO_INTEL_SERIAL 1 or 0 for a Windows build on Side by side or One at a time, nothing on Auto or Linux.");
        // (Shader time limit, Longer: Longshot's three waits on a stuck driver, for a Windows build only; Normal sets none)
        var longer = MakeGameCommand.For(win, request with { IntelLongerLimits = true }, Platform.Windows, home, "L", _ => false).Environment;
        Require(longer["RX_VK_PREPARE_STALL_S"] == "1800" && longer["RX_VK_PRECOMPILE_STALL_S"] == "180" && longer["RX_VK_PREDICT_STALL_S"] == "180"
                && !longer.ContainsKey("RX_VK_NO_INTEL_SERIAL")
                && GamePlay.IntelLongerLimits.All(v => !w.Environment.ContainsKey(v.Name))
                && GamePlay.IntelLongerLimits.All(v => !MakeGameCommand.For(lin, request with { IntelLongerLimits = true }, Platform.Linux, home, "/L")
                                                           .Environment.ContainsKey(v.Name)),
                "Shader time limit: Longer gives a Windows build the three longer waits, Normal and Linux none.");
        var si = l.StartInfo();
        Require(si.ArgumentList.SequenceEqual(l.Arguments) && !si.Environment.ContainsKey("LONGSHOT_VK_PREBUILT") && si.RedirectStandardOutput,
                "The process start info.");

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
