using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App;
using NoFrills.MemoryProfiler.App.ViewModels;
using NUnit.Framework;

[assembly: AvaloniaTestApplication(typeof(NoFrills.MemoryProfiler.UiTests.TestAppBuilder))]

namespace NoFrills.MemoryProfiler.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // The look is a setting, and the tests must not depend on what is in the settings file of
        // whichever machine they run on.
        NoFrills.MemoryProfiler.App.App.ForcedProfilerTheme = true;

        return AppBuilder.Configure<NoFrills.MemoryProfiler.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>
/// Builds the real window in a headless renderer. This catches what a compile cannot: markup that
/// does not parse, a missing theme, and bindings that do not resolve against the view model.
/// </summary>
public sealed class TestMainWindow
{
    private static string? DumpPath => NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
        NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention);

    [AvaloniaTest]
    public void WindowOpensWithItsTabs()
    {
        var window = new MainWindow();
        window.Show();

        Assert.That(window.DataContext, Is.InstanceOf<MainViewModel>());

        var tabs = window.GetVisualDescendants().OfType<TabItem>().ToList();

        Assert.That(tabs.Select(tab => tab.Header?.ToString()), Is.EquivalentTo(
            new[] { "Explore", "Probable leaks", "Compare", "Live processes", "Evolution" }));
    }

    [AvaloniaTest]
    public void DataGridsAreThemedAndBound()
    {
        var window = new MainWindow();
        window.Show();

        var grids = window.GetVisualDescendants().OfType<DataGrid>().ToList();

        Assert.That(grids, Is.Not.Empty, "the DataGrid theme must be available, otherwise nothing renders");
        Assert.That(grids[0].Columns, Is.Not.Empty);
    }

    [AvaloniaTest]
    public async Task LoadingADumpFillsTheTypeList()
    {
        var dump = DumpPath;

        if (dump == null)
        {
            Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
                NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention));
        }

        var window = new MainWindow();
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        await model.LoadDumpAsync(dump!, asBaseline: false);

        Assert.That(model.HasSnapshot, Is.True);
        Assert.That(model.Types, Is.Not.Empty);
        Assert.That(model.SnapshotDescription, Does.Contain("objects"));

        model.TypeFilter = "RingBuffer";
        Assert.That(model.Types.Select(row => row.TypeName), Has.All.Contains("RingBuffer"));
    }

    [AvaloniaTest]
    public async Task AnalyzingPoolBlocksFillsTheHolderView()
    {
        var dump = DumpPath;

        if (dump == null)
        {
            Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
                NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention));
        }

        var window = new MainWindow();
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        await model.LoadDumpAsync(dump!, asBaseline: false);
        await model.AnalyzePoolBlocksCommand.ExecuteAsync(null);

        Assert.That(model.RootPaths, Is.Not.Empty);
        Assert.That(model.DirectReferrers, Is.Not.Empty);
        Assert.That(model.SelectedRootPath, Is.Not.Null);
        Assert.That(model.HolderSummary, Does.Contain("Held by the application"));

        var verdicts = model.RootPaths.Select(path => path.CategoryDisplay).ToList();
        Assert.That(verdicts, Has.Some.EqualTo("investigate"));
        Assert.That(verdicts, Has.Some.EqualTo("free in pool"));
    }
    /// <summary>
    /// Renders the window with the reference dump analyzed and writes a picture of it, which is
    /// the only way to check the layout without a screen.
    /// </summary>
    [AvaloniaTest]
    public async Task RenderedWindowIsWrittenAsAPicture()
    {
        var dump = DumpPath;

        if (dump == null)
        {
            Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
                NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention));
        }

        var window = new MainWindow { Width = 1500, Height = 950 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        await model.LoadDumpAsync(dump!, asBaseline: false);
        await model.AnalyzePoolBlocksCommand.ExecuteAsync(null);

        // Show the holder tab, which is the one worth looking at.
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 1;

        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null, "the window must render");

        var output = Path.Combine(Path.GetTempPath(), "memory-profiler-window.png");
        frame!.Save(output);

        TestContext.Out.WriteLine($"rendered window written to {output}");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(10_000), "a rendered window is not a blank image");
    }
}
