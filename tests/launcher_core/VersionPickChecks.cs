using LongshotLauncher.Core;

/// The home's Track and Version dropdowns (Core/VersionPick.cs, ReleaseManifest.OnChannel): a track's versions from the release
/// list the update check read, which of them are installed, and what the main button does for the one picked
static partial class Program
{
    static void VersionPickChecks()
    {
        // (the server's order is newest published first, but not always the version order: a fix of an older stable after a newer
        // one; the old release candidates the server still has are on no channel)
        var list = ReleaseManifest.ParseList(Json(
            ("nightly-189-aa95d35f", false, true, ""),
            ("nightly-188-a00c485c", false, true, ""),
            ("nightly-190-cccccccc", true, true, "a draft CI is still making"),
            ("v3.1.0-rc.2", false, true, ""),
            ("nightly-182-5274fb72", false, true, ""),
            ("v3.0.2", false, false, "a fix of v3.0, after v3.1.0"),
            ("v3.1.0", false, false, ""),
            ("v3.0.1", false, false, ""),
            ("v3.1.0-rc.1", false, true, ""),
            ("v3.0.0", false, false, ""),
            ("v3.0.0-rc.4", false, true, ""),
            ("nightly-188-a00c485c", false, true, "listed twice"),
            ("v2.9.0", false, false, "2.x: no version of Longshot")));
        string Tags(IEnumerable<ListedRelease> rs) => string.Join(" ", rs.Select(r => r.Tag));
        var p = Product.Longshot;

        Require(Tags(ReleaseManifest.OnChannel(list, "nightly", p)) == "nightly-189-aa95d35f nightly-188-a00c485c nightly-182-5274fb72",
                "A channel's releases, newest first: the nightlies, no draft, a tag listed twice once.");
        Require(Tags(ReleaseManifest.OnChannel(list, "stable", p)) == "v3.1.0 v3.0.2 v3.0.1 v3.0.0",
                "The stable channel: the stables only, in version order, not the server's; never an old release candidate.");
        Throws<ArgumentException>(() => ReleaseManifest.OnChannel(list, "rc", p), "rc was taken for a channel.");
        foreach (var c in Versions.Channels)
            Require(ReleaseManifest.Newest(list, c, p) == ReleaseManifest.OnChannel(list, c, p)[0].Tag, $"{c}'s newest is the first of its releases.");
        Require(ReleaseManifest.OnChannel(Array.Empty<ListedRelease>(), "nightly", p).Count == 0
                && ReleaseManifest.Newest(Array.Empty<ListedRelease>(), "nightly", p) == null, "No release: nothing on the channel.");

        var t0 = DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        BuildEntry Build(string id, string? version, string channel, bool isDefault = false, int day = 0) =>
            new() { Id = id, Version = version, Channel = channel, Default = isDefault, Updated = t0.AddDays(day) };
        string Marks(IEnumerable<TrackVersion> vs) =>
            string.Join(" ", vs.Select(v => v.Tag + (v.Installed ? "+" : "") + (v.Newest ? "*" : "")));

        var nightly = Build("n", "nightly-188-a00c485c", "nightly", isDefault: true);
        var stable = Build("s", "v3.0.0", "stable", day: 1);
        var builds = new[] { nightly, stable };

        // ---- the versions of a track ----
        Require(Marks(VersionPick.Of("nightly", builds, list, p)) == "nightly-189-aa95d35f* nightly-188-a00c485c+ nightly-182-5274fb72",
                "A track's versions: the server's, newest first and marked, the installed one marked.");
        Require(Marks(VersionPick.Of("stable", builds, list, p)) == "v3.1.0* v3.0.2 v3.0.1 v3.0.0+", "The stable track: the stables, v3.0.0 installed.");
        Require(Marks(VersionPick.Of("nightly", builds, null, p)) == "nightly-188-a00c485c+",
                "No answer from the server yet: the installed versions only, none called the newest.");
        Require(Marks(VersionPick.Of("stable", new[] { nightly }, null, p)) == "", "No answer and nothing installed on the track: no versions.");
        var gone = Build("old", "nightly-150-0caf0618", "nightly", day: 2);
        Require(Marks(VersionPick.Of("nightly", new[] { nightly, gone }, list, p))
                == "nightly-189-aa95d35f* nightly-188-a00c485c+ nightly-182-5274fb72 nightly-150-0caf0618+",
                "An installed nightly the server no longer keeps is still listed, in its place in the order.");
        var dev = Build("d", "dev", "nightly");
        Require(Marks(VersionPick.Of("nightly", new[] { dev }, list, p)) == "nightly-189-aa95d35f* nightly-188-a00c485c nightly-182-5274fb72 dev+",
                "A version on no order (dev) comes after the ones the order ranks.");
        Require(VersionPick.Of("stable", new[] { Build("r", "v3.0.1", "") }, list, p).Single(v => v.Installed).Tag == "v3.0.1",
                "A build with no channel of its own is on its version's track (stable).");
        Require(VersionPick.Of("stable", new[] { Build("r", "v3.1.0-rc.1", "rc") }, list, p).Single(v => v.Installed).Tag == "v3.1.0-rc.1",
                "A build of an old release candidate, on rc, is on the stable track (rc is no channel now).");
        var twice = new[] { nightly, Build("n2", "nightly-188-a00c485c", "nightly", day: 5) };
        Require(VersionPick.Of("nightly", twice, list, p).Count(v => v.Tag == "nightly-188-a00c485c") == 1
                && VersionPick.Of("nightly", twice, list, p).Single(v => v.Installed).Build!.Id == "n",
                "A version built twice is one row, and its build is the default one.");

        // ---- what the main button does ----
        var play = VersionPick.Plan("nightly", "nightly-188-a00c485c", builds);
        Require(play is { Action: PickAction.Play, Build.Id: "n" }, "An installed version: Play starts its build.");
        var newer = VersionPick.Plan("nightly", "nightly-189-aa95d35f", builds);
        Require(newer is { Action: PickAction.Replace, Build.Id: "n", Older: false }, "A newer version of a track with a build: an update in its place.");
        var back = VersionPick.Plan("nightly", "nightly-182-5274fb72", builds);
        Require(back is { Action: PickAction.Replace, Build.Id: "n", Older: true }, "An older version of a track with a build: going back, in its place.");
        Require(VersionPick.Plan("stable", "v3.1.0", new[] { nightly }) is { Action: PickAction.Build, Build: null },
                "A track with no build: a new build of the version picked.");
        Require(VersionPick.Plan("stable", "v3.0.1", builds) is { Action: PickAction.Replace, Build.Id: "s", Older: false },
                "Each track replaces its own build, never another track's.");
        var two = new[] { nightly, gone };
        Require(VersionPick.Plan("nightly", "nightly-189-aa95d35f", two).Build!.Id == "n",
                "Two builds on a track: a pick replaces the default one when the player chose none.");
        Require(VersionPick.Plan("nightly", "nightly-189-aa95d35f", two, chosen: gone).Build!.Id == "old",
                "Two builds on a track: a pick replaces the one the player selected.");
        Require(VersionPick.Plan("nightly", "nightly-189-aa95d35f", two, chosen: stable).Build!.Id == "n",
                "A selected build on another track is not the one replaced.");
        var notDefault = new[] { stable, Build("s2", "v3.0.1", "stable", day: 4), Build("s0", "v3.0.0-rc.4", "stable", day: 3) };
        Require(VersionPick.Plan("stable", "v3.1.0", notDefault).Build!.Id == "s2", "No default on the track: the last built is replaced.");
        Require(VersionPick.Plan("nightly", "nightly-189-aa95d35f", new[] { dev }) is { Action: PickAction.Replace, Older: false },
                "A build on no order (dev) is replaced as an update, not called a way back.");
    }
}
