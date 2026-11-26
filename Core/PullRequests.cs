using System.Globalization;
using System.Text.Json;

namespace LongshotLauncher.Core;

/// An open pull request of Longshot as Beanstalk, the Test Farm, lists it (PullRequests.ListUrl): its number, title, author, labels
/// and the commit its branch is at (Head, 40 hex). AuthorBigBean: its author is a BigBean, one of the maintainers who approve the
/// farm's tests; Approved: a BigBean approved its head commit for the farm (approval is pinned to the commit, so a new push is
/// not approved until someone approves it again), ApprovedBy who did. Draft: Forgejo's mark, or a WIP: title (IsDraft)
public sealed record PullRequest(int Number, string Title, string Author, string Head, bool Draft, IReadOnlyList<string> Labels,
                                 bool AuthorBigBean, bool Approved, IReadOnlyList<string> ApprovedBy)
{
    /// Its build: pr-&lt;number&gt;-&lt;the first 8 hex of its head&gt;
    public string Tag => Versions.PullRequestTag(Number, Head);

    /// A BigBean wrote it, or a BigBean approved its head: one warning before it is built, not two (PullRequestWarningViewModel)
    public bool Trusted => AuthorBigBean || Approved;

    public string Page => PullRequests.PageOf(Number);

    /// A player on platform can test it: one labelled for the other system only is not (platform/windows: Windows only;
    /// platform/linux, platform/steam-deck: Linux), and one labelled for neither, or both, is for both
    public bool For(Platform platform)
    {
        bool Has(string label) => Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
        var windows = Has(PullRequests.WindowsLabel);
        var linux = Has(PullRequests.LinuxLabel) || Has(PullRequests.SteamDeckLabel);
        return platform == Platform.Windows ? windows || !linux : linux || !windows;
    }
}

/// Helping test a pull request (docs/pull-requests.md). The open ones come from Beanstalk, the Test Farm, in one
/// answer (GET /api/v1/public/pulls), which it serves from its own copy of git.magicbean.dev's list (a minute old at most) and
/// its database, so an update check costs git.magicbean.dev nothing. Beanstalk also says who the BigBeans are and which heads they
/// approved, which git.magicbean.dev tells no one without signing in. A pull request's build, pr-&lt;number&gt;-&lt;commit&gt;, is made on
/// this PC from the source of its head commit, by longshot-builder's from-source (SourceBuilds)
public static class PullRequests
{
    public const string Beanstalk = "https://beanstalk.magicbean.dev";
    public const string ListUrl = Beanstalk + "/api/v1/public/pulls";
    public const string WindowsLabel = "platform/windows", LinuxLabel = "platform/linux", SteamDeckLabel = "platform/steam-deck";

    /// The most pull requests read from one answer (Longshot has about 300 open)
    public const int MaxListed = 2000;

    public static string PageOf(int number) => $"{Product.Server}/{Product.Longshot.Repo}/pulls/{number.ToString(CultureInfo.InvariantCulture)}";

    /// The pull request the player named: its number ("332", "#332") or its link on git.magicbean.dev
    /// (https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/332, with or without https://, and with /files, a #comment or a ?query
    /// after it, as copied from the browser); null for anything else, another repository's or another server's link included
    public static int? NumberOf(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (!t.All(char.IsAsciiDigit))
        {
            var server = Product.Server + "/";
            var host = server[(server.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var rest = t.StartsWith(server, StringComparison.OrdinalIgnoreCase) ? t[server.Length..]
                : t.StartsWith(host, StringComparison.OrdinalIgnoreCase) ? t[host.Length..] : null;
            var pulls = Product.Longshot.Repo + "/pulls/";
            if (rest == null || !rest.StartsWith(pulls, StringComparison.OrdinalIgnoreCase)) return null;
            t = new string(rest[pulls.Length..].TakeWhile(char.IsAsciiDigit).ToArray());
            var after = rest[(pulls.Length + t.Length)..];
            if (after.Length > 0 && after[0] is not ('/' or '#' or '?')) return null;
        }
        return t.Length is > 0 and <= 9 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// Work in progress, as Longshot's CI reads it (scripts/check_pr_description.py's is_draft): Forgejo's draft mark, or a title
    /// that starts with "WIP:" or "[WIP]" in any case. Listed and built as any other, marked WIP, after the same warnings
    public static bool IsDraft(string? title, bool draft = false)
    {
        var t = (title ?? "").TrimStart();
        return draft || t.StartsWith("WIP:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[WIP]", StringComparison.OrdinalIgnoreCase);
    }

    /// Why this PC cannot build it (null: it can): for the other system. A draft (WIP) can be built: the warnings say what it is
    public static string? Problem(PullRequest pr, Platform platform) =>
        !pr.For(platform) ? platform == Platform.Windows
            ? Tr.F("#{0} is for Linux only.", pr.Number)
            : Tr.F("#{0} is for Windows only.", pr.Number)
        : null;

    /// The open pull requests, newest change first; null when Beanstalk did not answer or answered what cannot be read (an error
    /// page): the update check goes on without them. Asked twice for an answer that may come next time (no answer, a server
    /// error), as Releases reads the release list
    public static async Task<IReadOnlyList<PullRequest>?> ListAsync(IFetcher fetcher, CancellationToken cancel = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Parse(await fetcher.GetTextAsync(ListUrl, "application/json", cancel).ConfigureAwait(false)); }
            catch (FetchException e) when (!e.Transient || attempt == 1) { return null; }
            catch (InvalidDataException) { return null; }
            catch (FetchException) { }
            await Task.Delay(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
        }
    }

    /// Beanstalk's answer: {"pulls": [{"number", "title", "author", "head", "draft", "labels", "author_bigbean", "approved",
    /// "approved_by"}, ...]}. A pull request that cannot be one (no number, no 40-hex head) is left out; InvalidDataException for
    /// an answer that is not that object
    public static IReadOnlyList<PullRequest> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("pulls", out var pulls)
                || pulls.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Beanstalk's list of pull requests has no pulls.");
            var list = new List<PullRequest>();
            var seen = new HashSet<int>();
            foreach (var p in pulls.EnumerateArray())
                if (Read(p) is { } pr && seen.Add(pr.Number) && list.Count < MaxListed) list.Add(pr);
            return list;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Beanstalk's list of pull requests cannot be read: " + e.Message, e);
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static IReadOnlyList<string> Strings(JsonElement e, string name, int most)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var s in v.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 and <= 100 } t && list.Count < most) list.Add(t);
        return list;
    }

    static PullRequest? Read(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number
            || !n.TryGetInt32(out var number) || number <= 0 || number > 999_999_999)
            return null;
        var head = (Str(p, "head") ?? "").ToLowerInvariant();
        if (head.Length != 40 || !head.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        var title = (Str(p, "title") ?? "").Trim();
        if (title.Length > 300) title = title[..300];
        var author = Str(p, "author") ?? "";
        if (author.Length > 100) author = "";
        return new PullRequest(number, title, author, head, IsDraft(title, Flag(p, "draft")), Strings(p, "labels", 50),
                               Flag(p, "author_bigbean"), Flag(p, "approved"), Strings(p, "approved_by", 10));
    }
}
