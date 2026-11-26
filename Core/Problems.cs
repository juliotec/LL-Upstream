using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// What went wrong, in the words a player needs, and what they can do about it: from a build that stopped (longshot-builder's
/// error code, else its last message, ProblemViewModel) or a game that closed unexpectedly (its log, CrashCheck). Matched on the
/// codes and messages the builder and the game write, so a new message shows as Unknown rather than as a wrong fix.
public enum ProblemKind
{
    Unknown,
    WrongGameVersion,   // the game files are another build than the supported one
    IncompleteDump,     // the dump has damaged files with nothing to put in their place
    DiskFull,           // no room left on the drive the build writes to
    BuildMemory,        // the PC ran out of memory while compiling
    NoMeshShaders,      // the game closed unexpectedly on a graphics card or driver with no mesh shaders
    GpuLost,            // the graphics driver stopped answering (device lost)
    GameCrashed,        // the game closed with a crash report in its log
    GameRunning,        // the build was done, but Longshot ran from its folder at the swap (Windows)
    BlockedFolder,      // the build tools are under a folder Windows lets no program list (Longshot#1010)
    NupMoved,           // (longshot-builder) the .nup the game reads its files from is not where it was built from
    NotAGameFile,       // (longshot-builder) the file picked is not a .nup the builder reads
    NupDamaged,         // (longshot-builder) the .nup's files are laid out in a way the game refuses
    NupPathTooLong,     // (longshot-builder) the .nup's full path is longer than the game takes
    BuildRunning,       // (longshot-builder) another build holds the same build files
    FolderRefused,      // (longshot-builder) the game folder can't be built into (not writable, a link, inside the builder's folders)
    ToolsIncomplete,    // (longshot-builder) the builder's own files are not all there
    LicenceNeeded,      // (longshot-builder) an upscaler's vendor files are needed without its licence accepted
}

/// What a fix button does (the screen decides how)
/// AcceptLicence: the licence the build was refused for shown, the build again once accepted (longshot-builder's license_not_accepted)
public enum FixKind { None, PickOtherFiles, ChooseOtherFolder, LowerBuildMemory, DriverPage, BuildAgain, AcceptLicence }

public sealed record Fix(FixKind Kind, string Label, string? Link = null);

/// A problem as the screens show it. Headline, Detail and each Fix's Label are the English, marked for the catalogs (Tr.N): a
/// screen shows them in the player's language (Tr.T). Signature: what identifies the same crash on another PC (the exception and
/// where it happened, never a path), for grouping reports; null for a build problem
public sealed record Problem(ProblemKind Kind, string Headline, string Detail, IReadOnlyList<Fix> Fixes, string? Signature = null);

public static partial class Problems
{
    static readonly Fix BuildAgain = new(FixKind.BuildAgain, Tr.N("Build again"));

    /// The words that start the message of build tools under a folder Windows lets no program list (MakeGameRun.Blocked):
    /// BlockedFolder
    public const string BlockedFolderWords = "Windows doesn't let programs look inside";

    /// The driver download page for a maker (SystemInfo's names); null for another
    public static string? DriverPage(string? vendor) => vendor switch
    {
        SystemInfo.Nvidia => "https://www.nvidia.com/Download/index.aspx",
        SystemInfo.Amd => "https://www.amd.com/en/support/download/drivers.html",
        SystemInfo.Intel => "https://www.intel.com/content/www/us/en/download-center/home.html",
        _ => null,
    };

