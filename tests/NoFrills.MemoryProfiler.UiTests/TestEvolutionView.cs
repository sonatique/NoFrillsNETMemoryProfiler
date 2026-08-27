using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.Controls;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// Exercises the evolution view on a series built for the test.
///
/// Reducing a dump is cached in a file next to it, so a series can be prepared by writing those
/// cache files: no gigabyte of dumps needed to check the view, the chart and the ranking.
/// </summary>
public sealed class TestEvolutionView
{
    private string m_root = string.Empty;

    [SetUp]
    public void CreateSeries()
    {
        m_root = Path.Combine(Path.GetTempPath(), "MemoryProfilerUiTests", Guid.NewGuid().ToString("N"));

        var store = new DumpStore(m_root);
        var start = new DateTime(2026, 8, 21, 9, 0, 0);

        for (var step = 0; step < 8; ++step)
        {
            var takenAt = start.AddMinutes(15 * step);
            var path = store.NewDumpPath("Sample.Application", 4242, takenAt);

            File.WriteAllText(path, "stand in for a dump; the summary next to it is what is read");

            var types = new List<TypeStatistic>
            {
                // Grows at every step: this is what the view must surface.
                new("Sample.Pipeline.Reception[]", 100 + 40 * step, 20_000_000 + 8_000_000L * step),
                // Grows too, more slowly.
                new("System.Byte[]", 200 + 5 * step, 400_000_000 + 4_000_000L * step),
                // Goes up and down: used and released.
                new("Sample.Pipeline.Packet[]", 50, 30_000_000 + (step % 2 == 0 ? 12_000_000 : 0)),
                // Never moves.
                new("System.String", 9000, 12_000_000),
            };

            var summary = new SnapshotSummary(
                path,
                takenAt,
                types.Sum(type => type.Count),
                types.Sum(type => type.TotalBytes),
                types);

            File.WriteAllText(path + ".summary.json", JsonSerializer.Serialize(summary));
        }
    }

    [TearDown]
    public void RemoveSeries()
    {
        if (Directory.Exists(m_root)) Directory.Delete(m_root, recursive: true);
    }

    private EvolutionViewModel LoadedModel()
    {
        var model = new EvolutionViewModel();

        model.RefreshSessions(new DumpStore(m_root));

        return model;
    }

    [AvaloniaTest]
    public async Task TheSeriesOfAProcessIsListedAndLoaded()
    {
        var model = LoadedModel();

        Assert.That(model.Sessions.Select(session => session.ProcessName), Is.EqualTo(new[] { "Sample.Application" }));
        Assert.That(model.SelectedSession!.Dumps, Has.Count.EqualTo(8));

        await model.LoadSeriesCommand.ExecuteAsync(null);

        Assert.That(model.SeriesDescription, Does.Contain("8 dumps").And.Contain("1.8 h"));
        Assert.That(model.Trends, Is.Not.Empty);
    }

    [AvaloniaTest]
    public async Task WhatGrowsSteadilyIsRankedAndPlottedByDefault()
    {
        var model = LoadedModel();
        await model.LoadSeriesCommand.ExecuteAsync(null);

        Assert.That(model.Trends[0].TypeName, Is.EqualTo("(whole heap)"), "the heap total is the reference curve");
        Assert.That(model.Trends[0].IsPlotted, Is.True);

        var reception = model.Trends.Single(row => row.TypeName.Contains("Reception"));
        var packets = model.Trends.SingleOrDefault(row => row.TypeName.Contains("Packet"));

        Assert.That(reception.ShapeDisplay, Is.EqualTo("grows at every step"));
        Assert.That(reception.IsPlotted, Is.True, "a type that only grows must be plotted without asking");
        Assert.That(reception.GrowthDisplay, Does.StartWith("+"));
        Assert.That(packets?.ShapeDisplay, Is.Not.EqualTo("grows at every step"));

        Assert.That(model.Trends.Select(row => row.TypeName), Has.None.EqualTo("System.String"),
            "a type that never moves is noise in this view");
    }

    [AvaloniaTest]
    public async Task TickingATypeAddsItsCurveToTheChart()
    {
        var model = LoadedModel();
        await model.LoadSeriesCommand.ExecuteAsync(null);

        var plottedBefore = model.ChartSeries!.Count;
        var notPlotted = model.Trends.First(row => !row.IsPlotted);

        notPlotted.IsPlotted = true;

        Assert.That(model.ChartSeries, Has.Count.EqualTo(plottedBefore + 1));
        Assert.That(model.ChartSeries!.Select(series => series.Name), Does.Contain(notPlotted.TypeName));
        Assert.That(model.ChartTimes, Has.Count.EqualTo(8), "the curves are plotted against when each dump was taken");
    }

    [AvaloniaTest]
    public async Task SwitchingToObjectCountsChangesTheCurvesAndTheUnit()
    {
        var model = LoadedModel();
        await model.LoadSeriesCommand.ExecuteAsync(null);

        var megabytes = model.ChartSeries!.First(series => series.Name == "(whole heap)").Values[0];

        model.ShowObjectCounts = true;

        var counts = model.ChartSeries!.First(series => series.Name == "(whole heap)").Values[0];

        Assert.That(model.ValueUnit, Is.EqualTo("objects"));
        Assert.That(counts, Is.Not.EqualTo(megabytes));
        Assert.That(counts, Is.EqualTo(9350).Within(1), "the first dump holds 9350 objects");
    }

    [AvaloniaTest]
    public async Task TheChartRendersTheLoadedSeries()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow { Width = 1500, Height = 950 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        model.Evolution.RefreshSessions(new DumpStore(m_root));
        await model.Evolution.LoadSeriesCommand.ExecuteAsync(null);

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;

        Dispatcher.UIThread.RunJobs();

        var chart = window.GetVisualDescendants().OfType<TimeSeriesChart>().SingleOrDefault();
        Assert.That(chart, Is.Not.Null, "the evolution tab must contain the chart");
        Assert.That(chart!.Series, Is.Not.Empty);

        var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);

        var output = Path.Combine(Path.GetTempPath(), "memory-profiler-evolution.png");
        frame!.Save(output);

        TestContext.Out.WriteLine($"rendered evolution view written to {output}");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(10_000), "a rendered chart is not a blank image");
    }
}
