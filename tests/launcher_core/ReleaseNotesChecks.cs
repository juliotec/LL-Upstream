using LongshotLauncher.Core;

/// What's new (Core/ReleaseNotes.cs, Core/Markdown.cs): a release page's notes read from its markdown, and which pages are
/// shown from the player's version to the one offered
static partial class Program
{
    static void ReleaseNotesChecks()
    {
        MarkdownChecks();

        // (a page's text, as scripts/release.py writes it: the downloads, a rule, then the What's new and git-cliff's notes)
        const string page = "# ⬇️ Download Longshot Launcher\n\n### [🪟 &nbsp;Download for Windows](https://git-cdn.magicbean.dev/x.exe)\n\n" +
                            "Run it, and press **Build**.\n\n---\n\n## What's new\n\n- **Faster.** It is.\n\n## v1.0.0-rc.6 (2026-11-15)\n";
        Require(ReleaseNotes.Text(page) == "## What's new\n\n- **Faster.** It is.\n\n## v1.0.0-rc.6 (2026-11-15)",
                "A page's notes: what follows the downloads at its top.");
        Require(ReleaseNotes.Text("## Notes\n\n- one") == "## Notes\n\n- one" && ReleaseNotes.Text(null) == "" && ReleaseNotes.Text("  \n") == "",
                "A page with no downloads is all notes; one with no text has none.");
        Require(ReleaseNotes.Text("a\r\n---\r\n\r\nb") == "b", "A page with Windows' line ends.");

        // (the list the update check read, with each page's text: ListedRelease.Body)
        var list = ReleaseManifest.ParseList(Json(
            ("v3.0.0", false, false, "Downloads\n\n---\n\n## v3.0.0\n\n- the first"),
            ("v3.1.0-rc.1", false, true, "Downloads\n\n---\n\n## v3.1.0-rc.1\n\n- rc one"),
            ("v3.1.0-rc.2", false, true, "Downloads\n\n---\n\n## v3.1.0-rc.2, new since rc.1\n\n- rc two"),
            ("v3.1.0", false, false, "Downloads\n\n---\n\n## v3.1.0\n\n- the second"),
            ("v3.2.0", false, false, "Downloads\n\n---\n\n## v3.2.0\n\n- the third"),
            ("v3.3.0-rc.1", false, true, "Downloads\n\n---\n\n## v3.3.0-rc.1"),
            ("v3.3.0-rc.2", true, true, "a draft"),
            ("v3.3.0", false, false, ""),
            ("nightly-40-aaaaaaaa", false, true, "Downloads\n\n---\n\n## nightly-40"),
            ("nightly-41-bbbbbbbb", false, true, "Downloads\n\n---\n\n## nightly-41"),
            ("v2.9.0", false, false, "2.x")));
        Require(list[0].Body!.EndsWith("- the first", StringComparison.Ordinal) && list.First(r => r.Tag == "v3.3.0").Body == "",
                "The release list keeps each page's text.");
        string Tags(IReadOnlyList<ReleaseNote> notes) => string.Join(" ", notes.Select(n => n.Tag));
        var p = Product.Longshot;

        Require(Tags(ReleaseNotes.Between(list, "v3.1.0", "v3.2.0", p)) == "v3.2.0", "One stable to the next: its notes.");
        Require(Tags(ReleaseNotes.Between(list, "v3.0.0", "v3.2.0", p)) == "v3.2.0 v3.1.0",
                "Two stables behind: each stable since, newest first (each one's notes start at the stable before it).");
        Require(Tags(ReleaseNotes.Between(list, "v3.1.0-rc.1", "v3.1.0-rc.2", p)) == "v3.1.0-rc.2",
                "An rc after the first covers every rc since the last stable: its notes alone.");
        Require(Tags(ReleaseNotes.Between(list, "v3.0.0", "v3.1.0-rc.2", p)) == "v3.1.0-rc.2", "From the last stable to an rc: the rc's notes.");
        Require(Tags(ReleaseNotes.Between(list, "v3.1.0-rc.1", "v3.3.0", p)) == "v3.3.0 v3.2.0 v3.1.0",
                "From an rc to a stable two later: each stable since, the one that came out of the player's rc too.");
        Require(Tags(ReleaseNotes.Between(list, "v3.2.0", "v3.3.0-rc.1", p)) == "v3.3.0-rc.1", "A first rc: since the version before it.");
        Require(Tags(ReleaseNotes.Between(list, "v3.1.0-rc.2", "v3.3.0-rc.1", p)) == "v3.3.0-rc.1 v3.2.0 v3.1.0",
                "A first rc follows the version before it, then the stables.");
        Require(Tags(ReleaseNotes.Between(list, "nightly-12-cccccccc", "nightly-41-bbbbbbbb", p)) == "nightly-41-bbbbbbbb",
                "A nightly: the newest alone (each nightly's notes hold the ones before it).");
        Require(Tags(ReleaseNotes.Between(list, "dev", "v3.2.0", p)) == "v3.2.0" && Tags(ReleaseNotes.Between(list, null, "v3.2.0", p)) == "v3.2.0"
                && Tags(ReleaseNotes.Between(list, "v3.3.0", "v3.1.0", p)) == "v3.1.0",
                "A version that is none, or newer than the one offered: the offered one's notes alone.");
        Require(ReleaseNotes.Between(list, "v3.2.0", "v3.3.0-rc.2", p).Count == 0 && ReleaseNotes.Between(null, "v3.2.0", "v3.3.0", p).Count == 0
                && ReleaseNotes.Between(list, "v3.2.0", "v3.9.0", p).Count == 0,
                "No notes for a draft, a version not listed, or no list (the window opens the release page).");
        Require(ReleaseNotes.Since(list.Select(r => r.Tag), "v3.1.0", p) == "v3.0.0" && ReleaseNotes.Since(list.Select(r => r.Tag), "v3.0.0", p) == null
                && ReleaseNotes.Since(list.Select(r => r.Tag), "v3.1.0", Product.Launcher) == "v3.0.0"
                && ReleaseNotes.Since(list.Select(r => r.Tag), "v2.9.0", p) == null,
                "Where a page's notes start: never a 2.x number.");
        var many = Enumerable.Range(0, 20).Select(i => ($"v3.{i}.0", false, false, "x")).ToArray();
        Require(ReleaseNotes.Between(ReleaseManifest.ParseList(Json(many)), "v3.0.0", "v3.19.0", p).Count == ReleaseNotes.Most,
                "A player many versions behind reads the newest few.");

        var empty = ReleaseNotes.Between(list, "v3.2.0", "v3.3.0", p).Single();
        Require(empty.Blocks.Count == 0 && empty.Page == "https://git.magicbean.dev/MagicBeanDev/Longshot/releases/tag/v3.3.0",
                "A page with no notes: no blocks, and its page.");
        var rc2 = ReleaseNotes.Between(list, "v3.1.0-rc.1", "v3.1.0-rc.2", p).Single();
        Require(rc2.Blocks.Select(b => b.Kind).SequenceEqual([MdKind.Heading, MdKind.Item]) && rc2.Blocks[1].Text == "rc two",
                "A page's notes as blocks, without its downloads.");
        Require(ReleaseNotes.PageOf(Product.Launcher, "v1.0.0") == "https://git.magicbean.dev/MagicBeanDev/LongshotLauncher/releases/tag/v1.0.0",
                "The launcher's release page.");
    }

