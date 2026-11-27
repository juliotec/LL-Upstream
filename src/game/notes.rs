//! Release Markdown is parsed locally. Links are followed only on request; images retain
//! their description and link without making background requests from the notes viewer.
use crate::NoteBlock;
use pulldown_cmark::{Event, Options, Parser, Tag, TagEnd};
use slint::{ModelRc, StyledText, VecModel};
use std::fmt::Write as _;

const NOTES_START: &str = "<!-- longshot-launcher:release-notes -->";

/// Published release pages retain their installer instructions above this marker.
pub(crate) fn launcher_body(markdown: &str) -> &str {
    let boundary = if markdown.contains(NOTES_START) {
        Parser::new(markdown)
            .into_offset_iter()
            .find(|(event, _)| matches!(event, Event::Html(html) if html.trim() == NOTES_START))
    } else if matches!(
        markdown.lines().next(),
        Some("# 🎮 To play, get the Longshot Launcher" | "# ⬇️ Download Longshot Launcher")
    ) {
        // Releases published before the marker end this specific download banner with a rule.
        Parser::new(markdown)
            .into_offset_iter()
            .find(|(event, _)| matches!(event, Event::Rule))
    } else {
        None
    };
    boundary.map_or(markdown, |(_, span)| {
        markdown[span.end..].trim_start_matches(['\r', '\n'])
    })
}

pub(super) fn https(url: &str) -> bool {
    url.parse::<ureq::http::Uri>().is_ok_and(|uri| {
        uri.scheme_str() == Some("https")
            && uri
                .authority()
                .is_some_and(|a| !a.host().is_empty() && !a.as_str().contains('@'))
    })
}

#[derive(Default)]
struct Inline {
    markup: String,
    plain: String,
    links: Vec<Option<String>>,
}

impl Inline {
    fn text(&mut self, text: &str) {
        // pulldown-cmark has already decoded HTML entities and escapes. Do not
        // reinterpret literal content as a fresh link, HTML tag or formatting.
        let text = text.replace(":tada:", "🎉");
        self.plain.push_str(&text);
        for c in text.chars() {
            match c {
                '&' => self.markup.push_str("&amp;"),
                '<' => self.markup.push_str("&lt;"),
                '>' => self.markup.push_str("&gt;"),
                c if c.is_ascii_punctuation() => {
                    self.markup.push('\\');
                    self.markup.push(c);
                }
                _ => self.markup.push(c),
            }
        }
    }
    fn begin_link(&mut self, url: &str) {
        // A linked image uses the surrounding link, just as a browser does.
        let allowed = https(url) && !self.links.iter().any(Option::is_some);
        if allowed {
            self.markup.push('[');
        }
        self.links.push(allowed.then(|| url.to_owned()));
    }
    fn end_link(&mut self) {
        if let Some(Some(url)) = self.links.pop() {
            let _ = write!(self.markup, "](<{}>)", url.replace('>', "%3E"));
        }
    }
    fn styled(&self, strong: bool) -> StyledText {
        let source = self.markup.trim();
        let source = if strong {
            format!("**{source}**")
        } else {
            source.to_owned()
        };
        StyledText::from_markdown(&source)
            .unwrap_or_else(|_| StyledText::from_plain_text(&self.plain))
    }
}

#[derive(Default)]
struct Document {
    blocks: Vec<NoteBlock>,
    inline: Inline,
    heading: i32,
    level: usize,
    lists: Vec<Option<u64>>,
    marker: String,
    quote: usize,
    code: bool,
    table_head: bool,
    cells: Vec<StyledText>,
}

