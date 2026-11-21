using LongshotLauncher;
using LongshotLauncher.Core;
using LongshotLauncher.ViewModels;

// Run with dotnet run --project tests/launcher_app/LauncherAppTests.csproj
var root = Path.Combine(Path.GetTempPath(), "launcher-app-" + Guid.NewGuid().ToString("N"));
try
{
    var home = new LauncherHome(root);
    Directory.CreateDirectory(root);
    new LauncherSettings
    {
        BuildsFolder = Path.Combine(root, "builds"),
        HookshotSavesAsked = true,
        Nx2naviSavesAsked = true,
        InstalledLauncherAsked = true,
    }.Save(home.Settings);
    var service = LauncherService.Open(Platform.Linux, home);
    var main = new MainViewModel(service, new StartRequest(StartKind.Launch, null, [], null));
    var settings = new SettingsViewModel(main);
    settings.Gamescope = true;
    settings.GamescopeOptions = "  a\\ ";

    var saved = LauncherSettings.Load(home.Settings);
    if (!saved.Gamescope || saved.GamescopeOptions != "  a\\ ")
        throw new Exception("The settings setter did not save the gamescope options as typed.");
    var command = GamePlay.StartInfo(root, Platform.Linux, new Dictionary<string, string>(),
                                     gamescope: saved.Gamescope, gamescopeOptions: saved.GamescopeOptions);
    if (command.FileName != "gamescope" || command.ArgumentList.Count != 3 ||
        command.ArgumentList[0] != "a " || command.ArgumentList[1] != "--" ||
        command.ArgumentList[2] != GameFolder.ExePath(root, Platform.Linux))
        throw new Exception("The saved escaped final space did not reach the gamescope argument.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
