namespace LongshotLauncher.Core;

/// One version on a track, as the home's Version list shows it (VersionPick.Of): its tag, when it was published (null: not on
/// the server's list), whether it is the track's newest, and the build of it on this track (null: not installed).
/// NeedsNightly: the release list shows it is built with the new builder, which this launcher does not run
/// (NeedsNightlyLauncher)
public sealed record TrackVersion(string Tag, DateTimeOffset? Published, bool Newest, BuildEntry? Build, bool NeedsNightly = false)
{
    public bool Installed => Build != null;
}

/// What the home's main button does for the version picked (VersionPick.Plan)
public enum PickAction
{
    /// The version is installed on its track: Play starts that build
    Play,
    /// Another version of the track is installed: the picked one is built in its place, in its folder, as an update is
    /// (JobKind.Update; Older: going back)
    Replace,
    /// Nothing on the track yet: a new build of it, in a folder of its own (JobKind.Build)
    Build,
}

/// Build: the build Play starts (Play), or the one the version replaces (Replace); null for a new build
public sealed record PickPlan(PickAction Action, string Version, BuildEntry? Build, bool Older);

/// The home's Track and Version dropdowns: a track (stable, nightly) and one of its versions, and what building or playing
/// it means. One build per track is the rule, as Other versions had it: picking another version of a track that has a build
/// builds it in that build's place (in its folder, the row then that version), never a second folder. Building beside it is
/// still Build another version (ChannelPickerViewModel), so a track can hold several builds ("nightly (2)"): each of their
/// versions is marked installed, and the version a pick replaces is the build the player chose (the row selected) when it is on
/// the track, else the track's default, else its last built.
/// The versions are the ones the update check read (ReleaseManifest.OnChannel: every release the server
/// keeps, its newest 5 nightlies), and the installed ones the list lacks (no answer yet, or a nightly the server no longer keeps)
public static class VersionPick
{
    /// The track a build is on: the channel chosen for it, else its version's (UpdatePolicy.ChannelOf)
    public static string TrackOf(BuildEntry build) => UpdatePolicy.ChannelOf(build.Channel, build.Version);

    /// The builds on a track, in the list's order
    public static IReadOnlyList<BuildEntry> On(string track, IEnumerable<BuildEntry> builds) =>
        builds.Where(b => TrackOf(b) == track).ToList();

    /// A track's versions, newest first (Versions.Compare; a version on no order, such as a test build's "dev", after the
    /// listed ones). listed: the release list the update check read (Releases.Listed), null before it answered: then only the
    /// installed versions
    public static IReadOnlyList<TrackVersion> Of(string track, IEnumerable<BuildEntry> builds, IEnumerable<ListedRelease>? listed,
                                                 Product product, Platform? platform = null)
    {
        var mine = On(track, builds);
        var on = listed != null && Versions.IsChannel(track) ? ReleaseManifest.OnChannel(listed, track, product) : Array.Empty<ListedRelease>();
        var rows = on.Select((r, i) => new TrackVersion(r.Tag, r.Published, i == 0, BuildOf(r.Tag, mine),
                                                      platform is { } p && ReleaseManifest.ListsNewBuilder(r, p))).ToList();
        foreach (var tag in mine.Select(b => b.Version).OfType<string>().Distinct(StringComparer.Ordinal))
            if (rows.All(r => r.Tag != tag)) rows.Add(new TrackVersion(tag, null, false, BuildOf(tag, mine)));
        // (OrderByDescending is stable: versions the order does not rank keep their place, after the ones it does)
        var order = Comparer<string>.Create((a, b) => Versions.Newer(a, b, product.FirstMajor) ? 1 : Versions.Newer(b, a, product.FirstMajor) ? -1 : 0);
        return rows.OrderByDescending(r => r.Tag, order).ToList();
    }

    /// The pull request track's versions: the installed pull requests' builds first (an older push of one that moved on stays
    /// its build's version: its update is the newer push, UpdatePolicy), then the open ones Beanstalk lists that a BigBean wrote or
    /// approved, or that the player found by number (found), each at its head, newest change first; a draft, one for the other
    /// system, or one already installed left out. pulls: null before Beanstalk answered, then only the installed ones
    public static IReadOnlyList<TrackVersion> OfPullRequests(IEnumerable<BuildEntry> builds, IReadOnlyList<PullRequest>? pulls,
                                                             Platform platform, IReadOnlySet<int>? found = null)
    {
        var mine = On(Versions.PullRequest, builds);
        var rows = new List<TrackVersion>();
        foreach (var tag in mine.Select(b => b.Version).OfType<string>().Distinct(StringComparer.Ordinal))
            rows.Add(new TrackVersion(tag, null, false, BuildOf(tag, mine)));
        var have = mine.Select(b => Versions.PullRequestOf(b.Version)?.Number).OfType<int>().ToHashSet();
        foreach (var pr in pulls ?? Array.Empty<PullRequest>())
            if ((pr.Trusted || found?.Contains(pr.Number) == true) && PullRequests.Problem(pr, platform) == null && have.Add(pr.Number))
                rows.Add(new TrackVersion(pr.Tag, null, false, null));
        return rows;
    }

    /// What the main button does for version on track. chosen: the build the player picked in Your versions (the row selected),
    /// the one a Replace takes the place of when it is on the track
    public static PickPlan Plan(string track, string version, IEnumerable<BuildEntry> builds, BuildEntry? chosen = null)
    {
        var mine = On(track, builds);
        if (BuildOf(version, mine) is { } have) return new PickPlan(PickAction.Play, version, have, false);
        var replaces = chosen != null && mine.Any(b => b.Id == chosen.Id) ? chosen : First(mine);
        return replaces == null
            ? new PickPlan(PickAction.Build, version, null, false)
            : new PickPlan(PickAction.Replace, version, replaces, Versions.Newer(replaces.Version, version));
    }

    /// The build of a version among a track's: the default first, then the last built
    static BuildEntry? BuildOf(string version, IEnumerable<BuildEntry> mine) => First(mine.Where(b => b.Version == version));

    static BuildEntry? First(IEnumerable<BuildEntry> builds) =>
        builds.OrderByDescending(b => b.Default).ThenByDescending(b => b.Updated).FirstOrDefault();
}
