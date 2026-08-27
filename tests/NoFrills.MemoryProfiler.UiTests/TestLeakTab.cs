using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The leak list as it is used: open a dump, look for leaks, read why, and jump to the holders of
/// the suspect.
/// </summary>
public sealed class TestLeakTab
{
    /// <summary>What the reference dumps hold, kept beside them rather than written down here.</summary>
    private static NoFrills.MemoryProfiler.Tests.ReferenceExpectations Expected =>
        NoFrills.MemoryProfiler.Tests.ReferenceExpectations.Find()
        ?? throw new IgnoreException(NoFrills.MemoryProfiler.Tests.ReferenceExpectations.WhereToPutIt());

    private static async Task<MainViewModel> Loaded()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention);

        if (path == null)
        {
            Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
                NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention));
        }

        var model = new MainViewModel { ProcessSource = () => Array.Empty<LiveProcess>() };
        await model.LoadDumpAsync(path!, asBaseline: false);

        return model;
    }

    [AvaloniaTest]
    public async Task TheKnownLeakIsListedFirstWithItsReasons()
    {
        var model = await Loaded();

        await model.FindLeaksCommand.ExecuteAsync(null);

        Assert.That(model.Leaks, Is.Not.Empty);

        var first = model.Leaks[0];

        Assert.That(first.HeldBy, Does.Contain(Expected.Holder),
            "the leak this profiler was written to find must be at the top");
        Assert.That(first.Explanation, Does.Contain("held by the application"));
        Assert.That(first.Explanation, Does.Contain("Path to a root:"));
        Assert.That(first.SizeDisplay, Does.Contain("MB"));
        Assert.That(model.LeakSummary, Does.Contain("suspect"));
    }

    [AvaloniaTest]
    public async Task ASuspectCanBeOpenedInTheHolderView()
    {
        var model = await Loaded();

        await model.FindLeaksCommand.ExecuteAsync(null);

        model.SelectedLeak = model.Leaks[0];
        model.InspectLeakCommand.Execute(null);

        await model.AnalysisCompleted;

        Assert.That(model.SelectedTabIndex, Is.EqualTo(MainViewModel.ExploreTabIndex));
        Assert.That(model.SelectionDescription, Does.Contain(model.Leaks[0].TypeName));
        Assert.That(model.RootPaths, Is.Not.Empty, "the holder view must show that type, analyzed");
    }

    [AvaloniaTest]
    public async Task OpeningAnotherDumpDropsTheSuspectsOfThePreviousOne()
    {
        var model = await Loaded();

        await model.FindLeaksCommand.ExecuteAsync(null);
        Assert.That(model.Leaks, Is.Not.Empty);

        var other = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries);

        if (other == null) Assert.Ignore("a second dump is needed");

        await model.LoadDumpAsync(other!, asBaseline: false);

        Assert.That(model.Leaks, Is.Empty, "suspects of one heap say nothing about another");
        Assert.That(model.LeakSummary, Does.Contain("Find leaks"));
    }

    [AvaloniaTest]
    public async Task TheTabRendersItsFindings()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore("reference dump not available");

        var window = new NoFrills.MemoryProfiler.App.MainWindow { Width = 1500, Height = 950 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        await model.LoadDumpAsync(path!, asBaseline: false);
        await model.FindLeaksCommand.ExecuteAsync(null);

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = MainViewModel.LeakTabIndex;

        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);

        var output = Path.Combine(Path.GetTempPath(), "memory-profiler-leaks.png");
        frame!.Save(output);

        TestContext.Out.WriteLine($"rendered leak view written to {output}");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(10_000));
    }

    [AvaloniaTest]
    public void TheLeakTabIndexIsTheLeakTab()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow();
        window.Show();

        var items = window.GetVisualDescendants().OfType<TabControl>().First()
            .GetVisualDescendants().OfType<TabItem>().ToList();

        Assert.That(items[MainViewModel.LeakTabIndex].Header?.ToString(), Is.EqualTo("Probable leaks"));
    }
}
