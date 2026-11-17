using Avalonia.VisualTree;
// The launcher's screens drawn without a display, into PNGs: each scene sets up a launcher of its own (a home in a temporary
// folder, no game files, no network) and the page it shows, and the window is rendered by Skia through Avalonia.Headless.
//
//   dotnet run --project tools/screenshots -- [out folder] [scene ...]
//
// Out folder: default work/screenshots. No scene named: every scene. The scenes are below (Scenes); a pull request that changes
// a screen adds or updates the scene that shows it.
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using LongshotLauncher;
using LongshotLauncher.Core;
using LongshotLauncher.ViewModels;

namespace LongshotScreenshots;

static class Program
{
    /// A scene: its name (the PNG's), what it shows, and how it sets the launcher up (the platform it draws, then the page, then
    /// what is opened over the page: a dialog)
    sealed record Scene(string Name, string Shows, Platform Platform, Action<LauncherService>? Setup, Func<MainViewModel, object?> Page,
                        Action<MainViewModel>? Then = null, bool ScrollToEnd = false);

    // (a build's first lines, as make_game.py says them, with when: the build page's rows, bar, time and details)
    static readonly (string Line, double At)[] Transcript =
    [
        ("@@stage read Reading your game files", 2),
        ("Ocarina of Time.nup: 25.02 GB, 1 titles, 601 dirs, 186592 files", 2.2),
        ("title 0: 0400486024dec000 v131072  exefs 5 files (main 29953289)  romfs 186587 files, 26.24 GB", 2.2),
        ("extracting title 0 (v131072) to /home/player/Games/Longshot/v3.0.0/.game (356 GB free)", 2.3),
        ("  494.6 MiB written (4329 files completed)", 4.3),
        ("[hos host] [debug] nup: 40588 romfs files with two different sizes", 5),
        ("  4296.5 MiB written (22151 files completed)", 11.4),
        ("[hos host] [warn] nup: Tex/CmnTex_Map_Soil_FairyFountain_Floor_D_Nrm.bntx.zs is damaged: a similar file is used", 12),
        ("  9512.0 MiB written (90000 files completed)", 40),
        ("[hos host] [trace] nup: 1024 directory entries read", 41),
        ("  12902.3 MiB written (121904 files completed)", 52.1),
    ];

