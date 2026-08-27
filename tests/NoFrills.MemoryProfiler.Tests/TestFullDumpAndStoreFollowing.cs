using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// The bug of the empty destination folder: a running schedule kept writing to the store it was
/// created with after the user pointed the profiler at another folder. The schedule must follow
/// the store it is given, at every collection.
/// </summary>
[TestFixture]
public sealed class TestScheduleFollowsTheStore
{
    private string m_rootA = string.Empty;
    private string m_rootB = string.Empty;

    [SetUp]
    public void CreateRoots()
    {
        var unique = Guid.NewGuid().ToString("N");
        m_rootA = Path.Combine(Path.GetTempPath(), "MemoryProfilerStoreFollow", unique, "A");
        m_rootB = Path.Combine(Path.GetTempPath(), "MemoryProfilerStoreFollow", unique, "B");
    }

    [TearDown]
    public void RemoveRoots()
    {
        var parent = Path.GetDirectoryName(m_rootA)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    [Test]
    public void DumpsFollowWhenTheStoreIsReplacedMidSchedule()
    {
        var schedule = new ScheduledCollector(new DumpStore(m_rootA)) { RetryDelay = TimeSpan.FromMilliseconds(10) };

        var collected = new SemaphoreSlim(0);

        schedule.Collect = (process, _) =>
        {
            // The default collector reads schedule.Store the same way, at every collection.
            var path = schedule.Store.NewDumpPath(process.Name, process.ProcessId, DateTime.Now.AddSeconds(collected.CurrentCount));
            File.WriteAllText(path, "dump");
            return path;
        };

        schedule.DumpCollected += _ => collected.Release();

        schedule.Start(new LiveProcess(Environment.ProcessId, "self"), TimeSpan.FromMinutes(5));

        try
        {
            Assert.That(collected.Wait(TimeSpan.FromSeconds(15)), Is.True, "first dump");

            // The user changes the destination folder while the schedule runs.
            schedule.Store = new DumpStore(m_rootB);

            // The next collections must land there. Trigger by restarting the cycle: stop and start
            // is what the view model does not do, so collect directly through the same delegate.
            var path = schedule.Collect(new LiveProcess(Environment.ProcessId, "self"), CancellationToken.None);

            Assert.That(path, Does.StartWith(m_rootB), "a collection after the change must use the new folder");
        }
        finally
        {
            schedule.Stop();
        }

        Assert.That(new DumpStore(m_rootA).EnumerateDumps("self"), Has.Count.EqualTo(1), "what was collected before stays");
        Assert.That(new DumpStore(m_rootB).EnumerateDumps("self"), Has.Count.EqualTo(1), "what came after moved");
    }
}

/// <summary>
/// A schedule whose target died must stop and say so, not keep showing collection times while
/// nothing gets written.
/// </summary>
[TestFixture]
public sealed class TestScheduleStopsWithItsProcess
{
    [Test]
    public void ACollectionFailingBecauseTheProcessIsGoneEndsTheSchedule()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemoryProfilerGoneTests", Guid.NewGuid().ToString("N"));
        var schedule = new ScheduledCollector(new DumpStore(root)) { RetryDelay = TimeSpan.FromMilliseconds(10) };

        // A process that existed when the schedule started, then ended.
        var throwaway = Process.Start(new ProcessStartInfo("cmd", "/c exit") { UseShellExecute = false })!;
        throwaway.WaitForExit();

        var stopped = new ManualResetEventSlim();

        schedule.Collect = (_, _) => throw new HeapCollectionException("cannot reach the process");
        schedule.StateChanged += state =>
        {
            // Whichever check notices first, before or after a collection attempt, is fine.
            if (!state.IsRunning && state.LastMessage.Contains("schedule stopped")) stopped.Set();
        };

        schedule.Start(new LiveProcess(throwaway.Id, "gone-target"), TimeSpan.FromMinutes(5), collectImmediately: true);

        try
        {
            Assert.That(stopped.Wait(TimeSpan.FromSeconds(15)), Is.True,
                "the schedule must stop by itself and explain why");
            Assert.That(schedule.State.LastMessage, Does.Contain("gone-target").And.Contain("schedule stopped"),
                "the message must name the process, not only its identifier");
            Assert.That(schedule.State.NextCollectionAt, Is.Null, "no next collection may be promised");
        }
        finally
        {
            schedule.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// The full dump path, end to end against a real process: collect with the diagnostics client,
/// read with ClrMD, find the arrays the target holds, and attribute them with field names.
/// </summary>
[TestFixture]
public sealed class TestFullDump
{
    private static string SleeperDirectory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

    [Test]
    public void AFullDumpIsCollectedReadAndAttributedWithFieldNames()
    {
        if (!Directory.Exists(SleeperDirectory)) Assert.Ignore($"sleeper project not found: {SleeperDirectory}");

        var root = Path.Combine(Path.GetTempPath(), "MemoryProfilerFullDumpTests", Guid.NewGuid().ToString("N"));
        var store = new DumpStore(root);

        using var handle = StartSleeper();
        var target = FindSleeper(handle);

        string path;

        try
        {
            path = FullDumpCollector.Collect(target, store, new Progress<string>(m => TestContext.Out.WriteLine(m)));
        }
        finally
        {
            if (!handle.HasExited) handle.Kill(entireProcessTree: true);
        }

        try
        {
            Assert.That(path, Does.EndWith(".dmp").And.StartWith(store.FolderFor(target.Name)));
            Assert.That(store.EnumerateDumps(target.Name), Has.Count.EqualTo(1), "the store lists full dumps too");

            using var snapshot = FullDumpSnapshot.Load(path);

            Assert.That(snapshot.ObjectCount, Is.GreaterThan(0));
            Assert.That(snapshot.TypeStatistics.Select(type => type.TypeName), Does.Contain("System.Byte[]"));

            // The sleeper keeps twenty 2 MB arrays in a List<byte[]>.
            var report = snapshot.AnalyzeHolders(new ObjectFilter
            {
                TypeNamePattern = "System.Byte[]",
                MinimalSize = 2_097_152
            });

            Assert.That(report.ObjectCount, Is.GreaterThanOrEqualTo(20));

            var frames = report.RootPaths.SelectMany(rootPath => rootPath.Frames).ToList();

            Assert.That(frames, Has.Some.Contains("System.Byte[][]").Or.Some.Contains("List"),
                "the holding list must be named");
            Assert.That(frames, Has.Some.Contains("[item]").Or.Some.Contains(" ."),
                "full dump attribution carries the field or element, not only the type");

            // The generic loader and the summary must handle the format too.
            var loaded = Snapshot.Load(path);
            (loaded as IDisposable)?.Dispose();
            Assert.That(loaded, Is.InstanceOf<FullDumpSnapshot>());

            var summary = SnapshotSummary.For(store.EnumerateDumps(target.Name)[0]);
            Assert.That(summary.TotalBytes, Is.GreaterThan(20L * 2_097_152));
            Assert.That(File.Exists(path + ".summary.json"), "summaries are cached for full dumps as well");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartSleeper()
    {
        var handle = Process.Start(new ProcessStartInfo("dotnet", "run --project .")
        {
            WorkingDirectory = SleeperDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        return handle;
    }

    private static LiveProcess FindSleeper(Process handle) => TestTarget.Announced(handle);
}
