using Avalonia;
using LongshotLauncher.Core;
using LongshotLauncher.ViewModels;

namespace LongshotLauncher;

static class Program
{
    /// The start's request (CommandLine.cs)
    public static StartRequest Start { get; private set; } = StartRequest.Parse(Array.Empty<string>());
    /// The arguments as given, for the copy started after Install
    public static IReadOnlyList<string> Args { get; private set; } = Array.Empty<string>();
    /// This launcher's file and kind (installed, portable), and its home
    public static LauncherLayout Layout { get; private set; } = new(Platforms.Current, null, LauncherKind.Plain);
    public static LauncherHome Home { get; private set; } = LauncherHome.Resolve(Platforms.Current);
    /// --uninstall: the window asks, then takes the installed launcher away (Installer)
    public static bool Uninstalling { get; private set; }
    /// What the start put right of an update cut short (SelfUpdate.Recover), for the home to say; null: nothing
    public static string? Recovered { get; private set; }

    static InstanceLock? hold;
    /// Where a crash with no log file open writes (LauncherLog.Crash): the logs folder once the home is known, null before it (the
    /// default home may not be this launcher's: a portable one's is beside it)
    public static string? CrashLogs { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // (the launcher's own update: a new launcher answers --self-check before it is put in place; --rollback puts back the one the
        // last update replaced. SelfUpdate.cs)
        if (args is [SelfUpdate.CheckArgument])
        {
            Console.WriteLine(SelfUpdate.CheckLine(SelfUpdate.OwnTag()));
            return 0;
        }
        // (started as an administrator by the launcher itself, to raise NVIDIA's shader cache and nothing else: LauncherService)
        if (args is [LauncherService.RaiseNvidiaCacheArgument])
            return OperatingSystem.IsWindows() ? LauncherService.RaiseNvidiaCacheHere() : 1;
        // (an exception nothing catches, on any thread: in the log file before the process ends (LauncherLog.Crash), so a launcher
        // that closes at its start (#152) leaves why; before the home is held, appended to its log file. A task's exception nobody
        // looked at ends nothing: logged, the launcher carries on)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LauncherLog.Crash(e.IsTerminating ? "unhandled exception, the launcher ends" : "unhandled exception", e.ExceptionObject, CrashLogs);
        TaskScheduler.UnobservedTaskException += (_, e) => LauncherLog.Crash("a background task's exception nobody saw", e.Exception);
        // (any DWriteCore.dll Skia would take before Windows' own DirectWrite, for the no-fonts message: FontCheck)
        FontCheck.FindDWriteCore();
        var platform = Platforms.Current;
        // (started from the .old or .new an update cut short left, its own file gone: that file is put back and started instead)
        if (SelfUpdate.CurrentFile(platform) is { } file && SelfUpdate.LeftoverOf(file) is { } own && !File.Exists(own))
        {
            try
            {
                SelfUpdate.Recover(own);
                if (File.Exists(own))
                {
                    WindowsInstall.Start(own, args);
                    return 0;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
        if (args is [SelfUpdate.RollbackArgument]) return Rollback();
        // (its windows grouped as its Start menu shortcut, which names this id: before any window is made)
        if (OperatingSystem.IsWindows()) WindowsInstall.UseAppId();
        Args = args;
        Layout = LauncherLayout.Current(platform);
        Home = LauncherHome.Resolve(platform, null, Layout);
        CrashLogs = Home.Logs;
        Start = StartRequest.Parse(args);
        if (Start.Kind == StartKind.Play) return PlayWithoutWindow(Start);
        // (a launch with no arguments on Linux may be Steam bringing back a Play this launcher just
        // routed to its own tile: the arm says so, and it is claimed HERE, before the lock below.
        // The sender's window still holds that lock while its routed launch arrives, so a launch
        // that took the lock first answered "the launcher is already open", exited, and left the
        // arm to expire while the player saw Steam say "Starting game" and nothing start (Deck,
        // 2026-11-21). With no arm this is the tile pressed from the couch, and the window below,
        // with the release check at its start, is exactly what that launch should get.
        // (round-five L4: only a launch Steam itself started can be that arrival — TakeArrival asks
        // the environment, which is where Steam marks what it launched (measured on a Deck LCD,
        // 2026-11-22: SteamGameId + SteamAppId at the tile's Exe; a desktop icon, a menu entry or a
        // terminal run carries neither). Without that gate, clicking the desktop icon while a routed
        // Play was pending claimed its arm: the icon's launch played the game headless where its
        // user expected a window, and the routed arrival then found nothing)
        if (args.Length == 0 && SteamLaunchRoute.TakeArrival(Home.Root, platform, Environment.GetEnvironmentVariable) is { } arrival)
            return PlayWithoutWindow(Start, arrival);
        Uninstalling = platform == Platform.Windows && Start.Ignored.Contains(Installer.UninstallArgument);

        // (one launcher per home: a second start brings the first one's window forward and ends)
        if (!TakeLock())
        {
            if (Uninstalling) WindowsInstall.Tell("The Longshot Launcher is open. Close it, then uninstall it.");
            else
            {
                WindowsInstall.LetOthersForward();
                InstanceLock.Poke(Home.Root);
                Console.Error.WriteLine("Longshot: the launcher is already open; its window was brought forward.");
            }
            return 0;
        }
        // (a start that hands over to the installed launcher, on the same home, appends to the log: that one rolls it, and the run
        // before is kept)
        var installing = platform == Platform.Windows && !Uninstalling && Start.Ignored.Contains(Installer.InstallArgument);
        OpenLog(roll: !installing && !(!Uninstalling && Layout.OffersInstall(SelfUpdate.OwnTag(), Environment.GetEnvironmentVariable)));
        if (installing) return InstallNow();
        if (!Uninstalling) MoveToCache();
        // (the Windows download started again once the launcher it installed has updated itself: that newer launcher starts, with
        // these arguments, in place of an offer to put this older one back over it. Installer.InstalledIsNewer, #129)
        if (!Uninstalling && Layout.OffersInstall(SelfUpdate.OwnTag(), Environment.GetEnvironmentVariable) && StartNewerInstalled(args)) return 0;
        if (Layout.File is { } current)
        {
            try { Recovered = SelfUpdate.Recover(current); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // (an installed launcher's Apps & features entry follows its own updates; after one, its Start menu shortcut and the game's
        // shortcuts are made again, so none is left pointing at the program the update moved aside: Installer.Refresh)
        if (OperatingSystem.IsWindows() && Layout.Kind == LauncherKind.Installed && SelfUpdate.OwnTag() != "dev")
        {
            try
            {
                var registry = new UserRegistry();
                if (Installer.IsRegistered(registry) && Installer.Refresh(InstallPlaces.Of(), SelfUpdate.OwnTag(), registry, new ShellShortcuts()))
                    WindowsShortcuts.Refresh(LauncherService.LauncherPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                          or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        // Graceful shutdown goes through the window thread so a build can finish putting its files back first.
        using var sigterm = OperatingSystem.IsWindows() ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,
                signal =>
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime) return;
                    signal.Cancel = true;
                    Ui.Post(() =>
                    {
                        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
                        if (ViewModels.BuildStepViewModel.Shutdown.Terminating) return;
                        if (ViewModels.BuildStepViewModel.Current is { } build) build.Terminated(() => desktop.Shutdown());
                        else ViewModels.BuildStepViewModel.Shutdown.Terminate(close => close(), () => desktop.Shutdown(), TimeSpan.FromSeconds(2));
                    });
                });
        try
        {
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            LauncherLog.Add($"the launcher closed ({code})");
            return code;
        }
        // (a PC whose DirectWrite gives the window no fonts: said, with what DirectWrite answered, not a silent crash. FontCheck)
        catch (Exception e) when (FontCheck.IsFontFailure(e))
        {
            LauncherLog.Crash("the window could not start: no fonts", e);
            WindowsInstall.Tell(FontCheck.Message(e));
            return 1;
        }
        finally { ReleaseLock(); }
    }

    /// The log file in the home this launcher holds (LauncherLog.OpenFile; file: play.log for one that does not hold it): its
    /// first line, which launcher on which system
    static void OpenLog(bool roll = true, string file = LauncherLog.FileName)
    {
        var kind = Layout.Kind switch { LauncherKind.Installed => "installed", LauncherKind.Portable => "portable", _ => "not installed" };
        LauncherLog.OpenFile(Home.Logs, $"Longshot Launcher {SelfUpdate.OwnTag()} ({kind}) started; {BugReport.System()}; home {Home.Root}"
                                        + (Args.Count > 0 ? "; arguments: " + string.Join(" ", Args) : ""), file: file, roll: roll);
    }

    /// (Linux) The build files and SDKs a launcher from before kept in its home, moved to the cache folder (LauncherHome.MoveToCache),
    /// only while this launcher holds the home: no other launcher builds in them then. The build files' folder the player chose
    /// stays where it is
    static void MoveToCache()
    {
        if (hold == null) return;
        try
        {
            var settings = LauncherSettings.Load(Home.Settings);
            if (settings.Problem != null) return;   // (the folder chosen cannot be told: nothing moves until it can)
            var chosen = settings.Work is { Length: > 0 } w ? Path.GetFullPath(w) : null;
            foreach (var line in Home.MoveToCache(old => chosen != null && Path.TrimEndingDirectorySeparator(Path.GetFullPath(old))
                                                                           .Equals(Path.TrimEndingDirectorySeparator(chosen), StringComparison.Ordinal)))
                LauncherLog.Add("cache: " + line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { LauncherLog.Add("cache: not moved: " + e.Message); }
    }

    static bool TakeLock()
    {
        try { hold = InstanceLock.TryAcquire(Home.Root, Layout.Platform); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hold = null; return true; }   // (a home it cannot write: the window says so later)
        return hold != null;
    }

    /// The home let go (before another launcher is started on it: after an update, after Install)
    public static void ReleaseLock()
    {
        hold?.Dispose();
        hold = null;
    }

    /// "Run portable": the "portable" file beside the program, and the home beside it from now on. False when that home is held
    /// by another launcher already (it is poked)
    public static bool BecomePortable()
    {
        var file = Layout.File ?? throw new InvalidOperationException("This launcher was not started from its own file.");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, LauncherLayout.PortableMarker),
                          "This file makes the Longshot Launcher beside it portable: its data stays in the LongshotLauncher-data folder here.\r\n");
        ReleaseLock();
        Layout = LauncherLayout.Current(Layout.Platform);
        Home = LauncherHome.Resolve(Layout.Platform, null, Layout);
        if (TakeLock())
        {
            LauncherLog.Add("now portable: its home is " + Home.Root);
            CrashLogs = Home.Logs;
            OpenLog();
            return true;
        }
        InstanceLock.Poke(Home.Root);
        return false;
    }

    /// "Install" (or --install): this program installed (Installer), then the installed copy started with the same arguments
    public static string Install()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Only the Windows launcher installs itself.");
        var file = Layout.File ?? throw new InvalidOperationException("This launcher was not started from its own file.");
        var at = InstallPlaces.Of();
        var installed = Installer.Install(file, SelfUpdate.OwnTag(), at, new UserRegistry(), new ShellShortcuts());
        ReleaseLock();
        WindowsInstall.Start(installed, Args.Where(a => a != Installer.InstallArgument));
        return installed;
    }

    /// The installed launcher started with these arguments, when it is newer than this one (Installer.InstalledIsNewer); false when
    /// it is not, or it could not be read or started (then the download offers Install or Run portable as before)
    static bool StartNewerInstalled(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var at = InstallPlaces.Of();
        try
        {
            if (!Installer.InstalledIsNewer(SelfUpdate.OwnTag(), at, new UserRegistry())) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        ReleaseLock();
        try
        {
            WindowsInstall.Start(at.Program, args);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TakeLock();
            return false;
        }
    }

    static int InstallNow()
    {
        try
        {
            Install();
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception
                                      or System.Security.SecurityException)
        {
            WindowsInstall.Tell("The launcher could not be installed: " + e.Message);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(App.Fonts())   // (the launcher's own Inter as the default font: a system without a default font's name starts too)
        .UseWaylandWithFallback()   // (native Wayland when there is a compositor: through XWayland, a scaled desktop draws the window blurry)
        // (the window's app_id and WM_CLASS: the name of the launcher's .desktop entries, so that a taskbar or dock shows their icon)
        .With(new WaylandPlatformOptions { AppId = Shortcuts.LauncherEntryName })
        .With(new X11PlatformOptions { WmClass = Shortcuts.LauncherEntryName })
        .LogToTrace();

    static int Rollback()
    {
        var platform = Platforms.Current;
        if (SelfUpdate.CurrentFile(platform) is not { } current)
        {
            Console.Error.WriteLine("Longshot: --rollback works from the launcher's own file (its .exe, or the AppImage).");
            return 1;
        }
        try
        {
            if (!SelfUpdate.Rollback(current, platform))
            {
                Console.Error.WriteLine("Longshot: no earlier launcher was kept beside " + current + ".");
                return 1;
            }
            Console.WriteLine("Longshot: the earlier launcher is back in place. Start it again.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Longshot: the earlier launcher could not be put back: " + e.Message);
            return 1;
        }
    }

    /// --play [folder]: that build's game, else the default build's, with no window. Also the way a
    /// routed arrival plays: Main has already claimed its handoff (TakeArrival) and passes it in, so
    /// the arm is read exactly once, by whoever won the claim
    /// Whether Longshot itself is running here (Saves.Running also names emulators and Hookshot, and
    /// the audit's fourth report proved a Count>0 test can suppress a legitimate press because an
    /// unrelated Eden process exists)
    static bool LongshotRunning() =>
        Saves.Running(Platform.Linux).Any(n => n.Contains("Longshot", StringComparison.OrdinalIgnoreCase));

    static int PlayWithoutWindow(StartRequest start, SteamLaunchRoute.Handoff? claimed = null)
    {
        // (the home is the window launcher's, which may be open: play.log, appended to, holds what this start did to the saves)
        OpenLog(file: LauncherLog.PlayName);
        try
        {
            var service = LauncherService.Open();
            // (SteamLaunchRoute: a desktop-mode Play that routed armed a one-shot naming the build
            // that Play asked for — carried in here by Main (a bare arrival) or claimed here (a bare
            // --play) — and without it a routed Play of a non-default row would launch the default
            // build instead (the review's P1). An explicit --play <folder> on this command line IS
            // the user's own request, not our routing artifact, so it never consumes a handoff
            // another Play is still waiting on (the review's probe: an unrelated explicit Play took
            // a pending handoff and the intended launch came up with the wrong build).
            // (round-five L4, the same rule for the bare form: an unquoted `--play` typed in a
            // terminal, or left on a shortcut by an old tile, carries no build and could claim a
            // routed Play's arm — only a launch Steam itself started (SteamGameId/SteamAppId in
            // its environment) may consume one. A bare --play from anywhere else starts the
            // default build, which is exactly what the no-handoff branch below does)
            var handoff = claimed ?? (start.Folder == null && SteamLaunchRoute.FromSteamLaunch(Environment.GetEnvironmentVariable)
                ? SteamLaunchRoute.TakeHandoff(SteamLaunchRoute.HandoffPath(Home.Root)) : null);
            // (round-five L3, the legacy-tile hole from the audit: a tile whose persisted launch
            // options are `--play` arrives HERE, and after a refused routed Play the arm it was
            // meant to consume is abandoned. Finding no handoff it used to fall back to the
            // default build — a second game beside the one the refusal already started direct.
            // The audit's third report: a bare timer suppressed a legitimate new press too. So the
            // guard requires EVIDENCE — a cancellation this recent AND a Longshot game actually
            // running now (the sender's direct fallback). No running game: the press starts its
            // build like any other. The message states only what was checked)
            if (handoff == null && start.Folder == null && claimed == null
                && SteamLaunchRoute.FromSteamLaunch(Environment.GetEnvironmentVariable)
                && (SteamLaunchRoute.DirectStartCommitted(Home.Root)
                    || (SteamLaunchRoute.RecentCancellation(Home.Root) && LongshotRunning())))
            {
                Console.Error.WriteLine("Longshot: no second game was started: a Play of this launcher just went direct, and its game is starting or already running.");
                return 0;
            }
            var folder = start.Folder ?? handoff?.Folder ?? service.Registry.Default?.Folder;
            if (folder == null || !GameFolder.IsBuilt(folder, service.Platform))
            {
                Console.Error.WriteLine("Longshot: no built game to start. Open the launcher and build one, or pass its folder.");
                return 1;
            }
            service.PlayFolder(Path.GetFullPath(folder), handoff, arrived: true);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Longshot: the game could not start: " + e.Message);
            return 1;
        }
    }
}