    static readonly Scene[] Scenes =
    [
        new("home", "The home with no build yet", Platform.Linux, null, _ => null),
        new("launcher-settings", "The launcher's Settings: an update's build tools downloaded early, when chosen", Platform.Windows,
            s => { s.Settings.CheckUpdates = true; s.Settings.DownloadUpdatesEarly = true; }, m => new SettingsViewModel(m), ScrollToEnd: true),
        new("settings-linux", "Step 2, the build settings, on Linux with an AMD GPU", Platform.Linux,
            s => { s.GpuCards = [new Gpu(SystemInfo.Amd)]; s.Settings.Fsr = true; s.Settings.FsrLinuxNotice = true; },
            m => Settings(m)),
        new("settings-windows", "Step 2, the build settings, on Windows with an NVIDIA GPU", Platform.Windows,
            s => { s.GpuCards = [new Gpu(SystemInfo.Nvidia, "NVIDIA GeForce RTX 4070")]; s.Settings.Dlss = true; s.Settings.DlssLicense = true; },
            m => Settings(m)),
        new("settings-windows-unsupported", "Step 2 on Windows with a GeForce GTX: DLSS and FSR 4 unsupported, in red, DLSS on all the same",
            Platform.Windows,
            s => { s.GpuCards = [new Gpu(SystemInfo.Nvidia, "NVIDIA GeForce GTX 1660 SUPER")]; s.Settings.Dlss = true; s.Settings.DlssLicense = true; },
            m => Settings(m)),
        new("settings-windows-intel", "Step 2, the build settings, on Windows with Intel graphics: Intel shader compiles", Platform.Windows,
            s => s.GpuCards = [new Gpu(SystemInfo.Intel, "Intel(R) Iris(R) Xe Graphics")],
            m => Settings(m)),
        new("settings-windows-intel-side", "Step 2 on Windows with Intel graphics, Side by side and the Longer time limit", Platform.Windows,
            s =>
            {
                s.GpuCards = [new Gpu(SystemInfo.Intel, "Intel(R) Iris(R) Xe Graphics")];
                s.Settings.IntelCompiles = GamePlay.IntelCompilesSideBySide;
                s.Settings.IntelLongerLimits = true;
            },
            m => Settings(m)),
        new("fsr-linux-notice", "The AMD FSR 4 notice on Linux, over step 2", Platform.Linux, s => s.GpuCards = [new Gpu(SystemInfo.Amd)],
            m => Settings(m), m => m.Modal = new LicenceViewModel(m, LicenceOf.Amd, true, () => { }, () => { })),
        new("xess-licence", "Intel's licence for XeSS, over step 2 on Windows with Intel graphics", Platform.Windows,
            s => s.GpuCards = [new Gpu(SystemInfo.Intel, "Intel(R) Arc(TM) B580 Graphics")],
            m => Settings(m), m => m.Modal = new LicenceViewModel(m, LicenceOf.Intel, false, () => { }, () => { })),
        new("build", "Step 3, a build reading the game files", Platform.Linux, null, m => Build(m, showLog: false)),
        new("build-log", "Step 3 with Show details, at Debug", Platform.Linux, null, m => Build(m, showLog: true)),
        // (the failures with Copy for a bug report: the words are NetFailure's and the other screens' own)
        new("home-check-failed", "The home with no build, the update check failed, Copy for a bug report", Platform.Windows,
            s => s.SetNewest(s.DefaultChannel, null), _ => null,
            m => m.Home.ShowCheckFailed("The secure connection to git.magicbean.dev failed. Check that this PC's date and time are right; an antivirus " +
                                        "or a network that inspects HTTPS can also cause this. (The remote certificate was rejected by the provided RemoteCertificateValidationCallback)")),
        new("home-notice", "The home with a notice, Copy for a bug report", Platform.Windows, null, _ => null,
            m => m.Home.Notice = "The launcher could not update itself: The connection to git-cdn.magicbean.dev was cut. A firewall, antivirus or VPN " +
                                 "may be blocking the site. (Connection reset by peer)"),
        // (the old versions' folders updates left before they stayed in their build's folder: Keep or Delete)
        new("home-leftovers", "The home offering to delete the old versions' folders updates left on the disk", Platform.Windows,
            s => s.Registry.Add(@"C:\Users\Player\Games\Longshot\nightly-189-aa95d35f", "nightly-189-aa95d35f", Versions.Nightly, makeDefault: true),
            _ => null,
            m => m.Home.ShowLeftovers([new Leftover(@"C:\Users\Player\Games\Longshot\nightly-130-0caf0618", "nightly-130-0caf0618"),
                                       new Leftover(@"C:\Users\Player\Games\Longshot\nightly-77-9f64464b", "nightly-77-9f64464b")],
                                      78_400_000_000)),
        new("home-leftover", "The same offer for one old version", Platform.Windows,
            s => s.Registry.Add(@"C:\Users\Player\Games\Longshot\nightly-189-aa95d35f", "nightly-189-aa95d35f", Versions.Nightly, makeDefault: true),
            _ => null,
            m => m.Home.ShowLeftovers([new Leftover(@"C:\Users\Player\Games\Longshot\nightly-130-0caf0618", "nightly-130-0caf0618")], 38_900_000_000)),
        new("tools-failed", "Step 1, the tools' download failed, Copy for a bug report", Platform.Windows, null,
            m => Files(m, "git.magicbean.dev did not answer in time. The server may be down or busy, or the network slow or blocking it: try again in a few minutes.")),
        // (Linux paths: a Windows path is not split into its folder and name on the Linux PCs the screenshots are drawn on)
        new("files-not-usable", "Step 1, game files that can't be used: why under each, Check again, Copy for a bug report", Platform.Linux, null,
            m => Files(m, null,
                ("/home/player/Games/Ocarina of Time/0400486024dec000_v131072.nup", new GameCheck(GameCheckStatus.Unreadable,
                    "nup.py stopped (exit status 1): the .nup file is incomplete: it is 9214 MB, its header says 25624 MB. Download or copy it again")),
                ("/home/player/Downloads/Ocarina of Time.nup", new GameCheck(GameCheckStatus.OtherVersion,
                    "This is build 7c1a33b0. Longshot supports build e0f3ccfa.")))),
        new("files-not-checked", "Step 1, a check that could not run because of this PC: not said to be the file's", Platform.Linux, null,
            m => Files(m, null,
                ("/home/player/Games/Ocarina of Time/0400486024dec000_v131072.nup", new GameCheck(GameCheckStatus.CheckFailed,
                    "Building on Linux needs a few programs this PC lacks: clang, lld. Install them with your package manager, then check again.",
                    ThisPc: true)))),
        new("crash", "The game closed unexpectedly, Copy for a bug report", Platform.Windows, null,
            m => new ProblemPageViewModel(m, new Problem(ProblemKind.GameCrashed, "The game closed unexpectedly",
                                                         "If it happens again at the same place, the latest graphics driver is worth a try.",
                                                         [new Fix(FixKind.DriverPage, "Get the latest graphics driver", Problems.DriverPage(SystemInfo.Nvidia))],
                                                         "exception_access_violation longshot.exe"), m.ShowHome, crashed: true)),
        // (What's new: a v3.0.0 build, the stable channel at v3.2.0, the release list's pages as scripts/release.py writes them)
        new("home-update", "The home with an update: What's new beside it", Platform.Linux, UpdateOffered, _ => null),
        new("whats-new", "What's new, from the build's version to the update, newest first", Platform.Linux, UpdateOffered, _ => null,
            m => m.Home.ShowNotes(m.Home.Default!)),
        new("whats-new-launcher", "What's new in the launcher the banner offers", Platform.Linux, LauncherOffered, _ => null,
            m => m.Home.LauncherNotesCommand.Execute(null)),
        // (Other versions: a nightly build, the newest 5 nightlies on the server, its own the newest)
        new("home-other-versions", "A version's row opened: Other versions, beside Saves", Platform.Windows, NightliesListed, _ => null,
            m => m.Home.Select(m.Home.Builds[0])),
        new("home-other-versions-second", "A second version's row opened: Play this version, Set as default, Other versions", Platform.Windows,
            s =>
            {
                NightliesListed(s);
                s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable", "v3.0.0", Versions.Stable, makeDefault: true);
            },
            _ => null, m => m.Home.Select(m.Home.Builds.First(b => !b.IsDefault))),
        new("other-versions", "Other versions of a nightly build: the channel's others on the server, the newest older one picked",
            Platform.Windows, NightliesListed, _ => null, m => m.Home.ShowOtherVersions(m.Home.Builds[0])),
        new("settings-go-back", "Step 2 going back to an older nightly, in the build's own folder", Platform.Windows, NightliesListed,
            m => new SettingsStepViewModel(new BuildJob(m, JobKind.Update, Versions.Nightly, "nightly-187-9e6f231d", m.Service.Registry.Builds[0]))
                 { Free = 412L << 30 }),
        new("hookshot-import-problem", "Importing Hookshot's saves failed, Copy for a bug report", Platform.Windows, null, _ => null,
            m => m.Modal = new HookshotImportViewModel(m) { Problem = @"The saves could not be copied: Access to the path 'C:\Users\Player\Saved Games\Longshot\slot1' is denied." }),
    ];

