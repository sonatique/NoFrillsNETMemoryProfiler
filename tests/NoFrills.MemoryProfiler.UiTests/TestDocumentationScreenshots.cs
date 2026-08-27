using System.Diagnostics;
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
/// Renders the pictures used in the readme, so they show the application as it is now rather than
/// as it was when someone last remembered to take a screenshot.
///
/// They are taken of a dump of the test target rather than of a real application's heap. A picture
/// of a real heap publishes the name of every type in it, and this one anybody who clones the
/// repository can reproduce.
/// </summary>
public sealed class TestDocumentationScreenshots
{
    private static string DocFolder => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "doc"));

    private static string SleeperDirectory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

    private static string s_dumpPath = string.Empty;
    private static string s_storeRoot = string.Empty;

    [OneTimeSetUp]
    public void CollectFromTheTestTarget()
    {
        if (!Directory.Exists(SleeperDirectory)) Assert.Ignore($"test target not found: {SleeperDirectory}");

        s_storeRoot = Path.Combine(Path.GetTempPath(), "MemoryProfilerScreenshots", Guid.NewGuid().ToString("N"));

        using var handle = Process.Start(new ProcessStartInfo("dotnet", "run --project .")
        {
            WorkingDirectory = SleeperDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        var target = NoFrills.MemoryProfiler.Tests.TestTarget.Announced(handle);

        try
        {
            s_dumpPath = FullDumpCollector.Collect(target, new DumpStore(s_storeRoot));
        }
        finally
        {
            if (!handle.HasExited) handle.Kill(entireProcessTree: true);
        }
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (!Directory.Exists(s_storeRoot)) return;

        try
        {
            Directory.Delete(s_storeRoot, recursive: true);
        }
        catch (IOException)
        {
            // The window still has the dump open: it is only released when the snapshot is
            // collected, which is after this runs. A file left in the temporary folder is not
            // worth failing a green run over.
        }
    }

    [AvaloniaTest]
    public async Task TheExploreView()
    {
        var (window, model) = await Opened();

        model.TypeFilter = "Byte[]";
        model.SelectedType = model.Types.First(row => row.TypeName.Contains("Byte[]"));

        await model.AnalysisCompleted;
        Dispatcher.UIThread.RunJobs();

        Assert.That(model.RootPaths, Is.Not.Empty, "a picture is only worth taking if it shows an answer");

        Save(window, "explore-view.png");
    }

    [AvaloniaTest]
    public async Task TheProbableLeaksView()
    {
        var (window, model) = await Opened();

        await model.FindLeaksCommand.ExecuteAsync(null);

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = MainViewModel.LeakTabIndex;

        Dispatcher.UIThread.RunJobs();

        Assert.That(model.Leaks, Is.Not.Empty);
        Assert.That(
            model.Leaks.Select(row => row.HeldBy),
            Has.Some.Contains("LeakyQueue"),
            "the target holds a collection that keeps what it removes, which is what the picture is of");

        Save(window, "probable-leaks.png");
    }

    private static async Task<(Window Window, MainViewModel Model)> Opened()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow { Width = 1600, Height = 1000 };
        window.Show();

        var model = (MainViewModel)window.DataContext!;
        model.SelectionAnalysisDelay = TimeSpan.Zero;

        await model.LoadDumpAsync(s_dumpPath, asBaseline: false);

        return (window, model);
    }

    private static void Save(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);

        Directory.CreateDirectory(DocFolder);

        var output = Path.Combine(DocFolder, name);
        frame!.Save(output);

        TestContext.Out.WriteLine($"written {output}");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(10_000));
    }
}
