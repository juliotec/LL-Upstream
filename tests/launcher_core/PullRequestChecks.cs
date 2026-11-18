using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LongshotLauncher.Core;

// The Pull request track (docs/pull-requests.md): the names, Beanstalk's list, the track's versions, the source
// archive and the toolchain made from it, and the home and its warnings as the source has them
static partial class Program
{
    const string HeadA = "1a2b3c4d5e6f708192a3b4c5d6e7f80910111213", HeadB = "bbbbbbbb5e6f708192a3b4c5d6e7f80910111213";

    static PullRequest Pr(int number, string head, bool bigbean = false, bool approved = false, bool draft = false,
                          string title = "fix(audio): the thing", params string[] labels) =>
        new(number, title, "someone", head, draft, labels, bigbean, approved, approved ? new[] { "maint1" } : Array.Empty<string>());

    static async Task PullRequestChecks(string repo)
    {
        // ---- names ----
        Require(Versions.PullRequestOf("pr-222-1a2b3c4d") == (222, "1a2b3c4d") && Versions.PullRequestOf("pr-0-1a2b3c4d") == null
                && Versions.PullRequestOf("pr-222-1A2B3C4D") == null && Versions.PullRequestOf("pr-222-1a2b3c4") == null
                && Versions.PullRequestOf("nightly-2029-c544828f") == null && Versions.PullRequestOf(null) == null,
                "A pull request's build is pr-<number>-<8 lowercase hex>, its number from 1.");
        Require(Versions.PullRequestTag(222, HeadA) == "pr-222-1a2b3c4d" && Versions.TrackOf("pr-222-1a2b3c4d") == Versions.PullRequest
                && Versions.Shown("pr-222-1a2b3c4d") == "PR #222 · 1a2b3c4d" && !Versions.IsChannel(Versions.PullRequest)
                && Versions.IsTrack(Versions.PullRequest) && Versions.IsTrack(Versions.Nightly) && !Versions.IsTrack("Longshot"),
                "pr-222-1a2b3c4d is on the pr track, shown \"PR #222 · 1a2b3c4d\"; pr is a track but not a channel.");
        Require(!Versions.Newer("pr-222-bbbbbbbb", "pr-222-1a2b3c4d") && !Versions.Newer("pr-222-1a2b3c4d", "nightly-2029-c544828f")
                && !Versions.Newer("nightly-2029-c544828f", "pr-222-1a2b3c4d"), "A pull request's build is ordered against nothing.");
        Require(LauncherSettings.TrackFolder(Versions.PullRequest) == "pr" && InstanceFolders.TrackName("pr") && InstanceFolders.TrackName("pr (2)")
                && LauncherSettings.FolderFor("/b", Versions.PullRequest, "pr-222-1a2b3c4d", _ => false) == Path.Combine("/b", "pr", "pr-222-1a2b3c4d"),
                "A pull request's build is built in builds/pr/pr-<number>-<commit>, renamed as it moves to another push or pull request.");

        // ---- updates ----
        Require(UpdatePolicy.UpdateTo("pr-222-1a2b3c4d", "pr", "pr-222-bbbbbbbb") == "pr-222-bbbbbbbb", "A pull request's build: its newer push.");
        Require(UpdatePolicy.UpdateTo("pr-222-1a2b3c4d", "pr", "pr-223-bbbbbbbb") == null, "Never another pull request's build.");
        Require(UpdatePolicy.UpdateTo("pr-222-1a2b3c4d", "nightly", "nightly-2031-bbbbbbbb") == null
                && UpdatePolicy.UpdateTo("nightly-2029-aaaaaaaa", "nightly", "pr-222-1a2b3c4d") == null
                && UpdatePolicy.UpdateTo("dev", "stable", "pr-222-1a2b3c4d") == null,
                "No channel's version updates a pull request's build, and no pull request's build updates any other.");

        // ---- the number the player names ----
        foreach (var (text, number) in new (string, int?)[]
                 {
                     ("332", 332), (" #332 ", 332), ("https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/332", 332),
                     ("git.magicbean.dev/MagicBeanDev/Longshot/pulls/332/files", 332), ("https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/332#issuecomment-1", 332),
                     ("https://git.magicbean.dev/magicbeandev/longshot/pulls/332?tab=x", 332), ("0", null), ("", null), ("abc", null),
                     ("https://git.magicbean.dev/MagicBeanDev/LongshotLauncher/pulls/332", null), ("https://github.com/MagicBeanDev/Longshot/pulls/332", null),
                     ("https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/332x", null), ("1234567890", null),
                 })
            Require(PullRequests.NumberOf(text) == number, $"NumberOf({text}) is {number?.ToString() ?? "null"}.");
        Require(PullRequests.IsDraft("WIP: x") && PullRequests.IsDraft(" [wip] x") && PullRequests.IsDraft("x", true) && !PullRequests.IsDraft("fix: WIP: x"),
                "A draft is Forgejo's mark or a WIP: title, as Longshot's CI reads it.");

        // ---- Beanstalk's list ----
        var json = $$"""
            {"generated_at": "2026-11-18T07:00:00Z", "pulls": [
              {"number": 1558, "title": "perf: faster", "author": "mooki", "head": "{{HeadA.ToUpperInvariant()}}", "draft": false,
               "labels": ["platform/linux"], "updated_at": "x", "author_bigbean": true, "approved": false, "approved_by": []},
              {"number": 1557, "title": "WIP: not yet", "author": "rando", "head": "{{HeadB}}", "draft": false, "labels": [],
               "author_bigbean": false, "approved": false, "approved_by": []},
              {"number": 1556, "title": "fix: x", "author": "rando", "head": "{{HeadB}}", "draft": false, "labels": [],
               "author_bigbean": false, "approved": true, "approved_by": ["maint1", 3]},
              {"number": 1555, "title": "no head", "author": "rando", "head": "abc"},
              {"number": "1554", "head": "{{HeadA}}"},
              {"number": 1558, "title": "twice", "head": "{{HeadB}}"},
              "nonsense"
            ]}
            """;
        var list = PullRequests.Parse(json);
        Require(list.Count == 3 && list[0] is { Number: 1558, AuthorBigBean: true, Approved: false, Trusted: true, Draft: false } a
                && a.Head == HeadA && a.Tag == "pr-1558-1a2b3c4d" && a.Labels.SequenceEqual(new[] { "platform/linux" })
                && list[1] is { Number: 1557, Draft: true, Trusted: false } && list[2] is { Approved: true, Trusted: true } c
                && c.ApprovedBy.SequenceEqual(new[] { "maint1" }),
                "Beanstalk's list: a BigBean's or an approved head is trusted, a WIP: title a draft; one with no 40-hex head, no number, or twice is left out.");
        foreach (var bad in new[] { "[]", "{}", "{\"pulls\": 3}", "<html>", "" })
            Require(Fails(() => PullRequests.Parse(bad)), $"Beanstalk's list refused: {bad}.");
        Require(list[0].For(Platform.Linux) && !list[0].For(Platform.Windows) && list[1].For(Platform.Windows)
                && Pr(1, HeadA, labels: new[] { "platform/windows", "platform/linux" }).For(Platform.Windows)
                && !Pr(1, HeadA, labels: new[] { "platform/steam-deck" }).For(Platform.Windows),
                "A pull request labelled for the other system only is not for this PC; one for both, or neither, is.");
        Require(PullRequests.Problem(list[0], Platform.Windows) != null && PullRequests.Problem(list[1], Platform.Linux) != null
                && PullRequests.Problem(list[2], Platform.Linux) == null, "A draft, or one for the other system, cannot be built here.");

        var server = new FakeFetcher();
        server.Texts[PullRequests.ListUrl] = () => json;
        Require((await PullRequests.ListAsync(server))?.Count == 3 && server.Asked.SequenceEqual(new[] { PullRequests.ListUrl })
                && PullRequests.ListUrl == "https://beanstalk.magicbean.dev/api/v1/public/pulls",
                "The list is one question, to Beanstalk, not to git.magicbean.dev.");
        Require(await PullRequests.ListAsync(new FakeFetcher()) == null, "No answer: no list, and the update check goes on.");
        server.Texts[PullRequests.ListUrl] = () => "<html>error</html>";
        Require(await PullRequests.ListAsync(server) == null, "An error page is no list.");

        // ---- the track's versions ----
        var mine = new BuildEntry { Id = "p", Name = "pr", Folder = "/b/pr/pr-1556-11111111", Version = "pr-1556-11111111", Channel = Versions.PullRequest };
        var night = new BuildEntry { Id = "n", Name = "n", Folder = "/b/nightly/x", Version = "nightly-2029-c544828f", Channel = Versions.Nightly };
        var builds = new[] { mine, night };
        var pulls = new[]
        {
            Pr(1600, HeadA, bigbean: true), Pr(1599, HeadA), Pr(1598, HeadA, approved: true, draft: true),
            Pr(1556, HeadB, approved: true), Pr(1597, HeadB, approved: true, labels: new[] { "platform/windows" }), Pr(1596, HeadB),
        };
        var rows = VersionPick.OfPullRequests(builds, pulls, Platform.Linux, new HashSet<int> { 1596 });
        Require(rows.Select(r => r.Tag).SequenceEqual(new[] { "pr-1556-11111111", "pr-1600-1a2b3c4d", "pr-1596-bbbbbbbb" })
                && rows[0].Installed && !rows[1].Installed,
                "The track lists the installed pull requests first (an older push stays its build's version), then the trusted open ones and " +
                "the ones found by number; never a draft, one for the other system, or an untrusted one nobody looked for.");
        Require(VersionPick.OfPullRequests(builds, null, Platform.Linux).Select(r => r.Tag).SequenceEqual(new[] { "pr-1556-11111111" }),
                "Before Beanstalk answers: only the installed ones.");
        Require(VersionPick.Plan(Versions.PullRequest, "pr-1600-1a2b3c4d", builds) is { Action: PickAction.Replace, Older: false } p && p.Build == mine
                && VersionPick.Plan(Versions.PullRequest, "pr-1600-1a2b3c4d", new[] { night }).Action == PickAction.Build
                && VersionPick.Plan(Versions.PullRequest, "pr-1556-11111111", builds).Action == PickAction.Play,
                "Another pull request is built in the place of the one installed (switching); none installed, in a folder of its own.");

        // ---- the source archive ----
        var scratch = Path.Combine(Path.GetTempPath(), "pr-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var source = new Dictionary<string, string>
            {
                ["nx2/make_game.py"] = "print('pr')\n", ["nx2/new.py"] = "new\n", ["third_party/zbic/zbic.c"] = "int x;\n",
                ["tools.lock"] = "", ["docs/x.md"] = "docs\n", ["scripts/run.sh"] = "#!/bin/sh\n",
            };
            byte[] Archive(string? commit, Action<TarWriter>? more = null, IReadOnlyDictionary<string, string>? files = null)
            {
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
                {
                    if (commit != null) tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = commit }));
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "longshot/"));
                    foreach (var (path, text) in files ?? source)
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "longshot/" + path)
                        {
                            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                            Mode = path.EndsWith(".sh", StringComparison.Ordinal) ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100,
                        });
                    more?.Invoke(tar);
                }
                return ms.ToArray();
            }
            var files = PullRequestSource.Files(Archive(HeadA), HeadA);
            Require(files.Count == source.Count && files.Single(f => f.Path == "scripts/run.sh").Executable && !files.Single(f => f.Path == "nx2/new.py").Executable,
                    "The archive's files, without its top folder, the run bit kept.");
            Require(Fails(() => PullRequestSource.Files(Archive(HeadB), HeadA)) && Fails(() => PullRequestSource.Files(Archive(null), HeadA)),
                    "An archive that is not of the commit asked for, or names none, is refused.");
            Require(Fails(() => PullRequestSource.Files(Archive(HeadA, t => t.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "longshot/link") { LinkName = "/etc/passwd" })), HeadA))
                    && Fails(() => PullRequestSource.Files(Archive(HeadA, files: new Dictionary<string, string> { ["../x"] = "x" }), HeadA))
                    && Fails(() => PullRequestSource.Files(new byte[] { 1, 2, 3 }, HeadA)),
                    "A link, a path out of its folder, or no archive at all is refused.");
            Require(PullRequestSource.ArchiveUrl(HeadA) == "https://git.magicbean.dev/api/v1/repos/MagicBeanDev/Longshot/archive/" + HeadA + ".tar.gz",
                    "The source is the archive of the exact commit, by its full hash.");

            // ---- the toolchain: the nightly's tools linked, the pull request's source over them ----
            var root = Path.Combine(scratch, "toolchains");
            var from = Path.Combine(root, "nightly-2029-c544828f");
            var share = Path.Combine(from, "usr", "share", "longshot");
            void Put(string rel, string text)
            {
                var path = Path.Combine(from, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            const string lockText = "tool sysroot 1 linux-x64 " + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + " 10 usr/lib/longshot/sysroot - https://x/s.zip\n";
            Put("usr/share/longshot/nx2/make_game.py", "print('nightly')\n");
            Put("usr/share/longshot/nx2/old.py", "old\n");
            Put("usr/share/longshot/scripts/run.sh", "old\n");
            Put("usr/share/longshot/third_party/zbic/zbic.c", "int y;\n");
            Put("usr/share/longshot/third_party/zbic/zbic.so", "built\n");
            Put("usr/share/longshot/tools.lock", lockText);
            Put("usr/share/longshot/VERSION", "nightly-2029-c544828f\n");
            Put("usr/python/bin/python3", "python\n");
            Put("usr/lib/longshot/sysroot/lib.a", "sysroot\n");
            Put("toolchain.json", """
                {"format": 1, "tag": "nightly-2029-c544828f", "kind": "Linux", "run": null, "parts": [
                  {"sha": "bbbb", "size": 5, "dest": ".", "url": "https://x/AppImage", "entries": []},
                  {"sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 10, "dest": "usr/lib/longshot", "from": null, "to": "sysroot",
                   "url": "https://x/s.zip", "entries": [{"path": "usr/lib/longshot/sysroot/lib.a", "link": null}]},
                  {"sha": "cccc", "size": 3, "dest": "usr/share/longshot", "url": "https://x/n.zip", "entries": [{"path": "usr/share/longshot/nx2/old.py", "link": null}]}
                ]}
                """);
            var chains = new Toolchains(root, new FakeFetcher());
            var nightly = chains.Find("nightly-2029-c544828f")!;
            var withLock = source.ToDictionary(kv => kv.Key, kv => kv.Key == "tools.lock" ? lockText : kv.Value);
            var srcFiles = PullRequestSource.Files(Archive(HeadA, files: withLock), HeadA);
            var made = chains.InstallPullRequest("pr-222-1a2b3c4d", nightly, srcFiles, Platform.Linux);
            var mshare = Path.Combine(made.Folder, "usr", "share", "longshot");
            Require(chains.Find("pr-222-1a2b3c4d") != null && File.ReadAllText(made.MakeGame) == "print('pr')\n"
                    && !File.Exists(Path.Combine(mshare, "nx2", "old.py")) && File.Exists(Path.Combine(mshare, "nx2", "new.py"))
                    && !Directory.Exists(Path.Combine(mshare, "docs")) && File.ReadAllText(Path.Combine(mshare, "VERSION")) == "pr-222-1a2b3c4d\n"
                    && File.ReadAllText(Path.Combine(mshare, "third_party", "zbic", "zbic.c")) == "int x;\n",
                    "The pull request's source replaces the nightly's (a file it removed is gone); its docs are left out; VERSION names it.");
            Require(File.ReadAllText(Path.Combine(share, "nx2", "make_game.py")) == "print('nightly')\n" && File.Exists(Path.Combine(share, "nx2", "old.py")),
                    "The nightly's tools are untouched.");
            var helper = Path.Combine(mshare, "third_party", "zbic", "zbic.so");
            File.WriteAllText(helper, "rebuilt\n");
            Require(File.ReadAllText(Path.Combine(share, "third_party", "zbic", "zbic.so")) == "built\n",
                    "A native helper the source lacks is copied, never linked: rebuilding it leaves the nightly's alone.");
            if (!OperatingSystem.IsWindows())
                Require((File.GetUnixFileMode(Path.Combine(mshare, "scripts", "run.sh")) & UnixFileMode.UserExecute) != 0,
                        "A script keeps its run bit.");
            var record = File.ReadAllText(Path.Combine(made.Folder, Toolchains.RecordName));
            Require(record.Contains("\"pr-222-1a2b3c4d\"", StringComparison.Ordinal) && record.Contains("usr/lib/longshot/sysroot/lib.a", StringComparison.Ordinal)
                    && !record.Contains("old.py", StringComparison.Ordinal) && !record.Contains("https://x/AppImage", StringComparison.Ordinal),
                    "Its record keeps the tools (reused by the next version) but no part a source file replaced, nor the builder.");
            Toolchains.DeleteTree(from);
            Require(File.ReadAllText(Path.Combine(made.Folder, "usr", "python", "bin", "python3")) == "python\n",
                    "The nightly's tools can be deleted afterwards (pruned): the pull request's keep theirs.");

            // (a pull request that changes the tools is refused, and leaves nothing)
            Toolchains.DeleteTree(made.Folder);
            Directory.CreateDirectory(from);
            foreach (var (rel, text) in new[] { ("usr/share/longshot/nx2/make_game.py", "n\n"), ("usr/share/longshot/tools.lock", lockText) }) Put(rel, text);
            Put("toolchain.json", "{\"format\": 1, \"tag\": \"nightly-2029-c544828f\", \"kind\": \"Linux\", \"parts\": []}");
            var other = withLock.ToDictionary(kv => kv.Key, kv => kv.Key == "tools.lock" ? lockText.Replace("aaaa", "abab") : kv.Value);
            Require(Fails(() => chains.InstallPullRequest("pr-222-1a2b3c4d", chains.Find("nightly-2029-c544828f")!,
                                                          PullRequestSource.Files(Archive(HeadA, files: other), HeadA), Platform.Linux))
                    && !Directory.Exists(chains.PathOf("pr-222-1a2b3c4d")) && !Directory.Exists(chains.PathOf("pr-222-1a2b3c4d") + ".part"),
                    "A pull request whose tools.lock differs from the nightly's is refused, and leaves nothing.");
        }
        finally
        {
            Toolchains.DeleteTree(scratch);
        }

        // ---- the home and its warnings, as the source has them ----
        var home = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "HomeViewModel.cs"));
        var vms = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "PullRequestViewModels.cs"));
        var service = File.ReadAllText(Path.Combine(repo, "App", "LauncherService.cs"));
        Require(home.Contains("AskPullRequest(p.Version, JobKind.Update, b);") && home.Contains("AskPullRequest(p.Version, JobKind.Build, null);")
                && home.Contains("AskPullRequest(to, JobKind.Update, b.Entry);")
                && System.Text.RegularExpressions.Regex.Matches(home + vms, @"StartWizard\([^)]*Versions\.PullRequest").Count == 2,
                "Every build of a pull request's new code goes through its warnings; only a rebuild of a push already built skips them.");
        Require(vms.Contains("if (build.Pr.Trusted) PullRequestConfirmViewModel.Start(main, build);")
                && vms.Contains("else main.Modal = new PullRequestConfirmViewModel(main, build);"),
                "A BigBean's or an approved pull request has one warning; any other a second one, to confirm.");
        Require(service.Contains("b => Versions.IsPullRequest(b.Version) ? null : UpdateFor(b)"),
                "Nothing of a pull request is fetched early, before its warnings.");
        Require(service.Contains("if (Registry.Builds.Any(b => Versions.IsPullRequest(b.Version))) await PullRequestsAsync(fresh: true, cancel);"),
                "The update check asks Beanstalk about pull requests only when one is installed, once for all of them.");
    }

    static bool Fails(Action a)
    {
        try { a(); return false; }
        catch (Exception e) when (e is InvalidDataException or FormatException or ArgumentException) { return true; }
    }
}
