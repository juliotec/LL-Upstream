using System.Text.Json.Nodes;
using Avalonia.VisualTree;
// The launcher's screens drawn without a display, into PNGs: each scene sets up a launcher of its own (a home in a temporary
// folder, no game files, no network) and the page it shows, and the window is rendered by Skia through Avalonia.Headless.
//
//   dotnet run --project tools/screenshots -- [--lang <code>] [--size <width>x<height>] [out folder] [scene ...]
//
// --lang: the launcher's language (Tr.All's codes, de for German): long translations are where a screen runs out of room.
// --size: the window resized to that (its smallest is MainWindow's MinWidth and MinHeight). Out folder: default work/screenshots. No scene named: every scene. The scenes are below (Scenes); a pull request that changes
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
                        Action<MainViewModel>? Then = null, bool ScrollToEnd = false, int OpenDropdown = -1, double ScrollDown = 0,
                        bool PressDetails = false, bool Tv = false, PadAction[]? Presses = null);

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
        // (the TV view, as in Steam's Gaming Mode on a 1080p TV: full screen, scaled up; a controller's first press focuses the main button)
        new("tv-home", "The TV view of the home (Steam's Gaming Mode), a controller's first press on the main button", Platform.Linux, null,
            _ => null, Tv: true, Presses: [PadAction.Down]),
        new("tv-settings", "The TV view of the launcher's Settings, moved through with a controller", Platform.Linux, null,
            m => new SettingsViewModel(m), Tv: true, Presses: [PadAction.Down, PadAction.Up, PadAction.Up, PadAction.Right]),
        // (a dialog keeps a controller's focus: its first press on Decline, and down from there stays on it, never the page behind)
        new("tv-dialog", "The TV view with a dialog over step 2: a controller's first press on Decline, its focus kept in the dialog", Platform.Linux,
            s => s.GpuCards = [new Gpu(SystemInfo.Amd)], m => Settings(m),
            m => m.Modal = new LicenceViewModel(m, LicenceOf.Amd, true, () => { }, () => { }), Tv: true,
            Presses: [PadAction.Down, PadAction.Left, PadAction.Down, PadAction.Down]),
        new("launcher-settings", "The launcher's Settings: an update's build tools downloaded early, when chosen", Platform.Windows,
            s => { s.Settings.CheckUpdates = true; s.Settings.DownloadUpdatesEarly = true; }, m => new SettingsViewModel(m), ScrollToEnd: true),
        // (the two folders, each with what it holds: the builds folder's instances, and the build files every build reuses)
        new("launcher-settings-folders", "The launcher's Settings: the builds folder and the build files, each with what it holds", Platform.Windows,
            null, m => new SettingsViewModel(m), ScrollDown: 300),
        new("settings-linux", "Step 2, the build settings, on Linux with an AMD GPU", Platform.Linux,
            s => { s.GpuCards = [new Gpu(SystemInfo.Amd)]; s.Settings.Fsr = true; s.Settings.FsrLinuxNotice = true; },
            m => Settings(m)),
        new("settings-windows", "Step 2, the build settings, on Windows with an NVIDIA GPU", Platform.Windows,
            s => { s.GpuCards = [new Gpu(SystemInfo.Nvidia, "NVIDIA GeForce RTX 4070")]; s.Settings.Dlss = true; s.Settings.DlssLicense = true; },
            m => Settings(m)),
        // (a desktop's Ryzen graphics beside its RTX card: Windows lists both, the game renders on the card, and the rows judge it)
        new("settings-windows-igpu", "Step 2 on Windows, Ryzen graphics beside an RTX 5080: DLSS is Your GPU, FSR 4 is not, and says so in red",
            Platform.Windows,
            s =>
            {
                s.GpuCards = [new Gpu(SystemInfo.Amd, "AMD Radeon(TM) Graphics"), new Gpu(SystemInfo.Nvidia, "NVIDIA GeForce RTX 5080")];
                s.VulkanProbe = () => Task.FromResult<IReadOnlyList<GpuRequirement>?>(
                [
                    new(true, 2UL << 30, false, GpuKind.Integrated, 1, SystemInfo.Amd, "AMD Radeon(TM) Graphics"),
                    new(true, 16UL << 30, true, GpuKind.Discrete, 1, SystemInfo.Nvidia, "NVIDIA GeForce RTX 5080"),
                ]);
                s.Settings.Dlss = true;
                s.Settings.DlssLicense = true;
            },
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
        // (the Ready screen a finished build shows: the four ticks, with Steam found and a second
        // build, so the Steam and "Play starts this version" ones are there too — the row wraps)
        new("ready", "The Ready screen: the four ticks (Desktop, menu, Steam, Play starts this version)", Platform.Linux,
            ReadySetup, m => Ready(m)),
        // (the failures with Copy for a bug report: the words are NetFailure's and the other screens' own)
        new("home-check-failed", "The home with no build, the update check failed, Copy for a bug report", Platform.Windows,
            s => s.SetNewest(s.DefaultChannel, null), _ => null,
            m => m.Home.ShowCheckFailed("The secure connection to git.magicbean.dev failed. Check that this PC's date and time are right; an antivirus " +
                                        "or a network that inspects HTTPS can also cause this. (The remote certificate was rejected by the provided RemoteCertificateValidationCallback)")),
        new("home-notice", "The home with a notice, Copy for a bug report", Platform.Windows, null, _ => null,
            m => m.Home.Notice = "The launcher could not update itself: The connection to git-cdn.magicbean.dev was cut. A firewall, antivirus or VPN " +
                                 "may be blocking the site. (Connection reset by peer)"),
        new("home-nvidia-cache", "The home offering to raise NVIDIA's shader cache to 10 GB (the driver wants an administrator)",
            Platform.Windows, null, _ => null, m => m.Home.OfferNvidiaCache(1024)),
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
        // (instances from before builds/<track>/<version>: one an old launcher named after its first version, one named after its
        // track. Keep or Move. Linux paths, as below)
        new("home-rename-folders", "The home offering to move the instances in the old folder layout into builds/<track>/<version>", Platform.Linux,
            s =>
            {
                s.Registry.Add("/home/player/Games/Longshot/v3.0.0-rc.5", "v3.0.0", Versions.Stable, makeDefault: true);
                s.Registry.Add("/home/player/Games/Longshot/nightly", "nightly-189-aa95d35f", Versions.Nightly);
            },
            _ => null,
            m => m.Home.ShowRenames(m.Service.Registry.Builds
                .Select(b => new InstanceFolders.Move(b, b.Folder, $"/home/player/Games/Longshot/{b.Channel}/{b.Version}")).ToList())),
        // (each of the home's top banners at 960x600, over a home with builds: the launcher's update, the saves found in Hookshot
        // and NX2navi; home-notice, home-leftovers and home-rename-folders above; home-update with none)
        new("home-banner-launcher", "The home with the launcher's update banner over it", Platform.Linux, LauncherOffered, _ => null),
        new("home-banner-hookshot", "The home offering to import Hookshot's saves", Platform.Windows, UpdateOffered, _ => null,
            m => m.Home.HookshotOffer = "Found 3 slots, the last saved today at 14:02, in Hookshot. Copy them into Longshot? Hookshot keeps its own."),
        new("home-banner-nx2navi", "The home offering to import NX2navi's saves", Platform.Windows, UpdateOffered, _ => null,
            m => m.Home.Nx2naviOffer = @"Found 2 slots, the last saved yesterday at 21:40, in C:\Users\Player\AppData\Local\Programs\NX2navi\game. " +
                                       "Copy them into Longshot? The originals stay where they are."),
        new("home-banner-details", "The home's old-layout offer with Which folders pressed: the folders and what a move keeps", Platform.Linux,
            s =>
            {
                s.Registry.Add("/home/player/Games/Longshot/v3.0.0-rc.5", "v3.0.0", Versions.Stable, makeDefault: true);
                s.Registry.Add("/home/player/Games/Longshot/nightly", "nightly-189-aa95d35f", Versions.Nightly);
            },
            _ => null,
            m => m.Home.ShowRenames(m.Service.Registry.Builds
                .Select(b => new InstanceFolders.Move(b, b.Folder, $"/home/player/Games/Longshot/{b.Channel}/{b.Version}")).ToList()),
            PressDetails: true),
        // (a portable launcher with no build yet, on a PC whose installed launcher has some: use them here?)
        new("home-installed-offer", "A portable launcher's empty home offering the installed launcher's builds and saves", Platform.Windows, null, _ => null,
            m => m.Home.InstalledOffer = Tr.F("The launcher installed on this PC has {0} and saves ({1}). Use them here? Its builds stay where they are; its saves are copied.",
                                              Tr.P(2, "1 build", "{0} builds"), "2 slots, saved yesterday 22:14")),
        new("home-add-folder", "Your instances: Add a build folder… beside Build another version", Platform.Windows,
            s => s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable", "v3.0.0", Versions.Stable, makeDefault: true), _ => null),
        new("tools-failed", "Step 1, the tools' download failed, Copy for a bug report", Platform.Windows, null,
            m => Files(m, "git.magicbean.dev did not answer in time. The server may be down or busy, or the network slow or blocking it: try again in a few minutes.")),
        // (Linux paths: a Windows path is not split into its folder and name on the Linux PCs the screenshots are drawn on)
        new("files-not-usable", "Step 1, game files that can't be used: why under each, Check again, Copy for a bug report", Platform.Linux, null,
            m => Files(m, null,
                ("/home/player/Games/Ocarina of Time/0400486024dec000_v131072.nup", new GameCheck(GameCheckStatus.Unreadable,
                    "nup.py stopped (exit status 1): the .nup file is incomplete: it is 9214 MB, its header says 25624 MB. Download or copy it again")),
                ("/home/player/Downloads/Ocarina of Time.nup", new GameCheck(GameCheckStatus.OtherVersion,
                    "This is build 7c1a33b0. Longshot supports build e0f3ccfa.")))),
        new("files-nx-not-usable", "Step 1, .nx files that can't be used: one holding NCA files, one damaged (nxar.py's words)", Platform.Linux, null,
            m => Files(m, null, NxCheck("/home/player/Games/Ocarina of Time/Ocarina of Time (DNSP).nx",
                                        "nxar.py stopped (exit status 1): /home/player/Games/Ocarina of Time/Ocarina of Time (DNSP).nx holds the game as " +
                                        "NCA files (made by nxconvert pack from a DNSP or DXCI), which Longshot does not read."),
                                NxCheck("/home/player/Downloads/Ocarina of Time.nx",
                                        "nxar.py stopped (exit status 1): romfs/Pack/ZsDic.pack.zs is damaged in the archive (its SHA-256 differs). " +
                                        "Copy or convert the file again"))),
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
        // (two builds whose game files are hard links of each other's: each row says what it holds of its own, the footer the
        // total and what of it they share; then with the shader store too)
        new("home-shared-sizes", "Two builds sharing their game files: each row its own size, the footer what they share", Platform.Windows,
            s => SharedSizes(s, 0), _ => null),
        new("home-shared-store", "The same with the shader store: the footer counts it, and says they share it", Platform.Windows,
            s => SharedSizes(s, 3_758_096_384), _ => null),
        // (the Track and Version dropdowns: a nightly build, the newest 5 nightlies on the server, its own the newest)
        new("home-row-opened", "A version's row opened: Saves, Open folder, Delete", Platform.Windows, NightliesListed, _ => null,
            m => m.Home.Select(m.Home.Builds[0])),
        new("home-row-opened-second", "A second version's row opened: Play this version, Set as default; the dropdowns on it", Platform.Windows,
            s =>
            {
                NightliesListed(s);
                s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable", "v3.0.0", Versions.Stable, makeDefault: true);
            },
            _ => null, m => m.Home.Select(m.Home.Builds.First(b => !b.IsDefault))),
        new("home-versions", "The Version dropdown open: the nightly track's versions, a check beside the installed one",
            Platform.Windows, NightliesListed, _ => null, OpenDropdown: 1),
        // (a stable launcher and the newest nightlies built with the new builder: marked, and picking one opens the dialog)
        new("home-versions-needs-nightly", "The Version dropdown open: the newest nightlies are built with the new builder, marked Needs the nightly launcher",
            Platform.Linux, NewBuilderListed, _ => null, OpenDropdown: 1),
        new("needs-nightly-launcher", "A version built with the new builder picked: the dialog with the way out", Platform.Linux, NewBuilderListed,
            _ => null, m => m.Modal = new NeedsNightlyLauncherViewModel(m)),
        new("home-tracks", "The Track dropdown open: the tracks by name", Platform.Windows,
            s =>
            {
                NightliesListed(s);
                s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable", "v3.0.0", Versions.Stable);
            },
            _ => null, OpenDropdown: 0),
        // (a newer version shown once in each place: the nightly build behind its track's newest, Update available beside the
        // version and on its row, Update beside Play, the newest and What's new opposite the title; a row on another track too)
        new("home-nightly-update", "A nightly build with a newer nightly out: Update beside Play, Update available, What's new opposite the title",
            Platform.Windows, NightlyBehind, _ => null),
        new("home-versions-update", "The Version dropdown open on a track whose build has a newer version: Update available on its row",
            Platform.Windows, NightlyBehind, _ => null, OpenDropdown: 1),
        new("home-row-update", "A build on another track with a newer version: only Update available on its row", Platform.Windows,
            s =>
            {
                NightlyBehind(s);
                s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable\v3.0.0", "v3.0.0", Versions.Stable, makeDefault: true);
                s.SetNewest(Versions.Stable, "v3.0.0");
            },
            _ => null),
        // (the Pull request track: a pull request's build behind its newer push, and Beanstalk's list of open ones)
        new("home-pr-track", "The Pull request track: a pull request's build with a newer push, who wrote it, Read it, Find a pull request",
            Platform.Linux, PullRequestsListed, _ => null, m => m.Home.PickTrack = m.Home.Tracks.First(t => t.Id == Versions.PullRequest)),
        new("home-pr-versions", "The Version dropdown on the Pull request track: the installed one, then the ones a BigBean wrote or approved",
            Platform.Linux, PullRequestsListed, _ => null, m => m.Home.PickTrack = m.Home.Tracks.First(t => t.Id == Versions.PullRequest),
            OpenDropdown: 1),
        new("home-pr-switch", "Another pull request picked: Switch to it, built in the place of the one installed", Platform.Linux,
            PullRequestsListed, _ => null, m =>
            {
                m.Home.PickTrack = m.Home.Tracks.First(t => t.Id == Versions.PullRequest);
                m.Home.PickVersion = m.Home.TrackVersions.First(v => v.Tag.StartsWith("pr-1563-", StringComparison.Ordinal));
            }),
        new("pr-warning-trusted", "Building a pull request a BigBean wrote: one softer warning", Platform.Linux, PullRequestsListed, _ => null,
            m => m.Modal = new PullRequestWarningViewModel(m, new PullRequestBuild(m.Service.PullRequestOf(1563)!, JobKind.Update, m.Service.Registry.Builds.First(b => b.Channel == Versions.PullRequest)))),
        new("pr-warning", "Building a pull request nobody approved: the first warning, then Continue", Platform.Linux, PullRequestsListed, _ => null,
            m => m.Modal = new PullRequestWarningViewModel(m, new PullRequestBuild(m.Service.PullRequestOf(1571)!, JobKind.Build, null))),
        new("pr-confirm", "Building a pull request nobody approved: the second warning, to confirm", Platform.Linux, PullRequestsListed, _ => null,
            m => m.Modal = new PullRequestConfirmViewModel(m, new PullRequestBuild(m.Service.PullRequestOf(1571)!, JobKind.Build, null))),
        new("pr-find", "Find a pull request by its number or link", Platform.Linux, PullRequestsListed, _ => null,
            m => m.Modal = new PullRequestFindViewModel(m) { Text = "https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/1571" }),
        new("build-files-left", "The last version deleted: its build files offered to go too, or kept for Storage to free later", Platform.Linux,
            null, _ => null, m => m.Modal = new BuildFilesViewModel(m, m.Service.Work, 23L << 30)),
        new("home-go-back", "An older nightly picked: Go back to it, built in the nightly build's place", Platform.Windows, NightliesListed,
            _ => null, m => m.Home.PickVersion = m.Home.TrackVersions.First(v => v.Tag == "nightly-187-9e6f231d")),
        new("home-build-track", "A track with no build picked: Build its newest, into a folder of its own", Platform.Windows,
            s =>
            {
                NightliesListed(s);
                s.SetNewest(Versions.Stable, "v3.1.0");
                s.Releases.Listed = [.. s.Releases.Listed!, new("v3.1.0", false, false,
                    DateTimeOffset.Parse("2026-11-12T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    ReleaseNotes.PageOf(Product.Longshot, "v3.1.0"), "", [])];
            },
            _ => null, m => m.Home.PickTrack = m.Home.Tracks.First(t => t.Id == Versions.Stable)),
        new("settings-go-back", "Step 2 going back to an older nightly: its folder takes that version's name when the build starts", Platform.Windows, NightliesListed,
            m => new SettingsStepViewModel(new BuildJob(m, JobKind.Update, Versions.Nightly, "nightly-187-9e6f231d", m.Service.Registry.Builds[0]))
                 { Free = 412L << 30 }),
        // (the NFC page over fake tags made in the scene's own NFC folder: past the game's 256, then Delete all asked and said yes to)
        new("nfc", "The NFC page: 300 tags, past the game's 256, Delete selected and Delete all", Platform.Windows, s => FakeNfc(s, 300),
            m => new NfcViewModel(m)),
        new("nfc-delete-all", "The NFC page's Delete all, asked first with how many go", Platform.Windows, s => FakeNfc(s, 300),
            m => new NfcViewModel(m), m => ((NfcViewModel)m.Page).DeleteAllCommand.Execute(null)),
        new("nfc-deleted-all", "The NFC page after Delete all: no tags, the 256 note gone, Delete all off", Platform.Windows, s => FakeNfc(s, 300),
            m => new NfcViewModel(m), m =>
            {
                ((NfcViewModel)m.Page).DeleteAllCommand.Execute(null);
                ((NfcDeleteViewModel)m.Modal!).DeleteCommand.Execute(null);
            }),
        // (the Storage page: what Longshot keeps on the PC by what it is for, Faster updates shown, two versions of one dump)
        new("storage", "The Storage page: what Longshot keeps by what it is for, one bar of its groups, Faster updates' items with what removing each costs",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageShown, free: 115L << 30)),
        new("storage-shaders", "The Storage page's shader caches: one row a version, each removable on its own with the warning",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageShown, free: 115L << 30, show: Storage.Group.ShaderCaches)),
        new("storage-games", "The Storage page's installed games: one row a version, each deleted as Home deletes it",
            Platform.Windows, s => { NightliesListed(s); s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable\v3.0.3", "v3.0.3", Versions.Stable, makeDefault: false); },
            m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, show: Storage.Group.Games)),
        new("storage-copies", "The Storage page's game files: one row a copy on the disk, with the versions using it and what freeing it takes",
            Platform.Windows, s => { NightliesListed(s); s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable\v3.0.3", "v3.0.3", Versions.Stable, makeDefault: false); },
            m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, show: Storage.Group.GameFiles)),
        new("storage-running", "The Storage page while Longshot runs: the versions' Remove off, with why on hover", Platform.Windows,
            s => { NightliesListed(s); s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable\v3.0.3", "v3.0.3", Versions.Stable, makeDefault: false); },
            m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, show: Storage.Group.Games, running: true)),
        new("storage-deleting", "The Storage page deleting a version: pinned with how much of it is gone, every Remove off meanwhile", Platform.Windows,
            s => { NightliesListed(s); s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable\v3.0.3", "v3.0.3", Versions.Stable, makeDefault: false); },
            m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, show: Storage.Group.Games, running: false)
                .ShownBusy("Deleting v3.0.3…", 10_900_000_000, 0.42)),
        new("storage-measuring", "The Storage page measuring again after a removal: a sweep, the sizes the last ones until it is done",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageShown, free: 115L << 30, running: false).ShownBusy("Updating the sizes…", 0, 0)),
        new("storage-first-measure", "The Storage page's first measure: nothing to show yet, its progress where the bar goes",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageShown, free: 115L << 30, running: false).ShownFirstMeasure()),
        new("storage-drives","The Storage page with Longshot on two drives: All drives picked, the dropdown in place of the free space",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageOnDrives(m), free: 115L << 30, show: Storage.Group.BuildTools, running: false)),
        new("storage-drive-c", "The Storage page with C: picked: only what Longshot keeps on C:, and Remove lets only that go",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageOnDrives(m), free: 41L << 30, show: Storage.Group.BuildTools, running: false,
                                                                         onDrive: "C:")),
        // (Remove everything: what goes and what stays with their sizes, the saves' own tick off, then on, then what is left)
        new("storage-remove-everything", "Storage's Remove everything: what goes and what stays with the sizes measured, the saves kept unless ticked",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, running: false),
            m => m.Modal = new RemoveEverythingViewModel(m, StorageListed(m), running: false)),
        new("storage-remove-everything-saves", "Storage's Remove everything with the saves ticked: they go too, with the warning that no copy is kept",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, running: false),
            m => m.Modal = new RemoveEverythingViewModel(m, StorageListed(m), running: false) { Saves = true }),
        new("storage-remove-everything-done", "Storage's Remove everything done: what was kept and why, then the launcher closes",
            Platform.Windows, NightliesListed, m => new StorageViewModel(m, StorageListed(m), free: 115L << 30, running: false),
            m => m.Modal = new RemoveEverythingViewModel(m, StorageListed(m), running: false).ShownDone(new RemoveEverything.Done(
                [new(@"C:\Users\Player\Games\Longshot\nightly-77-9f64464b", RemoveEverything.Reason.Yours),
                 new(@"C:\Users\Player\AppData\Roaming\Longshot", RemoveEverything.Reason.Yours),
                 new(@"C:\Users\Player\AppData\Local\Longshot\save-backups", RemoveEverything.Reason.Yours),
                 new(@"D:\Longshot build files", RemoveEverything.Reason.Others)], []))),
        // (a completely cold build: offered after a failed build, then ticked in step 2's Advanced for the player to read and start)
        new("build-failed", "The build stopped on a message the launcher doesn't know: Build again, or Try a completely cold build", Platform.Windows, null,
            m => new ProblemViewModel(Build(m, showLog: false), "clang stopped with exit code 1 compiling sa/gen/code_412.c", "Compiling", 41)),
        new("settings-cold", "Step 2's Advanced with a completely cold build ticked: what it deletes and what it costs", Platform.Windows, null,
            m =>
            {
                var step = Settings(m);
                step.AdvancedOpen = true;
                step.Cold = true;
                return step;
            }, ScrollToEnd: true),
        // (#148, #163: Build workers' Automatic keeps threads free at low priority; Every thread is the opt-in for speed)
        new("settings-workers", "Step 2's Advanced: Build workers on Automatic, some threads left free so the PC stays usable", Platform.Windows, null,
            m =>
            {
                var step = Settings(m);
                step.AdvancedOpen = true;
                return step;
            }, ScrollToEnd: true),
        new("settings-workers-choices", "Step 2's Build workers dropdown: Automatic, Every thread (fastest), then a number", Platform.Windows, null,
            m =>
            {
                var step = Settings(m);
                step.AdvancedOpen = true;
                return step;
            }, ScrollToEnd: true, OpenDropdown: 4),
        new("settings-workers-every", "Step 2's Build workers on Every thread: the fastest build, at normal priority", Platform.Windows,
            s => s.Settings.BuildWorkers = LauncherSettings.EveryThread,
            m =>
            {
                var step = Settings(m);
                step.AdvancedOpen = true;
                return step;
            }, ScrollToEnd: true),
        // (the Saves page: Longshot's save, made here from made-up files, three slots saved at different times)
        new("saves", "The Saves page: the slots every build shares, and the ways in and out", Platform.Windows, ThreeSlots,
            m => new SavesViewModel(m)),
        new("saves-export-pick", "Export saves: an emulator, another folder, or a .zip file", Platform.Windows, ThreeSlots,
            m => new SavesViewModel(m), m => m.Modal = new SavePickViewModel((SavesViewModel)m.Page!, SaveMode.Export)),
        new("saves-import-pick", "Import saves: an emulator, another folder, or a .zip file", Platform.Windows, ThreeSlots,
            m => new SavesViewModel(m), m => m.Modal = new SavePickViewModel((SavesViewModel)m.Page!, SaveMode.Import)),
        // (a .zip of saves: written from Longshot's save, or read and checked before it is imported)
        new("saves-zip-export", "Export saves to a new .zip file: nothing in the saves changes", Platform.Windows, ThreeSlots,
            m => new SavesConfirmViewModel(new SavesViewModel(m), new SaveOp { Mode = SaveMode.Export, Zip = Documents(m, SaveArchive.DefaultName(DateTime.Now)) })),
        new("saves-zip-import", "Import saves from a .zip file: its slots, what they replace, and the backup made first", Platform.Windows,
            s => { ThreeSlots(s); ZipOf(s, "Saves from my other PC.zip", "slot_00", "slot_02"); },
            m => new SavesConfirmViewModel(new SavesViewModel(m), new SaveOp { Mode = SaveMode.Import, Zip = Documents(m, "Saves from my other PC.zip") })),
        new("saves-zip-refused", "A .zip that is not only saves: refused, and why", Platform.Windows,
            s =>
            {
                ThreeSlots(s);
                using var zip = System.IO.Compression.ZipFile.Open(Documents(s, "Downloaded saves.zip"), System.IO.Compression.ZipArchiveMode.Create);
                foreach (var name in new[] { "slot_00/progress.sav", "slot_00/caption.sav", "setup.exe" })
                    using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(name);
            },
            m => new SavesConfirmViewModel(new SavesViewModel(m), new SaveOp { Mode = SaveMode.Import, Zip = Documents(m, "Downloaded saves.zip") })),
        // (the backups the copies made: an import from Lichii, the versions' saves brought together, a version deleted)
        new("saves-backups-listed", "The Saves page with backups: Restore from a backup, how many and the newest", Platform.Windows,
            s => { ThreeSlots(s); MadeBackups(s); }, m => new SavesViewModel(m)),
        new("saves-backups", "Restore from a backup: every save the copies backed up, newest first, what made it", Platform.Windows,
            s => { ThreeSlots(s); MadeBackups(s); }, m => new BackupsViewModel(new SavesViewModel(m))),
        new("saves-backups-linux", "Restore from a backup on Linux: the launcher's folder and the older oot-save-backups, each row saying which",
            Platform.Linux, s => { ThreeSlots(s); LinuxBackups(s); }, m => new BackupsViewModel(new SavesViewModel(m))),
        new("saves-restore-export", "Restoring an export's backup: the other program's old save, into Longshot", Platform.Windows,
            s => { ThreeSlots(s); MadeBackups(s); },
            m =>
            {
                var page = new SavesViewModel(m);
                return new SavesConfirmViewModel(page, new SaveOp { Mode = SaveMode.Restore, Backup = page.Backups.First(b => b.Kind == BackupKind.Export) });
            }),
        new("saves-restore", "Restoring a backup: its slots, what they replace, and Longshot's save backed up first", Platform.Windows,
            s => { ThreeSlots(s); MadeBackups(s); },
            m =>
            {
                var page = new SavesViewModel(m);
                return new SavesConfirmViewModel(page, new SaveOp { Mode = SaveMode.Restore, Backup = page.Backups.First(b => b.Kind == BackupKind.Import) });
            }),
        new("hookshot-import-problem", "Importing Hookshot's saves failed, Copy for a bug report", Platform.Windows, null, _ => null,
            m => m.Modal = new HookshotImportViewModel(m) { Problem = @"The saves could not be copied: Access to the path 'C:\Users\Player\Saved Games\Longshot\slot1' is denied." }),
        // (the settings tick: off, Longshot's game options replace Lichii's; on, Lichii keeps its own option.sav)
        new("saves-confirm-replace", "Export confirm: Longshot's game settings replace Lichii's (the tick is off)", Platform.Windows,
            SavesFixture, m => SavesConfirm(m, keep: false)),
        new("saves-confirm-keep", "Export confirm: the tick keeps Lichii's game settings (option.sav)", Platform.Windows,
            SavesFixture, m => SavesConfirm(m, keep: true)),
        new("saves-confirm-no-settings", "Export confirm: Lichii's save has no game settings to keep, so the tick is not shown",
            Platform.Windows, SavesNoSettingsFixture, m => SavesConfirm(m, keep: false)),
        // (the save editor: Longshot's own editor in the build's tools. Nothing is run here: its answers are the report in
        // save-editor-report.json, authored with invented names, and the few lines of text an edit says)
        new("saves-progress", "Saves: each slot has Progress, which opens the save editor", Platform.Linux, s => EditorReady(s), m => new SavesViewModel(m)),
        new("saves-progress-off", "Saves with a version whose tools have no save editor yet: Progress is off, and says why", Platform.Linux,
            s => { SeedSlots(s); s.SaveEditorOverride = () => (null, SaveEditorWhy.NoEditor); }, m => new SavesViewModel(m)),
        new("save-editor", "A slot's progress: every category with what the slot has, and what is missing by name and area", Platform.Linux,
            s => EditorReady(s), m => Editor(m)),
        new("save-editor-skulltulas", "A category with areas: what the slot has in each, and every missing one by name and where", Platform.Linux, s => EditorReady(s),
            m => Editor(m), m => Opened(m).Categories.First(c => c.Category.Id == "skulltulas").PickCommand.Execute(null)),
        new("save-editor-confirm", "Set one area: what the editor says it would change, where the backup goes", Platform.Linux, s => EditorReady(s),
            m => Editor(m), m => m.Modal = new SaveEditConfirmViewModel(Opened(m), SaveEditPlan.Set("slot_00", "skulltulas/HollowWood", "Set everything in Gold Skulltulas, Hollow Wood: done, owned or found."), null)),
        new("save-editor-running", "The same edit while a game runs: refused, nothing changed", Platform.Linux, s => EditorReady(s, ["Longshot"]),
            m => Editor(m), m =>
            {
                var confirm = new SaveEditConfirmViewModel(Opened(m), SaveEditPlan.Set("slot_00", "skulltulas/HollowWood", "Set everything in Gold Skulltulas, Hollow Wood: done, owned or found."), null);
                m.Modal = confirm;
                confirm.ApplyCommand.Execute(null);
            }),
        new("save-editor-give", "Give: an item, hearts, rupees or a dungeon's small keys, within what the game allows", Platform.Linux, s => EditorReady(s),
            m => Editor(m), m => m.Modal = new SaveEditAskViewModel(Opened(m), SaveAsk.Give)),
        new("save-editor-start", "Start place: where Link begins, from the places the game has", Platform.Linux, s => EditorReady(s),
            m => Editor(m), m => m.Modal = new SaveEditAskViewModel(Opened(m), SaveAsk.Start)),
        new("save-editor-failed", "The editor could not read the slot (a damaged file here): the launcher's words and the editor's, Try again, Copy for a bug report", Platform.Linux,
            s => EditorReady(s, tells: _ => EditorRefuses("report", "damaged", "slot_00/progress.sav is 23 bytes: not a save file")),
            m => Editor(m)),
    ];

    /// Longshot's shared save and Lichii's, both with slots and game settings, for the export confirm screens
    static void SavesFixture(LauncherService service) => SavesFixture(service, settings: true);

    /// The same, with Lichii's save holding no option.sav: the tick that keeps its settings is not shown
    static void SavesNoSettingsFixture(LauncherService service) => SavesFixture(service, settings: false);

    static void SavesFixture(LauncherService service, bool settings)
    {
        // (a player's backup folder, as the confirm page names it: the scene's home is a temporary one)
        service.SaveEnv = service.SaveEnv with { BackupFolder = @"C:\Users\Player\AppData\Local\Longshot\oot-save-backups" };
        SaveWithSlots(Saves.ImportTarget(service.SaveEnv), 2, settings: true);
        SaveWithSlots(LichiiSave(service.SaveEnv), settings ? 3 : 2, settings);
    }

    static void SaveWithSlots(string folder, int slots, bool settings = true)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "account.sav"), "account");
        if (settings) File.WriteAllText(Path.Combine(folder, "option.sav"), "option");
        for (var n = 0; n < slots; n++)
        {
            var slot = Directory.CreateDirectory(Path.Combine(folder, $"slot_{n:00}")).FullName;
            File.WriteAllText(Path.Combine(slot, "progress.sav"), "progress");
            File.WriteAllText(Path.Combine(slot, "caption.sav"), "caption");
        }
    }

    /// Lichii's data folder and the save inside it (Core/Saves.cs's layouts: nand/user/save/account/&lt;profile&gt;/&lt;title&gt;)
    static string LichiiFolder(SaveEnvironment env) => Path.Combine(Path.GetDirectoryName(env.LongshotUser)!, "lichii");

    static string LichiiSave(SaveEnvironment env) =>
        Path.Combine(LichiiFolder(env), "nand", "user", "save", Saves.NoAccount, "0123456789abcdef0123456789abcdef", Saves.Title);

    /// The export confirm screen, its settings tick as asked
    static SavesConfirmViewModel SavesConfirm(MainViewModel main, bool keep)
    {
        var page = new SavesViewModel(main);
        var env = main.Service.SaveEnv;
        var program = Saves.Named("lichii");
        var (folder, profiles) = Saves.Locate(program, LichiiFolder(env), env);
        var confirm = new SavesConfirmViewModel(page, new SaveOp
        {
            Mode = SaveMode.Export,
            Set = new SaveSet(program.Name, program.Display, folder, profiles, null),
            Folder = folder,
        });
        confirm.KeepSettings = keep;
        return confirm;
    }

    /// Longshot's save with three slots (made-up files: only their names and times are a save's), saved 2 hours, a day and 6 days ago
    /// Fake NFC tags in the scene's NFC folder (no real dump: CONTRIBUTING.md): 540 patterned bytes with an NTAG215's capability
    /// container at 0x0C and a figure's 0x02 at 0x5B (docs/nfc.md)
    static void FakeNfc(LauncherService service, int count)
    {
        var folder = Directory.CreateDirectory(NfcTags.Folder(service.Platform, service.SaveEnv.Getenv, null)).FullName;
        for (var n = 1; n <= count; n++)
        {
            var tag = new byte[540];
            for (var i = 0; i < tag.Length; i++) tag[i] = (byte)(i * 7 + n * 13);
            new byte[] { 0xF1, 0x10, 0xFF, 0xEE }.CopyTo(tag, 0x0C);
            tag[0x5B] = 0x02;
            File.WriteAllBytes(Path.Combine(folder, $"Figure {n:000}.bin"), tag);
        }
    }

    static void ThreeSlots(LauncherService service) => FakeSave(Saves.ImportTarget(service.SaveEnv), "slot_00", "slot_01", "slot_03");

    /// The backups a player has after a while (made-up saves, as the copies make them): the versions' saves brought together
    /// 9 days ago, an import from Lichii 3 days ago, a version deleted yesterday
    static void MadeBackups(LauncherService service)
    {
        var env = service.SaveEnv;
        var root = env.BackupRoot;
        string Stamp(TimeSpan ago) => (DateTime.Now - ago).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        FakeSave(Path.Combine(root, "gather-" + Stamp(TimeSpan.FromDays(9)), "shared", Saves.UserId), DateTime.Now.AddDays(-9), "slot_00");
        FakeSave(Path.Combine(root, "gather-" + Stamp(TimeSpan.FromDays(9)), "v3.0.0-rc.5", Saves.UserId), DateTime.Now.AddDays(-9), "slot_00", "slot_01");
        FakeSave(Path.Combine(root, "import-lichii-" + Stamp(TimeSpan.FromDays(3))), DateTime.Now.AddDays(-3), "slot_00", "slot_01", "slot_02");
        FakeSave(Path.Combine(root, "delete-" + Stamp(TimeSpan.FromDays(1)), "nightly (2)", Saves.UserId), DateTime.Now.AddDays(-1), "slot_04");
        FakeSave(Path.Combine(root, "export-lichii-" + Stamp(TimeSpan.FromDays(2))), DateTime.Now.AddDays(-5), "slot_00", "slot_01");
    }

    /// Backups on Linux in both folders (issue #188): the launcher's own (longshot/save-backup) and the older oot-save-backups,
    /// where Longshot's script and the game's own backups go
    static void LinuxBackups(LauncherService service)
    {
        var env = service.SaveEnv;
        string Stamp(TimeSpan ago) => (DateTime.Now - ago).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        FakeSave(Path.Combine(env.BackupRoot, "import-lichii-" + Stamp(TimeSpan.FromDays(1))), DateTime.Now.AddDays(-2), "slot_00", "slot_01");
        FakeSave(Path.Combine(env.Data, "oot-save-backups", "export-eden-" + Stamp(TimeSpan.FromDays(4))), DateTime.Now.AddDays(-6), "slot_00");
        FakeSave(Path.Combine(env.Data, "oot-save-backups", "gather-" + Stamp(TimeSpan.FromDays(30)), "shared", Saves.UserId), DateTime.Now.AddDays(-31), "slot_00", "slot_02");
    }

    /// A file in the scene's player's Documents folder
    static string Documents(LauncherService service, string name) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(service.SaveEnv.Getenv("USERPROFILE")!, "Documents")).FullName, name);

    static string Documents(MainViewModel main, string name) => Documents(main.Service, name);

    /// A .zip of saves in Documents, made by the launcher from another made-up save
    static void ZipOf(LauncherService service, string name, params string[] slots)
    {
        var other = Path.Combine(service.SaveEnv.Getenv("USERPROFILE")!, "other-pc", SaveArchive.Top);
        FakeSave(Path.Combine(other, Saves.UserId), slots);
        SaveArchive.ExportSave(other, Documents(service, name), service.OwnTag, () => []);
    }

    static void FakeSave(string folder, params string[] slots) => FakeSave(folder, DateTime.Now, slots);

    /// A made-up save whose slots were saved 2 hours, a day and 6 days before at
    static void FakeSave(string folder, DateTime at, params string[] slots)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "account.sav"), "account");
        File.WriteAllText(Path.Combine(folder, "option.sav"), "option");
        var ago = new[] { TimeSpan.FromHours(2), TimeSpan.FromDays(1), TimeSpan.FromDays(6) };
        for (var i = 0; i < slots.Length; i++)
        {
            Directory.CreateDirectory(Path.Combine(folder, slots[i]));
            File.WriteAllText(Path.Combine(folder, slots[i], "progress.sav"), "progress " + slots[i]);
            File.WriteAllText(Path.Combine(folder, slots[i], "caption.sav"), "caption " + slots[i]);
            File.SetLastWriteTime(Path.Combine(folder, slots[i], "progress.sav"), at - ago[i % ago.Length]);
        }
    }

    // ---- the save editor's scenes ----

    static readonly SaveEditorTool EditorTool = new("/home/player/.local/share/longshot/launcher/toolchains/v3.1.0/usr/python/bin/python3",
        "/home/player/.local/share/longshot/launcher/toolchains/v3.1.0/usr/share/longshot/scripts/longshot_save_editor.py",
        "/home/player/.local/share/longshot/launcher/toolchains/v3.1.0/usr/share/longshot", "/home/player/Games/Longshot/v3.1.0");

    /// The editor's answers as JSON (its guide, "For programs"): invented items, places and dungeons that match the authored report
    static string EditorItems() => new JsonObject
    {
        ["schema"] = 1, ["command"] = "items", ["ok"] = true,
        ["items"] = new JsonArray(new (string Id, string Label, string Category, bool Counted)[]
        {
            ("Blade_Dawn", "Sword of Dawn", "equipment", false), ("Item_Lantern", "Lantern of Dusk", "items", false), ("Item_Claw", "Grapple Claw", "items", false),
            ("Item_Bomb", "Bombs", "items", true), ("Item_Arrow", "Arrows", "items", true), ("Pouch_L", "Large Bomb Pouch", "upgrades", false),
            ("Quiver_L", "Large Quiver", "upgrades", false), ("Wallet_L", "Large Wallet", "upgrades", false),
        }.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["label"] = i.Label, ["category"] = i.Category, ["counted"] = i.Counted }).ToArray()),
        ["gives"] = new JsonArray("hearts", "rupees"),
        ["small_keys"] = new JsonArray(new[] { ("StoneKeep", 4), ("HollowTemple", 6), ("SaltCavern", 5), ("EmberDepths", 7) }
            .Select(k => (JsonNode)new JsonObject { ["dungeon"] = k.Item1, ["of"] = k.Item2 }).ToArray()),
        ["hearts"] = new JsonObject { ["min"] = 1, ["max"] = 20 },
    }.ToJsonString();

    static string EditorPlaces() => new JsonObject
    {
        ["schema"] = 1, ["command"] = "places", ["ok"] = true,
        ["places"] = new JsonArray(new (string Area, string Name, string? Age)[]
        {
            ("MeadowVillage_Adult", "(default)", "adult"), ("MeadowVillage_Adult", "Gate", "adult"), ("MeadowVillage_Child", "(default)", "child"),
            ("StoneMarket", "(default)", null), ("StoneMarket", "Fountain", null), ("HollowWood", "Clearing", null), ("SaltCavern", "EntrancePos", null),
        }.Select(p => (JsonNode)new JsonObject
        {
            ["area"] = p.Area, ["name"] = p.Name, ["default"] = p.Name == "(default)", ["spec"] = p.Area + "/" + p.Name, ["age"] = p.Age, ["loadable"] = true,
        }).ToArray()),
    }.ToJsonString();

    /// An edit's answer: what it said, the files, and (not a dry run) the backup it made and what it read back
    static string EditorEdit(string command, string slot, string[] said, string[] files, bool dry) => new JsonObject
    {
        ["schema"] = 1, ["command"] = command, ["ok"] = true, ["slot"] = slot, ["dry_run"] = dry, ["changed"] = true,
        ["said"] = new JsonArray(said.Select(x => (JsonNode)x).ToArray()), ["changes"] = new JsonArray(),
        ["files"] = new JsonArray(files.Select(x => (JsonNode)x).ToArray()),
        ["backup"] = dry ? null : "/home/player/.local/share/longshot/save-backup/edit-5a3c9e1f0b7d2468-20261116-091503",
        ["read_back"] = dry ? null : new JsonObject { ["complete"] = false, ["missing_total"] = 346 },
    }.ToJsonString();

    /// What the editor says to a command (the arguments after its fixed ones): the authored report, the lists, and each edit's answer
    static SaveEditorRun EditorSays(IReadOnlyList<string> args)
    {
        switch (args[0])
        {
            case "report": return new SaveEditorRun(0, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-editor-report.json")), "");
            case "items": return new SaveEditorRun(0, EditorItems(), "");
            case "places": return new SaveEditorRun(0, EditorPlaces(), "");
        }
        var dry = args.Contains("--dry-run");
        var progress = new[] { $"{args[1]}/progress.sav", $"{args[1]}/caption.sav" };
        return new SaveEditorRun(0, args[0] switch
        {
            "set" or "clear" => EditorEdit(args[0], args[1], [$"{args[0]} {args[2]}: 3 of 7 changed"], progress, dry),
            "give" => EditorEdit("give", args[1], args.Skip(2).Where(a => !a.StartsWith('-')).Select(a => a.Contains('=') ? a.Replace('=', ':').Replace("hearts:", "hearts: ") : a + ": given").ToArray(), progress, dry),
            "start" => EditorEdit("start", args[1], ["start: adult Link in StoneMarket at 12.0, 5.0, -4.0"], progress, dry),
            _ => EditorEdit("copy", args[2], [$"copy {args[1]} -> {args[2]} (caption.sav, progress.sav)"], [$"{args[2]}/caption.sav", $"{args[2]}/progress.sav"], dry),
        }, "");
    }

    /// A refusal as the editor says it: JSON on stdout, exit 1
    static SaveEditorRun EditorRefuses(string command, string code, string message) =>
        new(1, new JsonObject { ["schema"] = 1, ["command"] = command, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), "");

    /// Saves for the scene: three slots in the shared save folder of the scene's own home
    static string SeedSlots(LauncherService service)
    {
        var folder = Saves.ImportTarget(service.SaveEnv);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "account.sav"), "x");
        var now = DateTime.Now;
        foreach (var (n, ago) in new[] { (0, TimeSpan.FromHours(3)), (1, TimeSpan.FromDays(2)), (2, TimeSpan.FromDays(9)) })
        {
            var slot = Directory.CreateDirectory(Path.Combine(folder, $"slot_{n:00}")).FullName;
            File.WriteAllText(Path.Combine(slot, "progress.sav"), "x");
            File.SetLastWriteTime(Path.Combine(slot, "progress.sav"), now - ago);
        }
        return folder;
    }

    /// A save editor that can run: the slots seeded, the editor answering as EditorSays (or as tells says), a game running or not
    static void EditorReady(LauncherService service, IReadOnlyList<string>? running = null, Func<IReadOnlyList<string>, SaveEditorRun>? tells = null)
    {
        var folder = SeedSlots(service);
        // (the build's game folder, with its version's stamp: the page says whose game files it reads)
        var game = Directory.CreateDirectory(Path.Combine(service.SaveEnv.Getenv("HOME")!, "Games", "Longshot", "v3.1.0")).FullName;
        File.WriteAllText(Path.Combine(game, GameFolder.StampName), "version=v3.1.0\n");
        var tool = EditorTool with { Game = game };
        // (the folder the backups go to, as the player's would be, not the scene's own home)
        service.SaveEnv = service.SaveEnv with { BackupFolder = "/home/player/.local/share/longshot/save-backup" };
        var runner = new SaveEditorRunner((start, _) => Task.FromResult((tells ?? EditorSays)(start.ArgumentList.Skip(9).ToList())));
        service.SaveEditorOverride = () => (new SaveEditing(tool, service.SaveEnv, folder, runner, () => running ?? []), SaveEditorWhy.Ready);
    }

    /// The Saves page with its slot 1 opened in the editor
    static SaveEditorViewModel Editor(MainViewModel main) =>
        new(new SavesViewModel(main), main.Service.SaveEditor().Editing!, "slot_00");

    static SaveEditorViewModel Opened(MainViewModel main) => (SaveEditorViewModel)main.Page;

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

    /// A nightly build (the default) and a pull request's build one push behind, and the open pull requests Beanstalk lists: one a
    /// BigBean wrote, one a BigBean approved, one nobody approved (found only by its number), a draft
    static void PullRequestsListed(LauncherService service)
    {
        // (Linux paths: a folder under one that is missing is not taken for a build deleted outside the launcher)
        service.Registry.Add("/home/player/Games/Longshot/nightly/nightly-189-aa95d35f", "nightly-189-aa95d35f", Versions.Nightly, makeDefault: true);
        service.SetNewest(Versions.Nightly, "nightly-189-aa95d35f");
        service.Registry.Add("/home/player/Games/Longshot/pr/pr-1548-3f9a0c21", "pr-1548-3f9a0c21", Versions.PullRequest);
        PullRequest Pr(int n, string title, string author, string head, bool bigbean, bool approved, bool draft = false) =>
            new(n, title, author, head + new string('0', 40 - head.Length), draft, [], bigbean, approved, approved ? ["mooki"] : []);
        service.SetPullRequests(
        [
            Pr(1571, "perf(renderer): batch the shadow draws by material", "newcomer42", "c0ffee12", false, false),
            Pr(1563, "fix(audio): Navi's voice no longer cuts out after a cutscene", "mooki", "8d2e41b7", true, false),
            Pr(1560, "feat(menu): a page for the controller's gyro", "TrashPanda", "51aa9e03", false, true),
            Pr(1558, "WIP: perf(host): filter translated store marks", "HumanBean", "b268d901", true, false, draft: true),
            Pr(1548, "fix(saves): the Kokiri sword keeps its slot after a reload", "omgitsgela", "a7c4410e", false, true),
        ]);
        service.AddFound(1571);
    }

    /// What the Storage page shows: two versions of one dump (a stable and a nightly), typical sizes from Longshot's builder
    /// (the game files ~26 GB once, a version's shader caches ~10 GB, a build's work ~1.1 GB, an earlier launcher's stores)
    static readonly IReadOnlyList<Storage.Item> StorageShown =
    [
        new(Storage.Kind.GameFiles, 26_200_000_000, 26_200_000_000, new("a", "v3.0.3", @"C:\Users\Player\Games\Longshot\stable\v3.0.3"), Users:
            [new("a", "v3.0.3", @"C:\Users\Player\Games\Longshot\stable\v3.0.3"),
             new("b", "Nightly 189 · aa95d35f", @"C:\Users\Player\Games\Longshot\nightly\nightly-189-aa95d35f")]),
        new(Storage.Kind.Programs, 1_050_000_000, 1_050_000_000, new("a", "v3.0.3", @"C:\Users\Player\Games\Longshot\stable\v3.0.3")),
        new(Storage.Kind.Programs, 1_050_000_000, 1_050_000_000, new("b", "Nightly 189 · aa95d35f", @"C:\Users\Player\Games\Longshot\nightly\nightly-189-aa95d35f")),
        new(Storage.Kind.ShaderCache, 9_600_000_000, 9_600_000_000, new("a", "v3.0.3", @"C:\Users\Player\Games\Longshot\stable\v3.0.3")),
        new(Storage.Kind.ShaderCache, 9_800_000_000, 9_800_000_000, new("b", "Nightly 189 · aa95d35f", @"C:\Users\Player\Games\Longshot\nightly\nightly-189-aa95d35f")),
        new(Storage.Kind.ShaderStore, 3_758_096_384, 3_758_096_384),
        new(Storage.Kind.RecentCode, 990_000_000, 990_000_000),
        new(Storage.Kind.RecentTranslation, 130_000_000, 130_000_000),
        new(Storage.Kind.OlderCode, 2_420_000_000, 2_420_000_000),
        new(Storage.Kind.OlderTranslations, 3_220_000_000, 3_220_000_000),
        new(Storage.Kind.Builders, 1_900_000_000, 0),
        new(Storage.Kind.GraphicsLibraries, 700_000_000, 700_000_000),
        new(Storage.Kind.UnfinishedDownloads, 200_000_000, 200_000_000),
        new(Storage.Kind.Saves, 330_000_000, 0),
        new(Storage.Kind.ModsAndSettings, 70_000_000, 0),
        new(Storage.Kind.Logs, 100_000_000, 100_000_000),
    ];

    /// StorageShown with each version's items under the id of its row on Home (the folder's), for the rows' delete
    /// And two game folders no row lists, as updates before #104 left them: one nothing of the player's is in, one with mods
    static IReadOnlyList<Storage.Item> StorageListed(MainViewModel m) =>
        StorageShown.Select(i => i.Game is { } g && m.Service.Registry.FindByFolder(g.Folder) is { } b ? i with { Game = g with { Id = b.Id } } : i)
            .Concat([
                new(Storage.Kind.Unlisted, 11_400_000_000, 11_400_000_000,
                    new("unlisted:130", "Nightly 130 · 0caf0618", @"C:\Users\Player\Games\Longshot\nightly-130-0caf0618", Unlisted: true)),
                new(Storage.Kind.Unlisted, 10_900_000_000, 10_900_000_000,
                    new("unlisted:77", "Nightly 77 · 9f64464b", @"C:\Users\Player\Games\Longshot\nightly-77-9f64464b", Unlisted: true, HoldsYours: true)),
                // (a second copy of the game files, an old version's own; a third, a PR build's, linked from a developer's base)
                new(Storage.Kind.GameFiles, 26_100_000_000, 26_100_000_000,
                    new("unlisted:77", "Nightly 77 · 9f64464b", @"C:\Users\Player\Games\Longshot\nightly-77-9f64464b", Unlisted: true),
                    Users: [new("unlisted:77", "Nightly 77 · 9f64464b", @"C:\Users\Player\Games\Longshot\nightly-77-9f64464b", Unlisted: true)]),
                new(Storage.Kind.GameFiles, 26_200_000_000, 90_000_000,
                    new("unlisted:130", "Nightly 130 · 0caf0618", @"C:\Users\Player\Games\Longshot\nightly-130-0caf0618", Unlisted: true),
                    Users: [new("unlisted:130", "Nightly 130 · 0caf0618", @"C:\Users\Player\Games\Longshot\nightly-130-0caf0618", Unlisted: true)],
                    Elsewhere: [@"C:\Users\Player\Longshot-dev\bases\47430e8140b7\game", @"C:\Users\Player\Longshot-dev\bases\47430e8140b7\game-out"]),
            ]).ToList();

    /// StorageListed on two drives, as a Windows PC with its games on E: and the launcher's own folder and the saves on C:: the
    /// builders on both (an older one left on C: before the folder moved)
    static IReadOnlyList<Storage.Item> StorageOnDrives(MainViewModel m) =>
        StorageListed(m).SelectMany(i => i.Kind == Storage.Kind.Builders
                ? new[] { i with { Bytes = 1_200_000_000, Drive = "E:" }, i with { Bytes = 700_000_000, Frees = 700_000_000, Drive = "C:" } }
                : new[] { i with { Drive = i.Kind is Storage.Kind.Saves or Storage.Kind.Logs or Storage.Kind.GraphicsLibraries or Storage.Kind.UnfinishedDownloads ? "C:" : "E:" } })
            .ToList();

    /// A nightly build of the newest nightly, and the release list the update check read: the 5 nightlies the server keeps
    /// Two builds measured as one PC's were (Disk.Measure): nightly-486 and v3.0.0 share 24.4 GB of game files by hard links, the
    /// nightly counting them; store: the shader store's bytes
    static void SharedSizes(LauncherService service, long store)
    {
        const long gb = 1L << 30;
        var nightly = service.Registry.Add(@"C:\Users\Player\Games\Longshot\nightly\nightly-486-3f1c2a7b", "nightly-486-3f1c2a7b", Versions.Nightly);
        var stable = service.Registry.Add(@"C:\Users\Player\Games\Longshot\stable\v3.0.0", "v3.0.0", Versions.Stable, makeDefault: true);
        (nightly.Bytes, nightly.Counted, nightly.Frees) = ((long)(36.6 * gb), (long)(36.6 * gb), (long)(12.2 * gb));
        (stable.Bytes, stable.Counted, stable.Frees) = ((long)(36.0 * gb), (long)(11.6 * gb), (long)(11.6 * gb));
        nightly.Updated = DateTimeOffset.Now.AddDays(-1);
        service.ShaderStoreOverride = () => store;
    }

    static void NightliesListed(LauncherService service)
    {
        service.Registry.Add(@"C:\Users\Player\Games\Longshot\nightly\nightly-189-aa95d35f", "nightly-189-aa95d35f", Versions.Nightly, makeDefault: true);
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

    /// The nightly releases NightliesListed has, two of them built with the new builder (their manifest in the list)
    static void NewBuilderListed(LauncherService service)
    {
        // (a folder that exists, or the home drops the build as deleted outside the launcher)
        var folder = Path.Combine(Path.GetTempPath(), "needs-nightly-shot", "nightly-189-aa95d35f");
        Directory.CreateDirectory(folder);
        service.Registry.Add(folder, "nightly-189-aa95d35f", Versions.Nightly, makeDefault: true);
        service.SetNewest(Versions.Nightly, "nightly-189-aa95d35f");
        ListedRelease Nightly(string tag, string at) =>
            new(tag, false, true, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), ReleaseNotes.PageOf(Product.Longshot, tag), "", []);
        service.Releases.Listed =
        [
            Nightly("nightly-189-aa95d35f", "2026-11-15T11:10:00Z"),
            Nightly("nightly-188-a00c485c", "2026-11-15T10:10:00Z"),
            Nightly("nightly-187-9e6f231d", "2026-11-15T09:22:00Z"),
        ];
        service.Releases.Listed = service.Releases.Listed!.Select(r => r.Tag is "nightly-189-aa95d35f" or "nightly-188-a00c485c"
            ? r with { Assets = [(ReleaseManifest.BuilderManifestName(r.Tag, "linux-x64"), "https://git.magicbean.dev/x.json", 10)] } : r).ToList();
    }

    /// The nightly releases NightliesListed has, with the build two nightlies behind: its track's newest is out
    static void NightlyBehind(LauncherService service)
    {
        service.Registry.Add(@"C:\Users\Player\Games\Longshot\nightly\nightly-187-9e6f231d", "nightly-187-9e6f231d", Versions.Nightly, makeDefault: true);
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

    /// A game file whose check stopped with make_game.py's @@error text: the status the launcher gives it (GameCheck.Of)
    static (string, GameCheck) NxCheck(string path, string error) => (path, new GameCheck(GameCheck.Of(error), error));

    /// A Steam of the scene's own (its userdata, loginusers.vdf and steam.sh) and two builds, so the
    /// Ready screen shows all four ticks: Steam is read from $XDG_DATA_HOME, and the shortcuts the
    /// screen writes go to the scene's own HOME, both restored when the scene is done
    static void ReadySetup(LauncherService s)
    {
        var home = s.Home.Root;
        var steam = Path.Combine(home, "xdg", "Steam");
        var config = Path.Combine(steam, "userdata", "33811576", "config");
        Directory.CreateDirectory(config);
        File.WriteAllBytes(Path.Combine(config, "shortcuts.vdf"), []);
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        // (the account Steam says it used: the low 32 bits of the id are the userdata folder)
        File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"),
            "\"users\"\n{\n\t\"76561197994077304\"\n\t{\n\t\t\"AccountName\"\t\t\"player\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}\n");
        File.WriteAllText(Path.Combine(steam, "steam.sh"), "#!/bin/bash\n");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(home, "xdg"));
        Environment.SetEnvironmentVariable("HOME", Path.Combine(home, "home"));
        // (the launcher's own home too: EnsureLogo and EnsureLogoIco write the logo there, and
        // LauncherHome.Resolve puts LONGSHOT_LAUNCHER_HOME ahead of HOME)
        Environment.SetEnvironmentVariable("LONGSHOT_LAUNCHER_HOME", home);
        s.Registry.Add("/home/player/Games/Longshot/v3.0.0", "v3.0.0", Versions.Stable, makeDefault: true);
        s.Registry.Add("/home/player/Games/Longshot/nightly-189-aa95d35f", "nightly-189-aa95d35f", Versions.Nightly);
    }

    static ReadyViewModel Ready(MainViewModel main) =>
        new(main, main.Service.Registry.Builds.First(b => b.Default), JobKind.Build, null, TimeSpan.FromMinutes(24), null);

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

    /// --size: the window's, for every scene that is not the TV view
    static (double Width, double Height)? size;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--check-vulkan") return VulkanChecks.Run();
        var lang = Tr.English;
        while (args.Length >= 2 && args[0] is "--lang" or "--size")
        {
            if (args[0] == "--lang") lang = Tr.Pick(args[1]) ?? throw new ArgumentException("no language " + args[1]);
            else
            {
                var wh = args[1].Split('x');
                size = (double.Parse(wh[0], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(wh[1], System.Globalization.CultureInfo.InvariantCulture));
            }
            args = args[2..];
        }
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("work", "screenshots"));
        var wanted = args.Skip(1).ToHashSet(StringComparer.Ordinal);
        var unknown = wanted.Where(w => Scenes.All(s => s.Name != w)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine("no scene " + string.Join(", ", unknown) + "; the scenes: " + string.Join(", ", Scenes.Select(s => s.Name)));
            return 2;
        }
        Directory.CreateDirectory(outDir);
        MainWindow.Headless = true;
        AppBuilder.Configure<App>().UseSkia().With(App.Fonts()).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Tr.Use(lang);
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
        // (a scene may point the environment at its own Steam and home: restored when it is done)
        var realHome = Environment.GetEnvironmentVariable("HOME");
        var realData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var realLauncherHome = Environment.GetEnvironmentVariable("LONGSHOT_LAUNCHER_HOME");
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
            // (Vulkan not read: the scene's GpuCards are the cards the switches judge, unless the scene says which the game takes)
            service.VulkanProbe = () => Task.FromResult<IReadOnlyList<GpuRequirement>?>(null);
            scene.Setup?.Invoke(service);
            var main = new MainViewModel(service, StartRequest.Parse([]));
            // (the TV view: as Steam's Gaming Mode asks for it, on a 1080p screen)
            Environment.SetEnvironmentVariable("LONGSHOT_LAUNCHER_TV", scene.Tv ? "1" : "0");
            var window = new MainWindow { DataContext = main };
            if (scene.Tv)
            {
                window.Width = 1920;
                window.Height = 1080;
            }
            else if (size is { } resized)
            {
                window.Width = resized.Width;
                window.Height = resized.Height;
            }
            window.Show();
            if (scene.Page(main) is { } page) main.Navigate(page);
            scene.Then?.Invoke(main);
            // (layout, bindings and the page's cross-fade settled before the frame is taken)
            for (var i = 0; i < 30; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            // (a scene about a dropdown: the page's nth ComboBox open, its list drawn over the page)
            if (scene.OpenDropdown >= 0)
            {
                window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>().ElementAt(scene.OpenDropdown).IsDropDownOpen = true;
                for (var i = 0; i < 10; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
            }
            // (a banner's Details pressed: its whole text shown)
            if (scene.PressDetails)
            {
                foreach (var more in window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(b => b.Classes.Contains("details")))
                    more.IsChecked = true;
                for (var i = 0; i < 10; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
            }
            // (a scene about the end of a long page: its scroll at the bottom)
            if (scene.ScrollToEnd || scene.ScrollDown > 0)
            {
                foreach (var scroll in window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>())
                    if (scene.ScrollToEnd) scroll.ScrollToEnd();
                    else scroll.Offset = new Avalonia.Vector(0, scene.ScrollDown);   // (a scene about the middle of a long page)
                for (var i = 0; i < 10; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
            }
            // (a controller's presses: PadInput's own, as if made on one)
            foreach (var press in scene.Presses ?? [])
            {
                window.Press(press);
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
            // (whatever a scene pointed these at: the run's own, so the next scene starts clean)
            Environment.SetEnvironmentVariable("HOME", realHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", realData);
            Environment.SetEnvironmentVariable("LONGSHOT_LAUNCHER_HOME", realLauncherHome);
            try { home.Delete(recursive: true); }
            catch (IOException) { }
        }
    }
}
