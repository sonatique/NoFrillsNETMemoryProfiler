using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Collects a dump from a real process and reads it back. This is the only way to know the
/// collection path works: it talks to the runtime of another process over its diagnostic pipe.
/// </summary>
[TestFixture]
public sealed class TestLiveCollection
{
    internal static string SleeperDirectory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

    [Test]
    public void ProfilableProcessesAreListed()
    {
        var processes = LiveCollector.GetProfilableProcesses();

        Assert.That(processes, Is.Not.Empty, "the test host itself is a .NET process");
        Assert.That(
            processes.Select(process => process.ProcessId),
            Does.Contain(Environment.ProcessId));
    }

    [Test]
    public void DumpIsCollectedFromARunningProcessAndCanBeRead()
    {
        if (!Directory.Exists(SleeperDirectory)) Assert.Ignore($"sleeper project not found: {SleeperDirectory}");

        using var target = StartSleeper();

        try
        {
            var store = new DumpStore(Path.Combine(Path.GetTempPath(), "MemoryProfilerTests"));

            var path = LiveCollector.Collect(
                new LiveProcess(target.Id, target.ProcessName),
                store,
                TimeSpan.FromMinutes(2),
                new Progress<string>(message => TestContext.Out.WriteLine(message)));

            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(store.FolderFor(target.ProcessName)),
                "a collected dump belongs to the folder of its process");

            Assert.That(File.Exists(path), "the collector must write a dump file");

            var snapshot = HeapSnapshot.Load(path);

            Assert.That(snapshot.NodeCount, Is.GreaterThan(0));
            Assert.That(snapshot.TotalSize, Is.GreaterThan(20L * 2_097_152), "the target holds 40 MB of arrays");

            // The target keeps twenty 2 MB arrays alive, which the analysis must find and attribute.
            var blocks = snapshot.SelectObjects(new ObjectFilter
            {
                TypeNamePattern = "System.Byte[]",
                MinimalSize = 2_097_152
            });

            Assert.That(blocks, Has.Count.GreaterThanOrEqualTo(20));

            var report = HolderAnalysis.Run(snapshot, new ObjectFilter
            {
                TypeNamePattern = "System.Byte[]",
                MinimalSize = 2_097_152
            });

            Assert.That(report.RootPaths, Is.Not.Empty);
            Assert.That(
                report.RootPaths.SelectMany(rootPath => rootPath.Frames),
                Has.Some.Contains("List").Or.Some.Contains("Byte[][]"),
                "the arrays are held by the list of the target");

            File.Delete(path);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
        }
    }

    internal static Process StartSleeper(string arguments = "")
    {
        var process = Process.Start(new ProcessStartInfo("dotnet", $"run --project . -- {arguments}")
        {
            WorkingDirectory = SleeperDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        // "dotnet run" builds first, so wait for the target to say it is up rather than guessing.
        return Process.GetProcessById(TestTarget.Announced(process).ProcessId);
    }
}