impl Document {
    fn block(&mut self) {
        let inline = std::mem::take(&mut self.inline);
        if inline.plain.trim().is_empty() {
            return;
        }
        let kind = if self.code {
            "code"
        } else if self.heading != 0 {
            "heading"
        } else if self.quote != 0 {
            "quote"
        } else {
            "paragraph"
        };
        // Older published nightlies used the same heading without a divider.
        let title = inline.plain.trim();
        if self.heading == 2
            && (title == "Earlier changes" || title.starts_with("Earlier changes (since "))
            && self.blocks.last().is_some_and(|b| b.kind != "rule")
        {
            self.rule();
        }
        self.blocks.push(NoteBlock {
            text: if self.code {
                StyledText::from_plain_text(inline.plain.trim_end())
            } else {
                inline.styled(self.heading != 0)
            },
            plain: inline.plain.trim().into(),
            kind: kind.into(),
            marker: std::mem::take(&mut self.marker).into(),
            level: if self.heading != 0 {
                self.heading
            } else {
                self.level as i32
            },
            ..Default::default()
        });
    }
    fn rule(&mut self) {
        self.blocks.push(NoteBlock {
            kind: "rule".into(),
            ..Default::default()
        });
    }

    fn row(&mut self) {
        self.blocks.push(NoteBlock {
            kind: if self.table_head {
                "table-head"
            } else {
                "table-row"
            }
            .into(),
            cells: ModelRc::new(VecModel::from(std::mem::take(&mut self.cells))),
            ..Default::default()
        });
    }
    fn event(&mut self, event: Event<'_>) {
        match event {
            Event::Start(Tag::Heading { level, .. }) => {
                self.block();
                self.heading = level as i32;
            }
            Event::End(TagEnd::Heading(_)) => {
                self.block();
                self.heading = 0;
            }
            Event::Start(Tag::BlockQuote(_)) => {
                self.block();
                self.quote += 1;
            }
            Event::End(TagEnd::BlockQuote(_)) => {
                self.block();
                self.quote = self.quote.saturating_sub(1);
            }
            Event::Start(Tag::CodeBlock(_)) => {
                self.block();
                self.code = true;
            }
            Event::End(TagEnd::CodeBlock) => {
                self.block();
                self.code = false;
            }
            Event::Start(Tag::List(number)) => {
                self.block();
                self.lists.push(number);
            }
            Event::End(TagEnd::List(_)) => {
                self.block();
                self.lists.pop();
                self.level = self.lists.len();
            }
            Event::Start(Tag::Item) => {
                self.block();
                self.level = self.lists.len();
                self.marker = if let Some(Some(n)) = self.lists.last_mut() {
                    let label = format!("{n}.");
                    *n += 1;
                    label
                } else {
                    "•".into()
                };
            }
            Event::End(TagEnd::Item | TagEnd::Paragraph) => self.block(),
            Event::Start(Tag::Strong) | Event::End(TagEnd::Strong) => {
                self.inline.markup.push_str("**")
            }
            Event::Start(Tag::Emphasis) | Event::End(TagEnd::Emphasis) => {
                self.inline.markup.push('*')
            }
            Event::Start(Tag::Strikethrough) | Event::End(TagEnd::Strikethrough) => {
                self.inline.markup.push_str("~~")
            }
            Event::Start(Tag::Link { dest_url, .. } | Tag::Image { dest_url, .. }) => {
                self.inline.begin_link(&dest_url)
            }
            Event::End(TagEnd::Link | TagEnd::Image) => self.inline.end_link(),
            Event::Text(text) => self.inline.text(&text),
            Event::Code(text) => {
                let fence =
                    "`".repeat(text.split(|c| c != '`').map(str::len).max().unwrap_or(0) + 1);
                let _ = write!(self.inline.markup, "{fence} {text} {fence}");
                self.inline.plain.push_str(&text);
            }
            Event::SoftBreak => self.inline.text(" "),
            Event::HardBreak => {
                self.inline.markup.push_str("  \n");
                self.inline.plain.push('\n');
            }
            Event::Rule => {
                self.block();
                self.rule();
            }
            Event::Start(Tag::TableHead) => {
                self.table_head = true;
            }
            Event::End(TagEnd::TableCell) => {
                self.cells.push(self.inline.styled(self.table_head));
                self.inline = Inline::default();
            }
            Event::End(TagEnd::TableRow) => self.row(),
            Event::End(TagEnd::TableHead) => {
                self.row();
                self.table_head = false;
            }
            Event::InlineHtml(tag) if matches!(tag.as_ref(), "<br>" | "<br/>" | "<br />") => {
                self.event(Event::HardBreak)
            }
            Event::TaskListMarker(checked) => {
                self.inline.text(if checked { "☑ " } else { "☐ " })
            }
            _ => {}
        }
    }
}

