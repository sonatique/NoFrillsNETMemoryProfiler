using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Runs the analysis against the dump the block retention was found in, and checks it still
/// reaches the same conclusions. The dump is not in source control: the tests are ignored when it
/// is not there.
/// </summary>
[TestFixture]
public sealed class TestHeapAnalysis
{
    private static HeapSnapshot? s_snapshot;
    private static ReferenceExpectations? s_expected;

    private static ReferenceExpectations Expected => s_expected!;

    [OneTimeSetUp]
    public void LoadSnapshot()
    {
        var path = ReferenceDumps.Find(ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore(ReferenceDumps.WhereToPutIt(ReferenceDumps.PoolRetention));

        s_expected = ReferenceExpectations.Find();

        if (s_expected == null) Assert.Ignore(ReferenceExpectations.WhereToPutIt());

        s_snapshot ??= HeapSnapshot.Load(path!);
    }

    private static HeapSnapshot Snapshot => s_snapshot!;

    [Test]
    public void SnapshotReportsTheHeapContent()
    {
        Assert.That(Snapshot.NodeCount, Is.EqualTo(4_878_269));
        Assert.That(Snapshot.TotalSize, Is.EqualTo(1_248_165_016));
        Assert.That(Snapshot.TypeStatistics, Is.Not.Empty);

        var largest = Snapshot.TypeStatistics[0];
        Assert.That(largest.TypeName, Does.Contain("System.Byte[]"));
    }

    [Test]
    public void PoolBlocksAreFound()
    {
        var blocks = Snapshot.SelectObjects(ObjectFilter.ForPoolBlocks());

        Assert.That(blocks, Has.Count.EqualTo(402));
        Assert.That(Snapshot.TotalSizeOf(blocks), Is.EqualTo(402L * ObjectFilter.DefaultPoolBlockSize));
    }

    [Test]
    public void HolderAnalysisNamesTheApplicationHolders()
    {
        var report = HolderAnalysis.Run(Snapshot, ObjectFilter.ForPoolBlocks());

        Assert.That(report.ObjectCount, Is.EqualTo(402));
        Assert.That(report.UnreachableCount, Is.Zero, "a collected dump only contains reachable objects");

        var applicationPaths = report.RootPaths.Where(p => p.Category == HolderCategory.Application).ToList();
        var frames = applicationPaths.SelectMany(p => p.Frames).ToList();

        Assert.That(frames, Has.Some.Contains(Expected.Holder),
            "the holder that kept the blocks in this dump must be named");
        Assert.That(frames, Has.Some.Contains(Expected.SecondHolder),
            "and so must the second one");
    }

    /// <summary>The classifier knows only what every heap has, so the pool of this dump is named here.</summary>
    private static HolderAnalysisOptions WithThePoolOfThisDump() => new()
    {
        Classifier = HolderClassifier.Default.With(
            new HolderRule(Expected.PoolClass, HolderCategory.Pooled, "Free block waiting in the pool"))
    };

    [Test]
    public void ExpectedHoldersAreNotReportedAsSuspicious()
    {
        var report = HolderAnalysis.Run(Snapshot, ObjectFilter.ForPoolBlocks());

        var pooled = report.RootPaths.Where(p => p.Category == HolderCategory.Pooled).Sum(p => p.ObjectCount);
        var finalizing = report.RootPaths.Where(p => p.Category == HolderCategory.AwaitingFinalizer).Sum(p => p.ObjectCount);
        var pinned = report.RootPaths.Where(p => p.Category == HolderCategory.Pinned).Sum(p => p.ObjectCount);

        Assert.That(pooled, Is.EqualTo(145), "blocks available in the pool");

        Assert.That(finalizing, Is.EqualTo(55), "dead objects waiting for their finalizer");
        Assert.That(pinned, Is.EqualTo(16), "blocks pinned for a transfer");

        Assert.That(report.SuspiciousBytes, Is.LessThan(report.TotalBytes),
            "the expected holders must not be counted as worth investigating");
    }

    [Test]
    public void IgnoringTheAgeDictionaryChangesTheAttribution()
    {
        var withoutIgnore = HolderAnalysis.Run(
            Snapshot,
            ObjectFilter.ForPoolBlocks(),
            new HolderAnalysisOptions { IgnoredReferrerPatterns = Array.Empty<string>() });

        var instrumentationFrames = withoutIgnore.RootPaths
            .SelectMany(p => p.Frames)
            .Count(frame => frame.Contains("System.Byte[],System.Int64"));

        Assert.That(instrumentationFrames, Is.GreaterThan(0),
            "without the exclusion the block age dictionary claims blocks");

        var withIgnore = HolderAnalysis.Run(Snapshot, ObjectFilter.ForPoolBlocks());

        Assert.That(
            withIgnore.RootPaths.SelectMany(p => p.Frames),
            Has.None.Contains("System.Byte[],System.Int64"),
            "the exclusion must hide it and attribute the blocks to their real holder");
    }

    [Test]
    public void DirectReferrersUseEveryEdgeNotOnlyTheShortestPath()
    {
        var report = HolderAnalysis.Run(Snapshot, ObjectFilter.ForPoolBlocks());

        var totalReferences = report.DirectReferrers.Sum(referrer => referrer.ReferenceCount);

        Assert.That(totalReferences, Is.GreaterThan(report.ObjectCount),
            "objects are referenced from several places, all of which must be reported");

        Assert.That(
            report.DirectReferrers.Select(referrer => referrer.TypeName),
            Has.Some.Contains(Expected.DirectReferrer));
    }

    [Test]
    public void ComparingASnapshotWithItselfShowsNoChange()
    {
        var deltas = SnapshotDiff.Compare(Snapshot, Snapshot);

        Assert.That(deltas, Is.Not.Empty);
        Assert.That(deltas.Select(delta => delta.DeltaBytes), Has.All.Zero);
        Assert.That(deltas.Select(delta => delta.DeltaCount), Has.All.Zero);
    }

    [Test]
    public void ReportIsWritten()
    {
        var report = HolderAnalysis.Run(Snapshot, ObjectFilter.ForPoolBlocks());
        var markdown = ReportWriter.Write(Snapshot, report, HolderClassifier.Default);

        Assert.That(markdown, Does.Contain("Heap analysis"));
        Assert.That(markdown, Does.Contain("What holds them"));
        Assert.That(markdown, Does.Contain(Expected.Holder));
    }
}
