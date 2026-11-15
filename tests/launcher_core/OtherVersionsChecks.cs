using LongshotLauncher.Core;

/// Other versions (Core/OtherVersions.cs, ReleaseManifest.OnChannel): the versions a build can be changed to, from the release
/// list the update check read
static partial class Program
{
    static void OtherVersionsChecks()
    {
        // (the server's order is newest published first, but not always the version order: an rc made after a stable's fix)
        var list = ReleaseManifest.ParseList(Json(
            ("nightly-189-aa95d35f", false, true, ""),
            ("nightly-188-a00c485c", false, true, ""),
            ("nightly-190-cccccccc", true, true, "a draft CI is still making"),
            ("v3.1.0-rc.2", false, true, ""),
            ("nightly-182-5274fb72", false, true, ""),
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
        Require(Tags(ReleaseManifest.OnChannel(list, "rc", p)) == "v3.1.0-rc.2 v3.1.0-rc.1 v3.0.1 v3.0.0 v3.0.0-rc.4",
                "The rc channel: its candidates and the stables, in version order, not the server's.");
        Require(Tags(ReleaseManifest.OnChannel(list, "stable", p)) == "v3.0.1 v3.0.0", "The stable channel: the stables only.");
        foreach (var c in Versions.Channels)
            Require(ReleaseManifest.Newest(list, c, p) == ReleaseManifest.OnChannel(list, c, p)[0].Tag, $"{c}'s newest is the first of its releases.");
        Require(ReleaseManifest.OnChannel(Array.Empty<ListedRelease>(), "nightly", p).Count == 0
                && ReleaseManifest.Newest(Array.Empty<ListedRelease>(), "nightly", p) == null, "No release: nothing on the channel.");

        var nightly = new BuildEntry { Version = "nightly-188-a00c485c", Channel = "nightly" };
        var others = OtherVersions.Of(nightly, list, p);
        Require(string.Join(" ", others.Select(o => o.Tag + (o.Older ? "<" : ">"))) == "nightly-189-aa95d35f> nightly-182-5274fb72<",
                "A nightly build's other versions: the channel's newer and older nightlies, not its own.");
        Require(OtherVersions.Of(new BuildEntry { Version = "v3.0.1", Channel = "stable" }, list, p).Select(o => (o.Tag, o.Older))
                    .SequenceEqual(new[] { ("v3.0.0", true) }), "A stable build goes back to the stable before it, not to a release candidate.");
        Require(string.Join(" ", OtherVersions.Of(new BuildEntry { Version = "v3.1.0-rc.1", Channel = "" }, list, p).Select(o => o.Tag))
                == "v3.1.0-rc.2 v3.0.1 v3.0.0 v3.0.0-rc.4", "A build with no channel of its own follows its version's track (rc).");
        Require(OtherVersions.Of(new BuildEntry { Version = "dev", Channel = "nightly" }, list, p).All(o => !o.Older)
                && OtherVersions.Of(new BuildEntry { Version = "dev", Channel = "nightly" }, list, p).Count == 3,
                "A build on no track (dev): every version of its channel, none of them called older.");
        Require(OtherVersions.Of(nightly, null, p).Count == 0, "No answer from the server yet: no other versions.");
        Require(OtherVersions.Of(new BuildEntry { Version = "nightly-182-5274fb72", Channel = "nightly" },
                                 ReleaseManifest.ParseList(Json(("nightly-182-5274fb72", false, true, ""))), p).Count == 0,
                "A channel with only the build's own version: nothing to change to.");
    }
}
