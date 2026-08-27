using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoFrills.MemoryProfiler.App.Controls;
using NoFrills.MemoryProfiler.Core;

namespace NoFrills.MemoryProfiler.App.ViewModels;

/// <summary>One type in the evolution view; ticking it adds it to the plot.</summary>
public sealed partial class TrendRow : ObservableObject
{
    private readonly Action m_onPlottedChanged;

    public TrendRow(TypeTrend trend, Color color, Action onPlottedChanged)
    {
        Trend = trend;
        Color = color;
        m_onPlottedChanged = onPlottedChanged;
    }

    public TypeTrend Trend { get; }
    public Color Color { get; }

    [ObservableProperty] private bool m_isPlotted;

    partial void OnIsPlottedChanged(bool value) => m_onPlottedChanged();

    public string TypeName => Trend.TypeName;
    public string FirstDisplay => ReportWriter.Bytes(Trend.FirstBytes);
    public string LastDisplay => ReportWriter.Bytes(Trend.LastBytes);
    public string GrowthDisplay => (Trend.GrowthBytes >= 0 ? "+" : "-") + ReportWriter.Bytes(Math.Abs(Trend.GrowthBytes));
    public string RateDisplay => $"{(Trend.BytesPerHour >= 0 ? "+" : "-")}{ReportWriter.Bytes((long)Math.Abs(Trend.BytesPerHour))}/h";
    public string ShapeDisplay => Trend.GrowsSteadily ? "grows at every step" : "varies";
    public string ColorKey => Trend.GrowsSteadily ? "accumulating" : "";
}

/// <summary>One suspected leak as the list shows it.</summary>
public sealed class LeakRow(LeakFinding finding)
{
    public LeakFinding Finding { get; } = finding;

    public string TypeName { get; } = finding.TypeName;
    public string HeldBy { get; } = finding.HeldBy;
    public string Verdict { get; } = finding.ConfidenceDisplay;

    /// <summary>How strong the suspicion is, so the list can show it without a second glance.</summary>
    public bool IsLikely { get; } = finding.Confidence >= 0.7;
    public bool IsPossible { get; } = finding.Confidence is >= 0.45 and < 0.7;
    public string ConfidenceDisplay { get; } = $"{finding.Confidence:P0}";
    public string SizeDisplay { get; } = ReportWriter.Bytes(finding.Bytes);
    public string CountDisplay { get; } = $"{finding.ObjectCount:N0}";

    public string RateDisplay { get; } = finding.BytesPerHour > 0
        ? $"+{ReportWriter.Bytes((long)finding.BytesPerHour)}/h"
        : string.Empty;

    public string Explanation { get; } =
        string.Join(Environment.NewLine, finding.Reasons.Select(reason => "- " + reason))
        + Environment.NewLine + Environment.NewLine + finding.Recommendation
        + Environment.NewLine + Environment.NewLine + "Path to a root:" + Environment.NewLine
        + string.Join(Environment.NewLine, finding.HolderPath);
}

/// <summary>One line of a comparison, with sizes a reader can take in at a glance.</summary>
public sealed class DeltaRow(TypeDelta delta)
{
    public string TypeName { get; } = delta.TypeName;
    public int DeltaCount { get; } = delta.DeltaCount;
    public long DeltaBytes { get; } = delta.DeltaBytes;

    public string DeltaBytesDisplay { get; } =
        (delta.DeltaBytes >= 0 ? "+" : "-") + ReportWriter.Bytes(Math.Abs(delta.DeltaBytes));

    public string BeforeDisplay { get; } = ReportWriter.Bytes(delta.BaselineBytes);
    public string AfterDisplay { get; } = ReportWriter.Bytes(delta.CurrentBytes);
}

