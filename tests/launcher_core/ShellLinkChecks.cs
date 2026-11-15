using LongshotLauncher.Core;

static partial class Program
{
    /// (Windows) Shortcuts written through the shell's own ShellLink and read back: what they start and the AppUserModelID each
    /// names, so the Start menu lists the launcher's and the game's apart (ShellLinks). In the scratch folder, never the Start menu
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void ShellLinkChecks()
    {
        var root = Folder("shell-links");
        var program = Path.Combine(root, "Programs", "LongshotLauncher", "LongshotLauncher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, "MZ");
        var launcher = Path.Combine(root, "Start Menu", "Longshot Launcher.lnk");
        var game = Path.Combine(root, "Start Menu", "Longshot.lnk");
        ShellLinks.Write(launcher, program, "", Path.GetDirectoryName(program)!, program, "the launcher", Shortcuts.LauncherAppId);
        ShellLinks.Write(game, program, "--play", Path.GetDirectoryName(program)!, program, "Longshot", Shortcuts.GameAppId);
        var l = ShellLinks.Read(launcher);
        var g = ShellLinks.Read(game);
        Require(l is { } a && a.Target == program && a.Arguments == "" && a.AppId == Shortcuts.LauncherAppId,
                $"The launcher's shortcut starts the launcher and names its id ({l}).");
        Require(g is { } b && b.Target == program && b.Arguments == "--play" && b.AppId == Shortcuts.GameAppId,
                $"The game's shortcut starts the launcher's Play and names the game's id ({g}).");
        ShellLinks.Write(game, program, "--play", Path.GetDirectoryName(program)!, program, "Longshot", Shortcuts.GameAppId);
        Require(ShellLinks.Read(game)?.AppId == Shortcuts.GameAppId, "A shortcut written again over itself keeps its id.");
        Require(ShellLinks.Read(Path.Combine(root, "none.lnk")) == null, "No shortcut there: none read.");
        Throws<IOException>(() => ShellLinks.Write(Path.Combine(program, "inside a file.lnk"), program, "", root, program, "x", Shortcuts.GameAppId),
                            "A shortcut the shell cannot write fails as an IOException, which every caller takes.");
    }
}
