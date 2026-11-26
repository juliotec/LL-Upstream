using LongshotLauncher.Core;

// The Pull request track (docs/pull-requests.md): the names, Beanstalk's list, the track's versions, the build from source (the
// newest nightly's builder's from-source: SourceBuilds), and the home and its warnings as the source has them
static partial class Program
{
    const string HeadA = "1a2b3c4d5e6f708192a3b4c5d6e7f80910111213", HeadB = "bbbbbbbb5e6f708192a3b4c5d6e7f80910111213";

    static PullRequest Pr(int number, string head, bool bigbean = false, bool approved = false, bool draft = false,
                          string title = "fix(audio): the thing", params string[] labels) =>
        new(number, title, "someone", head, draft, labels, bigbean, approved, approved ? new[] { "maint1" } : Array.Empty<string>());

    // ---- versions built from source: a pull request's and main's tags, and SourceBuilds, the one place that builds them

    static void SourceBuildChecks()
    {
        var sha = "4f2c9e1a7b3d5f6081a2b3c4d5e6f708192a3b4c";
        Require(Versions.MainTag(sha) == "main-4f2c9e1a" && Versions.MainOf("main-4f2c9e1a") == "4f2c9e1a" && Versions.MainOf("main-4F2C9E1A") == null
                && Versions.TrackOf("main-4f2c9e1a") == Versions.PullRequest && Versions.Shown("main-4f2c9e1a") == "main \u00b7 4f2c9e1a"
                && Versions.IsSourceBuild("main-4f2c9e1a") && Versions.IsSourceBuild("pr-12-4f2c9e1a") && !Versions.IsSourceBuild("v4.0.0"),
                "main-<sha8> is a build from source, on the pull requests' track, as pr-<n>-<sha8> is.");
        Require(SourceBuilds.ParseHead("{\"name\": \"main\", \"commit\": {\"id\": \"" + sha.ToUpperInvariant() + "\"}}") == sha
                && SourceBuilds.ParseHead("{\"commit\": {\"id\": \"abc\"}}") == null && SourceBuilds.ParseHead("<html>") == null,
                "main's newest commit from git.magicbean.dev's branch answer; anything else is none.");
        static Task<PullRequest?> None(int _, CancellationToken __) => Task.FromResult<PullRequest?>(null);
        Require(SourceBuilds.CommitAsync(new Releases(new FakeFetcher(), Platform.Linux, []), "main-4f2c9e1a", sha, None).GetAwaiter().GetResult() == sha,
                "main's build is of the commit Add version read.");
        var moved = new FakeFetcher();
        moved.Texts[SourceBuilds.MainUrl] = () => "{\"commit\": {\"id\": \"aaaaaaaa" + sha[8..] + "\"}}";
        var threw = "";
        try { SourceBuilds.CommitAsync(new Releases(moved, Platform.Linux, []), "main-4f2c9e1a", null, None).GetAwaiter().GetResult(); }
        catch (InvalidDataException e) { threw = e.Message; }
        Require(threw.Contains("moved on", StringComparison.Ordinal), "main moved past the tag's commit since: said, nothing built: " + threw);
        var draft = new PullRequest(7, "WIP: a change", "x", sha, true, [], false, false, []);
        Require(PullRequests.Problem(draft, Platform.Linux) == null, "A WIP pull request can be built (after its warnings).");
    }