    /// longshot-builder's error codes a player can act on (its docs/dev/reference/build-events.md, "Codes"), in the launcher's own
    /// words: a code is stable, the builder's text is not translated. Null for a code with no words of the launcher's (the builder's
    /// own failures, io, tool_failed, bad_data, and the rarer ones): the screen then says the builder's text (FromBuild of it)
    public static Problem? FromCode(string? code)
    {
        var pick = new Fix(FixKind.PickOtherFiles, Tr.N("Pick other game files"));
        var folder = new Fix(FixKind.ChooseOtherFolder, Tr.N("Build to another folder"));
        switch (code)
        {
            // (the game reads its romfs from the .nup in place, through the index in its folder: a .nup moved, renamed or deleted
            // since. nup_missing: the .nup an update builds from is not there at all)
            case "nup_moved" or "nup_missing":
                return new Problem(ProblemKind.NupMoved, Tr.N("Your .nup is not where it was"),
                    Tr.N("Longshot reads the game's files from your .nup where it is, so the .nup must stay in its place. Put it back, " +
                    "or pick it where it is now and build again."), [new Fix(FixKind.PickOtherFiles, Tr.N("Pick the .nup again"))]);
            // (the .nup or dump folder just picked, or an update's, is not there: moved, deleted, or on a drive not connected)
            case "game_not_found":
                return new Problem(ProblemKind.NupMoved, Tr.N("Your game files were not found"),
                    Tr.N("The .nup or dump folder to build from is not there: it was moved, renamed or deleted, or its drive is not " +
                    "connected. Connect the drive, or pick the game files where they are now, then build again."), [pick]);
            // (the .nup's files laid out in a way the game refuses: data past its end, or files sharing data but not its size. Damaged
            // when it was dumped or copied: another copy, or a new dump)
            case "nup_damaged":
                return new Problem(ProblemKind.NupDamaged, Tr.N("Your .nup is damaged"),
                    Tr.N("The files inside your .nup are not laid out the way the game reads them: it was damaged when it was dumped or " +
                    "copied. Pick another copy of it, or dump the game again."), [pick]);
            // (the .nup's full path is longer than the 1023 bytes the game takes)
            case "nup_path_too_long":
                return new Problem(ProblemKind.NupPathTooLong, Tr.N("Your .nup's path is too long"),
                    Tr.N("The game can't open a .nup whose full path is this long. Move the .nup to a folder with a shorter path, such " +
                    "as one at the top of a drive, then pick it there and build again."), [new Fix(FixKind.PickOtherFiles, Tr.N("Pick the .nup again"))]);
            case "not_a_nup" or "not_a_module":
                return new Problem(ProblemKind.NotAGameFile, Tr.N("This is not a game file Longshot can read"),
                    Tr.N("Pick the .nup file of your dump of the game, or dump the game again."), [pick]);
            // (the same words as the builder's stops for them, FromBuild)
            case "unsupported_build":
                return FromBuild("Longshot supports build");
            case "nup_incomplete" or "romfs_incomplete" or "game_folder_incomplete" or "dump_unreadable" or "dump_damaged":
                return FromBuild("dump is incomplete");
            // (longshot-builder's own figures, Disk.Needs)
            case "disk_full":
                return new Problem(ProblemKind.DiskFull, Tr.N("The drive is full"),
                    Tr.N("The build needs about 5 GB free while it runs: about 4 GB where the build files go and 1 GB where the game " +
                    "goes. Free up space on that drive, or build to another drive."), [folder, BuildAgain]);
            // (longshot-builder swaps every file in or none: a game running from the folder leaves it as it was, nothing built kept
            // in it)
            case "game_running":
                return new Problem(ProblemKind.GameRunning, Tr.N("Longshot is still running"),
                    Tr.N("The new files can't replace the game's while Longshot runs from its folder, so the game folder is as it was " +
                    "before this build. Save and close Longshot, then build again."), [BuildAgain]);
            case "work_folder_busy":
                return new Problem(ProblemKind.BuildRunning, Tr.N("Another build is running"),
                    Tr.N("Another build of Longshot is using the same build files. Wait for it to finish, then build again."), [BuildAgain]);
            // (a build into the same game folder from another work folder: the game folder's own lock)
            case "game_folder_busy":
                return new Problem(ProblemKind.BuildRunning, Tr.N("Another build is running"),
                    Tr.N("Another build of Longshot is writing the same game folder. Wait for it to finish, then build again."), [BuildAgain]);
            case "game_folder_not_writable" or "game_folder_overlaps":
                return new Problem(ProblemKind.FolderRefused, Tr.N("Longshot can't build into this folder"),
                    Tr.N("The game's folder can't be written, is a link, or is inside the build files' folder. Pick another folder " +
                    "for the game, then build again."), [folder, BuildAgain]);
            // (the game or work folder is the game files' or inside them, or they are inside it: the build would write over them)
            case "game_files_overlap":
                return new Problem(ProblemKind.FolderRefused, Tr.N("Longshot can't build into this folder"),
                    Tr.N("The game's folder and your game files are one inside the other, so the build would write over them. Pick " +
                    "another folder for the game, then build again."), [folder, BuildAgain]);
            // (a build from a dump into a game folder holding a romfs folder the builder did not make: the player's own, such as a
            // mod's files, which the dump's files would be linked over)
            case "romfs_folder_theirs":
                return new Problem(ProblemKind.FolderRefused, Tr.N("Longshot can't build into this folder"),
                    Tr.N("The game's folder already has a romfs folder that Longshot did not make, and a build from your dump would " +
                    "write over the files in it. Move that romfs folder out of the game's folder, or pick another folder for the " +
                    "game, then build again."), [folder, BuildAgain]);
            case "license_not_accepted":
                return new Problem(ProblemKind.LicenceNeeded, Tr.N("A licence is needed for this upscaler"),
                    Tr.N("The build downloads this upscaler's files from its maker, and only under their licence. Read it and accept it to " +
                    "build again, or turn the upscaler off in the settings."), [new Fix(FixKind.AcceptLicence, Tr.N("Read the licence"))]);
            case "tools_incomplete" or "stage_not_ready":
                return new Problem(ProblemKind.ToolsIncomplete, Tr.N("Some of the builder's files are missing"),
                    Tr.N("This version's builder is incomplete: an antivirus may have removed some of its files. Restore them from " +
                    "its quarantine (Windows Security: Protection history), then build again."), [BuildAgain]);
        }
        return null;
    }

