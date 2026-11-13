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
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: LauncherCoreTests <vectors.json> <fixtures folder> <scratch folder>");
            return 2;
        }
        using var vectors = JsonDocument.Parse(File.ReadAllText(args[0]));
        var fixtures = args[1];
        scratch = args[2];
        Directory.CreateDirectory(scratch);

        VersionChecks(vectors.RootElement);
        await ReleaseChecks(Path.Combine(fixtures, "release"));
        await SelfUpdateChecks();
        await InstallChecks();
        ManifestChecks();
        await ToolchainChecks();
        RegistryChecks();
        SettingsChecks();
        ProgressChecks();
        CommandChecks();
        UpdatePolicyChecks();
        CommandLineChecks();
        SavesChecks.Run(Folder("saves"));
        HookshotChecks(fixtures);
        Utf8Checks();
        await ScreenChecks();
        checks += SavesChecks.Count;
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
        forge.Add("v3.1.0-rc.1", key, files: Builder("v3.1.0-rc.1"));
        forge.Add("v3.1.0-rc.2", key, draft: true, files: Builder("v3.1.0-rc.2"));
        forge.Add("nightly-9-aaaaaaaa", key, files: Builder("nightly-9-aaaaaaaa"));
        forge.Add("nightly-12-bbbbbbbb", key, files: Builder("nightly-12-bbbbbbbb"));
        forge.Add("tools", null);
        forge.Add("v2.9.0", key, prerelease: false, files: Builder("v2.9.0"));
        Require(await longshot.NewestAsync("stable") == "v3.0.0" && await longshot.NewestAsync("rc") == "v3.1.0-rc.1"
                && await longshot.NewestAsync("nightly") == "nightly-12-bbbbbbbb",
                "Each channel's newest: stable, the newest release candidate (not a draft), the nightly by its count; never 2.x or tools.");
        var rc = (await longshot.ChannelAsync("rc"))!;
        Require(rc.Builder["linux-x64"].Size == image.Length && rc.Builder["linux-x64"].Sha256 == ShaOf(image) && rc.Tools.ContainsKey("windows-x64")
                && rc.Commit == Commit && rc.Notes == "https://git.magicbean.dev/MagicBeanDev/Longshot/releases/tag/v3.1.0-rc.1",
                "A release's files, with the SHA-256 its signed SHA256SUMS gives.");
        Require((await longshot.BuilderAsync("v3.1.0-rc.1")).Url.EndsWith("/Longshot-v3.1.0-rc.1-x86_64.AppImage", StringComparison.Ordinal),
                "The Linux builder of a version.");
        forge.Add("v3.1.0", key, files: Builder("v3.1.0"));
        Require(await longshot.NewestAsync("rc") == "v3.1.0" && await longshot.NewestAsync("stable") == "v3.1.0", "rc offers a stable once it is out.");
        // (pulling a bad version: deleted, or back to a draft, its channel falls back by itself)
        forge.Remove("v3.1.0");
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
        await ThrowsAsync<InvalidDataException>(() => new Releases(forge.Server, Platform.Linux, Array.Empty<ReleaseKey>(), NoWait).VersionAsync("v3.1.0-rc.1"),
                                                "A launcher with no key took a release.");
        await ThrowsAsync<FetchException>(() => longshot.VersionAsync("v3.5.0"), "A version with no release.");
        forge.Add("v3.3.0", stranger, files: Builder("v3.3.0"));
        var fresh = new Releases(forge.Server, Platform.Linux, keys, NoWait, Product.Longshot);
        Require(await fresh.ChannelAsync("stable") == null && fresh.Problem!.Contains("not signed by a key"),
                "The newest stable, refused, offers nothing, and says why.");
        forge.Remove("v3.3.0");
        // (a file of the release that SHA256SUMS does not name is not taken, nor one that is not this system's)
        forge.Add("v3.4.0-rc.1", key, files: Builder("v3.4.0-rc.1"), sums: s => string.Join("", s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.EndsWith("x86_64.AppImage", StringComparison.Ordinal)).Select(l => l + "\n")));
        await ThrowsAsync<InvalidDataException>(() => new Releases(forge.Server, Platform.Linux, keys, NoWait).BuilderAsync("v3.4.0-rc.1"),
                                                "A file the signed SHA256SUMS does not name was taken.");
        // (the server: tried twice, then nothing; the window says the check failed, quietly)
        var tries = 0;
        var flaky = new FakeFetcher();
        flaky.Texts[longshot.ListUrl(1)] = () => ++tries < 2 ? throw new FetchException("authored", 502) : "[]";
        Require(await new Releases(flaky, Platform.Linux, keys, NoWait).NewestAsync("stable") == null && tries == 2, "A server error is tried again.");
        Require(await new Releases(new FakeFetcher(), Platform.Linux, keys, NoWait).ChannelAsync("stable") == null, "No answer, no channel.");

        // (the launcher's own releases: its own versions, from v1.0.0)
        var own = new FakeForgejo(Product.Launcher);
        var launcher = new Releases(own.Server, Platform.Linux, keys, NoWait, Product.Launcher);
        own.Add("v1.0.0", key, files: LauncherFiles("v1.0.0"));
        own.Add("v1.1.0-rc.1", key, files: LauncherFiles("v1.1.0-rc.1"));
        own.Add("nightly-30-cccccccc", key, files: LauncherFiles("nightly-30-cccccccc"));
        var stable = (await launcher.ChannelAsync("stable"))!;
        Require(stable.Version == "v1.0.0" && stable.Launcher["linux-x64"].Url.EndsWith("/LongshotLauncher-v1.0.0-x86_64.AppImage", StringComparison.Ordinal)
                && stable.Launcher["windows-x64"].Url.EndsWith("/LongshotLauncher-v1.0.0-windows-x64-setup.exe", StringComparison.Ordinal)
                && stable.Launcher[ReleaseManifest.WindowsPortable].Url.EndsWith("/LongshotLauncher-v1.0.0-windows-x64-portable.zip", StringComparison.Ordinal)
                && stable.Builder.Count == 0 && await launcher.NewestAsync("rc") == "v1.1.0-rc.1"
                && await launcher.NewestAsync("nightly") == "nightly-30-cccccccc", "The launcher's own three channels, and its three files.");
        // (an update check reads each product's release list once, whatever the number of channels)
        own.Server.Asked.Clear();
        var three = await new Releases(own.Server, Platform.Linux, keys, NoWait, Product.Launcher).ChannelsAsync(Versions.Channels);
        Require(three["stable"]?.Version == "v1.0.0" && three["rc"]?.Version == "v1.1.0-rc.1" && three["nightly"]?.Version == "nightly-30-cccccccc"
                && own.Server.Asked.Count(u => u.Contains("/releases?", StringComparison.Ordinal)) == 1, "Three channels from one fetch of the list.");
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
                && SelfUpdate.UpdateTo("v1.0.0", R("v1.1.0-rc.1"), Platform.Linux, "rc") == "v1.1.0-rc.1",
                "A newer launcher on its channel.");
        Require(SelfUpdate.UpdateTo("v1.0.1", R("v1.0.0"), Platform.Linux) == null && SelfUpdate.UpdateTo("v1.0.0", R("v1.0.0"), Platform.Linux) == null
                && SelfUpdate.UpdateTo("dev", R("v1.0.1"), Platform.Linux, "stable") == null
                && SelfUpdate.UpdateTo("nightly-2041-aaaaaaaa", R("v1.0.1"), Platform.Linux) == null
                && SelfUpdate.UpdateTo("v1.0.0", R("v1.0.1", new Dictionary<string, FileRef> { ["windows-x64"] = both["windows-x64"] }), Platform.Linux) == null
                && SelfUpdate.UpdateTo("v1.0.0", null, Platform.Linux) == null && SelfUpdate.UpdateTo("v1.1.0-rc.2", R("v1.1.0-rc.1"), Platform.Linux, "rc") == null,
                "Never an older or the same launcher on its channel, never a build from a checkout, never one for the other system.");
        Require(SelfUpdate.UpdateTo("v1.0.0", R("nightly-2042-bbbbbbbb"), Platform.Linux, "nightly") == "nightly-2042-bbbbbbbb"
                && SelfUpdate.UpdateTo("nightly-2041-aaaaaaaa", R("v1.0.0"), Platform.Linux, "stable") == "v1.0.0",
                "The channel the player chose: its newest, across the nightly and the stables.");
        Require(SelfUpdate.ChannelOf("v1.2.0-rc.1", null) == "rc" && SelfUpdate.ChannelOf("dev", null) == "stable"
                && SelfUpdate.ChannelOf("v1.0.0", "nightly") == "nightly" && SelfUpdate.ChannelOf("v1.0.0", "bogus") == "stable",
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
        Require(SelfUpdate.Compare("v1.1.0", "v1.0.0") == 1 && SelfUpdate.Compare("v1.0.0", "v1.1.0-rc.1") == -1
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

    // ---- the parts lists (LONGSHOT_LAUNCHER_PARTS, a list made on this PC; releases go through tools.lock) ----

    const string Sha = "552191b5754c49a5a20723276a6fc56541ea1922c4c50b7b669022333fc1b40f";

    static void ManifestChecks()
    {
        var setup = PartsManifest.ParseSetup($"""
            tag v3.3.0
            part {Sha} 100 . - - https://example.invalid/LongshotBuilder-app-552191b5754c.zip
            part {Sha} 191037046 LongshotBuilder/tools llvm-mingw-20261104-ucrt-x86_64 llvm-mingw https://github.com/mstorsjo/llvm-mingw/releases/download/20261104/llvm-mingw-20261104-ucrt-x86_64.zip
            run LongshotBuilder/LongshotBuilder.exe
            """);
        Require(setup.Tag == "v3.3.0" && setup.Run == "LongshotBuilder/LongshotBuilder.exe" && setup.Parts.Count == 2, "The setup list's lines.");
        Require(setup.Parts[0].Dest == "." && setup.Parts[0].From == null && setup.Parts[1].From == "llvm-mingw-20261104-ucrt-x86_64"
                && setup.Parts[1].To == "llvm-mingw" && setup.Parts[1].Size == 191037046, "The setup list's part fields.");
        foreach (var bad in new[]
                 {
                     $"tag v1\npart {Sha} 1 . - - https://x/y\n",                                   // (no run)
                     $"tag v1\nrun a\n",                                                              // (no part)
                     $"tag ..\nrun a\npart {Sha} 1 . - - https://x/y\n",
                     $"tag v1\nrun a\npart {Sha.ToUpperInvariant()} 1 . - - https://x/y\n",
                     $"tag v1\nrun a\npart {Sha} 1 . - - http://x/y\n",
                     $"tag v1\nrun a\npart {Sha} 1 ../up - - https://x/y\n",
                     $"tag v1\nrun a\npart {Sha} 1 . a/b - https://x/y\n",
                     $"tag v1\nrun /abs\npart {Sha} 1 . - - https://x/y\n",
                     $"tag v1\nrun a\npart {Sha} -1 . - - https://x/y\n",
                     $"tag v1\nrun a\npart {Sha} 1 . - https://x/y\n",
                 })
            Throws<FormatException>(() => PartsManifest.ParseSetup(bad), "A setup list the setup refuses was read: " + bad);
        Require(PartsManifest.ParseSetup($"# note\n\ntag v1\nrun a\nfuture line\npart {Sha} 1 . - - https://x/y\n").Parts.Count == 1,
                "The setup reads past lines it does not know.");

        var linux = PartsManifest.ParseLinux($"tag nightly-2029-c544828f\npart {Sha} 10 usr/share/longshot https://example.invalid/a.zip\ntree\n" +
                                             $"part {Sha} 20 usr/python https://example.invalid/b.zip\nfile bin/python3\nfile lib/x.py\n");
        Require(linux.Kind == ListKind.Linux && linux.Parts[0].Files == null && linux.Parts[1].Files!.SequenceEqual(new[] { "bin/python3", "lib/x.py" }),
                "The Linux list's tree and file lines.");
        foreach (var bad in new[]
                 {
                     $"tag v1\nfile x\n",
                     $"tag v1\npart {Sha} 10 a https://x/a.zip\nunknown\n",
                     $"part {Sha} 10 a https://x/a.zip\n",
                     $"tag v1\npart {Sha} 10 a https://x/a.zip extra\n",
                     $"tag v1\npart {Sha} 10 /abs https://x/a.zip\n",
                     $"tag v1\npart {Sha} 10 a/../.. https://x/a.zip\n",
                     $"tag v1\npart {Sha} 10 a https://x/a.zip\nfile ../x\n",
                     $"tag v1\npart {Sha} 10 a file:///tmp/a.zip\n",
                     $"tag v1\npart {Sha} 10 a https://x/a.zip\ntree\nfile x\n",
                 })
            Throws<FormatException>(() => PartsManifest.ParseLinux(bad), "A list linux_update.parse_list refuses was read: " + bad);
        Require(PartsManifest.ParseLinux($"tag v1\npart {Sha} 10 a file:///tmp/a.zip\n", local: true).Parts.Count == 1, "A local list's file link.");
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


    static string Line(FakeFetcher f, string url, byte[] bytes, string dest)
    {
        f.Files[url] = bytes;
        return $"part {ShaOf(bytes)} {bytes.Length} {dest} {url}";
    }

    static async Task ToolchainChecks()
    {
        var root = Folder("toolchains");
        var gh = new FakeFetcher();
        var tools = Zip(("nx2/make_game.py", "print('authored')\n", null, 0x1ED), ("nx2/link.py", null, "make_game.py", 0),
                        ("README", "authored\n", null, 0x1A4));
        var python = Zip(("bin/python3", "#!/bin/sh\n", null, 0x1ED));
        var python2 = Zip(("bin/python3", "#!/bin/sh\n# 2\n", null, 0x1ED));
        var sysroot = Zip(("lib/libc.so.6", "authored", null, 0x1A4), ("lib64/ld.so", null, "/lib/libc.so.6", 0));
        var v1 = PartsManifest.ParseLinux("tag v3.3.0\n" +
                                          Line(gh, "https://x/tools.zip", tools, "usr/share/longshot") + "\ntree\n" +
                                          Line(gh, "https://x/python.zip", python, "usr/python") + "\nfile bin/python3\n" +
                                          Line(gh, "https://x/sysroot.zip", sysroot, Toolchains.SysrootPart) + "\ntree\n");
        var chains = new Toolchains(root, gh);
        var seen = new List<double>();
        var t1 = await chains.InstallAsync(v1, p => seen.Add(p.Fraction));
        Require(File.ReadAllText(t1.MakeGame) == "print('authored')\n" && t1.Python == Path.Combine(root, "v3.3.0", "usr", "python", "bin", "python3")
                && File.Exists(t1.Python), "The tools are where make_game.py and Python are looked for.");
        Require(seen.Count > 3 && seen.Zip(seen.Skip(1)).All(p => p.Second >= p.First) && Math.Abs(seen[^1] - 1) < 1e-9,
                "The install's progress goes up to 1.");
        Require(new FileInfo(Path.Combine(t1.ToolsRoot, "nx2", "link.py")).LinkTarget == "make_game.py", "A part's relative link.");
        var rebased = new FileInfo(Path.Combine(root, "v3.3.0", "usr", "lib", "longshot", "sysroot", "lib64", "ld.so"));
        Require(rebased.LinkTarget == "../lib/libc.so.6", "A sysroot's rooted link is rebased inside it: " + rebased.LinkTarget);
        if (!OperatingSystem.IsWindows())
            Require((File.GetUnixFileMode(t1.MakeGame) & UnixFileMode.UserExecute) != 0
                    && (File.GetUnixFileMode(Path.Combine(t1.ToolsRoot, "README")) & (UnixFileMode.UserExecute | UnixFileMode.OtherWrite)) == 0,
                    "The parts' modes: executables stay so, nothing is world-writable.");
        Require(chains.IsInstalled("v3.3.0") && !Directory.Exists(Path.Combine(root, "v3.3.0.part")), "Installed, and no staging left.");

        // (the next version: the unchanged parts come from v3.3.0, only the changed one is downloaded)
        gh.Asked.Clear();
        var v2 = PartsManifest.ParseLinux("tag v3.3.1\n" +
                                          Line(gh, "https://x/tools.zip", tools, "usr/share/longshot") + "\ntree\n" +
                                          Line(gh, "https://x/python2.zip", python2, "usr/python") + "\nfile bin/python3\n" +
                                          Line(gh, "https://x/sysroot.zip", sysroot, Toolchains.SysrootPart) + "\ntree\n");
        var t2 = await chains.InstallAsync(v2);
        Require(gh.Asked.SequenceEqual(new[] { "https://x/python2.zip" }), "Unchanged parts were downloaded again: " + string.Join(", ", gh.Asked));
        Require(File.ReadAllText(t2.MakeGame) == "print('authored')\n" && File.ReadAllText(t2.Python).Contains("# 2")
                && new FileInfo(Path.Combine(t2.ToolsRoot, "nx2", "link.py")).LinkTarget == "make_game.py"
                && new FileInfo(Path.Combine(root, "v3.3.1", "usr", "lib", "longshot", "sysroot", "lib64", "ld.so")).LinkTarget == "../lib/libc.so.6",
                "A kept part is the same files, links included.");
        Require(File.ReadAllText(t1.Python) == "#!/bin/sh\n", "The older version kept its own Python.");

        // (a part whose bytes differ from its list: nothing is left, the version is not installed)
        var python3 = Zip(("bin/python3", "#!/bin/sh\n# 3\n", null, 0x1ED));
        var tampered = PartsManifest.ParseLinux("tag v3.4.0\n" + Line(gh, "https://x/tools.zip", tools, "usr/share/longshot") + "\ntree\n" +
                                                $"part {ShaOf(python3)} {python3.Length} usr/python https://x/python3.zip\nfile bin/python3\n");
        gh.Files["https://x/python3.zip"] = Zip(("bin/python3", "#!/bin/sh\n# 4\n", null, 0x1ED));
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(tampered), "A part with another checksum was installed.");
        Require(!Directory.Exists(Path.Combine(root, "v3.4.0")) && !Directory.Exists(Path.Combine(root, "v3.4.0.part"))
                && !Directory.EnumerateFiles(root, "*.zip.part", SearchOption.AllDirectories).Any(), "A failed install left files.");
        gh.Files["https://x/python3.zip"] = python3;
        var longer = PartsManifest.ParseLinux("tag v3.4.0\n" + $"part {ShaOf(python3)} {python3.Length - 1} usr/python https://x/python3.zip\nfile bin/python3\n");
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(longer), "A part larger than its list was installed.");
        Require(!Directory.Exists(Path.Combine(root, "v3.4.0.part")), "A part too large left its staging.");
        // (a zip with a name outside its folder, or that its list does not name)
        var evil = Zip(("../evil", "x", null, 0x1A4));
        var slip = PartsManifest.ParseLinux("tag v3.4.0\n" + Line(gh, "https://x/evil.zip", evil, "usr/share/longshot") + "\ntree\n");
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(slip), "A zip's ../ name was unpacked.");
        Require(!File.Exists(Path.Combine(root, "evil")) && !File.Exists(Path.Combine(root, "v3.4.0.part", "usr", "share", "evil")), "../evil was written.");
        var unlisted = PartsManifest.ParseLinux("tag v3.4.0\n" + Line(gh, "https://x/tools.zip", tools, "usr/share/longshot") + "\nfile nx2/make_game.py\n");
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(unlisted), "A zip's file its list does not name was unpacked.");
        var outward = Zip(("x", null, "../../../../outside", 0));
        var outwardList = PartsManifest.ParseLinux("tag v3.4.0\n" + Line(gh, "https://x/out.zip", outward, "usr/share/longshot") + "\ntree\n");
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(outwardList), "A link out of the version's folder was made.");
        var absolute = Zip(("x", null, "/etc/passwd", 0));
        var absoluteList = PartsManifest.ParseLinux("tag v3.4.0\n" + Line(gh, "https://x/abs.zip", absolute, "usr/share/longshot") + "\ntree\n");
        await ThrowsAsync<InvalidDataException>(() => chains.InstallAsync(absoluteList), "An absolute link outside the sysroot was made.");
        Require(!Directory.Exists(Path.Combine(root, "v3.4.0")) && !Directory.Exists(Path.Combine(root, "v3.4.0.part")), "A refused part left files.");

        // (cancelled half way through a download: nothing is left)
        using (var cancel = new CancellationTokenSource())
        {
            var big = Zip(("nx2/make_game.py", string.Concat(Enumerable.Range(0, 200).Select(_ => Guid.NewGuid().ToString())), null, 0x1A4));
            var cancelled = PartsManifest.ParseLinux("tag v3.5.0\n" + Line(gh, "https://x/big.zip", big, "usr/share/longshot") + "\ntree\n");
            gh.DuringDownload = n => { if (n > 100) cancel.Cancel(); };
            await ThrowsAsync<OperationCanceledException>(() => chains.InstallAsync(cancelled, null, cancel.Token), "The cancel was not heard.");
            gh.DuringDownload = null;
            Require(!Directory.Exists(Path.Combine(root, "v3.5.0")) && !Directory.Exists(Path.Combine(root, "v3.5.0.part"))
                    && !Directory.EnumerateFiles(root, "*.zip.part", SearchOption.AllDirectories).Any(), "A cancelled install left files.");
        }

        // (Windows' list: dest . with the zip's LongshotBuilder/ folder, and llvm-mingw's top folder renamed)
        var app = Zip(("LongshotBuilder/LongshotBuilder.exe", "MZ", null, 0x1A4), ("LongshotBuilder/tools/nx2/make_game.py", "w", null, 0x1A4),
                      ("LongshotBuilder/tools/python/python.exe", "MZ", null, 0x1A4));
        var llvm = Zip(("llvm-mingw-20261104-ucrt-x86_64/bin/clang.exe", "MZ", null, 0x1A4));
        var setupLines = "tag v3.3.0-rc.2\n" + Line(gh, "https://x/app.zip", app, ".").Replace(" . ", " . - - ") + "\n" +
                         $"part {ShaOf(llvm)} {llvm.Length} LongshotBuilder/tools llvm-mingw-20261104-ucrt-x86_64 llvm-mingw https://x/llvm.zip\n" +
                         "run LongshotBuilder/LongshotBuilder.exe\n";
        gh.Files["https://x/llvm.zip"] = llvm;
        var w = await chains.InstallAsync(PartsManifest.ParseSetup(setupLines));
        Require(w.ToolsRoot == Path.Combine(root, "v3.3.0-rc.2", "LongshotBuilder", "tools") && File.Exists(w.MakeGame)
                && w.Python == Path.Combine(w.ToolsRoot, "python", "python.exe")
                && File.Exists(Path.Combine(w.ToolsRoot, "llvm-mingw", "bin", "clang.exe")), "The Windows list's layout.");
        Require(chains.Find("v3.3.0-rc.2")?.Kind == ListKind.Setup, "The record says which list made it.");

        Require(chains.Installed().SequenceEqual(new[] { "v3.3.0", "v3.3.0-rc.2", "v3.3.1" }), "The installed versions.");
        Directory.CreateDirectory(Path.Combine(root, "v9.part"));
        var removed = chains.Prune(new[] { "v3.3.1" });
        Require(chains.Installed().SequenceEqual(new[] { "v3.3.1" }) && removed.Contains("v3.3.0") && removed.Contains("v9.part"),
                "Prune keeps the versions builds use and removes the rest.");
        Require(File.ReadAllText(Path.Combine(root, "v3.3.1", "usr", "share", "longshot", "nx2", "make_game.py")) == "print('authored')\n",
                "A version that shared files with a removed one keeps them.");
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
        var b = r.Add(Path.Combine(home, "b"), "v3.3.1-rc.1", "rc");
        var c = r.Add(Path.Combine(home, "c"), "nightly-2031-abcdef01", null, name: "Nightly");
        Require(r.Default == a && r.Builds.Count(x => x.Default) == 1, "The first build is the default.");
        Require(a.Channel == "stable" && b.Channel == "rc" && c.Channel == "nightly" && c.Name == "Nightly" && a.Name == "a",
                "A channel defaults to the version's track.");
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
    }

    static void SettingsChecks()
    {
        var path = Path.Combine(Folder("settings"), "settings.json");
        var d = LauncherSettings.Load(path);
        Require(d.MoveSaves && d.PrepareShaders && !d.Dlss && d.DefaultChannel == "stable" && d.MemoryGb == null, "The defaults.");
        var s = new LauncherSettings
        {
            Nup = "/n.nup", Language = "de", Dlss = true, DlssLicense = true, Fsr = true, FsrLicense = true, PrepareShaders = false,
            MemoryGb = 16, BuildWorkers = 6, Work = "/w", DefaultChannel = "nightly", MoveSaves = false,
        };
        s.Save(path);
        var t = LauncherSettings.Load(path);
        Require(t.Nup == "/n.nup" && t.Language == "de" && t.Dlss && t.DlssLicense && t.Fsr && t.FsrLicense && !t.PrepareShaders && t.MemoryGb == 16
                && t.BuildWorkers == 6 && t.Work == "/w" && t.DefaultChannel == "nightly" && !t.MoveSaves, "settings.json round trip.");
        File.WriteAllText(path, """{"dlss":true,"dlssLicense":false,"defaultChannel":"beta","memoryGb":0}""");
        var u = LauncherSettings.Load(path);
        Require(!u.Dlss && u.DefaultChannel == "stable" && u.MemoryGb == null, "Saved values outside their range are not taken.");
        File.WriteAllText(path, "nope");
        Require(LauncherSettings.Load(path).PrepareShaders, "Unreadable settings are the defaults.");
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
        Require(Math.Abs(p.Overall - 0.04) < 1e-9, "The tools' download fills its weight.");
        foreach (var line in transcript)
        {
            p.Feed(line);
            bars.Add(p.Overall);
            if (line.StartsWith("@@eta")) Require(p.Eta == 120, "The ETA passes through.");
            if (line.StartsWith("@@file c.c")) Require(p.Stages.First(s => s.Id == "compile").Note == "2 of 3 changed files", "The compile's note.");
        }
        Require(bars.Zip(bars.Skip(1)).All(x => x.Second >= x.First), "The bar went back: " + string.Join(" ", bars.Select(b => b.ToString("0.000"))));
        Require(bars[^1] == 1 && p.Finished && p.Folder == "/authored/game" && p.Stages.All(s => s.State == StageState.Done), "The bar ends full.");
        Require(Math.Abs(bars[bars.Count - 3] - 0.96) < 1e-9, "The shaders' half: 0.92 + 0.08 / 2.");

        var q = new BuildProgress(download: false, shaders: true);
        q.Feed("@@stage read x");
        Require(q.Overall == 0 && q.Current == "read", "With no download, the read starts at 0.");
        q.Feed("@@stage compile x");
        Require(Math.Abs(q.Overall - (0.22 - 0.04) / 0.96) < 1e-9, "The weights scale to the stages a build has.");
        q.Feed("the shaders were not prepared now (\"Prepare shaders now\" is unticked): the game prepares them at its first start");
        q.Feed("@@done /g");
        var shaders = q.Stages.First(s => s.Id == "shaders");
        Require(q.Overall == 1 && shaders.State == StageState.Skipped && shaders.Note!.Contains("unticked"), "Shaders left to the first start.");
        var r = new BuildProgress(download: false, shaders: true);
        r.Feed("@@stage finish x");
        r.Feed("@@done /g");
        Require(r.Stages.First(s => s.Id == "shaders").State == StageState.Skipped, "No @@stage shaders is no check mark.");
        var e = new BuildProgress(download: false, shaders: false, update: true);
        e.Feed("@@stage read x");
        e.Feed("@@error No space left");
        Require(e.Error == "No space left" && !e.Finished && e.Overall < 1 && e.Stages[0].Title.Contains("kept"), "A failed update.");
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
        var licenceless = MakeGameCommand.For(win, request with { DlssLicense = false, FsrLicense = false, Jobs = null, MemoryGb = null, PrepareShaders = true },
                                              Platform.Windows, home, "L", _ => false);
        Require(!licenceless.Arguments.Any(a => a.StartsWith("--fetch") || a.StartsWith("--accept") || a == "--fsr" || a == "--no-prepare-shaders")
                && licenceless.Environment["LONGSHOT_NGX_SDK"] == null && licenceless.Environment["LONGSHOT_FFX_SDK"] == null
                && !licenceless.Environment.ContainsKey("LONGSHOT_JOBS") && licenceless.Environment["LONGSHOT_MEM_LIMIT_GB"] == null,
                "No license, no DLSS or FSR; Automatic sets nothing.");

        var lin = new Toolchain("v3.3.0", Path.Combine(home.Toolchains, "v3.3.0"), ListKind.Linux, null);
        var l = MakeGameCommand.For(lin, request with { Source = new BuildSource.Folders("/d/romfs", "/d/exefs"), Out = "/g", Work = "/w" },
                                    Platform.Linux, home, "/L/Longshot.AppImage");
        Require(l.FileName == Path.Combine(lin.Folder, "usr", "python", "bin", "python3") && l.WorkingDirectory == Path.Combine(lin.Folder, "usr", "share", "longshot"),
                "Linux: the AppImage layout's Python.");
        Require(l.Arguments.SequenceEqual(new[]
                {
                    "-I", lin.MakeGame, "--romfs", "/d/romfs", "--exefs", "/d/exefs", "--out", "/g", "--language", "fr", "--work", "/w",
                    "--jobs", "6", "--no-prepare-shaders", "--fetch-ngx-sdk", "--accept-nvidia-license",
                }), "Linux arguments (no FSR): " + string.Join(" ", l.Arguments));
        Require(!l.Environment.ContainsKey("LONGSHOT_JOBS") && l.Environment["LONGSHOT_MEM_LIMIT_GB"] == "16"
                && l.Environment["LONGSHOT_VK_PREBUILT"] == null && l.Environment["LONGSHOT_TOOLS"] == lin.ToolsRoot
                && l.Environment["LONGSHOT_SYSROOT"] == Path.Combine(lin.LibRoot, "sysroot") && l.Environment["EMSDK"] == Path.Combine(lin.LibRoot, "emsdk")
                && l.Environment["LONGSHOT_FFX_SDK"] == null, "Linux environment (DLSS builds its own Vulkan back end).");
        var u = MakeGameCommand.For(lin, new BuildRequest(new BuildSource.Update("/g"), null), Platform.Linux, home, "/L");
        Require(u.Arguments.SequenceEqual(new[] { "-I", lin.MakeGame, "--update", "/g", "--work", home.Work })
                && u.Environment["LONGSHOT_VK_PREBUILT"] == Path.Combine(lin.LibRoot, "longshot_vk.so"), "An update: the folder, no --out or --language.");
        Throws<ArgumentOutOfRangeException>(() => MakeGameCommand.For(lin, request with { Jobs = 0 }, Platform.Linux, home, "/L"), "Zero workers.");
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
        var play = GamePlay.StartInfo("/g", Platform.Linux, env);
        Require(play.FileName == Path.Combine("/g", "bin", "longshot") && play.WorkingDirectory == "/g" && !play.Environment.ContainsKey("APPDIR"),
                "Play starts bin/longshot in its folder.");
    }

    static void UpdatePolicyChecks()
    {
        Require(UpdatePolicy.UpdateTo("v3.3.0", "stable", "v3.3.1") == "v3.3.1", "release: the newer release.");
        Require(UpdatePolicy.UpdateTo("v3.3.1", "stable", "v3.3.1") == null, "release: up to date.");
        Require(UpdatePolicy.UpdateTo("v3.4.0-rc.1", "stable", "v3.3.1") == null, "release: an RC newer than the release is not taken back.");
        Require(UpdatePolicy.UpdateTo("v3.4.0-rc.1", "rc", "v3.4.0-rc.2") == "v3.4.0-rc.2", "rc: the next RC.");
        Require(UpdatePolicy.UpdateTo("v3.4.0-rc.2", "rc", "v3.4.0") == "v3.4.0", "rc: its release.");
        Require(UpdatePolicy.UpdateTo("v3.4.0-rc.2", "rc", "v3.3.9") == null, "rc: never back to an older release.");
        Require(UpdatePolicy.UpdateTo("v3.3.0", "rc", "v3.4.0-rc.1") == "v3.4.0-rc.1", "A release on the rc channel: the next RC.");
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
        Require(UpdatePolicy.ChannelOf(null, "v3.4.0-rc.1") == "rc" && UpdatePolicy.ChannelOf("nightly", "v3.4.0") == "nightly", "A build's channel.");
        var b = new BuildEntry { Version = "v3.4.0-rc.1", Channel = "rc" };
        Require(UpdatePolicy.UpdateTo(b, new Dictionary<string, string?> { ["stable"] = "v3.5.0", ["rc"] = "v3.4.0-rc.3" }) == "v3.4.0-rc.3"
                && UpdatePolicy.UpdateTo(b, new Dictionary<string, string?> { ["stable"] = "v3.5.0" }) == null, "Each build asks its own channel.");
    }

    static void CommandLineChecks()
    {
        StartRequest P(params string[] a) => StartRequest.Parse(a);
        Require(P() is { Kind: StartKind.Window, Folder: null, Problem: null }, "No arguments: the window.");
        Require(P("--launch", "/g") is { Kind: StartKind.Launch, Folder: "/g" }, "--launch");
        Require(P("--update", "/g", "--tag", "v3.3.0", "--fetched") is { Kind: StartKind.Update, Folder: "/g", Tag: "v3.3.0", Fetched: true, Problem: null },
                "--update --tag --fetched");
        Require(P("--update", "C:\\Games\\Longshot é") is { Kind: StartKind.Update, Tag: null, Fetched: false }, "--update alone");
        Require(P("--play") is { Kind: StartKind.Play, Folder: null }, "--play: the default build");
        Require(P("--play", "/g") is { Kind: StartKind.Play, Folder: "/g" }, "--play <folder>");
        Require(P("--launch") is { Kind: StartKind.Window, Problem: not null }, "--launch with no folder");
        Require(P("--update", "--tag", "v1") is { Problem: not null }, "--update with no folder");
        Require(P("--tag", "../x", "--update", "/g") is { Tag: null, Problem: not null }, "A tag that is no version name");
        Require(P("--play", "/g", "--update", "/h") is { Problem: not null }, "Two at once");
        Require(P("--launch", "/g", "--tag", "v1") is { Problem: not null }, "--tag with --launch");
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
