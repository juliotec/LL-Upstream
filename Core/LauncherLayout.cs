namespace LongshotLauncher.Core;

/// How this launcher is kept on the PC (README, "Installing"):
///   Installed  Windows: %LOCALAPPDATA%\Programs\LongshotLauncher\LongshotLauncher.exe, with a Start menu shortcut and an entry in
///              Apps &amp; features (Installer); its data in the launcher's home (LauncherHome)
///   Portable   Windows: a file named "portable" beside the program, its data in LongshotLauncher-data beside it; Linux: an
///              &lt;AppImage&gt;.home folder beside the AppImage (the AppImage convention), its data there. Nothing written elsewhere
///   Plain      anything else: the Windows download not installed yet (it offers Install or Run portable when it starts), the
///              AppImage, or a build from a checkout
public enum LauncherKind { Plain, Installed, Portable }

/// The launcher's own file and its kind
public sealed record LauncherLayout(Platform Platform, string? File, LauncherKind Kind)
{
    public const string PortableMarker = "portable", PortableData = "LongshotLauncher-data", ProgramName = "LongshotLauncher.exe";

    /// Where the launcher installs itself on Windows (per user, no administrator rights): %LOCALAPPDATA%\Programs\LongshotLauncher
    public static string InstallFolder(Func<string, string?> getenv) => Path.Combine(LocalAppData(getenv), "Programs", "LongshotLauncher");

    public static string InstalledExe(Func<string, string?> getenv) => Path.Combine(InstallFolder(getenv), ProgramName);

    internal static string LocalAppData(Func<string, string?> getenv) =>
        getenv("LOCALAPPDATA") is { Length: > 0 } l ? l : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// This launcher's: its file (SelfUpdate.CurrentFile), installed or portable as the disk says
    public static LauncherLayout Current(Platform platform, Func<string, string?>? getenv = null, string? processPath = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        return Of(platform, SelfUpdate.CurrentFile(platform, getenv, processPath), getenv);
    }

    public static LauncherLayout Of(Platform platform, string? file, Func<string, string?> getenv)
    {
        if (file == null) return new LauncherLayout(platform, null, LauncherKind.Plain);
        if (platform == Platform.Linux)
        {
            // (the AppImage runtime resolves a symlink to its target BEFORE it sets APPIMAGE, and
            //  the <name>.home it uses is the TARGET's — measured in review against the exact
            //  runtime this repo pins: an alias in the library with real.AppImage.home beside the
            //  target ran portable on the target's home while the old check (alias+".home") read
            //  it plain, so routing armed where the arrival would never look. Follow the link
            //  before deciding portable, and key the layout's file off the resolved name)
            var real = RealPath(file);
            return new LauncherLayout(platform, real, Directory.Exists(real + ".home") ? LauncherKind.Portable : LauncherKind.Plain);
        }
        if (System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(file)!, PortableMarker))) return new LauncherLayout(platform, file, LauncherKind.Portable);
        var installed = Path.GetFullPath(InstalledExe(getenv));
        return new LauncherLayout(platform, file, string.Equals(Path.GetFullPath(file), installed, StringComparison.OrdinalIgnoreCase)
                                                      ? LauncherKind.Installed : LauncherKind.Plain);
    }

    /// A portable launcher's data folder: LongshotLauncher-data beside the Windows program, the &lt;AppImage&gt;.home folder on Linux;
    /// null when it is not portable
    public string? PortableHome => Kind != LauncherKind.Portable || File == null ? null
        : Platform == Platform.Linux ? File + ".home" : Path.Combine(Path.GetDirectoryName(File)!, PortableData);

    /// The file a Linux start actually RUNS: through a symlink the AppImage runtime resolves to the
    /// target (its $APPIMAGE is the target's path), so portable detection and the layout's own name
    /// must resolve too. Not a link (or an unreadable one): the path as given, unchanged
    static string RealPath(string file)
    {
        try { return System.IO.File.ResolveLinkTarget(file, true) is { } t ? t.FullName : file; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return file; }
    }

    /// Whether the first start asks Install or Run portable: the Windows download, neither installed nor portable, a release's
    /// (a build from a checkout does not ask; --install installs it anyway), with no LONGSHOT_LAUNCHER_HOME set
    public bool OffersInstall(string ownTag, Func<string, string?> getenv) =>
        Platform == Platform.Windows && Kind == LauncherKind.Plain && File != null && ownTag != "dev"
        && getenv(LauncherHome.Variable) is not { Length: > 0 };

    /// The asset of a launcher release this launcher updates from (ReleaseManifest.LauncherKeys): a portable Windows launcher takes
    /// the program from the portable zip, any other Windows launcher the setup program, Linux the AppImage
    public string UpdateKey => Platform == Platform.Linux ? "linux-x64" : Kind == LauncherKind.Portable ? ReleaseManifest.WindowsPortable : "windows-x64";
}
