using System.Formats.Tar;
using System.IO.Compression;

namespace LongshotLauncher.Core;

/// A pull request's build tools (docs/pull-requests.md), made on this PC the way Beanstalk's agents build a pull
/// request: the source of its head commit over the build tools of main, which here are the newest nightly's (the tools of its
/// tools.lock, checked against that nightly's signed release). The source is git.magicbean.dev's archive of that exact commit,
/// fetched over https by its full hash, and the archive must say it is that commit. Nothing about it is signed: the player chose
/// it after the warnings (PullRequestWarningViewModel). One whose tools.lock asks for other tools than the nightly's is refused, as
/// Beanstalk refuses to judge one, since its build would need tools nobody checked
public static class PullRequestSource
{
    /// The archive of a commit (Forgejo's git archive: one folder holding the tracked files, the commit in its pax header)
    public static string ArchiveUrl(string commit) => $"{Product.Longshot.Api}/archive/{commit}.tar.gz";

    /// The largest archive taken (Longshot's is about 20 MB)
    public const long MaxArchive = 128L << 20;

    /// The tools of pr-&lt;number&gt;-&lt;commit&gt;: the installed ones, else the nightly's (installed first, as a version's are, or kept from
    /// an installed version) with the pull request's source over them. find: the pull request as Beanstalk lists it now (null: not
    /// open any more); it must still be at that commit and buildable here (PullRequests.Problem)
    public static async Task<Toolchain> EnsureAsync(Toolchains chains, Releases releases, string tag,
                                                    Func<int, CancellationToken, Task<PullRequest?>> find,
                                                    Action<ToolsDownloadProgress>? progress = null, CancellationToken cancel = default,
                                                    Func<string, string, CancellationToken, Task>? extract = null)
    {
        if (chains.Find(tag) is { } have)
        {
            progress?.Invoke(new ToolsDownloadProgress(1, 0));
            return have;
        }
        if (Versions.PullRequestOf(tag) is not { } wanted) throw new ArgumentException("Not a pull request's build: " + tag, nameof(tag));
        var pr = await find(wanted.Number, cancel).ConfigureAwait(false)
                 ?? throw new InvalidDataException(Tr.F("Pull request #{0} is not open any more, or Beanstalk did not answer.", wanted.Number));
        if (PullRequests.Problem(pr, releases.Platform) is { } problem) throw new InvalidDataException(problem);
        if (pr.Tag != tag) throw new InvalidDataException(Tr.F("#{0} has a newer push ({1}): pick it again to build that one.", pr.Number, pr.Head[..8]));
        var nightly = await releases.NewestAsync(Versions.Nightly, cancel).ConfigureAwait(false)
                      ?? throw new InvalidDataException(Tr.T("No nightly could be read: a pull request is built with the newest nightly's tools."));
        // (the nightly's tools take most of the bar when they are downloaded; the source, about 20 MB, the rest)
        long total = 0;
        var tools = await ToolsSource.EnsureAsync(chains, releases, nightly, p =>
        {
            total = Math.Max(total, p.Total);
            progress?.Invoke(p with { Fraction = p.Fraction * 0.9 });
        }, cancel, extract).ConfigureAwait(false);
        progress?.Invoke(new ToolsDownloadProgress(0.9, total));
        var archive = await ReleaseManifest.FetchUncheckedAsync(chains.Fetcher, ArchiveUrl(pr.Head), MaxArchive, cancel).ConfigureAwait(false);
        var files = Files(archive, pr.Head);
        var made = await Task.Run(() => chains.InstallPullRequest(tag, tools, files, releases.Platform, cancel), cancel).ConfigureAwait(false);
        progress?.Invoke(new ToolsDownloadProgress(1, total + archive.Length));
        return made;
    }

    /// The archive's files: each path without the archive's top folder, '/' separated, with its bytes and whether it is executable.
    /// commit: the full hash its pax header must name (git archive's "comment"). InvalidDataException for another commit or none,
    /// anything else than plain files and folders (a link), a path that leaves the folder, or an archive that cannot be read
    public static IReadOnlyList<SourceFile> Files(byte[] tarGz, string commit)
    {
        var files = new List<SourceFile>();
        string? named = null;
        try
        {
            using var gz = new GZipStream(new MemoryStream(tarGz, writable: false), CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            while (tar.GetNextEntry() is { } e)
            {
                if (e is PaxGlobalExtendedAttributesTarEntry g)
                {
                    if (g.GlobalExtendedAttributes.TryGetValue("comment", out var c)) named = c.Trim();
                    continue;
                }
                if (e.EntryType == TarEntryType.Directory) continue;   // (folders are made with their files)
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("The pull request's source holds what is not a file: " + e.Name);
                var slash = e.Name.IndexOf('/');
                var path = slash < 0 ? "" : e.Name[(slash + 1)..];
                if (!ToolsLock.SafeName(path) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "."))
                    throw new InvalidDataException("The pull request's source names a file outside its folder: " + e.Name);
                var bytes = new MemoryStream();
                e.DataStream?.CopyTo(bytes);
                files.Add(new SourceFile(path, bytes.ToArray(), (e.Mode & UnixFileMode.UserExecute) != 0));
            }
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            throw new InvalidDataException("The pull request's source cannot be read: " + e.Message, e);
        }
        if (!string.Equals(named, commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The pull request's source is not of the commit it was asked for ({commit[..Math.Min(8, commit.Length)]}).");
        if (files.Count == 0) throw new InvalidDataException("The pull request's source is empty.");
        return files;
    }
}

/// A file of a pull request's source: its path ('/' separated, relative), its bytes, and whether it is executable
public sealed record SourceFile(string Path, byte[] Bytes, bool Executable);
