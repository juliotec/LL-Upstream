using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace LongshotLauncher.Core;

/// The launcher updating itself (docs/releases.md, "The launcher's own update"). It is one file on both systems, a
/// self-contained single-file program on Windows and an AppImage on Linux, so an update is one file, whatever that file is named:
///   1. download the release's launcher of this kind (LauncherLayout.UpdateKey: the setup program, the program out of the portable
///      zip, or the AppImage) beside this one as &lt;name&gt;.new, checked for its size and SHA-256 (the signed release names both);
///   2. start it with --self-check, which must answer with the version the release names, so a file that does not run on this
///      PC is never put in place;
///   3. swap: this file becomes &lt;name&gt;.old (kept for --rollback), the new one takes its name, so the file keeps the name it was
///      installed or started as (an installed launcher stays LongshotLauncher.exe, a portable one stays beside its "portable" file);
///   4. start the new one, and close.
/// A crash between the two renames of step 3 on Windows is put right at the next start (Recover).
/// Only a newer version on the launcher's own track is offered; a build from a checkout ("dev") never updates itself.
public static class SelfUpdate
{
    public const string TagKey = "LongshotTag";
    public const string CheckArgument = "--self-check", RollbackArgument = "--rollback";

    /// This launcher's version: the tag the release build stamped (-p:LongshotTag=, an AssemblyMetadata), else "dev"
    public static string OwnTag(Assembly? assembly = null) =>
        (assembly ?? Assembly.GetEntryAssembly())?.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == TagKey)?.Value is { Length: > 0 } tag && Versions.IsTagName(tag) ? tag : "dev";

    /// What --self-check prints
    public static string CheckLine(string tag) => "LongshotLauncher " + tag;

    /// The launcher's channel: the one the player chose, else its own version's track (a build from a checkout follows stable)
    public static string ChannelOf(string own, string? chosen) =>
        Versions.IsChannel(chosen) ? chosen! : Versions.TrackOf(own);

    /// The version to update the launcher to from its channel's newest: a newer one on its own track, or, after the player chose
    /// another channel, that channel's newest; never for a build from a checkout ("dev"), never one with no launcher for this system
    public static string? UpdateTo(string own, ReleaseInfo? newest, Platform platform, string? channel = null, string? key = null)
    {
        if (newest == null || newest.Version == own || !newest.Launcher.ContainsKey(key ?? ReleaseManifest.PlatformKey(platform))) return null;
        var track = Versions.TrackOf(own);
        var isVersion = Versions.IsRelease(own, Versions.LauncherFirstMajor) || track == Versions.Nightly;
        if (!isVersion) return null;
        if (channel != null && channel != track) return newest.Version;
        return Versions.Newer(newest.Version, own, Versions.LauncherFirstMajor) ? newest.Version : null;
    }

    /// How an offered launcher compares with this one, for the home's banner: newer, older (after the player switched to a channel
    /// whose newest is behind this one), or neither (a nightly against a release: no order between them)
    public static int Compare(string offered, string own) =>
        Versions.Newer(offered, own, Versions.LauncherFirstMajor) ? 1 : Versions.Newer(own, offered, Versions.LauncherFirstMajor) ? -1 : 0;

    /// The command that starts this launcher again from outside it, for the game folders' stamp (builder=) and the shortcuts:
    /// LONGSHOT_LAUNCHER_EXE when a package sets it (the Flatpak: a script that runs `flatpak run`, as /app/bin exists only inside
    /// its sandbox), else the AppImage on Linux, else this program
    public static string StartPath(Func<string, string?>? getenv = null, string? processPath = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        processPath ??= Environment.ProcessPath;
        return getenv(StartVariable) is { Length: > 0 } set ? set
            : getenv("APPIMAGE") is { Length: > 0 } appImage ? appImage : processPath ?? "LongshotLauncher";
    }

    public const string StartVariable = "LONGSHOT_LAUNCHER_EXE";

    /// The file this launcher runs from: the AppImage on Linux ($APPIMAGE), else the program itself, whatever it is named (the
    /// download's LongshotLauncher-&lt;v&gt;-windows-x64-setup.exe, the installed LongshotLauncher.exe, a renamed copy); null when it is
    /// not one that can replace itself (dotnet run: dotnet.exe; a dll)
    public static string? CurrentFile(Platform platform, Func<string, string?>? getenv = null, string? processPath = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        processPath ??= Environment.ProcessPath;
        if (platform == Platform.Linux)
            // (a Linux absolute path, even when the caller checks that platform from Windows)
            return getenv("APPIMAGE") is { Length: > 0 } appImage && appImage.StartsWith('/') ? appImage : null;
        if (processPath == null) return null;
        // (either separator: the name is read the same on any system)
        var name = processPath.Split('/', '\\')[^1];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath : null;
    }

    /// What a start finds left of an update cut short (the PC lost power, the launcher was killed), put right: with no current
    /// file but its .old, the .old goes back (Windows: the old one had been moved aside, the new one not yet in); a .new or its
    /// .part left over goes. Returns what was done, null when nothing was left over
    public static string? Recover(string current)
    {
        string? done = null;
        var backup = Backup(current);
        if (!File.Exists(current) && File.Exists(backup))
        {
            File.Move(backup, current);
            LauncherLog.Add($"launcher update cut short: {backup} put back as {current}");
            done = $"The last update of the launcher was cut short; the launcher before it is back as {Path.GetFileName(current)}.";
        }
        foreach (var left in new[] { Staged(current), Staged(current) + ".part", Staged(current) + ".zip.part" })
        {
            if (!File.Exists(left)) continue;
            File.Delete(left);
            LauncherLog.Add("launcher update cut short: deleted " + left);
            done ??= "The files of an update cut short were removed.";
        }
        return done;
    }

    /// The file of the launcher a leftover is of: &lt;name&gt; for &lt;name&gt;.old or &lt;name&gt;.new (a launcher started from one of them,
    /// when its own file is gone); null for any other file
    public static string? LeftoverOf(string file) =>
        file.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".new", StringComparison.OrdinalIgnoreCase) ? file[..^4] : null;

    public static string Staged(string current) => current + ".new";
    public static string Backup(string current) => current + ".old";

    /// The release's launcher for this system into &lt;current&gt;.new, checked; its path. Executable on Linux. zipEntry: the file is a
    /// zip (the portable one), checked as a whole, and the launcher is that entry of it
    public static async Task<string> DownloadAsync(IFetcher fetcher, FileRef file, string current, Action<double>? progress = null,
                                                   CancellationToken cancel = default, string? zipEntry = null)
    {
        var staged = Staged(current);
        var part = staged + (zipEntry != null ? ".zip.part" : ".part");
        try
        {
            using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await fetcher.DownloadAsync(file.Url, to, have =>
                {
                    if (have > file.Size) throw new InvalidDataException("The new launcher is larger than its release says.");
                    progress?.Invoke(file.Size == 0 ? 1 : have / (double)file.Size);
                }, cancel).ConfigureAwait(false);
                to.Flush(flushToDisk: true);
            }
            string sha;
            using (var f = File.OpenRead(part))
            {
                if (f.Length != file.Size) throw new InvalidDataException("The new launcher is not the size its release says.");
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, cancel).ConfigureAwait(false));
            }
            if (sha != file.Sha256) throw new InvalidDataException("The new launcher is not the file its release names (SHA-256).");
            if (zipEntry != null)
            {
                var program = staged + ".part";
                try
                {
                    Unzip(part, zipEntry, program);
                    File.Move(program, staged, overwrite: true);
                }
                finally { File.Delete(program); }
                return staged;
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                                           | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(part, staged, overwrite: true);
            return staged;
        }
        finally
        {
            File.Delete(part);
        }
    }

    /// The one entry of a checked zip (the portable launcher's program) into to; refused when the zip does not have it, or it is
    /// larger than any launcher
    static void Unzip(string zip, string entry, string to)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        var e = archive.Entries.FirstOrDefault(x => x.FullName == entry)
                ?? throw new InvalidDataException($"The portable launcher's zip has no {entry}.");
        if (e.Length > 1L << 30) throw new InvalidDataException($"The portable launcher's {entry} is larger than any launcher.");
        using (var from = e.Open())
        using (var file = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            from.CopyTo(file);
            file.Flush(flushToDisk: true);
        }
        if (new FileInfo(to).Length != e.Length) throw new InvalidDataException($"The portable launcher's {entry} did not unpack whole.");
    }

    /// Another AppImage's environment, without what says which AppImage this launcher runs from: APPIMAGE and APPDIR, which the
    /// AppImage runtime sets for the program it starts, and TARGET_APPIMAGE, which AppImageLauncher's binfmt-bypass sets for the
    /// runtime it starts. An AppImage's runtime mounts or unpacks TARGET_APPIMAGE when it is set, not itself: left in, the new
    /// launcher's check would run this launcher again
    public static void WithoutOurAppImage(IDictionary<string, string?> environment)
    {
        environment.Remove("APPIMAGE");
        environment.Remove("APPDIR");
        environment.Remove("TARGET_APPIMAGE");
    }

    /// Starts staged with --self-check: true when it answers CheckLine(tag) and ends well within timeout
    public static async Task<bool> SelfCheckAsync(string staged, string tag, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(staged) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(CheckArgument);
        // (its own AppImage, not this one's: under AppImageLauncher, TARGET_APPIMAGE would have it run this launcher again)
        WithoutOurAppImage(start.Environment);
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            using var p = Process.Start(start);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEndAsync(cancel.Token);
            _ = p.StandardError.ReadToEndAsync(cancel.Token);
            await p.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            return p.ExitCode == 0 && (await output.ConfigureAwait(false)).Split('\n').Any(l => l.Trim() == CheckLine(tag));
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    /// The new launcher in place of the current one, the current one kept as &lt;current&gt;.old. On Linux the current file is
    /// copied aside and the new one renamed over it, one rename, so there is never a moment without it (the running AppImage keeps
    /// its own, unlinked file). Windows does not let a running program be overwritten, only renamed: it goes aside first, and comes
    /// back if the new one cannot take its place.
    /// wait: how a retry waits (Thread.Sleep; a test's own)
    public static void Swap(string current, string staged, Platform platform, Action<TimeSpan>? wait = null)
    {
        var backup = Backup(current);
        if (platform == Platform.Linux)
        {
            File.Copy(current, backup + ".part", overwrite: true);
            File.Move(backup + ".part", backup, overwrite: true);
            File.Move(staged, current, overwrite: true);
            Swapped();
            return;
        }
        File.Move(current, backup, overwrite: true);
        try { WhileHeld(() => File.Move(staged, current), wait); }
        catch (Exception e) when (!File.Exists(current))
        {
            File.Move(backup, current);
            LauncherLog.Add($"launcher update: the new launcher could not take the place of {current} ({e.GetType().Name}: {e.Message}); the one before is back");
            throw;
        }
        Swapped();

        void Swapped() => LauncherLog.Add($"launcher update: the new launcher put in place at {current}, the one before kept as {backup}");
    }

    /// A program just written and started is often held a moment after its process has ended, on Windows: an antivirus scanning
    /// it, Windows checking a program it has not seen run, opening it without letting it be renamed or deleted. The --self-check
    /// has ended, nothing of the launcher's holds it, but the swap right after it answered "being used by another process"
    /// (#223). action is tried again while the file is held, waiting a little longer each time, for about 10 seconds in all; any
    /// other failure, or one held longer, is thrown
    public static void WhileHeld(Action action, Action<TimeSpan>? wait = null)
    {
        wait ??= Thread.Sleep;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException e) when (attempt < HeldWaits.Length && IsHeld(e)) { wait(HeldWaits[attempt]); }
        }
    }

    public static readonly TimeSpan[] HeldWaits =
        [.. new[] { 100, 250, 500, 1000, 2000, 2000, 2000, 2000 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    /// A Windows failure of a file another process has open: ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION, or the
    /// ERROR_ACCESS_DENIED File.Move answers for a file an antivirus holds (as an IOException; a file the player may not change
    /// is an UnauthorizedAccessException, never retried)
    public static bool IsHeld(IOException e) => unchecked((uint)e.HResult) is 0x80070020 or 0x80070021 or 0x80070005;

    /// The staged launcher deleted after an update, put in place or not; when it cannot be (still held, #223), it is left for the
    /// next start's Recover, and the update's own outcome stands: this never throws. True when it is gone
    public static bool Discard(string staged)
    {
        try
        {
            File.Delete(staged);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Add($"launcher update: {staged} could not be deleted yet ({e.GetType().Name}: {e.Message}); the next start removes it");
            return false;
        }
    }

    /// The launcher kept by the last update back in place (--rollback); false when none was kept
    public static bool Rollback(string current, Platform platform)
    {
        var backup = Backup(current);
        if (!File.Exists(backup)) return false;
        var aside = current + ".rolledback";
        if (platform == Platform.Linux)
        {
            File.Copy(current, aside, overwrite: true);
            File.Move(backup, current, overwrite: true);
            LauncherLog.Add($"launcher rollback: {backup} put back as {current}, the one it replaced kept as {aside}");
            return true;
        }
        File.Move(current, aside, overwrite: true);
        try { File.Move(backup, current); }
        catch (Exception) when (!File.Exists(current))
        {
            File.Move(aside, current);
            throw;
        }
        LauncherLog.Add($"launcher rollback: {backup} put back as {current}, the one it replaced kept as {aside}");
        return true;
    }
}
