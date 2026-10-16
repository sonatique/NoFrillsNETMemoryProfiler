using System.Diagnostics;
using Avalonia.Headless.NUnit;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// "Collect and open" once collected the dump and left it unopened: the opening ran inside the busy
/// state of the collection and returned at once. Checked against a real process, collected into a
/// folder of the test's own rather than the one the settings name.
/// </summary>
public sealed class TestCollectAndOpen
{
    private static string SleeperDirectory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

    [AvaloniaTest]
    public async Task TheCollectedDumpIsOpened()
    {
        if (!Directory.Exists(SleeperDirectory)) Assert.Ignore($"test target not found: {SleeperDirectory}");

        var root = Path.Combine(Path.GetTempPath(), "MemoryProfilerCollectAndOpen", Guid.NewGuid().ToString("N"));

        using var handle = Process.Start(new ProcessStartInfo("dotnet", "run --project .")
        {
            WorkingDirectory = SleeperDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        var target = NoFrills.MemoryProfiler.Tests.TestTarget.Announced(handle);

        try
        {
            var model = new MainViewModel
            {
                ProcessSource = () => new[] { target },
                CollectDump = (process, _, _, progress, token) =>
                    LiveCollector.Collect(process, new DumpStore(root), TimeSpan.FromMinutes(2), progress, token)
            };

            model.SelectedProcess = target;

            await model.CollectCommand.ExecuteAsync(null);

            Assert.That(model.HasSnapshot, Is.True, $"the dump must be opened, not only collected. Status: {model.Status}");
            Assert.That(model.SnapshotDescription, Does.Contain($"_{target.ProcessId}.gcdump"));
            Assert.That(model.Types, Is.Not.Empty);
        }
        finally
        {
            if (!handle.HasExited) handle.Kill(entireProcessTree: true);

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A file left in the temporary folder is not worth failing a green run over.
            }
        }
    }
}
