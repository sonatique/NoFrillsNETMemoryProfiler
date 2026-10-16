using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Reads a recording of the sleeper started with "allocate": one thread allocating byte arrays and
/// Garbage from Allocator.Churn, and the main thread inducing a blocking gen1 collection every
/// 100 ms, every fifth one a background gen2 instead. What it should be found to contain is known
/// from the program, so the pairing and the grouping can be checked against it.
/// </summary>
[TestFixture]
public sealed class TestGcTimeline
{
    private static string FixturePath => Path.Combine(
        TestContext.CurrentContext.TestDirectory, "Fixtures", "allocating-sleeper.nettrace");

    private static GcTimeline? s_timeline;

    private static GcTimeline Timeline => s_timeline ??= GcTimeline.Load(FixturePath);

    [Test]
    public void EveryCollectionIsPairedWithItsEnd()
    {
        Assert.That(Timeline.Collections, Has.Count.GreaterThanOrEqualTo(15));
        Assert.That(Timeline.Collections, Has.All.Matches<GcRun>(gc => gc.EndMs > gc.StartMs));
        Assert.That(Timeline.Collections.Select(gc => gc.Number), Is.Unique);
    }

    [Test]
    public void EachPauseIsCreditedToTheCollectionItWasFor()
    {
        var blocking = Timeline.Collections.Where(gc => !gc.IsBackground).ToList();
        var background = Timeline.Collections.Where(gc => gc.IsBackground).ToList();

        Assert.That(blocking, Has.Count.GreaterThanOrEqualTo(10));
        Assert.That(background, Has.Count.GreaterThanOrEqualTo(3), "the sleeper makes every fifth collection a background one");
        Assert.That(blocking, Has.All.Matches<GcRun>(gc => gc is { Generation: 1, Reason: "Induced" }));
        Assert.That(background, Has.All.Matches<GcRun>(gc => gc.Generation == 2));

        Assert.That(Timeline.Pauses, Has.All.Matches<GcPause>(pause => pause.Gc != null), "every suspension here is for a collection");
        Assert.That(Timeline.Pauses, Has.All.Matches<GcPause>(pause => pause.DurationMs > 0 && pause.DurationMs < 1000));

        // A blocking collection stops the threads once, a background one at its start and its end.
        foreach (var gc in blocking) Assert.That(Timeline.Pauses.Count(pause => pause.Gc == gc), Is.EqualTo(1), $"GC {gc.Number}");
        foreach (var gc in background) Assert.That(Timeline.Pauses.Count(pause => pause.Gc == gc), Is.EqualTo(2), $"GC {gc.Number}");

        Assert.That(
            Timeline.Pauses.Where(pause => pause.Gc!.IsBackground),
            Has.All.Matches<GcPause>(pause => pause.StartMs <= pause.Gc!.EndMs && pause.StartMs + pause.DurationMs >= pause.Gc.StartMs),
            "a pause of a background collection falls within it");
    }

    [Test]
    public void PausesAreSummarizedPerGeneration()
    {
        var blocking = Timeline.Collections.Count(gc => !gc.IsBackground);
        var background = Timeline.Collections.Count(gc => gc.IsBackground);

        Assert.That(Timeline.PausesByGeneration.Select(statistic => statistic.Generation), Is.EqualTo(new[] { "gen1", "gen2 background" }));

        var gen1 = Timeline.PausesByGeneration[0];
        var gen2 = Timeline.PausesByGeneration[1];

        Assert.That(gen1.Count, Is.EqualTo(blocking));
        Assert.That(gen2.Count, Is.EqualTo(2 * background));
        Assert.That(gen1.TriggeredBy, Is.EqualTo($"Induced {blocking}"));
        Assert.That(gen2.TriggeredBy, Does.EndWith($" {background}"), "a background collection pauses twice but is triggered once");

        foreach (var statistic in Timeline.PausesByGeneration)
        {
            var durations = Timeline.Pauses.Where(pause => pause.Generation == statistic.Generation).Select(pause => pause.DurationMs).ToList();

            Assert.That(statistic.TotalMs, Is.EqualTo(durations.Sum()).Within(1e-9));
            Assert.That(statistic.MaxMs, Is.EqualTo(durations.Max()));
            Assert.That(statistic.P50Ms, Is.LessThanOrEqualTo(statistic.P95Ms));
            Assert.That(statistic.P95Ms, Is.LessThanOrEqualTo(statistic.MaxMs));
            Assert.That(durations, Does.Contain(statistic.P50Ms), "a percentile is a pause that was measured");
        }
    }

