using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.ViewModels;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The three tables of the explore view are read across, so they have to start on the same line.
/// They did not, because each side computed its own header height; the sizes are measured here so
/// that cannot come back unnoticed.
/// </summary>
public sealed class TestExploreLayout
{
    private static async Task<(NoFrills.MemoryProfiler.App.MainWindow Window, MainViewModel Model)> Shown()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore("reference dump not available");

        var window = new NoFrills.MemoryProfiler.App.MainWindow { Width = 1600, Height = 1000 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        model.SelectionAnalysisDelay = TimeSpan.Zero;

        await model.LoadDumpAsync(path!, asBaseline: false);
        await model.AnalyzePoolBlocksCommand.ExecuteAsync(null);

        Dispatcher.UIThread.RunJobs();

        return (window, model);
    }

    private static double TopOf(Visual visual, Visual window) =>
        visual.TranslatePoint(new Point(0, 0), window)!.Value.Y;

    [AvaloniaTest]
    public async Task TheThreeTablesStartOnTheSameLine()
    {
        var (window, _) = await Shown();

        var grids = window.GetVisualDescendants().OfType<DataGrid>()
            .Where(grid => grid.IsVisible)
            .ToList();

        Assert.That(grids, Has.Count.EqualTo(3), "the types, the paths and the referrers");

        var tops = grids.Select(grid => TopOf(grid, window)).ToList();

        TestContext.Out.WriteLine($"table tops: {string.Join(", ", tops.Select(top => top.ToString("F1")))}");

        Assert.That(tops.Max() - tops.Min(), Is.LessThan(1),
            "a row of a grid has one height across its columns, so the tables must line up exactly");
    }

    [AvaloniaTest]
    public async Task TheWidthsAndTheHeightCanBeChanged()
    {
        var (window, _) = await Shown();

        var splitters = window.GetVisualDescendants().OfType<GridSplitter>()
            .Where(splitter => splitter.IsVisible)
            .ToList();

        Assert.That(splitters, Has.Count.EqualTo(3),
            "between the list and the paths, between the paths and the referrers, and above the path shown");

        var columns = splitters.Count(splitter => splitter.ResizeDirection == GridResizeDirection.Columns);
        var rows = splitters.Count(splitter => splitter.ResizeDirection == GridResizeDirection.Rows);

        Assert.That(columns, Is.EqualTo(2));
        Assert.That(rows, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task TheVerdictIsShownWhole()
    {
        var (_, model) = await Shown();

        var verdicts = model.RootPaths.Select(row => row.CategoryDisplay).Distinct().ToList();

        Assert.That(verdicts, Does.Contain("awaiting finalizer"));
        Assert.That(
            verdicts.Max(verdict => verdict.Length) * 8, Is.LessThan(150),
            "the verdict column is 150 wide and has to hold the longest of them");
    }
}
