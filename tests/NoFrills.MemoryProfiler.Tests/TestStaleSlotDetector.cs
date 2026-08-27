using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// The stale slot check, against a process built to contain the bug and its opposite.
///
/// The target holds forty queues that clear the slot they remove from and forty that only move an
/// index. Both hold two items and look identical from outside. Finding the second and leaving the
/// first alone is the whole point: a check that flags healthy collections is worse than none.
/// </summary>
[TestFixture]
public sealed class TestStaleSlotDetector
{
    private static string SleeperDirectory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

    private static string s_dumpPath = string.Empty;
    private static FullDumpSnapshot? s_snapshot;
    private static IReadOnlyList<StaleSlotFinding> s_findings = Array.Empty<StaleSlotFinding>();

    [OneTimeSetUp]
    public void CollectAndAnalyze()
    {
        if (!Directory.Exists(SleeperDirectory)) Assert.Ignore($"sleeper project not found: {SleeperDirectory}");

        var store = new DumpStore(Path.Combine(Path.GetTempPath(), "MemoryProfilerStaleSlot", Guid.NewGuid().ToString("N")));

        using var handle = Process.Start(new ProcessStartInfo("dotnet", "run --project .")
        {
            WorkingDirectory = SleeperDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        var target = TestTarget.Announced(handle);

        try
        {
            s_dumpPath = FullDumpCollector.Collect(target, store);
        }
        finally
        {
            if (!handle.HasExited) handle.Kill(entireProcessTree: true);
        }

        s_snapshot = FullDumpSnapshot.Load(s_dumpPath);
        s_findings = StaleSlotDetector.Find(s_snapshot, progress: new Progress<string>(TestContext.Out.WriteLine));

        foreach (var finding in s_findings)
        {
            TestContext.Out.WriteLine(
                $"{finding.HolderType}.{finding.ArrayField} counted by {finding.CountField}: " +
                $"{finding.InstancesWithStaleSlots}/{finding.InstancesInspected} instances, " +
                $"{finding.StaleSlots} stale slots, {ReportWriter.Bytes(finding.StaleBytes)}, " +
                $"holding [{string.Join(", ", finding.WhatIsStuck)}]");
        }
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        s_snapshot?.Dispose();

        var folder = Path.GetDirectoryName(Path.GetDirectoryName(s_dumpPath));

        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Test]
    public void TheQueueThatKeepsWhatItRemovesIsFound()
    {
        var leaky = s_findings.FirstOrDefault(finding => finding.HolderType.Contains("LeakyQueue"));

        Assert.That(leaky, Is.Not.Null, "the queue that never clears its slots must be reported");
        Assert.That(leaky!.InstancesWithStaleSlots, Is.GreaterThanOrEqualTo(30), "all forty of them hold stale items");

        // Sixty four added, sixty two removed without clearing: sixty two slots still refer.
        Assert.That(leaky.StaleSlots / leaky.InstancesWithStaleSlots, Is.EqualTo(62).Within(2));
        Assert.That(leaky.WhatIsStuck, Has.Some.Contains("Payload"), "what is stuck must be named");
        Assert.That(leaky.StaleBytes, Is.GreaterThan(0));
    }

    [Test]
    public void TheQueueThatClearsItsSlotsIsNotReported()
    {
        Assert.That(
            s_findings.Select(finding => finding.HolderType),
            Has.None.Contains("TidyQueue"),
            "a collection that clears what it removes is healthy and must not be accused");
    }

    [Test]
    public void TheFindingReadsAsSomethingActionable()
    {
        var leaky = s_findings.First(finding => finding.HolderType.Contains("LeakyQueue"));
        var asLeak = StaleSlotDetector.ToLeakFinding(leaky);

        Assert.That(asLeak.Confidence, Is.GreaterThan(0.6),
            "reading the fields is direct evidence, so it must outrank the statistical findings");
        Assert.That(asLeak.Recommendation, Does.Contain("Clear the slot"));
        Assert.That(asLeak.Reasons, Has.Some.Contains("more references than they say they contain"));
        Assert.That(asLeak.HeldBy, Does.Contain("m_storage"));
    }

    [Test]
    public void HealthyFrameworkCollectionsAreNotAccused()
    {
        // List<T> clears the slot it removes; the target holds several. If the check cannot tell
        // those apart from the leaking one it is unusable on a real heap.
        Assert.That(
            s_findings.Where(finding => finding.HolderType.StartsWith("System.Collections.Generic.List<")),
            Is.Empty,
            "the framework list is not leaking");
    }
}