    static async Task PullRequestChecks(string repo)
    {
        SourceBuildChecks();
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
        Require(PullRequests.Problem(list[0], Platform.Windows) != null && PullRequests.Problem(list[1], Platform.Linux) == null
                && PullRequests.Problem(list[2], Platform.Linux) == null,
                "One for the other system cannot be built here; a draft (WIP) can, after its warnings, which say it is one.");

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
        Require(rows.Select(r => r.Tag).SequenceEqual(new[] { "pr-1556-11111111", "pr-1600-1a2b3c4d", "pr-1598-1a2b3c4d", "pr-1596-bbbbbbbb" })
                && rows[0].Installed && !rows[1].Installed,
                "The track lists the installed pull requests first (an older push stays its build's version), then the trusted open ones and " +
                "the ones found by number, a trusted draft too; never one for the other system, or an untrusted one nobody looked for.");
        Require(VersionPick.OfPullRequests(builds, null, Platform.Linux).Select(r => r.Tag).SequenceEqual(new[] { "pr-1556-11111111" }),
                "Before Beanstalk answers: only the installed ones.");
        Require(VersionPick.Plan(Versions.PullRequest, "pr-1600-1a2b3c4d", builds) is { Action: PickAction.Replace, Older: false } p && p.Build == mine
                && VersionPick.Plan(Versions.PullRequest, "pr-1600-1a2b3c4d", new[] { night }).Action == PickAction.Build
                && VersionPick.Plan(Versions.PullRequest, "pr-1556-11111111", builds).Action == PickAction.Play,
                "Another pull request is built in the place of the one installed (switching); none installed, in a folder of its own.");

        // ---- the build: from source, by the newest nightly's builder's from-source (SourceBuilds)
        var sourceVersions = Folder("pr-from-source");
        var sourceChains = new Toolchains(sourceVersions, Platform.Linux);
        var sourceReleases = new Releases(new FakeFetcher(), Platform.Linux, [])
        {
            // (nightly-501 is make_game.py's, no builder manifest: the newest nightly with a builder is nightly-500)
            Listed = [new ListedRelease("nightly-501-bbbbbbbb", false, true, null, null, "c", []),
                      new ListedRelease("nightly-500-aaaaaaaa", false, true, null, null, "c", [(LongshotBuildRelease.ManifestName("nightly-500-aaaaaaaa", "linux-x64"), "https://e/m", 10)]),
                      new ListedRelease("v4.0.0", false, false, null, null, "c", [])],
        };
        async Task<string> Refusal(string tag, PullRequest? listed)
        {
            try { await SourceBuilds.EnsureAsync(sourceChains, sourceReleases, tag, null, (_, _) => Task.FromResult(listed)); }
            catch (InvalidDataException e) { return e.Message; }
            return "";
        }
        if (!OperatingSystem.IsWindows())
        {
            var made = Path.Combine(sourceVersions, "pr-222-1a2b3c4d");
            var nightlyBuilder = FakeBuilder(Path.Combine(sourceVersions, "nightly-500-aaaaaaaa"), "nightly-500-aaaaaaaa", $$"""
                echo '{"v":1,"type":"stage","stage":"rust","text":"Rust"}'
                echo '{"v":1,"type":"progress","stage":"rust","fraction":0.5,"overall":0.05}'
                mkdir -p '{{made}}'
                cp "$0" '{{made}}/Longshot.x86_64'
                echo '{"v":1,"commit":"{{HeadA}}","version":"pr-222-1a2b3c4d","signed":false}' > '{{made}}/source.json'
                echo '{"v":1,"type":"from_source","folder":"{{made}}","program":"{{made}}/Longshot.x86_64","commit":"{{HeadA}}","version":"pr-222-1a2b3c4d","signed":false,"downloaded":1}'
                echo '{"v":1,"type":"done","folder":"{{made}}","seconds":1,"compile_seconds":1,"previous_seconds":null}'
                """);
            var seen = new List<ToolsDownloadProgress>();
            var said = new List<string>();
            var built = await SourceBuilds.EnsureAsync(sourceChains, sourceReleases, "pr-222-1a2b3c4d", null, (_, _) => Task.FromResult<PullRequest?>(Pr(222, HeadA)),
                                                       "/c/longshot-builder", seen.Add, said.Add);
            Require(File.ReadAllLines(Path.Combine(sourceVersions, "nightly-500-aaaaaaaa", "args.txt")).SequenceEqual(new[]
                    {
                        "from-source", "--commit", HeadA, "--out", made, "--name", "pr-222-1a2b3c4d", "--cache", "/c/longshot-builder", "--output", "json",
                    }) && built.BuilderExe == Path.Combine(made, "Longshot.x86_64") && sourceChains.Find("pr-222-1a2b3c4d") != null
                    && seen.Any(p => Math.Abs(p.Fraction - 0.05) < 1e-9) && seen[^1].Fraction == 1 && said.Any(l => l.Contains("rust: Rust", StringComparison.Ordinal)),
                    "A pull request's build: the newest nightly's builder's from-source --commit <its head> --out <its version folder> --name <its tag> --cache <the launcher's>, "
                    + "its progress said: " + string.Join(" | ", said));
            ScriptBuilder(nightlyBuilder, "echo 'longshot-builder: unknown command: from-source' >&2\nexit 2");
            Require(await Refusal("pr-223-1a2b3c4d", Pr(223, HeadA)) == SourceBuilds.NotYet("pr-223-1a2b3c4d"),
                    "A nightly's builder from before from-source: not yet, said plainly.");
            ScriptBuilder(nightlyBuilder, "echo '{\"v\":1,\"type\":\"error\",\"code\":\"source_download_failed\",\"text\":\"the archive could not be downloaded\"}'\nexit 1");
            Require(await Refusal("pr-223-1a2b3c4d", Pr(223, HeadA)) == "the archive could not be downloaded" && sourceChains.Find("pr-223-1a2b3c4d") == null,
                    "from-source's error: its words, nothing installed.");
        }
        Require((await Refusal("pr-222-1a2b3c4d", Pr(222, HeadB))).Contains("newer push", StringComparison.Ordinal)
                && (await Refusal("pr-222-1a2b3c4d", null)).Contains("#222", StringComparison.Ordinal),
                "A newer push, or a pull request no longer open, is said as such first.");

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