    /// longshot-builder's warnings (build-events.md, "Codes": Warnings) worth the player's eye once the build is done, in the
    /// launcher's own words (Tr.N, as FromCode's): the Ready page says them under its heading. Null for the others, which only the
    /// build's details show ("warning: text (code)"), as the builder said them
    public static string? FromWarning(string? code) => code switch
    {
        // (a unit the compiler refused, made again from the same translation, came out different: no build does that on its own,
        // the PC's memory, processor or drive changed it. The builder compiled it again and the build went on)
        BuildEvents.TranslationDamaged => Tr.N("Some of what the build made came out different when it was made again, which a " +
            "healthy PC never does: your PC's memory, processor or drive changed it. The build made those files again and finished. " +
            "If you see this again, turn off any memory or processor overclock (XMP, EXPO), test your memory and check the drive " +
            "for errors."),
        _ => null,
    };

    static Fix DriverFix(string? vendor) =>
        DriverPage(vendor) is { } link ? new Fix(FixKind.DriverPage, Tr.N("Get the latest graphics driver"), link)
                                       : new Fix(FixKind.None, Tr.N("Update your graphics driver"));

    /// A build that stopped with no code the launcher has words for (FromCode): why, from the builder's last message (or the
    /// launcher's own, or the system's). Only the messages that mean one thing are matched; any other shows as said (Unknown)
    public static Problem FromBuild(string why)
    {
        var w = why ?? "";
        bool Has(string s) => w.Contains(s, StringComparison.OrdinalIgnoreCase);
        // (MakeGameRun.Blocked: moving the folder is the fix)
        if (Has(BlockedFolderWords))
            return new Problem(ProblemKind.BlockedFolder, Tr.N("Longshot is in a folder Windows protects"),
                Tr.N("Windows doesn't let programs look inside one of the folders Longshot is in (such as C:\\Users\\<you>\\Cookies), " +
                "so its build tools can't start. Move Longshot's folder somewhere else, for example C:\\Longshot, start Longshot " +
                "from there and build again."), [BuildAgain]);
        // (FromCode's unsupported_build: "This is build 1a2b3c4d. Longshot supports build e0f3ccfa.")
        if (Has("Longshot supports build"))
            return new Problem(ProblemKind.WrongGameVersion, Tr.N("These game files are a different version"),
                Tr.N("Longshot needs the game with update 2.0.0 installed (program 0400486024DEC000, update v131072). Pick " +
                "the files of that version, or dump your game again with the update."), [new Fix(FixKind.PickOtherFiles, Tr.N("Pick other game files"))]);
        if (Has("dump is incomplete") || Has("still has") && Has("damaged file"))
            return new Problem(ProblemKind.IncompleteDump, Tr.N("Your game dump is incomplete"),
                Tr.N("Some of the game's files are damaged or missing (a complete dump is about 23.8 GB). Dump the game again and " +
                "build from the new dump."), [new Fix(FixKind.PickOtherFiles, Tr.N("Pick other game files"))]);
        // (the system's own words: Linux's ENOSPC, Windows' ERROR_DISK_FULL)
        if (Has("No space left") || Has("not enough space") || Has("disk full")) return FromCode("disk_full")!;
        if (Has("ran out of memory") || Has("MemoryError") || Has("out of memory"))
            return new Problem(ProblemKind.BuildMemory, Tr.N("Your PC ran out of memory while building"),
                Tr.N("Close other programs, or set Build memory lower in step 2's Advanced, then build again: what was built so far " +
                "is kept."), [new Fix(FixKind.LowerBuildMemory, Tr.N("Use less memory")), BuildAgain]);
        return new Problem(ProblemKind.Unknown, Tr.N("The build stopped"),
            Tr.N("Something went wrong that Longshot doesn't recognise yet."), [BuildAgain]);
    }

