using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.ViewModels;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>Renders the merged view, the only way to check a layout without a screen.</summary>
public sealed class TestExploreRender
{
    [AvaloniaTest]
    public async Task TheExploreViewShowsTheListAndTheHoldersTogether()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore("reference dump not available");

        var expected = NoFrills.MemoryProfiler.Tests.ReferenceExpectations.Find();

        if (expected == null) Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceExpectations.WhereToPutIt());

        var window = new NoFrills.MemoryProfiler.App.MainWindow { Width = 1500, Height = 950 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        model.SelectionAnalysisDelay = TimeSpan.Zero;

        await model.LoadDumpAsync(path!, asBaseline: false);

        // A plentiful type of that heap, named beside the dump: which one it is says nothing about
        // the layout being rendered, and everything about whose application the dump came from.
        model.TypeFilter = expected!.TypeToInspect;
        model.SelectedType = model.Types.First(row => row.TypeName.Contains(expected.TypeToInspect));
        await model.AnalysisCompleted;

        Dispatcher.UIThread.RunJobs();

        var grids = window.GetVisualDescendants().OfType<DataGrid>().ToList();

        Assert.That(grids.Count, Is.GreaterThanOrEqualTo(3),
            "the type list, the paths and the referrers must all be on screen at once");

        var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);

        var output = Path.Combine(Path.GetTempPath(), "memory-profiler-explore.png");
        frame!.Save(output);

        TestContext.Out.WriteLine($"rendered explore view written to {output}");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(10_000));
    }
}
