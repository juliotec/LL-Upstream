using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// A piece of text in one style: bold, italic, code, or a link (Link: its https address)
public sealed record MdSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

/// A heading (Level 1 to 6), a paragraph, a list item (Level: how deep, 0 at the left; Number: "1." for a numbered one, null for
/// a bullet), a code block, or a rule (---)
public enum MdKind { Heading, Paragraph, Item, Code, Rule }

public sealed record MdBlock(MdKind Kind, IReadOnlyList<MdSpan> Spans, int Level = 0, string? Number = null)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

/// The markdown of a release's page (git-cliff's notes and the What's new above them: docs/releases.md), read into blocks the
/// window draws (Controls.Notes): headings, paragraphs, bullets and numbered items, rules, code, and in the text bold, italic,
/// code and links. Only what those pages use: no tables, no images (an image's words are kept), no HTML but its entities.
/// A link is kept only to an http(s) address; #123 links to the repository's pull request when repo is given
public static class Markdown
{
    static readonly Regex HeadingLine = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.CultureInvariant);
    static readonly Regex RuleLine = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
    static readonly Regex ItemLine = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$", RegexOptions.CultureInvariant);
    static readonly Regex FenceLine = new(@"^ {0,3}(```|~~~)", RegexOptions.CultureInvariant);

    public static IReadOnlyList<MdBlock> Parse(string? text, string? repo = null)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // (the block being gathered: a paragraph's or an item's lines, joined by spaces)
        var open = new StringBuilder();
        MdKind? kind = null;
        int level = 0;
        string? number = null;
        void Close()
        {
            if (kind is { } k && open.ToString().Trim() is { Length: > 0 } t) blocks.Add(new MdBlock(k, Inline(t, repo), level, number));
            open.Clear();
            kind = null;
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FenceLine.Match(line) is { Success: true } fence)
            {
                Close();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence.Groups[1].Value, StringComparison.Ordinal); i++) code.Add(lines[i]);
                blocks.Add(new MdBlock(MdKind.Code, new[] { new MdSpan(string.Join("\n", code), Code: true) }));
                continue;
            }
            if (line.Trim().Length == 0) { Close(); continue; }
            if (RuleLine.IsMatch(line))
            {
                Close();
                blocks.Add(new MdBlock(MdKind.Rule, Array.Empty<MdSpan>()));
                continue;
            }
            if (HeadingLine.Match(line) is { Success: true } h)
            {
                Close();
                blocks.Add(new MdBlock(MdKind.Heading, Inline(h.Groups[2].Value, repo), h.Groups[1].Length));
                continue;
            }
            if (ItemLine.Match(line) is { Success: true } item)
            {
                Close();
                kind = MdKind.Item;
                var indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                level = Math.Min(indent / 2, 4);
                number = char.IsDigit(item.Groups[2].Value[0]) ? item.Groups[2].Value.TrimEnd(')', '.') + "." : null;
                open.Append(item.Groups[3].Value);
                continue;
            }
            // (a line of text: more of the paragraph or item it follows, else a new paragraph; a quote's > goes)
            var words = line.TrimStart();
            if (words.StartsWith('>')) words = words[1..].TrimStart();
            if (kind == null)
            {
                kind = MdKind.Paragraph;
                level = 0;
                number = null;
            }
            else open.Append(' ');
            open.Append(words.TrimEnd());
        }
        Close();
        return blocks;
    }

    /// A line's text in its styles, next to each other in reading order (two spans side by side never share all their styles)
    public static IReadOnlyList<MdSpan> Inline(string text, string? repo = null)
    {
        var spans = new List<MdSpan>();
        Read(text, new MdSpan(""), spans, repo);
        // (merged: one span per run of one style)
        var merged = new List<MdSpan>();
        foreach (var s in spans)
        {
            if (s.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1] is var last && last with { Text = "" } == s with { Text = "" }) merged[^1] = last with { Text = last.Text + s.Text };
            else merged.Add(s);
        }
        return merged;
    }

    /// The text into spans, each in style plus what its own marks add
    static void Read(string text, MdSpan style, List<MdSpan> into, string? repo)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0) into.Add(style with { Text = WebUtility.HtmlDecode(plain.ToString()) });
            plain.Clear();
        }
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && !char.IsAsciiLetterOrDigit(text[i + 1]) && !char.IsWhiteSpace(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`' && text.IndexOf('`', i + 1) is var endCode and > 0)
            {
                Flush();
                into.Add(style with { Text = text[(i + 1)..endCode], Code = true });
                i = endCode + 1;
                continue;
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c && Closing(text, i + 2, new string(c, 2)) is var endBold and > 0)
            {
                Flush();
                Read(text[(i + 2)..endBold], style with { Bold = true }, into, repo);
                i = endBold + 2;
                continue;
            }
            // (_ only at a word's start: a snake_case name keeps its underscores)
            if ((c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))) && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
                && text[i + 1] != c && Closing(text, i + 1, c.ToString()) is var endItalic and > 0)
            {
                Flush();
                Read(text[(i + 1)..endItalic], style with { Italic = true }, into, repo);
                i = endItalic + 1;
                continue;
            }
            // ([words](address) and ![words](address): the image's words, as text)
            var image = c == '!' && i + 1 < text.Length && text[i + 1] == '[';
            if ((c == '[' || image) && LinkAt(text, image ? i + 1 : i) is { } link)
            {
                Flush();
                Read(link.Words, image || link.Url == null ? style : style with { Link = link.Url }, into, repo);
                i = link.End;
                continue;
            }
            if (c == '<' && text.IndexOf('>', i + 1) is var endAuto and > 0 && Web(text[(i + 1)..endAuto]) is { } auto)
            {
                Flush();
                into.Add(style with { Text = auto, Link = auto });
                i = endAuto + 1;
                continue;
            }
            if (c == 'h' && style.Link == null && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && BareUrl.Match(text, i) is { Success: true, Index: var at } bare && at == i)
            {
                Flush();
                var url = bare.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
                into.Add(style with { Text = url, Link = url });
                i += url.Length;
                continue;
            }
            if (c == '#' && repo != null && style.Link == null && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '&')
                && PullRef.Match(text, i) is { Success: true, Index: var refAt } pull && refAt == i)
            {
                Flush();
                into.Add(style with { Text = pull.Value, Link = $"{Product.Server}/{repo}/pulls/{pull.Groups[1].Value}" });
                i += pull.Length;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    static readonly Regex BareUrl = new(@"https?://[^\s<>()\[\]]+", RegexOptions.CultureInvariant);
    static readonly Regex PullRef = new(@"#(\d{1,7})\b", RegexOptions.CultureInvariant);

    /// Where the closing mark is, from start: one with text before it that does not end in a space; -1 when none
    static int Closing(string text, int start, string mark)
    {
        for (var at = text.IndexOf(mark, start, StringComparison.Ordinal); at >= 0; at = text.IndexOf(mark, at + 1, StringComparison.Ordinal))
        {
            if (at == start || char.IsWhiteSpace(text[at - 1])) continue;
            // (a single * is not the first of a closing **)
            if (mark.Length == 1 && at + 1 < text.Length && text[at + 1] == mark[0] && (at + 2 >= text.Length || text[at + 2] != mark[0])) { at++; continue; }
            if (mark == "_" && at + 1 < text.Length && char.IsLetterOrDigit(text[at + 1])) continue;
            // (***both***: the bold closes at the end of the run, the italic inside it)
            if (mark.Length == 2) while (at + 2 < text.Length && text[at + 2] == mark[0]) at++;
            return at;
        }
        return -1;
    }

    /// [words](address) at start: its words, its address when it is a web one (null: the words alone), and where it ends
    static (string Words, string? Url, int End)? LinkAt(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0)
            {
                if (i + 1 >= text.Length || text[i + 1] != '(') return null;
                var close = text.IndexOf(')', i + 2);
                if (close < 0) return null;
                var target = text[(i + 2)..close].Trim();
                // (a title after the address: [words](https://x "title"))
                if (target.IndexOf(' ') is var space and > 0) target = target[..space];
                return (text[(start + 1)..i], Web(target.Trim('<', '>')), close + 1);
            }
        }
        return null;
    }

    /// The address when it is a web page's (http or https, nothing else: not a file or a program)
    public static string? Web(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !address.Any(char.IsWhiteSpace) ? address : null;
}