    [GeneratedRegex(@"CRASH: exception (0x[0-9a-fA-F]+) \((read|write)[^)]*\) at (\S+)")]
    private static partial Regex CrashLine();

    /// The GPU the game ran on (gpu/vk/vk_main.cpp, after the device is made): "vk: NVIDIA GeForce RTX 4070 Ti, Vulkan 1.4, driver
    /// 0x..; mesh shaders yes, ...". Not the list of every device before it ("vk: device 0: <name>, Vulkan 1.3, driver 0x.., type 2")
    [GeneratedRegex(@"vk: (?!device \d+: )(.+?), Vulkan \d+\.\d+, driver ")]
    private static partial Regex GpuLine();

    /// A run that closed unexpectedly, from its log's lines (oldest first); null when the log shows no crash. vendor: the
    /// graphics card's maker when the log doesn't name the GPU the game ran on, for the driver link
    public static Problem? FromLog(IEnumerable<string> lines, string? vendor = null)
    {
        string? crash = null, module = null, gpu = null;
        bool lost = false, noMesh = false;
        foreach (var line in lines)
        {
            if (crash == null && CrashLine().Match(line) is { Success: true } m)
            {
                crash = m.Groups[1].Value.ToLowerInvariant();
                module = m.Groups[3].Value;
            }
            if (GpuLine().Match(line) is { Success: true } g) gpu = g.Groups[1].Value;
            if (line.Contains("device lost", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("VK_ERROR_DEVICE_LOST", StringComparison.Ordinal)) lost = true;
            if (line.Contains("this GPU has no mesh shaders", StringComparison.Ordinal)) noMesh = true;
        }
        // (the GPU that ran, not the first card Linux lists: a laptop's iGPU, or nothing on Windows)
        if (gpu != null) vendor = SystemInfo.VendorOfName(gpu);
        // (no mesh shaders alone is a warning: the game logs it at every start and goes on, vk_main.cpp. It is the likely cause
        // only of a run that then crashed or lost the device)
        if (!lost && crash == null) return null;
        if (noMesh)
            return new Problem(ProblemKind.NoMeshShaders, Tr.N("Your graphics card can't draw this game"),
                Tr.N("It, or its driver, has no mesh shaders, which the game needs. An NVIDIA RTX 20 series or newer, an AMD Radeon " +
                "RX 6000 or newer, or an Intel Arc card can. If your card is one of those, update its driver."), [DriverFix(vendor)],
                Signature(crash, module, lost ? "device lost" : null));
        if (lost)
            return new Problem(ProblemKind.GpuLost, Tr.N("The graphics driver stopped responding"),
                Tr.N("This is often fixed by the latest graphics driver."), [DriverFix(vendor)], Signature(crash, module, "device lost"));
        return new Problem(ProblemKind.GameCrashed, Tr.N("The game closed unexpectedly"),
            Tr.N("If it happens again at the same place, the latest graphics driver is worth a try."), [DriverFix(vendor)],
            Signature(crash, module, null));
    }

    /// The same crash on any PC: the exception code and the module and offset it happened at (the module's name only: no
    /// folder), or what was lost
    static string Signature(string? code, string? module, string? what)
    {
        var where = module == null ? "" : Path.GetFileName(module.Replace('\\', '/'));
        var parts = new[] { code, where.Length > 0 ? where : null, what }.Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" ", parts).Trim().ToLower(CultureInfo.InvariantCulture);
    }
}

