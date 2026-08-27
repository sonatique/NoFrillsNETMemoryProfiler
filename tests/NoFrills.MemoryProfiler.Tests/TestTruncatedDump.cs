using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// The dump the analyzer produced sits exactly on the ten million object limit of the gcdump
/// collector, which truncates silently. It must still analyze, and the profiler must say that
/// what it shows is only part of the heap.
/// </summary>
[TestFixture]
public sealed class TestTruncatedDump
{
    private const string TruncatedDump = "20260821_171602_49388.gcdump";

    [Test]
    public void ATruncatedDumpStillReportsHolders()
    {
        var path = ReferenceDumps.Find(TruncatedDump);

        if (path == null) Assert.Ignore(ReferenceDumps.WhereToPutIt(TruncatedDump));

        var snapshot = HeapSnapshot.Load(path!);

        TestContext.Out.WriteLine($"objects={snapshot.NodeCount:N0} truncated={snapshot.IsProbablyTruncated}");

        Assert.That(snapshot.IsProbablyTruncated, Is.True, "this dump is at the collector limit");

        var watch = Stopwatch.StartNew();
        var expected = ReferenceExpectations.Find();

        if (expected == null) Assert.Ignore(ReferenceExpectations.WhereToPutIt());

        var report = snapshot.AnalyzeHolders(ObjectFilter.ForType(expected.TypeToInspect));
        watch.Stop();

        TestContext.Out.WriteLine($"objects={report.ObjectCount:N0} paths={report.RootPaths.Count} " +
                                  $"referrers={report.DirectReferrers.Count} in {watch.Elapsed.TotalSeconds:F1} s");

        Assert.That(report.ObjectCount, Is.GreaterThan(0));
        Assert.That(report.RootPaths, Is.Not.Empty, "the type the user picked has holders");
    }
}
