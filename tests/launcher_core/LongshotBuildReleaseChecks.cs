using System.Security.Cryptography;
using System.Text;
using LongshotLauncher.Core;

/// A release of longshot-builder's kind (Core/LongshotBuildRelease.cs) and its install into a version folder (Core/Toolchains.cs):
/// the manifest read and refused, the builder's own fetch run with every installed version folder as a base (a fake builder
/// script here, saying fetch's events), its error said, and the first builder of a PC with none downloaded whole by the launcher,
/// checked against its manifest, with the record fetch writes
static partial class Program
{
    static string Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    static string Manifest(string version, string platform, string fullName, byte[] full, params (string From, byte[] Basis, string Name, byte[] Delta)[] deltas) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = 1, ["version"] = version, ["channel"] = Versions.TrackOf(version), ["platform"] = platform,
            ["full"] = new Dictionary<string, object> { ["name"] = fullName, ["size"] = full.Length, ["sha256"] = Hex(full) },
            ["deltas"] = deltas.Select(d => new Dictionary<string, object>
            {
                ["from"] = d.From, ["from_sha256"] = Hex(d.Basis), ["name"] = d.Name, ["size"] = d.Delta.Length, ["sha256"] = Hex(d.Delta),
            }).ToList(),
            ["stamps"] = new Dictionary<string, string> { ["compiler"] = "c0ffee", ["translator"] = "7a11" },
        });

    /// A version folder holding a builder that is a shell script, with fetch's record: the script writes its arguments, one a
    /// line, into args.txt beside it, then runs body
    static string FakeBuilder(string versionFolder, string version, string body)
    {
        Directory.CreateDirectory(versionFolder);
        var exe = Path.Combine(versionFolder, "Longshot.x86_64");
        ScriptBuilder(exe, "for a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$(dirname \"$0\")/args.txt\"\n" + body);
        ReleaseRecord(versionFolder, version);
        return exe;
    }

    /// A version folder's record as fetch writes it (Toolchains.ReleaseRecord), with its stamps
    static void ReleaseRecord(string versionFolder, string version, string translator = "7a11", string compiler = "c0ffee")
    {
        Directory.CreateDirectory(versionFolder);
        File.WriteAllText(Path.Combine(versionFolder, Toolchains.ReleaseRecord),
                          $$$"""{"v":1,"version":"{{{version}}}","channel":"{{{Versions.TrackOf(version)}}}","platform":"linux-x64","full":{"name":"f","size":1,"sha256":"{{{new string('a', 64)}}}"},"program":"Longshot.x86_64","stamps":{"compiler":"{{{compiler}}}","translator":"{{{translator}}}"}}""");
    }

    /// A builder that is a shell script running body
    static void ScriptBuilder(string exe, string body)
    {
        File.WriteAllText(exe, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// A version folder with an installed builder (a stand-in file) and fetch's record: Toolchains.Find's
    static Toolchain InstalledVersion(string versionsRoot, string tag, string translator = "7a11", string compiler = "c0ffee")
    {
        var folder = Path.Combine(versionsRoot, tag);
        ReleaseRecord(folder, tag, translator, compiler);
        File.WriteAllText(Path.Combine(folder, "Longshot.x86_64"), "builder");
        return new Toolchains(versionsRoot, Platform.Linux).Find(tag) ?? throw new Exception("The authored version was not found: " + tag);
    }

    static async Task LongshotBuildReleaseChecks()
    {
        const string v1 = "nightly-300-aaaaaaaa", v2 = "nightly-301-bbbbbbbb", v3 = "nightly-302-cccccccc", v4 = "nightly-303-dddddddd";
        var one = Encoding.UTF8.GetBytes("longshot-builder one " + new string('x', 300));
        var two = Encoding.UTF8.GetBytes("longshot-builder two " + new string('y', 300));

        // ---- the manifest: its full file and stamps (its deltas are the builder's)
        var deltaName = $"longshot-builder-{v1}-to-{v2}-linux-x64.zst-patch";
        var text = Manifest(v2, "linux-x64", "longshot-builder-" + v2 + "-linux-x64", two, (v1, one, deltaName, one));
        var m = LongshotBuildRelease.Parse(text, v2, "linux-x64");
        Require(m.Full.Name == $"longshot-builder-{v2}-linux-x64" && m.Full.Size == two.Length && m.Full.Sha256 == Hex(two)
                && m.Channel == "nightly" && m.Stamps["translator"] == "7a11" && m.Stamps["compiler"] == "c0ffee", "A manifest: its full file, its stamps.");
        foreach (var (bad, why) in new (string, string)[]
                 {
                     (text.Replace("\"v\":1", "\"v\":2"), "another format"),
                     (text, "another version's"),
                     (text.Replace("linux-x64\"", "windows-x64\""), "another system's"),
                     (text.Replace("\"channel\":\"nightly\"", "\"channel\":\"stable\""), "another channel's than its version's track"),
                     (text.Replace("\"channel\":\"nightly\",", ""), "no channel"),
                     (text.Replace(Hex(two), "abc"), "a SHA-256 that is not one"),
                     (text.Replace($"\"name\":\"longshot-builder-{v2}-linux-x64\"", $"\"name\":\"../longshot-builder-{v2}-linux-x64\""), "a name with a folder"),
                     ("{ not json", "not JSON"),
                 })
            Throws<InvalidDataException>(() => LongshotBuildRelease.Parse(bad, why == "another version's" ? v3 : v2, "linux-x64"), "A manifest refused: " + why);
        Require(LongshotBuildRelease.ManifestName(v2, "windows-x64") == $"{LongshotBuilder.Name}-{v2}-windows-x64.json", "The manifest's name.");
        // (the record the launcher writes for its first builder is fetch's: what the next fetch reads as a base)
        using (var record = System.Text.Json.JsonDocument.Parse(m.Record("Longshot.x86_64")))
        {
            var r = record.RootElement;
            Require(r.GetProperty("version").GetString() == v2 && r.GetProperty("platform").GetString() == "linux-x64"
                    && r.GetProperty("program").GetString() == "Longshot.x86_64" && r.GetProperty("full").GetProperty("sha256").GetString() == Hex(two)
                    && r.GetProperty("full").GetProperty("size").GetInt64() == two.Length && r.GetProperty("stamps").GetProperty("translator").GetString() == "7a11",
                    "The record is fetch's: its version, platform, program, full file and stamps.");
        }

        // ---- the first builder of a PC with none: downloaded whole by the launcher, checked by its manifest
        var home = new LauncherHome(Folder("lbr-home"));
        var versions = BuilderVersions.Root(home.Root);
        var chains = new Toolchains(versions, Platform.Linux, home.Toolchains);
        var fetcher = new FakeFetcher();
        const string server = "https://example.com/r/";
        ReleaseInfo Release(string tag, string manifest, params (string Name, byte[] Bytes)[] files)
        {
            var bytes = Encoding.UTF8.GetBytes(manifest);
            fetcher.Files[server + tag + ".json"] = bytes;
            foreach (var (n, b) in files) fetcher.Files[server + n] = b;
            return new ReleaseInfo("longshot", tag, "", null, null, new Dictionary<string, FileRef>(),
                                   new Dictionary<string, FileRef> { ["linux-x64"] = new(server + tag + ".json", Hex(bytes), bytes.Length) },
                                   files.ToDictionary(f => f.Name, f => (server + f.Name, (long)f.Bytes.Length)));
        }
        Require(chains.Fetcher() == null && chains.Installed().Count == 0, "A PC with no builder has none to fetch with.");
        var r1 = Release(v1, Manifest(v1, "linux-x64", "f1", one), ("f1", one));
        var log = new List<string>();
        var t1 = await chains.FetchAsync(_ => Task.FromResult(r1), fetcher, v1, log: log.Add);
        Require(File.ReadAllBytes(t1.BuilderExe).SequenceEqual(one) && t1.BuilderExe == Path.Combine(versions, v1, "Longshot.x86_64")
                && chains.Find(v1)?.BuilderExe == t1.BuilderExe && Toolchains.RecordedVersion(t1.Folder) == v1
                && !Directory.Exists(Path.Combine(t1.Folder, Toolchains.Staging)) && log.Single().Contains("downloaded whole", StringComparison.Ordinal),
                "No builder here: the full file, into its version folder as Longshot.x86_64, with fetch's record: " + string.Join(" | ", log));
        if (!OperatingSystem.IsWindows())
            Require((File.GetUnixFileMode(t1.BuilderExe) & UnixFileMode.UserExecute) != 0, "Executable on Linux.");
        Require(ShaderCaches.Stamp(t1) == "longshot-builder-7a11-c0ffee", "The shader caches' stamp is the record's.");
        // (its program gone (an antivirus's quarantine): not installed, so the next build fetches it)
        File.Move(t1.BuilderExe, t1.BuilderExe + ".gone");
        Require(chains.Find(v1) == null && !chains.IsInstalled(v1) && chains.Installed().Count == 0, "Its program gone: not installed.");
        File.Move(t1.BuilderExe + ".gone", t1.BuilderExe);
        Require(chains.Find(v1) != null && chains.Installed().SequenceEqual(new[] { v1 }), "Put back: installed.");
        // (a full file that is not the one the manifest names: refused, nothing installed, nothing left)
        var r2bad = Release(v2, Manifest(v2, "linux-x64", "f2", two), ("f2", one));
        await ThrowsAsync<InvalidDataException>(() => chains.BootstrapAsync(r2bad, fetcher), "A full file of other bytes is refused.");
        Require(chains.Find(v2) == null && !Directory.Exists(Path.Combine(versions, v2, Toolchains.Staging)), "Nothing left of a refused download.");
        // (a manifest that is not the one SHA256SUMS signs: refused)
        var r2 = Release(v2, Manifest(v2, "linux-x64", "f2", two), ("f2", two));
        await ThrowsAsync<InvalidDataException>(() => chains.BootstrapAsync(
            r2 with { BuilderManifests = new Dictionary<string, FileRef> { ["linux-x64"] = new(server + v2 + ".json", Hex(one), r2.BuilderManifests!["linux-x64"].Size) } },
            fetcher), "A manifest of other bytes is refused.");
        // (a release of make_game.py's kind: no manifest for this system)
        await ThrowsAsync<InvalidDataException>(() => chains.BootstrapAsync(r2 with { BuilderManifests = new Dictionary<string, FileRef>() }, fetcher),
                                                "A release with no builder for this system is refused.");

        if (!OperatingSystem.IsWindows())
        {
            // ---- a builder here: its own fetch, every version folder with a builder (and an older launcher's toolchains) its bases
            var fetchHome = new LauncherHome(Folder("lbr-fetch"));
            var fetchVersions = BuilderVersions.Root(fetchHome.Root);
            var fetching = new Toolchains(fetchVersions, Platform.Linux, fetchHome.Toolchains);
            var target = Path.Combine(fetchVersions, v3);
            FakeBuilder(Path.Combine(fetchVersions, v2), v2, $$"""
                echo '{"v":1,"type":"fetch_progress","step":"delta","hop":1,"hops":1,"bytes":50,"total":100}'
                echo '{"v":1,"type":"log","text":"made from the delta"}'
                mkdir -p '{{target}}'
                cp "$0" '{{target}}/Longshot.x86_64'
                printf '{"v":1,"version":"{{v3}}","platform":"linux-x64","full":{"sha256":"%s","size":1},"program":"Longshot.x86_64"}' {{new string('b', 64)}} > '{{target}}/longshot_release.json'
                echo '{"v":1,"type":"fetched","version":"{{v3}}","channel":"nightly","platform":"linux-x64","program":"{{target}}/Longshot.x86_64","how":"delta","from":"{{v2}}","through":[],"downloaded":2097152}'
                """);
            FakeBuilder(Path.Combine(fetchVersions, v1), v1, "exit 0");
            // (an older launcher's builder, in toolchains/<tag> with its record: a base too)
            var old = Path.Combine(fetchHome.Toolchains, "nightly-299-99999999");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "longshot-builder"), "old");
            File.WriteAllText(Path.Combine(old, Toolchains.LegacyRecord), """{"format":1,"tag":"nightly-299-99999999","kind":"LongshotBuild","run":"longshot-builder","sha256":"x"}""");
            Require(fetching.Fetcher()?.Tag == v2, "The newest installed builder fetches.");
            Require(fetching.Legacy("nightly-299-99999999")?.BuilderExe == Path.Combine(old, "longshot-builder"), "An older launcher's builder is read.");
            var seen = new List<ToolsDownloadProgress>();
            var said = new List<string>();
            var t3 = await fetching.FetchAsync(_ => throw new InvalidOperationException("no release read: the builder fetches"), new FakeFetcher(), v3, seen.Add, said.Add);
            var args = File.ReadAllLines(Path.Combine(fetchVersions, v2, "args.txt"));
            var from = args.Select((a, i) => (a, i)).Where(x => x.a == "--from").Select(x => args[x.i + 1]).ToHashSet();
            Require(args.Take(4).SequenceEqual(new[] { "fetch", v3, "--out", target }) && args[^2..].SequenceEqual(new[] { "--output", "json" })
                    && from.SetEquals(new[] { old, Path.Combine(fetchVersions, v1), Path.Combine(fetchVersions, v2) }),
                    "fetch <tag> --out <its version folder>, --from every installed version folder and an older launcher's toolchain, --output json: "
                    + string.Join(" ", args));
            Require(t3.BuilderExe == Path.Combine(target, "Longshot.x86_64") && fetching.Find(v3) != null
                    && seen.Any(p => p.Total == 100 && Math.Abs(p.Fraction - 0.5) < 1e-9) && seen[^1].Fraction == 1
                    && said.Any(l => l.Contains("made from the delta", StringComparison.Ordinal)) && said.Any(l => l.Contains("delta from " + v2, StringComparison.Ordinal)),
                    "The builder's fetch: its progress and log said, the version installed in its folder: " + string.Join(" | ", said));

            // (its error said in its words; a release of make_game.py's kind in the stable launcher's)
            var newest = Path.Combine(target, "Longshot.x86_64");
            ScriptBuilder(newest, "echo '{\"v\":1,\"type\":\"error\",\"code\":\"release_refused\",\"text\":\"not signed by a key it trusts\"}'\nexit 1");
            var refused = "";
            try { await fetching.FetchAsync(_ => throw new InvalidOperationException(), new FakeFetcher(), v4); }
            catch (InvalidDataException e) { refused = e.Message; }
            Require(refused == "not signed by a key it trusts" && fetching.Find(v4) == null, "fetch's error: its text: " + refused);
            ScriptBuilder(newest, "echo '{\"v\":1,\"type\":\"error\",\"code\":\"no_builder_in_release\",\"text\":\"x\"}'\nexit 1");
            try { await fetching.FetchAsync(_ => throw new InvalidOperationException(), new FakeFetcher(), "v3.0.0"); }
            catch (InvalidDataException e) { refused = e.Message; }
            Require(refused == OldVersions.NotBuiltHere("v3.0.0"), "A release of make_game.py's kind: the stable launcher's words: " + refused);
            // (a builder from before fetch: a command line it does not take, so the launcher downloads it whole)
            ScriptBuilder(newest, "echo 'longshot-builder: unknown command: fetch' >&2\nexit 2");
            var four = Encoding.UTF8.GetBytes("longshot-builder four");
            var r4 = Release(v4, Manifest(v4, "linux-x64", "f4", four), ("f4", four));
            said.Clear();
            var t4 = await fetching.FetchAsync(_ => Task.FromResult(r4), fetcher, v4, log: said.Add);
            Require(File.ReadAllBytes(t4.BuilderExe).SequenceEqual(four) && said.Any(l => l.Contains("has no fetch", StringComparison.Ordinal)),
                    "A builder with no fetch: the launcher downloads it whole: " + string.Join(" | ", said));

            // ---- an older launcher's toolchains go once no row needs them; the version folders are never pruned here
            var gone = fetching.Prune([]);
            Require(gone.SequenceEqual(new[] { "nightly-299-99999999" }) && !Directory.Exists(old) && fetching.Installed().Count == 4,
                    "A prune takes an older launcher's toolchains no row uses, never a version folder: " + string.Join(", ", gone));
        }

        // ---- the release: the manifest of each system, signed, and every file listed
        var listed = new ListedRelease(v3, false, false, null, null, "c", [($"longshot-builder-{v3}-linux-x64.json", "https://e/m", 10), ("f3", "https://e/f3", 3)]);
        var info = ReleaseManifest.Build(listed, new Dictionary<string, string> { [$"longshot-builder-{v3}-linux-x64.json"] = new string('a', 64) }, Product.Longshot);
        Require(info.BuilderManifests!.Keys.SequenceEqual(new[] { "linux-x64" }) && info.Assets!["f3"].Url == "https://e/f3",
                "A release with a manifest is longshot-builder's kind; its files are listed by name.");
        var unsigned = ReleaseManifest.Build(listed, new Dictionary<string, string>(), Product.Longshot);
        Require(unsigned.BuilderManifests!.Count == 0, "A manifest SHA256SUMS does not sign is not taken: a release of the old kind.");
        // (by the manifest's exact name; never a name merely holding "Builder", nor the name it had before Longshot renamed it)
        var named = new ListedRelease(v3, false, false, null, null, "c",
            [(LongshotBuildRelease.ManifestName(v3, "linux-x64"), "https://e/new", 10), ($"longshot-build-{v3}-windows-x64.json", "https://e/old", 10),
             ($"LongshotBuilder-{v3}-windows-x64.zip", "https://e/zip", 10), ($"longshot-builder-{v3}-linux-x64", "https://e/file", 10)]);
        var sums = named.Assets.ToDictionary(a => a.Name, _ => new string('b', 64));
        var both = ReleaseManifest.Build(named, sums, Product.Longshot);
        Require(both.BuilderManifests!["linux-x64"].Url == "https://e/new" && !both.BuilderManifests.ContainsKey("windows-x64"),
                "A manifest under the program's name only (no release ever shipped the old one).");
        var oldKind = ReleaseManifest.Build(named with { Assets = named.Assets.Where(a => !a.Name.EndsWith(".json", StringComparison.Ordinal)).ToList() }, sums, Product.Longshot);
        Require(oldKind.BuilderManifests!.Count == 0,
                "No manifest: the old kind, whatever names hold \"Builder\" (LongshotBuilder's zip, a lone longshot-builder file).");
        Require(LongshotBuilder.Name == "longshot-builder" && LongshotBuildRelease.ManifestName(v3, "linux-x64") == $"longshot-builder-{v3}-linux-x64.json",
                "The manifest's name.");
    }
}