/// Whether the last run of a build closed unexpectedly: its newest log (logs/longshot_log.txt, or longshot_log.txt beside the
/// game from a build before Longshot's game folder layout) shows a crash the launcher has not shown yet. Only the newest: a run since then went well (the player has played since), and a log the game rotated is not
/// offered. The caller leaves it out while a game runs (that log is the live run's)
public static class CrashCheck
{
    public sealed record Found(Problem Problem, string Log, long WriteTicks);

    /// The game's log, in its build's folder
    public const string LogName = "longshot_log.txt";

    /// Where the game's logs are: logs/ in its build's folder (Longshot's host/game_layout.c, from nightly-127 on)
    public const string LogsFolder = "logs";

    /// The game's log of its last run: logs/longshot_log.txt, or longshot_log.txt beside the game where a build from before
    /// the logs/ folder wrote it (the newer of the two, should both be there: the game moves the loose one into logs/ at
    /// its first start, and keeps it where it is when one of that name is there already); null when there is none
    public static string? LogPath(string buildFolder)
    {
        string? newest = null;
        var newestTicks = long.MinValue;
        foreach (var path in new[] { Path.Combine(buildFolder, LogsFolder, LogName), Path.Combine(buildFolder, LogName) })
        {
            if (!File.Exists(path)) continue;
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (ticks > newestTicks) (newest, newestTicks) = (path, ticks);
        }
        return newest;
    }

    /// The most of one log read (and a report carries): its end, where the problem is
    public const int LogTailBytes = 192 * 1024;

    /// The end of a file, at most max bytes, from the start of a line
    public static string Tail(string path, int max = LogTailBytes)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, f.Length - max);
        f.Seek(start, SeekOrigin.Begin);
        var text = new StreamReader(f, Encoding.UTF8).ReadToEnd();
        if (start > 0 && text.IndexOf('\n') is var nl and >= 0) text = "[the log's start left out]\n" + text[(nl + 1)..];
        return text;
    }

    /// seenTicks: the log's write time (UTC ticks) already handled (settings); notBeforeTicks: a log written before then is not
    /// offered (before the launcher first looked, or too long ago to be news)
    public static Found? Find(string buildFolder, long seenTicks, long notBeforeTicks = 0, string? vendor = null)
    {
        try
        {
            if (LogPath(buildFolder) is not { } path) return null;
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (ticks <= seenTicks || ticks < notBeforeTicks) return null;
            var tail = Tail(path);
            return Problems.FromLog(tail.Split('\n'), vendor) is { } p ? new Found(p, path, ticks) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
