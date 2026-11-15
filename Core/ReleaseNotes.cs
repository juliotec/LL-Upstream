namespace LongshotLauncher.Core;

/// One version's notes, as What's new shows them: its version, when it came out, its release page, and its notes read from the
/// page's text (Blocks: empty when the page has none)
public sealed record ReleaseNote(string Tag, DateTimeOffset? Published, string Page, IReadOnlyList<MdBlock> Blocks);

/// What's new between the version a player has and the one offered, from the release list the update check already read (its
/// pages' text: ListedRelease.Body), newest first. Each page's notes start after the version before it (scripts/release.py
/// notes, docs/releases.md "Release notes"), so the pages that cover the way from one to the other are a chain, each from the
/// one its notes start after (Since), until the player's own:
///   a stable    since the stable before it (its notes cover its release candidates)
///   a first rc  since the version before it (an rc or a stable)
///   a later rc  since the last stable (what is new since the rc before, then what the earlier ones brought)
///   a nightly   since the last version: only the newest is shown, as each nightly's notes hold the ones before it
/// A version the player has that is no version (a checkout's build), or newer than the one offered (a channel switch), gets the
/// offered one's notes alone.
public static class ReleaseNotes
{
    /// The most pages shown at once (a player many stables behind reads the newest ones; the rest are on the releases page)
    public const int Most = 8;

    /// The notes of each version from have (left out) to offered, newest first; empty when the list does not have offered
    public static IReadOnlyList<ReleaseNote> Between(IEnumerable<ListedRelease>? list, string? have, string offered, Product product)
    {
        var published = (list ?? Enumerable.Empty<ListedRelease>()).Where(r => !r.Draft && Versions.IsTagName(r.Tag))
            .GroupBy(r => r.Tag, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var notes = new List<ReleaseNote>();
        string? tag = offered;
        while (tag != null && published.TryGetValue(tag, out var r) && notes.Count < Most)
        {
            notes.Add(Note(r, product));
            tag = Since(published.Keys, tag, product);
            if (!Versions.Newer(tag, have, product.FirstMajor)) break;
        }
        return notes;
    }

    /// The version a release's notes start after, of those listed (null: none, or a nightly's, which are not followed)
    public static string? Since(IEnumerable<string> tags, string tag, Product product)
    {
        if (!Versions.IsRelease(tag, product.FirstMajor)) return null;
        var stable = Versions.TrackOf(tag, product.FirstMajor) == Versions.Stable;
        var firstRc = !stable && tag.EndsWith("-rc.1", StringComparison.Ordinal);
        string? best = null;
        foreach (var t in tags)
        {
            if (!Versions.IsRelease(t, product.FirstMajor) || !Versions.Newer(tag, t, product.FirstMajor)) continue;
            if (!firstRc && Versions.TrackOf(t, product.FirstMajor) != Versions.Stable) continue;
            if (best == null || Versions.Newer(t, best, product.FirstMajor)) best = t;
        }
        return best;
    }

    static ReleaseNote Note(ListedRelease r, Product product) =>
        new(r.Tag, r.Published, r.Page ?? PageOf(product, r.Tag), Markdown.Parse(Text(r.Body), product.Repo));

    /// A version's release page
    public static string PageOf(Product product, string tag) => $"{Product.Server}/{product.Repo}/releases/tag/{Uri.EscapeDataString(tag)}";

    /// The notes of a release page's text: what follows the downloads at its top, which end at the first rule (--- on its own
    /// line: scripts/release.py page_header); the whole text when it has none
    public static string Text(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var rule = Array.FindIndex(lines, l => l.Trim() == "---");
        return string.Join("\n", rule >= 0 ? lines[(rule + 1)..] : lines).Trim();
    }
}
