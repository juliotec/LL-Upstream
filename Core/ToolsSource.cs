namespace LongshotLauncher.Core;

/// file:// links read from this PC's disk; every other link through the fetcher it wraps (a parts list made on this PC, for
/// trying the launcher out without a release: LONGSHOT_LAUNCHER_PARTS)
public sealed class LocalFileFetcher(IFetcher inner) : IFetcher
{
    static string? LocalPath(string url) =>
        url.StartsWith("file://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;

    public async Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel)
    {
        if (LocalPath(url) is not { } path) return await inner.GetTextAsync(url, accept, cancel).ConfigureAwait(false);
        try { return await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new FetchException(e.Message, 404, e); }
    }

    public async Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel)
    {
        if (LocalPath(url) is not { } path)
        {
            await inner.DownloadAsync(url, to, progress, cancel).ConfigureAwait(false);
            return;
        }
        FileStream from;
        try { from = File.OpenRead(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new FetchException(e.Message, 404, e); }
        await using (from)
        {
            var buffer = new byte[1 << 20];
            long have = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                have += n;
                progress?.Invoke(have);
            }
        }
    }
}

/// How far the step 1 download is: Fraction 0..1 of Total bytes (the version's whole parts list)
public sealed record ToolsDownloadProgress(double Fraction, long Total);

/// A version's builder and build tools for the wizard's first step: the installed ones, else its release's (Windows: the builder's
/// zip and the tools of its tools.lock; Linux: its AppImage and the tools of the tools.lock inside it), checked against the release's signed SHA256SUMS, installed with
/// Toolchains. LONGSHOT_LAUNCHER_PARTS: a parts list on this PC instead (file:// links allowed), for trying the launcher out
public static class ToolsSource
{
    public const string LocalListVariable = "LONGSHOT_LAUNCHER_PARTS";

    /// The local parts list set for trying the launcher out, or null
    public static string? LocalList(Func<string, string?>? getenv = null) =>
        (getenv ?? Environment.GetEnvironmentVariable)(LocalListVariable) is { Length: > 0 } p ? p : null;

    /// A parts list from this PC: the Linux grammar with file:// links allowed, or the Windows setup's
    public static PartsList ReadLocal(string path, Platform platform)
    {
        var text = File.ReadAllText(path);
        return platform == Platform.Windows ? PartsManifest.ParseSetup(text) : PartsManifest.ParseLinux(text, local: true);
    }

    /// extract: how a Linux AppImage is unpacked (by default its own --appimage-extract; the tests pass theirs)
    public static async Task<Toolchain> EnsureAsync(Toolchains chains, Releases releases, string tag, string? localList = null,
                                                    Action<ToolsDownloadProgress>? progress = null, CancellationToken cancel = default,
                                                    Func<string, string, CancellationToken, Task>? extract = null)
    {
        if (chains.Find(tag) is { } have)
        {
            progress?.Invoke(new ToolsDownloadProgress(1, 0));
            return have;
        }
        if (localList == null && releases.Platform != Platform.Windows)   // (Linux: the release's AppImage, then the tools its tools.lock names)
        {
            var image = await releases.BuilderAsync(tag, cancel).ConfigureAwait(false);
            progress?.Invoke(new ToolsDownloadProgress(0, image.Size));
            return await chains.InstallAppImageAsync(tag, image, extract, p => progress?.Invoke(new ToolsDownloadProgress(p.Fraction, image.Size)),
                                                     cancel).ConfigureAwait(false);
        }
        var list = localList != null ? ReadLocal(localList, releases.Platform) : await releases.ToolsListAsync(tag, cancel).ConfigureAwait(false);
        if (list.Tag != tag) throw new InvalidDataException($"The parts list is for {list.Tag}, not {tag}.");
        var total = list.Parts.Sum(p => p.Size);
        progress?.Invoke(new ToolsDownloadProgress(0, total));
        return await chains.InstallAsync(list, p => progress?.Invoke(new ToolsDownloadProgress(p.Fraction, total)), cancel).ConfigureAwait(false);
    }
}