    /// A v3.0.0 build with v3.2.0 out on its channel, and the release list the update check read (two stables since)
    static void UpdateOffered(LauncherService service)
    {
        service.Registry.Add("/home/player/Games/Longshot/v3.0.0", "v3.0.0", Versions.Stable, makeDefault: true);
        service.SetNewest(Versions.Stable, "v3.2.0");
        const string downloads = "# 🎮 To play, get the Longshot Launcher\n\n### [🪟 &nbsp;Download the launcher for Windows](https://longshot.magicbean.dev/download/launcher/stable/windows)\n\n---\n\n";
        ListedRelease Release(string tag, string day, string notes) =>
            new(tag, false, false, DateTimeOffset.Parse(day + "T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                ReleaseNotes.PageOf(Product.Longshot, tag), "", [], downloads + notes);
        service.Releases.Listed =
        [
            Release("v3.0.0", "2026-10-20", "## v3.0.0 (2026-10-20)\n\n- The first stable"),
            Release("v3.1.0", "2026-11-02", "## What's new in v3.1.0\n\n**A faster game:** Castle Town's market from 34.8 to 45.8 fps on a B580.\n\n" +
                "- **Settings in the launcher's user folder**, so an update keeps them (#729)\n- Kokiri-start, a save right after the opening (#610)\n\n" +
                "**Fixed:**\n\n- The GPU device row keeps its card, not its place in the list (#736)\n- Pace at 240 while a movie plays (#488)"),
            Release("v3.2.0", "2026-11-14", "## What's new in v3.2.0\n\n- **The VRAM budgets set by the card**, not a menu row or a preset: 8 GB cards' mirrors 2 GB (#735)\n" +
                "- **A page on reporting slow performance**, with the switches that measure it ([help](https://git.magicbean.dev/MagicBeanDev/Longshot))\n\n" +
                "**Fixed:**\n\n- The start-up screen counts the compute pipelines (#721)\n- Honor `cache-off` for rolling pipeline files (#415)"),
        ];
    }

    /// A nightly build of the newest nightly, and the release list the update check read: the 5 nightlies the server keeps
    static void NightliesListed(LauncherService service)
    {
        service.Registry.Add(@"C:\Users\Player\Games\Longshot\nightly", "nightly-189-aa95d35f", Versions.Nightly, makeDefault: true);
        service.SetNewest(Versions.Nightly, "nightly-189-aa95d35f");
        ListedRelease Nightly(string tag, string at) =>
            new(tag, false, true, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), ReleaseNotes.PageOf(Product.Longshot, tag), "", []);
        service.Releases.Listed =
        [
            Nightly("nightly-189-aa95d35f", "2026-11-15T11:10:00Z"),
            Nightly("nightly-188-a00c485c", "2026-11-15T10:10:00Z"),
            Nightly("nightly-187-9e6f231d", "2026-11-15T09:22:00Z"),
            Nightly("nightly-182-5274fb72", "2026-11-14T08:07:00Z"),
            Nightly("nightly-179-70087604", "2026-11-13T07:01:00Z"),
        ];
    }

    /// A launcher on its stable channel offered, with its release page's notes
    static void LauncherOffered(LauncherService service)
    {
        UpdateOffered(service);
        var none = new Dictionary<string, FileRef>();
        service.SetLauncherUpdate(new ReleaseInfo("launcher", "v1.1.0", "", DateTimeOffset.Now, null, none, none, none));
        service.LauncherReleases.Listed =
        [
            new("v1.1.0", false, false, DateTimeOffset.Now, ReleaseNotes.PageOf(Product.Launcher, "v1.1.0"), "", [],
                "# ⬇️ Download Longshot Launcher\n\n---\n\n## What's new\n\n- **Release notes in the launcher.** What's new beside each update.\n" +
                "- **Fixes:**\n  - On Windows, Delete leaves out game files a folder with no row still holds.\n\n" +
                "## v1.1.0 (2026-11-15)\n\n### Features\n\n- **home:** What's new beside each update (#102)\n\n### Fixes\n\n" +
                "- **home:** On Windows, Delete leaves out game files a folder with no row still holds (#101)"),
        ];
    }

    /// Step 1 with the tools' download failed, why (null: the tools downloaded), and the game files listed with their checks
    static FilesStepViewModel Files(MainViewModel main, string? why, params (string Path, GameCheck Check)[] files)
    {
        var page = new FilesStepViewModel(Job(main), tools: false, search: false);
        if (why != null) page.ShowToolsFailed(why);
        else page.ShowToolsDone();
        foreach (var (path, check) in files)
            page.ShowFile(new FoundGameFile(path, "Found", 25L << 30, DateTime.Now, false), check);
        return page;
    }

    static BuildJob Job(MainViewModel main) => new(main, JobKind.Build, "stable", "v3.0.0", null);

    /// Step 2 with room for the build: the free space is this PC's otherwise (its temporary folder's drive)
    static SettingsStepViewModel Settings(MainViewModel main) => new(Job(main)) { Free = 412L << 30 };

    static BuildStepViewModel Build(MainViewModel main, bool showLog)
    {
        var page = new BuildStepViewModel(Job(main), start: false);
        page.Replay(Transcript.Select(t => (t.Line, TimeSpan.FromSeconds(t.At))));
        if (showLog)
        {
            page.Level = LogLevelChoice.Of(LogLevel.Debug);
            page.ShowLog = true;
        }
        return page;
    }

    [STAThread]
    static int Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("work", "screenshots"));
        var wanted = args.Skip(1).ToHashSet(StringComparer.Ordinal);
        var unknown = wanted.Where(w => Scenes.All(s => s.Name != w)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine("no scene " + string.Join(", ", unknown) + "; the scenes: " + string.Join(", ", Scenes.Select(s => s.Name)));
            return 2;
        }
        Directory.CreateDirectory(outDir);
        AppBuilder.Configure<App>().UseSkia().With(App.Fonts()).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Tr.Use(Tr.English);
        foreach (var scene in Scenes.Where(s => wanted.Count == 0 || wanted.Contains(s.Name)))
        {
            var path = Path.Combine(outDir, scene.Name + ".png");
            Shoot(scene, path);
            Console.WriteLine($"{path}: {scene.Shows}");
        }
        return 0;
    }

    static void Shoot(Scene scene, string path)
    {
        // (each scene in a home of its own: its settings, builds and saves start empty, and nothing of this PC's is read)
        var home = Directory.CreateTempSubdirectory("longshot-shot-");
        try
        {
            var service = LauncherService.Open(scene.Platform, new LauncherHome(home.FullName));
            // (a player's folders, as the screens show them: nothing is written there)
            var windows = scene.Platform == Platform.Windows;
            Disk.ShownDrive = windows ? "C:" : "/home";
            service.Settings.BuildsFolder = windows ? @"C:\Users\Player\Games\Longshot" : "/home/player/Games/Longshot";
            service.Settings.Work = windows ? @"C:\Users\Player\AppData\Local\Longshot\work" : "/home/player/.local/share/longshot/launcher/work";
            service.Settings.CheckUpdates = false;
            // (the saves looked for in an empty home of the scene's: neither this PC's Longshot saves nor its Hookshot ones are found)
            var player = Directory.CreateDirectory(Path.Combine(home.FullName, "player")).FullName;
            service.SaveEnv = new SaveEnvironment(scene.Platform, name => name switch
            {
                "HOME" or "USERPROFILE" => player,
                "XDG_DATA_HOME" => Path.Combine(player, ".local", "share"),
                "XDG_CONFIG_HOME" => Path.Combine(player, ".config"),
                "APPDATA" => Path.Combine(player, "AppData", "Roaming"),
                "LOCALAPPDATA" => Path.Combine(player, "AppData", "Local"),
                _ => null,
            });
            // (the update check's answer, without asking the server: the newest on the channel new builds start on is v3.0.0)
            service.SetNewest(service.DefaultChannel, "v3.0.0");
            service.Checked = true;
            scene.Setup?.Invoke(service);
            var main = new MainViewModel(service, StartRequest.Parse([]));
            var window = new MainWindow { DataContext = main };
            window.Show();
            if (scene.Page(main) is { } page) main.Navigate(page);
            scene.Then?.Invoke(main);
            // (layout, bindings and the page's cross-fade settled before the frame is taken)
            for (var i = 0; i < 30; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            // (a scene about the end of a long page: its scroll at the bottom)
            if (scene.ScrollToEnd)
            {
                foreach (var scroll in window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>()) scroll.ScrollToEnd();
                for (var i = 0; i < 10; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
            }
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame for " + scene.Name);
            frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally
        {
            try { home.Delete(recursive: true); }
            catch (IOException) { }
        }
    }
}
