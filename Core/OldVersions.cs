namespace LongshotLauncher.Core;

/// Versions built with make_game.py, the builder before longshot-builder. This launcher (the nightly, from the clean break) builds
/// and plays only versions of longshot-builder's kind: an old version still listed is shown as not supported, with the way back
/// to the stable Longshot Launcher (SelfUpdate's downgrade), which still plays it. Nothing of it is deleted here unless the
/// player deletes it: its folder, its saves and its tools stay for the stable launcher. Every version a release of
/// longshot-builder's kind made is played and built in its version folder (BuilderVersions), or carries longshot-builder's
/// record (LongshotBuilder.RecordFiles) where a launcher before put it
public static class OldVersions
{
    /// Whether the game in gameFolder (a row's: entry) was built with make_game.py: built (its program there), not in a version
    /// folder of launcherData, not marked by its row as longshot-builder's (BuildEntry.NupInPlace) and holding no record of it.
    /// A folder whose game is not there is no old version: nothing is known of it
    public static bool Is(string gameFolder, BuildEntry? entry, Platform platform, string launcherData)
    {
        try
        {
            return GameFolder.IsBuilt(gameFolder, platform) && !BuilderVersions.InVersions(gameFolder, launcherData) && entry?.NupInPlace != true
                   && !LongshotBuilder.RecordFiles.Any(f => File.Exists(Path.Combine(gameFolder, f)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// What an old version's card says
    public static string Shown => Tr.T("Not supported by this launcher: use the stable Longshot Launcher to play it");

    /// Why an old version is not started (Play, a shortcut, --play)
    public static string NotPlayed(string? version) =>
        Tr.F("{0} was built by Longshot's old builder, which this launcher doesn't run: use the stable Longshot Launcher to play it.", Versions.Shown(version));

    /// Why a release made for make_game.py is not built here (its release has no longshot-builder for this system)
    public static string NotBuiltHere(string version) =>
        Tr.F("{0} is built by Longshot's old builder, which this launcher doesn't run: use the stable Longshot Launcher to build and play it.", Versions.Shown(version));
}
