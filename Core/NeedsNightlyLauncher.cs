namespace LongshotLauncher.Core;

/// A version of Longshot built with the new builder (longshot-builder: its release lists the builder's manifest, ReleaseInfo.HasNewBuilder),
/// which this launcher does not know: it builds with make_game.py only. The way out is the nightly launcher, which the player's
/// own update channel (LauncherSettings.LauncherChannel) switches to
public static class NeedsNightlyLauncher
{
    const string Marker = "needs-nightly-launcher";

    /// The build record a game folder of the new builder has (Longshot's docs/dev/reference/builder.md)
    public const string BuildRecord = "longshot_build.json";

    public static string Text =>
        Tr.T("This version of Longshot is built with the new builder, which this launcher doesn't support. Switch the Longshot Launcher to its nightly channel to play it.");

    /// The refusal: an InvalidDataException, as every refused release is (the screens show its message), marked so Is knows it
    public static InvalidDataException Refusal()
    {
        var refusal = new InvalidDataException(Text);
        refusal.Data[Marker] = true;
        return refusal;
    }

    public static bool Is(Exception e) => e.Data.Contains(Marker);
}