/// <summary>
/// The evolution tab: reads a series of dumps of one process and shows what grows across them.
/// </summary>
public sealed partial class EvolutionViewModel : ObservableObject
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4E, 0x9F, 0xE8), Color.FromRgb(0xE8, 0x7D, 0x3E), Color.FromRgb(0x5C, 0xB8, 0x5C),
        Color.FromRgb(0xD9, 0x53, 0x4F), Color.FromRgb(0x9B, 0x59, 0xB6), Color.FromRgb(0xE0, 0xB4, 0x2E),
        Color.FromRgb(0x1A, 0xBC, 0x9C), Color.FromRgb(0xE9, 0x6B, 0xA8), Color.FromRgb(0x7F, 0x8C, 0x8D),
        Color.FromRgb(0x34, 0x98, 0xDB)
    };

    private List<SnapshotSummary> m_series = new();

    [ObservableProperty] private string m_status = "Pick a process, then load its dumps.";
    [ObservableProperty] private DumpSession? m_selectedSession;
    [ObservableProperty] private bool m_isBusy;
    [ObservableProperty] private string m_seriesDescription = string.Empty;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? m_chartSeries;
    [ObservableProperty] private IReadOnlyList<DateTime>? m_chartTimes;
    [ObservableProperty] private bool m_showObjectCounts;

    public ObservableCollection<DumpSession> Sessions { get; } = new();
    public ObservableCollection<TrendRow> Trends { get; } = new();
    public ObservableCollection<DeltaRow> StepDeltas { get; } = new();

    public event Action<string>? OpenDumpRequested;

    /// <summary>The series last loaded here, so the leak search can weigh growth as well.</summary>
    public IReadOnlyList<SnapshotSummary> LoadedSeries { get; private set; } = Array.Empty<SnapshotSummary>();

    public void RefreshSessions(DumpStore store)
    {
        var previous = SelectedSession?.ProcessName;

        Sessions.Clear();

        foreach (var session in store.EnumerateSessions()) Sessions.Add(session);

        SelectedSession = Sessions.FirstOrDefault(session => session.ProcessName == previous) ?? Sessions.FirstOrDefault();

        Status = Sessions.Count == 0
            ? "No dumps yet. Collect one from the Live processes tab."
            : $"{Sessions.Count} process folders in the store.";
    }

    [RelayCommand]
    private async Task LoadSeriesAsync()
    {
        var session = SelectedSession;

        if (session == null || session.Dumps.Count == 0)
        {
            Status = "Select a process that has dumps.";
            return;
        }

        if (session.Dumps.Count < 2)
        {
            Status = "At least two dumps are needed to show an evolution.";
            return;
        }

        IsBusy = true;

        try
        {
            var progress = new Progress<string>(message => Status = message);
            var dumps = session.Dumps;

            var series = await Task.Run(() =>
            {
                var summaries = new List<SnapshotSummary>();

                for (var i = 0; i < dumps.Count; ++i)
                {
                    ((IProgress<string>)progress).Report($"Reading dump {i + 1} of {dumps.Count}: {dumps[i].FileName}");
                    summaries.Add(SnapshotSummary.For(dumps[i], progress));
                }

                return summaries;
            });

            m_series = series;
            LoadedSeries = series;
            ShowTrends();

            var span = series[^1].TakenAt - series[0].TakenAt;

            SeriesDescription =
                $"{session.ProcessName}: {series.Count} dumps over {Format(span)}, " +
                $"heap {ReportWriter.Bytes(series[0].TotalBytes)} to {ReportWriter.Bytes(series[^1].TotalBytes)}";

            Status = SeriesDescription;
        }
        catch (Exception exception)
        {
            Status = $"Failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:F1} h" : $"{span.TotalMinutes:F0} min";

    private void ShowTrends()
    {
        Trends.Clear();
        StepDeltas.Clear();

        if (m_series.Count < 2) return;

        ChartTimes = m_series.Select(summary => summary.TakenAt).ToList();

        var trends = SeriesAnalysis.Trends(m_series);
        var total = SeriesAnalysis.TotalTrend(m_series);

        // The heap total is always plotted: it is the reference the type curves are read against.
        // The palette's secondary text colour: the heap total is the reference the type curves are
        // read against, so it stays neutral while the accent is spent on the types themselves.
        var totalRow = new TrendRow(total, Color.FromRgb(0xA8, 0x9A, 0x92), UpdateChart) { IsPlotted = true };
        Trends.Add(totalRow);

        var index = 0;

        foreach (var trend in trends.Take(60))
        {
            var row = new TrendRow(trend, Palette[index % Palette.Length], UpdateChart)
            {
                // The types that grow at every step are the interesting ones: plot the first few.
                IsPlotted = index < 4 && trend.GrowsSteadily
            };

            Trends.Add(row);
            ++index;
        }

        // What changed between the last two dumps, which is the most recent evidence.
        foreach (var delta in SeriesAnalysis.Compare(m_series[^2], m_series[^1]).Take(200))
        {
            StepDeltas.Add(new DeltaRow(delta));
        }

        UpdateChart();
    }

    partial void OnShowObjectCountsChanged(bool value)
    {
        OnPropertyChanged(nameof(ValueUnit));
        UpdateChart();
    }

    private void UpdateChart()
    {
        var plotted = Trends.Where(row => row.IsPlotted).ToList();

        ChartSeries = plotted
            .Select(row => new ChartSeries(
                row.TypeName,
                ShowObjectCounts
                    ? row.Trend.Counts.Select(count => (double)count).ToList()
                    : row.Trend.Bytes.Select(bytes => bytes / (1024d * 1024)).ToList(),
                row.Color))
            .ToList();
    }

    [RelayCommand]
    private void OpenLastDump()
    {
        var last = SelectedSession?.Dumps.LastOrDefault();

        if (last == null)
        {
            Status = "No dump to open.";
            return;
        }

        OpenDumpRequested?.Invoke(last.Path);
    }

    public string ValueUnit => ShowObjectCounts ? "objects" : "MB";
}