    static string Json(params (string Tag, bool Draft, bool Prerelease, string Body)[] releases) =>
        new System.Text.Json.Nodes.JsonArray(releases.Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["tag_name"] = r.Tag, ["draft"] = r.Draft, ["prerelease"] = r.Prerelease, ["body"] = r.Body,
            ["html_url"] = "https://git.magicbean.dev/MagicBeanDev/Longshot/releases/tag/" + r.Tag, ["assets"] = new System.Text.Json.Nodes.JsonArray(),
        }).ToArray()).ToJsonString();

    static void MarkdownChecks()
    {
        string Show(IReadOnlyList<MdSpan> spans) => string.Concat(spans.Select(s =>
            (s.Bold ? "B" : "") + (s.Italic ? "I" : "") + (s.Code ? "C" : "") + (s.Link != null ? "L<" + s.Link + ">" : "") + "[" + s.Text + "]"));

        var blocks = Markdown.Parse(
            "## What's new\n\n" +
            "- **Faster.** A new version\n  reuses the files.\n" +
            "- **Fixes:**\n  - Building no longer needs AVX2.\n  - The log is in colour.\n\n" +
            "### Features\n\n" +
            "1. First\n2) Second\n\n" +
            "A paragraph\nover two lines.\n\n" +
            "---\n\n" +
            "```\ncode *as is*\n```\n" +
            "> a quote\n" +
            "#nohashtag heading\n");
        Require(blocks.Select(b => b.Kind).SequenceEqual([MdKind.Heading, MdKind.Item, MdKind.Item, MdKind.Item, MdKind.Item, MdKind.Heading,
                MdKind.Item, MdKind.Item, MdKind.Paragraph, MdKind.Rule, MdKind.Code, MdKind.Paragraph]),
                "The blocks: " + string.Join(" ", blocks.Select(b => b.Kind)));
        Require(blocks[0].Level == 2 && blocks[0].Text == "What's new" && blocks[5].Level == 3, "A heading and its level.");
        Require(blocks[1].Text == "Faster. A new version reuses the files." && blocks[1].Level == 0 && blocks[1].Number == null,
                "A bullet over two lines is one item.");
        Require(blocks[3].Level == 1 && blocks[3].Text == "Building no longer needs AVX2." && blocks[4].Level == 1, "A bullet under a bullet.");
        Require(blocks[6].Number == "1." && blocks[7].Number == "2." && blocks[7].Text == "Second", "Numbered items.");
        Require(blocks[8].Text == "A paragraph over two lines.", "A paragraph's lines joined.");
        Require(blocks[10].Text == "code *as is*" && blocks[10].Spans.Single().Code, "Code is kept as it is.");
        Require(blocks[11].Text == "a quote #nohashtag heading", "A quote's words, and a # with no space is text.");
        Require(Markdown.Parse(null).Count == 0 && Markdown.Parse("\n \n").Count == 0, "No text, no blocks.");
        Require(Markdown.Parse("# Title #").Single().Text == "Title" && Markdown.Parse("***").Single().Kind == MdKind.Rule
                && Markdown.Parse("* * *").Single().Kind == MdKind.Rule && Markdown.Parse("* item").Single().Kind == MdKind.Item,
                "A heading's closing #s; *** a rule; * a bullet.");

        foreach (var (text, want) in new (string, string)[]
        {
            ("plain", "[plain]"),
            ("**Fixes:** one", "B[Fixes:][ one]"),
            ("a *word* and _another_", "[a ]I[word][ and ]I[another]"),
            ("***both***", "BI[both]"),
            ("**bold *and italic* in it**", "B[bold ]BI[and italic]B[ in it]"),
            ("snake_case_name and a_b", "[snake_case_name and a_b]"),
            ("a ** b ** c", "[a ** b ** c]"),
            ("an unclosed **bold", "[an unclosed **bold]"),
            ("`--user` and `a*b*`", "C[--user][ and ]C[a*b*]"),
            ("[the page](https://git.magicbean.dev/x \"title\")", "L<https://git.magicbean.dev/x>[the page]"),
            ("[**bold** link](https://a.example/b)", "BL<https://a.example/b>[bold]L<https://a.example/b>[ link]"),
            ("[a file](file:///etc/passwd) [a program](calc.exe) [relative](/x)", "[a file a program relative]"),
            ("![picture](https://a.example/p.png)", "[picture]"),
            ("see https://a.example/x, then <https://b.example/y>.", "[see ]L<https://a.example/x>[https://a.example/x][, then ]L<https://b.example/y>[https://b.example/y][.]"),
            ("javascript:alert(1) <javascript:x>", "[javascript:alert(1) <javascript:x>]"),
            ("\\*not italic\\* and \\_", "[*not italic* and _]"),
            ("&nbsp;Download &amp; play &lt;now&gt; &#39;s", "[\u00a0Download & play <now> 's]"),
            ("🪟 [x]", "[🪟 [x]]"),
        })
            Require(Show(Markdown.Inline(text)) == want, $"Markdown.Inline(\"{text}\"): {Show(Markdown.Inline(text))}, not {want}");
        Require(Show(Markdown.Inline("Delete leaves them (#101), not C#1 or #x", "MagicBeanDev/LongshotLauncher"))
                == "[Delete leaves them (]L<https://git.magicbean.dev/MagicBeanDev/LongshotLauncher/pulls/101>[#101][), not C#1 or #x]",
                "#123 links to the repository's pull request, only as a word of its own.");
        Require(Show(Markdown.Inline("(#101)")) == "[(#101)]", "No repository, no pull request link.");
        Require(Show(Markdown.Inline("it&#39;s", "MagicBeanDev/Longshot")) == "[it's]", "An entity's # is no pull request.");
        Require(Markdown.Web("https://git.magicbean.dev/a") != null && Markdown.Web("http://x.example") != null && Markdown.Web("file:///c") == null
                && Markdown.Web("C:\\Windows\\calc.exe") == null && Markdown.Web("https://a b") == null && Markdown.Web("mailto:a@b") == null,
                "Only a web page's address is a link.");
    }
}