    [Test]
    public void AllocationsAreGroupedByTypeThreadAndStack()
    {
        var total = Timeline.AllocatedBytes;

        Assert.That(Timeline.Allocations, Has.Count.GreaterThanOrEqualTo(100));

        foreach (var grouping in new[] { Timeline.AllocationsByType, Timeline.AllocationsByThread, Timeline.AllocationsByStack })
        {
            Assert.That(grouping.Sum(group => group.Bytes), Is.EqualTo(total), "a grouping accounts for every sample once");
            Assert.That(grouping.Sum(group => group.Samples), Is.EqualTo(Timeline.Allocations.Count));
            Assert.That(grouping.Select(group => group.Bytes), Is.Ordered.Descending);
        }

        // The worker allocates two Garbage of 80 bytes for every array of 1000: a sixth of the bytes.
        Assert.That(Timeline.AllocationsByType.Select(group => group.Name).Take(2), Is.EqualTo(new[] { "System.Byte[]", "Garbage" }));

        var worker = Timeline.Allocations.Where(sample => sample.TypeName == "Garbage").Select(sample => sample.ThreadId).Distinct().Single();

        Assert.That(Timeline.AllocationsByThread[0].Name, Is.EqualTo($"OS thread {worker}"));
        Assert.That(Timeline.AllocationsByThread[0].Bytes, Is.GreaterThan(total * 9 / 10));

        var frames = Timeline.AllocationsByStack[0].Name.Split(Environment.NewLine);

        Assert.That(frames[0], Does.StartWith("Allocator.Churn("), "the first application frame is the one allocating");
        Assert.That(frames, Has.Length.LessThanOrEqualTo(GcTimeline.StackFrameCount));
        Assert.That(
            Timeline.AllocationsByStack.SelectMany(group => group.Name.Split(Environment.NewLine)),
            Has.None.StartsWith("System.").And.None.StartsWith("Microsoft."),
            "the frames of the framework are collapsed");
    }

    [Test]
    public void TheReportHasEveryTable()
    {
        var report = ReportWriter.Write(Timeline);

        Assert.That(report, Does.Contain("## Pauses by generation").And.Contain("| gen2 background |"));
        Assert.That(report, Does.Contain("## Allocations by type").And.Contain("`` System.Byte[] ``"));
        Assert.That(report, Does.Contain("## Allocations by thread"));
        Assert.That(report, Does.Contain("## Allocations by stack").And.Contain("`` Allocator.Churn("));
    }

    /// <summary>The recording path itself, against a real process: the only way to know it works.</summary>
    [Test]
    public void ARunningProcessIsRecordedIntoTheStore()
    {
        if (!Directory.Exists(TestLiveCollection.SleeperDirectory)) Assert.Ignore($"sleeper project not found: {TestLiveCollection.SleeperDirectory}");

        using var target = TestLiveCollection.StartSleeper("allocate");

        try
        {
            var store = new DumpStore(Path.Combine(Path.GetTempPath(), "MemoryProfilerTests"));

            var path = GcTimelineCollector.Record(
                new LiveProcess(target.Id, target.ProcessName),
                store,
                TimeSpan.FromSeconds(1.5),
                new Progress<string>(message => TestContext.Out.WriteLine(message)));

            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(store.FolderFor(target.ProcessName)), "a recording is filed beside the dumps");
            Assert.That(path, Does.EndWith(GcTimeline.Extension));
            Assert.That(store.EnumerateDumps(target.ProcessName).Select(dump => dump.Path), Has.None.EqualTo(path), "a recording is not a dump of the series");

            var timeline = GcTimeline.Load(path);

            Assert.That(timeline.Collections, Has.Some.Matches<GcRun>(gc => gc.Reason == "Induced"));
            Assert.That(timeline.Pauses, Is.Not.Empty);
            Assert.That(timeline.AllocationsByType.Select(group => group.Name), Does.Contain("Garbage"));
            Assert.That(timeline.AllocationsByStack.Select(group => group.Name), Has.Some.StartsWith("Allocator.Churn("));

            File.Delete(path);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
        }
    }
}
