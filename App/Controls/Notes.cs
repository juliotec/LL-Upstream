using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using LongshotLauncher.Core;

namespace LongshotLauncher.Controls;

/// A release page's notes (Core.Markdown's blocks) drawn as text: headings, paragraphs, bullets and numbered items, rules and
/// code, with bold, italic, code and links in the text. A link opens through Open (the window's host: the browser)
public sealed class Notes : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MdBlock>?> BlocksProperty =
        AvaloniaProperty.Register<Notes, IReadOnlyList<MdBlock>?>(nameof(Blocks));
    public static readonly StyledProperty<Action<string>?> OpenProperty = AvaloniaProperty.Register<Notes, Action<string>?>(nameof(Open));

    static Notes()
    {
        BlocksProperty.Changed.AddClassHandler<Notes>((n, _) => n.Fill());
    }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    // (drawn again once in the window: the brushes are the window's resources)
    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Fill();
    }

    public IReadOnlyList<MdBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public Action<string>? Open
    {
        get => GetValue(OpenProperty);
        set => SetValue(OpenProperty, value);
    }

    IBrush Brush(string key, string fallback) =>
        this.TryFindResource(key, out var b) && b is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));

    void Fill()
    {
        Children.Clear();
        var first = true;
        foreach (var block in Blocks ?? Array.Empty<MdBlock>())
        {
            Children.Add(Draw(block, first));
            first = false;
        }
    }

    Control Draw(MdBlock block, bool first)
    {
        switch (block.Kind)
        {
            case MdKind.Rule:
                return new Border { Height = 1, Background = Brush("GlassLineStrong", "#2AFFFFFF"), Margin = new Thickness(0, 12) };
            case MdKind.Code:
                return new Border
                {
                    Margin = new Thickness(0, 4), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.Parse("#26000000")),
                    Child = new TextBlock
                    {
                        Text = block.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkSoft", "#A2A9B3"),
                        FontFamily = this.TryFindResource("Mono", out var mono) && mono is FontFamily f ? f : FontFamily.Default,
                    },
                };
            case MdKind.Heading:
                var heading = Text(block.Spans);
                heading.FontSize = block.Level switch { 1 => 17, 2 => 15, _ => 14 };
                heading.FontWeight = block.Level <= 2 ? FontWeight.Bold : FontWeight.SemiBold;
                heading.Foreground = Brush("Ink", "#EDEFF2");
                heading.Margin = new Thickness(0, first ? 0 : block.Level <= 2 ? 16 : 12, 0, 4);
                return heading;
            case MdKind.Item:
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(block.Level * 18, 2, 0, 2) };
                var marker = new TextBlock
                {
                    Text = block.Number ?? "•", FontSize = 13, Foreground = Brush("InkFaintText", "#8A919B"),
                    MinWidth = block.Number != null ? 20 : 14,
                };
                var words = Text(block.Spans);
                Grid.SetColumn(words, 1);
                row.Children.Add(marker);
                row.Children.Add(words);
                return row;
            default:
                var paragraph = Text(block.Spans);
                paragraph.Margin = new Thickness(0, first ? 0 : 4, 0, 4);
                return paragraph;
        }
    }

    /// A line of the notes, its spans in their styles; its links pressed open them
    LinkText Text(IReadOnlyList<MdSpan> spans)
    {
        var text = new LinkText(this)
        {
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkSoft", "#A2A9B3"), LineHeight = 19,
        };
        var inlines = new InlineCollection();
        var at = 0;
        foreach (var s in spans)
        {
            var run = new Run(s.Text);
            if (s.Bold) run.FontWeight = FontWeight.SemiBold;
            if (s.Bold) run.Foreground = Brush("Ink", "#EDEFF2");
            if (s.Italic) run.FontStyle = FontStyle.Italic;
            if (s.Code)
            {
                run.FontFamily = this.TryFindResource("Mono", out var mono) && mono is FontFamily f ? f : FontFamily.Default;
                run.FontSize = 12;
                run.Background = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
            }
            if (s.Link != null)
            {
                run.Foreground = Brush("InfoInk", "#B9C8FF");
                run.TextDecorations = TextDecorations.Underline;
                text.Links.Add((at, at + s.Text.Length, s.Link));
            }
            inlines.Add(run);
            at += s.Text.Length;
        }
        text.Inlines = inlines;
        return text;
    }

    /// A text whose links (where they are in its text) open when pressed, with the hand over them
    sealed class LinkText(Notes owner) : TextBlock
    {
        static readonly Cursor Hand = new(StandardCursorType.Hand);
        public readonly List<(int Start, int End, string Url)> Links = new();

        protected override Type StyleKeyOverride => typeof(TextBlock);

        string? LinkAt(PointerEventArgs e)
        {
            if (Links.Count == 0) return null;
            var p = e.GetPosition(this);
            var hit = TextLayout.HitTestPoint(new Point(p.X - Padding.Left, p.Y - Padding.Top));
            if (!hit.IsInside) return null;
            var at = hit.TextPosition;
            foreach (var (start, end, url) in Links)
                if (at >= start && at < end) return url;
            return null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Cursor = LinkAt(e) != null ? Hand : Cursor.Default;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton == MouseButton.Left && LinkAt(e) is { } url)
            {
                owner.Open?.Invoke(url);
                e.Handled = true;
            }
        }
    }
}
