using Avalonia;
using Avalonia.Controls;

namespace LongshotLauncher.Controls;

/// Children side by side, each as wide as its Share of all of theirs (the Storage page's bar: each group as wide as the space it
/// takes), at least MinChild wide so the smallest stays one to click, Spacing between them; as tall as the tallest
public sealed class ShareRow : Panel
{
    public static readonly AttachedProperty<double> ShareProperty = AvaloniaProperty.RegisterAttached<ShareRow, Control, double>("Share", 1.0);
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ShareRow, double>(nameof(Spacing), 3);
    public static readonly StyledProperty<double> MinChildProperty = AvaloniaProperty.Register<ShareRow, double>(nameof(MinChild), 12);

    static ShareRow()
    {
        AffectsParentMeasure<ShareRow>(ShareProperty);
        AffectsMeasure<ShareRow>(SpacingProperty, MinChildProperty);
    }

    public static double GetShare(Control c) => c.GetValue(ShareProperty);
    public static void SetShare(Control c, double value) => c.SetValue(ShareProperty, value);
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double MinChild { get => GetValue(MinChildProperty); set => SetValue(MinChildProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        var widths = Widths(shown, double.IsFinite(available.Width) ? available.Width : shown.Count * 120);
        double height = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].Measure(new Size(widths[i], available.Height));
            height = Math.Max(height, shown[i].DesiredSize.Height);
        }
        return new Size(double.IsFinite(available.Width) ? available.Width : widths.Sum() + Spacing * Math.Max(0, shown.Count - 1), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        var widths = Widths(shown, final.Width);
        double x = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].Arrange(new Rect(x, 0, widths[i], final.Height));
            x += widths[i] + Spacing;
        }
        return final;
    }

    /// Each child's width: its share of the width less the spacing; one that would be narrower than MinChild is that wide, and
    /// the rest share what is left (again, until none is below it)
    double[] Widths(IReadOnlyList<Control> shown, double width)
    {
        var n = shown.Count;
        var widths = new double[n];
        if (n == 0) return widths;
        var room = Math.Max(0, width - Spacing * (n - 1));
        var shares = shown.Select(c => Math.Max(0, GetShare(c))).ToArray();
        var fixedAt = new bool[n];
        for (var pass = 0; pass < n; pass++)
        {
            var left = room - MinChild * fixedAt.Count(f => f);
            var sum = shares.Where((_, i) => !fixedAt[i]).Sum();
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (fixedAt[i]) { widths[i] = MinChild; continue; }
                widths[i] = sum > 0 ? Math.Max(0, left) * shares[i] / sum : Math.Max(0, left) / Math.Max(1, fixedAt.Count(f => !f));
                if (widths[i] < MinChild) { fixedAt[i] = true; changed = true; }
            }
            if (!changed) break;
        }
        for (var i = 0; i < n; i++) if (fixedAt[i]) widths[i] = MinChild;
        return widths;
    }
}
