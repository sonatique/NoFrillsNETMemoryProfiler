using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Runs the schedule against a real process, so the whole chain is exercised: collect on a timer,
/// write into the store, reduce to summaries, and read the result as a series.
/// </summary>
[TestFixture]
public sealed class TestScheduledCollection
{
    private string m_root = string.Empty;

    [SetUp]
    public void CreateRoot()
    {
        m_root = Path.Combine(Path.GetTempPath(), "MemoryProfilerScheduleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_root);
    }

    [TearDown]
    public void RemoveRoot()
    {
        if (Directory.Exists(m_root)) Directory.Delete(m_root, recursive: true);
    }

    [Test]
    public void AnIntervalShorterThanAMinuteIsRefused()
    {
        var schedule = new ScheduledCollector(new DumpStore(m_root));

        Assert.That(
            () => schedule.Start(new LiveProcess(Environment.ProcessId, "self"), TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<ArgumentOutOfRangeException>(),
            "collecting pauses the target, so a tight interval must not be allowed");
    }

    [Test]
    public void TheScheduleStopsWhenTheTargetProcessEnds()
    {
        var schedule = new ScheduledCollector(new DumpStore(m_root));
        var stopped = new ManualResetEventSlim();

        schedule.StateChanged += state =>
        {
            if (!state.IsRunning && state.LastMessage.Contains("ended")) stopped.Set();
        };

        // A process id that is not running stands for a target that ended.
        schedule.Start(new LiveProcess(GetUnusedProcessId(), "gone"), TimeSpan.FromMinutes(1), collectImmediately: true);

        Assert.That(stopped.Wait(TimeSpan.FromSeconds(20)), Is.True, "the schedule must notice and stop by itself");
        Assert.That(schedule.State.IsRunning, Is.False);
    }

    [Test]
    public void ScheduledDumpsFormAReadableSeries()
    {
        var sleeperDirectory = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

        if (!Directory.Exists(sleeperDirectory)) Assert.Ignore($"sleeper project not found: {sleeperDirectory}");

        var target = StartSleeper(sleeperDirectory);
        using var targetHandle = target.Handle;

        var store = new DumpStore(m_root);
        var schedule = new ScheduledCollector(store);
        var collected = new List<string>();
        var twoCollected = new ManualResetEventSlim();

        schedule.DumpCollected += path =>
        {
            lock (collected)
            {
                collected.Add(path);
                if (collected.Count >= 2) twoCollected.Set();
            }
        };

        try
        {
            // One minute is the shortest interval allowed, so two dumps take a little over a minute.
            schedule.Start(target.Process, TimeSpan.FromMinutes(1), keepAtMost: 0);

            Assert.That(twoCollected.Wait(TimeSpan.FromMinutes(3)), Is.True,
                $"two scheduled dumps expected, got {collected.Count}: {schedule.State.LastMessage}");
        }
        finally
        {
            schedule.Stop();
            if (!target.Handle.HasExited) target.Handle.Kill(entireProcessTree: true);
        }

        var session = store.EnumerateSessions().Single(one => one.ProcessName == target.Process.Name);

        Assert.That(session.Dumps, Has.Count.GreaterThanOrEqualTo(2), "the dumps must land under the process name");
        Assert.That(session.Dumps[0].TakenAt, Is.LessThan(session.Dumps[1].TakenAt));

        // The series is what the evolution view reads.
        var series = session.Dumps.Select(dump => SnapshotSummary.For(dump)).ToList();

        Assert.That(series, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(series[0].TotalBytes, Is.GreaterThan(20L * 2_097_152), "the target holds 40 MB of arrays");
        Assert.That(series.Select(summary => summary.TakenAt), Is.Ordered);

        var total = SeriesAnalysis.TotalTrend(series);
        Assert.That(total.Bytes, Has.Count.EqualTo(series.Count));

        // Reading a summary again must come from the cache rather than the dump.
        Assert.That(File.Exists(session.Dumps[0].Path + ".summary.json"), "summaries must be cached next to the dump");

        var reread = SnapshotSummary.For(session.Dumps[0]);
        Assert.That(reread.TotalBytes, Is.EqualTo(series[0].TotalBytes));
    }

    private static int GetUnusedProcessId()
    {
        // Start a process and let it end: its id is then free and not running.
        var throwaway = Process.Start(new ProcessStartInfo("cmd", "/c exit") { UseShellExecute = false })!;
        throwaway.WaitForExit();

        return throwaway.Id;
    }

    private static (LiveProcess Process, Process Handle) StartSleeper(string directory)
    {
        var handle = Process.Start(new ProcessStartInfo("dotnet", "run --project .")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        return (TestTarget.Announced(handle), handle);
    }
}
