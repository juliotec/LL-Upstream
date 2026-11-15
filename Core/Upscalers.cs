namespace LongshotLauncher.Core;

/// NVIDIA DLSS and AMD FSR 4 in a build (step 2's switches, LauncherSettings). Either can be turned on whatever this PC's cards:
/// one none of them runs is said in red under its switch (Unsupported), and the build fetches its files all the same. Players
/// who swap upscalers in with OptiScaler need DLSS's and FSR 4's files beside the game on cards NVIDIA's and AMD's own don't run on
public static class Upscalers
{
    /// None of this PC's cards runs it (SystemInfo.DlssRuns, FsrRuns): its switch's line is a warning. No card found is not one
    /// (SystemInfo.Offers: unknown, nothing said)
    public static bool Unsupported(IReadOnlyList<Gpu> gpus, Func<Gpu, bool> runs) => !SystemInfo.Offers(gpus, runs);

    /// A build's request with the player's upscalers: switched on, and NVIDIA's licence, AMD's (Windows) or the FSR 4 notice
    /// (Linux) accepted. This PC's cards are not asked
    public static BuildRequest Chosen(BuildRequest request, LauncherSettings settings, Platform platform) =>
        request with
        {
            Dlss = settings.Dlss, DlssLicense = settings.DlssLicense,
            Fsr = settings.Fsr, FsrLicense = platform == Platform.Linux ? settings.FsrLinuxNotice : settings.FsrLicense,
        };
}
