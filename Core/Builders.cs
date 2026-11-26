namespace LongshotLauncher.Core;

/// A build as its builder takes the request: the command, and what it builds in (the upscalers the player chose and accepted the
/// licences of)
public sealed record BuilderRun(MakeGameCommand Command, bool Dlss, bool Fsr, bool Xess, IReadOnlyList<string> Notes);

public static class Builders
{
    /// The builder of a version's tools: its release's longshot-builder (LongshotBuildRelease, installed by ToolsSource), the only
    /// builder this launcher runs. Versions built with make_game.py, the builder before it, are the stable launcher's to build and
    /// play (OldVersions)
    public static LongshotBuilder For(Toolchain tools, Platform platform, LauncherHome home, string launcher) =>
        new(tools, tools.BuilderExe, platform, home, launcher);
}
