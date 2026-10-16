using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NoFrills.MemoryProfiler.App.Controls;
using NoFrills.MemoryProfiler.Core;

namespace NoFrills.MemoryProfiler.App.ViewModels;

/// <summary>One line of an allocation table, with its size readable and sortable as a number.</summary>
public sealed class AllocationRow(AllocationGroup group, long total)
{
    public string Name { get; } = group.Name;
    public long Bytes { get; } = group.Bytes;
    public int Samples { get; } = group.Samples;
    public string BytesDisplay { get; } = ReportWriter.Bytes(group.Bytes);
    public string ShareDisplay { get; } = ReportWriter.Percent(group.Bytes, total);
}

/// <summary>
/// The GC timeline tab: when the threads were stopped, for which collection, and what allocated.
/// </summary>
public sealed partial class GcTimelineViewModel : ObservableObject
{
    private const int RowLimit = 500;

    [ObservableProperty] private string m_description = "Record a process from the Live processes tab, or open a recording made before.";
    [ObservableProperty] private IReadOnlyList<TimelineBar>? m_bars;
    [ObservableProperty] private double m_durationMs;

    public GcTimeline? Loaded { get; private set; }

    public ObservableCollection<PauseStatistic> Generations { get; } = new();
    public ObservableCollection<GcPause> Pauses { get; } = new();
    public ObservableCollection<AllocationRow> ByType { get; } = new();
    public ObservableCollection<AllocationRow> ByThread { get; } = new();
    public ObservableCollection<AllocationRow> ByStack { get; } = new();

    public void Show(GcTimeline timeline)
    {
        Loaded = timeline;

        Fill(Generations, timeline.PausesByGeneration);
        // Past the limit, the longest pauses are the ones worth listing.
        Fill(Pauses, timeline.Pauses.OrderByDescending(pause => pause.DurationMs).Take(RowLimit).OrderBy(pause => pause.StartMs));
        Fill(ByType, timeline.AllocationsByType.Take(RowLimit).Select(group => new AllocationRow(group, timeline.AllocatedBytes)));
        Fill(ByThread, timeline.AllocationsByThread.Take(RowLimit).Select(group => new AllocationRow(group, timeline.AllocatedBytes)));
        Fill(ByStack, timeline.AllocationsByStack.Take(RowLimit).Select(group => new AllocationRow(group, timeline.AllocatedBytes)));

        DurationMs = timeline.DurationMs;
        Bars = timeline.Pauses.Select(pause => new TimelineBar(pause.StartMs, pause.DurationMs, pause.Generation, ColorOf(pause))).ToList();

        var longest = timeline.Pauses.Count == 0 ? 0 : timeline.Pauses.Max(pause => pause.DurationMs);

        Description =
            $"{System.IO.Path.GetFileName(timeline.FilePath)}: {timeline.DurationMs / 1000:F1} s, " +
            $"{timeline.Collections.Count:N0} collections, {timeline.Pauses.Count:N0} pauses " +
            $"totalling {timeline.TotalPauseMs:F1} ms (longest {longest:F2} ms), " +
            $"{ReportWriter.Bytes(timeline.AllocatedBytes)} allocated";
    }

    private static void Fill<T>(ObservableCollection<T> rows, IEnumerable<T> values)
    {
        rows.Clear();

        foreach (var value in values) rows.Add(value);
    }

    /// <summary>The colours of the evolution chart, one per kind of collection, grey for the rest.</summary>
    private static Color ColorOf(GcPause pause) => pause.Gc switch
    {
        null => Color.FromRgb(0x7F, 0x8C, 0x8D),
        { IsBackground: true } => Color.FromRgb(0x9B, 0x59, 0xB6),
        { Generation: 0 } => Color.FromRgb(0x4E, 0x9F, 0xE8),
        { Generation: 1 } => Color.FromRgb(0xE8, 0x7D, 0x3E),
        _ => Color.FromRgb(0xD9, 0x53, 0x4F)
    };
}