pub(super) fn blocks(markdown: &str) -> Vec<NoteBlock> {
    let mut doc = Document::default();
    let options =
        Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH | Options::ENABLE_TASKLISTS;
    for event in Parser::new_ext(markdown, options) {
        doc.event(event);
    }
    doc.block();
    doc.blocks
}

#[cfg(test)]
mod tests {
    use super::*;
    use slint::Model;
    #[test]
    fn earlier_changes_has_one_divider_in_new_and_preexisting_release_notes() {
        for separator in ["", "---\n\n"] {
            let body = format!(
                "New fixes.\n\n{separator}## Earlier changes (since v3.2.0)\n\nPrevious fixes."
            );
            let rows = blocks(&body);
            let kinds: Vec<_> = rows.iter().map(|row| row.kind.as_str()).collect();
            assert_eq!(kinds, ["paragraph", "rule", "heading", "paragraph"]);
        }
    }

    #[test]
    fn release_boundary_skips_only_the_web_preamble() {
        let marked =
            format!("# Installer\r\n\r\n---\r\n\r\n{NOTES_START}\r\n\r\n## Fixes\r\nOne fix.");
        assert_eq!(launcher_body(&marked), "## Fixes\r\nOne fix.");
        let legacy = "# 🎮 To play, get the Longshot Launcher\r\n\r\nDownload here.\r\n\r\n---\r\n\r\n## Nightly\nFixes\n\n---\nEarlier changes";
        assert_eq!(
            launcher_body(legacy),
            "## Nightly\nFixes\n\n---\nEarlier changes"
        );
        let unmarked = "# Notes\n\n---\n\nSecond section";
        assert_eq!(launcher_body(unmarked), unmarked);
        let example = format!("Example:\n\n```html\n{NOTES_START}\n```");
        assert_eq!(launcher_body(&example), example);
    }

    #[test]
    fn full_release_preserves_intro_styling_links_rule_and_table() {
        let source = "# 🎮 Play\n\n### [🪟 &nbsp;Download for Windows](https://example.test/setup)\n\n[![Linux](https://example.test/icon.svg)](https://example.test/linux)\n\n**Keep** your own `copy`.\n\n---\n:tada: Released!\n\n## What's new\n- A **faster** start.\n  1. Start [here](https://example.test/guide).\n\n| Place | FPS |\n| --- | --- |\n| Room | 72 |\n";
        let rows = blocks(source);
        assert_eq!(rows[0].kind, "heading");
        assert_eq!(rows[0].plain, "🎮 Play");
        assert!(rows[1].plain.contains('\u{a0}'));
        assert_eq!(rows[2].plain, "Linux");
        assert!(format!("{:?}", rows[1].text).contains("https://example.test/setup"));
        assert!(rows.iter().any(|b| b.kind == "rule"));
        assert!(rows.iter().any(|b| b.plain == "🎉 Released!"));
        let debug = format!("{:?}", rows[2].text);
        assert!(debug.contains("https://example.test/linux"));
        assert!(!debug.contains("https://example.test/icon.svg"));
        assert!(format!("{:?}", rows[3].text).contains("Strong"));
        assert!(
            rows.iter()
                .any(|b| b.marker == "•" && b.plain == "A faster start.")
        );
        let row = rows.iter().find(|b| b.kind == "table-row").unwrap();
        assert_eq!(row.cells.row_count(), 2);
        assert!(
            rows.iter()
                .any(|b| b.level == 2 && b.marker == "1." && b.plain == "Start here.")
        );
        assert!(!https("file:///private"));
        assert!(!https("https://trusted.example@outside.example/"));
        assert!(https("https://example.test/a"));
    }
}
