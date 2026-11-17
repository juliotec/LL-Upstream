using System.Text.Json.Nodes;
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
                        Action<MainViewModel>? Then = null, bool ScrollToEnd = false, int OpenDropdown = -1);

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
        new("home-tracks", "The Track dropdown open: what each track has installed", Platform.Windows,
            s =>
            {
                NightliesListed(s);
                s.Registry.Add(@"C:\Users\Player\Games\Longshot\stable", "v3.0.0", Versions.Stable);
            },
            _ => null, OpenDropdown: 0),
        new("home-go-back", "An older nightly picked: Go back to it, built in the nightly build's place", Platform.Windows, NightliesListed,
            _ => null, m => m.Home.PickVersion = m.Home.TrackVersions.First(v => v.Tag == "nightly-187-9e6f231d")),
        new("home-build-track", "A track with no build picked: Build its newest, into a folder of its own", Platform.Windows,
            s =>
            {
                NightliesListed(s);
                s.SetNewest(Versions.Candidate, "v3.1.0-rc.1");
                s.Releases.Listed = [.. s.Releases.Listed!, new("v3.1.0-rc.1", false, true,
                    DateTimeOffset.Parse("2026-11-12T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    ReleaseNotes.PageOf(Product.Longshot, "v3.1.0-rc.1"), "", [])];
            },
            _ => null, m => m.Home.PickTrack = m.Home.Tracks.First(t => t.Id == Versions.Candidate)),
        new("settings-go-back", "Step 2 going back to an older nightly, in the build's own folder", Platform.Windows, NightliesListed,
            m => new SettingsStepViewModel(new BuildJob(m, JobKind.Update, Versions.Nightly, "nightly-187-9e6f231d", m.Service.Registry.Builds[0]))
                 { Free = 412L << 30 }),
        // (Free up space, confirmed first: what it deletes, each part's size, the shader caches as an option)
        new("free-space", "Free up space in the launcher's Settings: what it deletes and why it was kept, the shader caches as an option",
            Platform.Windows, NightliesListed, m => new SettingsViewModel(m),
            m => m.Modal = new FreeSpaceViewModel((SettingsViewModel)m.Page,
                                                  ([3_100_000_000, 1_640_000_000, 410_000_000, 0, 2_900_000_000, 3_280_000_000], 9_800_000_000, 1))),
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

    /// A game file whose check stopped with make_game.py's @@error text: the status the launcher gives it (GameCheck.Of)
    static (string, GameCheck) NxCheck(string path, string error) => (path, new GameCheck(GameCheck.Of(error), error));

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
