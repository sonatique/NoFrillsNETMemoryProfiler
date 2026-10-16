using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.ViewModels;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>Opens the recording the core tests read, and checks the tab shows what it found.</summary>
public sealed class TestGcTimelineTab
{
    private static string FixturePath => Path.Combine(
        TestContext.CurrentContext.TestDirectory, "Fixtures", "allocating-sleeper.nettrace");

    [AvaloniaTest]
    public async Task OpeningARecordingFillsTheTabAndShowsIt()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow { Width = 1500, Height = 950 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        await model.LoadTraceAsync(FixturePath);

        Assert.That(model.SelectedTabIndex, Is.EqualTo(MainViewModel.GcTimelineTabIndex), "a recording just read is shown");
        Assert.That(model.Timeline.Generations.Select(row => row.Generation), Is.EqualTo(new[] { "gen1", "gen2 background" }));
        Assert.That(model.Timeline.Bars, Has.Count.EqualTo(model.Timeline.Pauses.Count));
        Assert.That(model.Timeline.ByType.First().Name, Is.EqualTo("System.Byte[]"));
        Assert.That(model.Timeline.ByThread, Is.Not.Empty);
        Assert.That(model.Timeline.ByStack.First().Name, Does.StartWith("Allocator.Churn("));
        Assert.That(model.Timeline.Description, Does.Contain("collections"));

        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);

        var output = Path.Combine(Path.GetTempPath(), "memory-profiler-gc-timeline.png");
        frame!.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

        TestContext.Out.WriteLine($"rendered GC timeline written to {output}");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(10_000));
    }

    [AvaloniaTest]
    public void TheGcTimelineTabIndexIsTheGcTimelineTab()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow();
        window.Show();

        var items = window.GetVisualDescendants().OfType<TabControl>().First()
            .GetVisualDescendants().OfType<TabItem>().ToList();

        Assert.That(items[MainViewModel.GcTimelineTabIndex].Header?.ToString(), Is.EqualTo("GC timeline"));
    }
}
