using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace NoFrills.MemoryProfiler.App.Controls;

public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, Color Color);

/// <summary>
/// Plots the series of a dump sequence against the moment each dump was taken.
///
/// Drawn directly rather than with a charting library: the need is one plot with a readout, and a
/// dependency that has to follow the UI framework version is a poor trade for that.
/// </summary>
public sealed class TimeSeriesChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<DateTime>?> TimesProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IReadOnlyList<DateTime>?>(nameof(Times));

    public static readonly StyledProperty<string> ValueUnitProperty =
        AvaloniaProperty.Register<TimeSeriesChart, string>(nameof(ValueUnit), "MB");

    static TimeSeriesChart()
    {
        AffectsRender<TimeSeriesChart>(SeriesProperty, TimesProperty, ValueUnitProperty);
    }

    public TimeSeriesChart()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<DateTime>? Times
    {
        get => GetValue(TimesProperty);
        set => SetValue(TimesProperty, value);
    }

    public string ValueUnit
    {
        get => GetValue(ValueUnitProperty);
        set => SetValue(ValueUnitProperty, value);
    }

    private int m_hoveredIndex = -1;

    /// <summary>
    /// The chart paints itself, so it has to read the palette rather than inherit it. Falling back
    /// keeps it drawable in a designer or a test with no application resources loaded.
    /// </summary>
    private IBrush Palette(string key, Color fallback)
    {
        if (this.TryFindResource(key, out var found) && found is IBrush brush) return brush;

        return new SolidColorBrush(fallback);
    }

    private Color PaletteColor(string key, Color fallback)
    {
        if (this.TryFindResource(key, out var found) && found is Color color) return color;

        return fallback;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        var layout = Measure();

        var hovered = layout.IndexAt(position.X);

        if (hovered == m_hoveredIndex) return;

        m_hoveredIndex = hovered;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        m_hoveredIndex = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var layout = Measure();
        var foreground = Palette("TextSecondaryBrush", Color.FromArgb(140, 128, 128, 128));
        var axisPen = new Pen(Palette("BorderBrush", Color.FromArgb(140, 128, 128, 128)), 1);
        var typeface = new Typeface(FontFamily.Default);

        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (layout.PointCount == 0)
        {
            Draw(context, "Load a series of dumps to plot how memory evolves.", 12, layout.Left, layout.Top, typeface, foreground);
            return;
        }

        // Horizontal grid and value labels.
        for (var i = 0; i <= GridLineCount; ++i)
        {
            var value = layout.MinimalValue + (layout.MaximalValue - layout.MinimalValue) * i / GridLineCount;
            var y = layout.YOf(value);

            context.DrawLine(axisPen, new Point(layout.Left, y), new Point(layout.Right, y));
            Draw(context, value.ToString("N0", CultureInfo.CurrentCulture), 11, 4, y - 8, typeface, foreground);
        }

        // Time labels, first and last.
        if (layout.Times != null && layout.Times.Count != 0)
        {
            Draw(context, layout.Times[0].ToString("MM-dd HH:mm"), 11, layout.Left, layout.Bottom + 4, typeface, foreground);

            var last = layout.Times[^1].ToString("MM-dd HH:mm");
            Draw(context, last, 11, layout.Right - 70, layout.Bottom + 4, typeface, foreground);
        }

        // Series.
        foreach (var series in layout.Series)
        {
            if (series.Values.Count < 2) continue;

            var pen = new Pen(new SolidColorBrush(series.Color), 2);
            var geometry = new StreamGeometry();

            using (var draw = geometry.Open())
            {
                draw.BeginFigure(new Point(layout.XOf(0), layout.YOf(series.Values[0])), false);

                for (var i = 1; i < series.Values.Count; ++i)
                {
                    draw.LineTo(new Point(layout.XOf(i), layout.YOf(series.Values[i])));
                }

                draw.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        DrawHover(context, layout, typeface);
        DrawLegend(context, layout, typeface, foreground);
    }

    private void DrawHover(DrawingContext context, Layout layout, Typeface typeface)
    {
        if (m_hoveredIndex < 0 || m_hoveredIndex >= layout.PointCount) return;

        var x = layout.XOf(m_hoveredIndex);
        var marker = new Pen(Palette("BorderBrush", Color.FromArgb(180, 128, 128, 128)), 1, DashStyle.Dash);

        context.DrawLine(marker, new Point(x, layout.Top), new Point(x, layout.Bottom));

        var lines = new List<(string Text, Color Color)>();
        var secondary = PaletteColor("TextSecondaryColor", Colors.Gray);

        if (layout.Times != null && m_hoveredIndex < layout.Times.Count)
        {
            lines.Add((layout.Times[m_hoveredIndex].ToString("yyyy-MM-dd HH:mm:ss"), secondary));
        }

        foreach (var series in layout.Series)
        {
            if (m_hoveredIndex >= series.Values.Count) continue;

            lines.Add(($"{series.Name}: {series.Values[m_hoveredIndex]:N1} {ValueUnit}", series.Color));
        }

        var width = lines.Count == 0 ? 0 : lines.Max(line => line.Text.Length) * 7 + 16;
        var height = lines.Count * 18 + 10;
        var boxX = x + width + 12 < layout.Right ? x + 10 : x - width - 10;
        var box = new Rect(boxX, layout.Top + 6, width, height);

        // The readout sits above the plot, which is what the elevated surface is for.
        context.FillRectangle(Palette("SurfaceElevatedBrush", Color.FromArgb(230, 32, 32, 32)), box, 4);

        for (var i = 0; i < lines.Count; ++i)
        {
            Draw(context, lines[i].Text, 12, box.X + 8, box.Y + 5 + i * 18, typeface,
                new SolidColorBrush(lines[i].Color));
        }
    }

    private static void DrawLegend(DrawingContext context, Layout layout, Typeface typeface, IBrush foreground)
    {
        var y = layout.Top;

        foreach (var series in layout.Series.Take(12))
        {
            context.FillRectangle(new SolidColorBrush(series.Color), new Rect(layout.Right + 8, y + 5, 10, 3));
            Draw(context, Shorten(series.Name), 11, layout.Right + 22, y, typeface, foreground);

            y += 16;
        }
    }

    private static string Shorten(string name)
    {
        var trimmed = name.Split('[')[0].TrimEnd();
        var lastDot = trimmed.LastIndexOf('.');
        var shortName = lastDot > 0 ? trimmed[(lastDot + 1)..] : trimmed;

        return shortName.Length > 26 ? shortName[..26] + "..." : shortName;
    }

    private static void Draw(DrawingContext context, string text, double size, double x, double y, Typeface typeface, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

        context.DrawText(formatted, new Point(x, y));
    }

    private const int GridLineCount = 5;
    private const double LegendWidth = 210;

    private Layout Measure()
    {
        var series = Series ?? Array.Empty<ChartSeries>();
        var pointCount = series.Count == 0 ? 0 : series.Max(one => one.Values.Count);

        var minimal = 0d;
        var maximal = 1d;

        if (pointCount != 0)
        {
            maximal = series.SelectMany(one => one.Values).DefaultIfEmpty(1).Max();
            if (maximal <= 0) maximal = 1;

            // Keep the baseline at zero: with memory, the distance to zero is the information.
            maximal *= 1.05;
        }

        return new Layout(
            series,
            Times,
            pointCount,
            minimal,
            maximal,
            Left: 60,
            Top: 10,
            Right: Math.Max(70, Bounds.Width - LegendWidth),
            Bottom: Math.Max(20, Bounds.Height - 24));
    }

    private sealed record Layout(
        IReadOnlyList<ChartSeries> Series,
        IReadOnlyList<DateTime>? Times,
        int PointCount,
        double MinimalValue,
        double MaximalValue,
        double Left,
        double Top,
        double Right,
        double Bottom)
    {
        public double XOf(int index) => PointCount <= 1
            ? (Left + Right) / 2
            : Left + (Right - Left) * index / (PointCount - 1);

        public double YOf(double value) => Bottom - (Bottom - Top) * (value - MinimalValue) / (MaximalValue - MinimalValue);

        public int IndexAt(double x)
        {
            if (PointCount == 0 || x < Left - 20 || x > Right + 20) return -1;
            if (PointCount == 1) return 0;

            var index = (int)Math.Round((x - Left) / (Right - Left) * (PointCount - 1));

            return Math.Clamp(index, 0, PointCount - 1);
        }
    }
}
