using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace NoFrills.MemoryProfiler.App.Controls;

/// <summary>One pause on the timeline: where it starts, how long it lasts, and what it was for.</summary>
public sealed record TimelineBar(double StartMs, double DurationMs, string Label, Color Color);

/// <summary>
/// Plots the pauses of a GC recording against time, one bar per pause, as tall as the pause was
/// long. Drawn directly like the evolution chart, for the same reason.
/// </summary>
public sealed class PauseTimelineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<TimelineBar>?> BarsProperty =
        AvaloniaProperty.Register<PauseTimelineChart, IReadOnlyList<TimelineBar>?>(nameof(Bars));

    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<PauseTimelineChart, double>(nameof(DurationMs));

    static PauseTimelineChart()
    {
        AffectsRender<PauseTimelineChart>(BarsProperty, DurationMsProperty);
    }

    public PauseTimelineChart()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<TimelineBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public double DurationMs
    {
        get => GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }

    private const double Left = 60;
    private const double Top = 10;
    private const double LegendWidth = 170;
    private const int GridLineCount = 4;

    /// <summary>Pauses are a millisecond on an axis of seconds: narrower than this, a bar is not seen.</summary>
    private const double MinimalBarWidth = 2;

    private TimelineBar? m_hovered;

    private double Right => Math.Max(Left + 10, Bounds.Width - LegendWidth);
    private double Bottom => Math.Max(Top + 10, Bounds.Height - 24);

    private double XOf(double ms) => Left + (Right - Left) * ms / Math.Max(DurationMs, 1);

    private IBrush Palette(string key, Color fallback)
    {
        if (this.TryFindResource(key, out var found) && found is IBrush brush) return brush;

        return new SolidColorBrush(fallback);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var x = e.GetPosition(this).X;

        // The nearest bar within a few pixels: pauses close together are told apart by moving along.
        var hovered = (Bars ?? Array.Empty<TimelineBar>())
            .Select(bar => (Bar: bar, Distance: Math.Abs(XOf(bar.StartMs) - x)))
            .Where(candidate => candidate.Distance <= 6)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Bar)
            .FirstOrDefault();

        if (hovered == m_hovered) return;

        m_hovered = hovered;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        m_hovered = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bars = Bars ?? Array.Empty<TimelineBar>();
        var foreground = Palette("TextSecondaryBrush", Color.FromArgb(140, 128, 128, 128));
        var axisPen = new Pen(Palette("BorderBrush", Color.FromArgb(140, 128, 128, 128)), 1);
        var typeface = new Typeface(FontFamily.Default);

        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (bars.Count == 0)
        {
            Draw(context, "Record a process, or open a recording, to see when its threads were stopped.", 12, Left, Top, typeface, foreground);
            return;
        }

        // Kept at zero: the length of a pause is the information.
        var maximal = bars.Max(bar => bar.DurationMs) * 1.1;
        double YOf(double ms) => Bottom - (Bottom - Top) * ms / maximal;

        for (var i = 0; i <= GridLineCount; ++i)
        {
            var value = maximal * i / GridLineCount;
            var y = YOf(value);

            context.DrawLine(axisPen, new Point(Left, y), new Point(Right, y));
            Draw(context, $"{value:F1} ms", 11, 4, y - 8, typeface, foreground);
        }

        Draw(context, "0 s", 11, Left, Bottom + 4, typeface, foreground);
        Draw(context, $"{DurationMs / 1000:F1} s", 11, Right - 40, Bottom + 4, typeface, foreground);

        foreach (var bar in bars)
        {
            var width = Math.Max(MinimalBarWidth, XOf(bar.StartMs + bar.DurationMs) - XOf(bar.StartMs));

            context.FillRectangle(new SolidColorBrush(bar.Color), new Rect(XOf(bar.StartMs), YOf(bar.DurationMs), width, Bottom - YOf(bar.DurationMs)));
        }

        var y0 = Top;

        foreach (var legend in bars.Select(bar => (bar.Label, bar.Color)).Distinct().OrderBy(legend => legend.Label, StringComparer.Ordinal))
        {
            context.FillRectangle(new SolidColorBrush(legend.Color), new Rect(Right + 8, y0 + 4, 10, 8));
            Draw(context, legend.Label, 11, Right + 22, y0, typeface, foreground);

            y0 += 16;
        }

        DrawHover(context, typeface);
    }

    private void DrawHover(DrawingContext context, Typeface typeface)
    {
        if (m_hovered == null) return;

        var text = $"{m_hovered.StartMs / 1000:F3} s   {m_hovered.DurationMs:F2} ms   {m_hovered.Label}";
        var x = XOf(m_hovered.StartMs);
        var width = text.Length * 7 + 16;
        var box = new Rect(x + width + 12 < Right ? x + 10 : x - width - 10, Top + 6, width, 26);

        context.FillRectangle(Palette("SurfaceElevatedBrush", Color.FromArgb(230, 32, 32, 32)), box, 4);
        Draw(context, text, 12, box.X + 8, box.Y + 5, typeface, new SolidColorBrush(m_hovered.Color));
    }

    private static void Draw(DrawingContext context, string text, double size, double x, double y, Typeface typeface, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

        context.DrawText(formatted, new Point(x, y));
    }
}
