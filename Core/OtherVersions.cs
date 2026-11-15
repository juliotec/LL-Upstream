namespace LongshotLauncher.Core;

/// One version a build can be changed to (OtherVersions.Of): its tag, when it was published, and whether it is older than the
/// build's own version, so building it goes back
public sealed record OtherVersion(string Tag, DateTimeOffset? Published, bool Older);

/// The versions a build can be changed to (Other versions, on Home): every release of its channel the server still has, newest
/// first, but its own. Picking one builds it in the build's place, as an update does (in its folder, its row then that
/// version), so a player can go back to a version before a change that broke their game, and Update then offers the newest
/// again. Only the build's own channel: a version of another channel is a new build of that channel (Build another version).
/// The server keeps every release and release candidate, and the newest 5 nightlies (docs/releases.md), so a nightly goes back
/// as far as those
public static class OtherVersions
{
    /// listed: the release list the update check read (Releases.Listed); none when it has not answered. A build that follows
    /// no channel (a track of its own, such as a test build's) has none
    public static IReadOnlyList<OtherVersion> Of(BuildEntry build, IEnumerable<ListedRelease>? listed, Product product)
    {
        var channel = UpdatePolicy.ChannelOf(build.Channel, build.Version);
        if (listed == null || !Versions.IsChannel(channel)) return Array.Empty<OtherVersion>();
        return ReleaseManifest.OnChannel(listed, channel, product)
                              .Where(r => r.Tag != build.Version)
                              .Select(r => new OtherVersion(r.Tag, r.Published, Versions.Newer(build.Version, r.Tag, product.FirstMajor)))
                              .ToList();
    }
}
