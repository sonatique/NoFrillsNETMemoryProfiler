using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// The "What holds them" view came up empty when analyzing a type picked from the Types tab.
/// This reproduces exactly that: a type-name filter, no size bounds, on the reference dump.
/// </summary>
[TestFixture]
public sealed class TestTypeFilterAnalysis
{
    [Test]
    public void AnalyzingATypePickedFromTheListReportsItsHolders()
    {
        var path = ReferenceDumps.Find(ReferenceDumps.AnalyzerSeries);

        if (path == null) Assert.Ignore(ReferenceDumps.WhereToPutIt(ReferenceDumps.AnalyzerSeries));

        var snapshot = HeapSnapshot.Load(path!);

        var expected = ReferenceExpectations.Find();

        if (expected == null) Assert.Ignore(ReferenceExpectations.WhereToPutIt());

        var filter = ObjectFilter.ForType(expected.TypeToInspect);
        var report = snapshot.AnalyzeHolders(filter);

        TestContext.Out.WriteLine($"objects={report.ObjectCount} paths={report.RootPaths.Count} " +
                                  $"referrers={report.DirectReferrers.Count} unreachable={report.UnreachableCount}");

        Assert.That(report.ObjectCount, Is.GreaterThan(0), "the type was picked from the list, so it exists");
        Assert.That(report.RootPaths, Is.Not.Empty, "objects that exist are held by something");
        Assert.That(report.DirectReferrers, Is.Not.Empty);
    }
}
