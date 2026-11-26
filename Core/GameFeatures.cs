namespace LongshotLauncher.Core;

/// What a version's game can do beyond what every Longshot game does (profile: it plays a profile, and reads its profile.json's
/// start_slot itself): what this very build was made with, never its version number (nightlies, pull requests, a build of one's
/// own). A game in a version folder: its builder's info says it (BuilderInfos: capabilities.game, read by the builder from the
/// build's record), never the game folder's files; none until its builder was asked. Any other game folder: the "features=" line
/// of its stamp (longshot_version.txt, GameFolder.ReadStamp), names with a comma or space between. No line: none of them, which
/// is every game from before it
public static class GameFeatures
{
    /// The stamp's line of what the game can do
    public const string StampKey = "features";

    /// The names the game can do; empty for none
    public static IReadOnlySet<string> Of(string gameFolder) =>
        BuilderVersions.VersionOfGame(gameFolder, Platforms.Current) is { } version
            ? BuilderInfos.OfVersion(version)?.Game ?? new HashSet<string>(StringComparer.Ordinal)
        : GameFolder.ReadStamp(gameFolder) is { } stamp && stamp.TryGetValue(StampKey, out var line)
            ? line.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// Whether the game's stamp's features= line names feature
    public static bool Has(string gameFolder, string feature) => Of(gameFolder).Contains(feature);
}
