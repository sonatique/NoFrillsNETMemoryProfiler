using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Calibrated against an investigation whose answer is known: an application held 843 MB of pooled
/// blocks because removing an entry from a ring buffer never cleared the slot it left, so two
/// stages of its pipeline went on holding blocks they had already handed on.
///
/// The detector is only worth having if it finds that by itself. What those two stages are called
/// is knowledge about someone's heap, so it lives beside the dumps rather than here.
/// </summary>
[TestFixture]
public sealed class TestLeakDetector
{
    /// <summary>What the reference dumps hold, kept beside them rather than written down here.</summary>
    private static ReferenceExpectations Expected =>
        ReferenceExpectations.Find() ?? throw new IgnoreException(ReferenceExpectations.WhereToPutIt());

    [Test]
    public void TheKnownLeakIsFoundInTheDumpItWasFoundIn()
    {
        var path = ReferenceDumps.Find(ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore(ReferenceDumps.WhereToPutIt(ReferenceDumps.PoolRetention));

        var snapshot = HeapSnapshot.Load(path!);
        var findings = LeakDetector.Find(snapshot);

        foreach (var finding in findings)
        {
            TestContext.Out.WriteLine(
                $"{finding.Confidence:F2} {finding.ConfidenceDisplay,-8} {finding.TypeName}");
            TestContext.Out.WriteLine($"      held by {finding.HeldBy}");
            TestContext.Out.WriteLine($"      {finding.ObjectCount:N0} objects, {ReportWriter.Bytes(finding.Bytes)}, " +
                                      $"share {finding.HolderShare:P0}");

            foreach (var reason in finding.Reasons) TestContext.Out.WriteLine($"      - {reason}");
        }

        Assert.That(findings, Is.Not.Empty, "the heap of that dump was 71% pooled blocks held wrongly");

        var holders = findings.SelectMany(finding => finding.HolderPath.Append(finding.HeldBy)).ToList();

        Assert.That(
            holders,
            Has.Some.Contains(Expected.Holder).Or.Some.Contains(Expected.SecondHolder),
            "the detector must name the part of the pipeline that was holding the blocks");

        // 827 MB wrongly held must come before curiosities of a few tens of megabytes.
        Assert.That(findings[0].HeldBy, Does.Contain(Expected.Holder),
            "the leak that mattered must be the first thing read");
        Assert.That(findings[0].Bytes, Is.GreaterThan(500L * 1024 * 1024));
    }

    [Test]
    public void WhatIsExpectedIsNotReportedAsALeak()
    {
        var path = ReferenceDumps.Find(ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore(ReferenceDumps.WhereToPutIt(ReferenceDumps.PoolRetention));

        var snapshot = HeapSnapshot.Load(path!);
        var findings = LeakDetector.Find(snapshot);

        foreach (var finding in findings)
        {
            Assert.That(finding.HeldBy, Does.Not.Contain("finalizer Handles"),
                "objects queued for finalization are dead already");
            Assert.That(finding.HeldBy, Does.Not.Contain("pinning Handles"),
                "a pinned buffer is a transfer in progress");
            Assert.That(finding.HeldBy, Does.Not.Contain(Expected.PoolClass),
                "a free block waiting in its pool is the pool working");
        }
    }

    [Test]
    public void ASeriesMakesTheCaseStronger()
    {
        // Whichever of the configured folders is a collection store; they are not all one, and a
        // store of full dumps is not one either: this reads the graph, which only a gcdump carries.
        var dumps = ReferenceDumps.Folders
            .SelectMany(folder => new DumpStore(folder).EnumerateSessions())
            .Select(session => session.Dumps)
            .FirstOrDefault(series => series.Count >= 3 && series.All(IsGcDump));

        if (dumps == null) Assert.Ignore("a series of at least three gcdumps is needed");

        var series = dumps.Select(dump => SnapshotSummary.For(dump)).ToList();
        var latest = HeapSnapshot.Load(dumps[^1].Path);

        var everything = new LeakDetectorOptions { MinimalConfidence = 0 };
        var withSeries = LeakDetector.Find(latest, series, everything);
        var withoutSeries = LeakDetector.Find(latest, null, everything);

        TestContext.Out.WriteLine($"truncated={latest.IsProbablyTruncated} candidates={withSeries.Count}");
        TestContext.Out.WriteLine("=== with the series ===");
        foreach (var finding in withSeries.Take(8))
        {
            TestContext.Out.WriteLine($"{finding.Confidence:F2} {finding.TypeName} held by {finding.HeldBy}");
            foreach (var reason in finding.Reasons) TestContext.Out.WriteLine($"      - {reason}");
        }

        Assert.That(withSeries, Is.Not.Empty);

        // Growth is evidence a single dump cannot provide, so the same type must score higher.
        var top = withSeries[0];
        var same = withoutSeries.FirstOrDefault(finding => finding.TypeName == top.TypeName);

        if (same != null && top.Reasons.Any(reason => reason.Contains("grows at every step")))
        {
            Assert.That(top.Confidence, Is.GreaterThan(same.Confidence),
                "knowing it grows must count for something");
        }
    }

    [Test]
    public void EveryFindingCarriesItsEvidence()
    {
        var path = ReferenceDumps.Find(ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore(ReferenceDumps.WhereToPutIt(ReferenceDumps.PoolRetention));

        var snapshot = HeapSnapshot.Load(path!);

        foreach (var finding in LeakDetector.Find(snapshot))
        {
            Assert.That(finding.Reasons, Is.Not.Empty, "a score nobody can argue with is a score nobody trusts");
            Assert.That(finding.Recommendation, Is.Not.Empty);
            Assert.That(finding.Confidence, Is.InRange(0d, 1d));
        }
    }

    private static bool IsGcDump(DumpFile dump) =>
        dump.Path.EndsWith(".gcdump", StringComparison.OrdinalIgnoreCase);
}
