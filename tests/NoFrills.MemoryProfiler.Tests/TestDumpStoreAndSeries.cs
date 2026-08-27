using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

[TestFixture]
public sealed class TestDumpStore
{
    private string m_root = string.Empty;

    [SetUp]
    public void CreateRoot()
    {
        m_root = Path.Combine(Path.GetTempPath(), "MemoryProfilerStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_root);
    }

    [TearDown]
    public void RemoveRoot()
    {
        if (Directory.Exists(m_root)) Directory.Delete(m_root, recursive: true);
    }

    [Test]
    public void DumpsArePlacedInAFolderPerProcessName()
    {
        var store = new DumpStore(m_root);

        var path = store.NewDumpPath("Contoso.Analyzer", 1234, new DateTime(2026, 8, 21, 14, 5, 9));

        Assert.That(Path.GetDirectoryName(path), Is.EqualTo(Path.Combine(m_root, "Contoso.Analyzer")));
        Assert.That(Path.GetFileName(path), Is.EqualTo("20260821_140509_1234.gcdump"));
        Assert.That(Directory.Exists(Path.GetDirectoryName(path)!), "the folder must be ready for the collector");
    }

    [Test]
    public void ProcessNamesThatCannotBeFolderNamesAreStillUsable()
    {
        var store = new DumpStore(m_root);

        var path = store.NewDumpPath("weird:name/here", 1, DateTime.Now);

        // The path as a whole contains the drive letter: check the folder name the store made up.
        var folderName = Path.GetFileName(Path.GetDirectoryName(path))!;

        Assert.That(folderName, Is.EqualTo("weird_name_here"));
        Assert.That(Directory.Exists(Path.GetDirectoryName(path)!), "the sanitized name must be usable");
    }

    [Test]
    public void SessionsListTheDumpsOfEachProcessOldestFirst()
    {
        var store = new DumpStore(m_root);

        Write(store, "AppOne", 1, new DateTime(2026, 8, 21, 10, 0, 0));
        Write(store, "AppOne", 1, new DateTime(2026, 8, 21, 9, 0, 0));
        Write(store, "AppTwo", 2, new DateTime(2026, 8, 21, 11, 0, 0));

        var sessions = store.EnumerateSessions();

        Assert.That(sessions.Select(session => session.ProcessName), Is.EqualTo(new[] { "AppOne", "AppTwo" }));
        Assert.That(sessions[0].Dumps, Has.Count.EqualTo(2));
        Assert.That(sessions[0].Dumps[0].TakenAt, Is.LessThan(sessions[0].Dumps[1].TakenAt),
            "a series is only meaningful in order");
    }

    [Test]
    public void TheTimeOfADumpComesFromItsName()
    {
        var store = new DumpStore(m_root);
        var expected = new DateTime(2026, 8, 21, 16, 30, 45);

        Write(store, "AppOne", 7, expected);

        Assert.That(store.EnumerateDumps("AppOne")[0].TakenAt, Is.EqualTo(expected));
    }

    [Test]
    public void ADumpNamedByAnotherToolFallsBackToItsFileDate()
    {
        var folder = Path.Combine(m_root, "AppOne");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "collected-by-hand.gcdump");
        File.WriteAllText(path, "x");

        var described = DumpStore.Describe(path);

        Assert.That(described.TakenAt, Is.EqualTo(File.GetLastWriteTime(path)).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void TrimmingKeepsTheMostRecentDumps()
    {
        var store = new DumpStore(m_root);

        for (var hour = 1; hour <= 5; ++hour) Write(store, "AppOne", 1, new DateTime(2026, 8, 21, hour, 0, 0));

        var deleted = store.Trim("AppOne", keepAtMost: 2);
        var left = store.EnumerateDumps("AppOne");

        Assert.That(deleted, Is.EqualTo(3));
        Assert.That(left, Has.Count.EqualTo(2));
        Assert.That(left[0].TakenAt.Hour, Is.EqualTo(4), "the oldest ones must go, not the newest");
        Assert.That(left[1].TakenAt.Hour, Is.EqualTo(5));
    }

    [Test]
    public void TrimmingKeepsEverythingWhenNoLimitIsSet()
    {
        var store = new DumpStore(m_root);

        for (var hour = 1; hour <= 3; ++hour) Write(store, "AppOne", 1, new DateTime(2026, 8, 21, hour, 0, 0));

        Assert.That(store.Trim("AppOne", keepAtMost: 0), Is.Zero);
        Assert.That(store.EnumerateDumps("AppOne"), Has.Count.EqualTo(3));
    }

    private static void Write(DumpStore store, string processName, int processId, DateTime takenAt) =>
        File.WriteAllText(store.NewDumpPath(processName, processId, takenAt), "not a real dump");
}

[TestFixture]
public sealed class TestSeriesAnalysis
{
    private static SnapshotSummary Summary(DateTime takenAt, params (string Type, int Count, long Bytes)[] types) =>
        new(
            $"dump-{takenAt:HHmmss}.gcdump",
            takenAt,
            types.Sum(type => type.Count),
            types.Sum(type => type.Bytes),
            types.Select(type => new TypeStatistic(type.Type, type.Count, type.Bytes)).ToList());

    private static IReadOnlyList<SnapshotSummary> Series()
    {
        var start = new DateTime(2026, 8, 21, 10, 0, 0);

        return new[]
        {
            Summary(start,                      ("Leaking", 10, 10_000_000), ("Stable", 5, 5_000_000), ("Noisy", 3, 9_000_000)),
            Summary(start.AddHours(1), ("Leaking", 20, 20_000_000), ("Stable", 5, 5_000_000), ("Noisy", 1, 3_000_000)),
            Summary(start.AddHours(2), ("Leaking", 30, 30_000_000), ("Stable", 5, 5_000_000), ("Noisy", 4, 12_000_000)),
            Summary(start.AddHours(3), ("Leaking", 40, 40_000_000), ("Stable", 5, 5_000_000), ("Noisy", 2, 6_000_000)),
        };
    }

    [Test]
    public void GrowingTypesAreRankedFirst()
    {
        var trends = SeriesAnalysis.Trends(Series());

        Assert.That(trends[0].TypeName, Is.EqualTo("Leaking"));
        Assert.That(trends[0].GrowthBytes, Is.EqualTo(30_000_000));
        Assert.That(trends[0].BytesPerHour, Is.EqualTo(10_000_000).Within(1));
    }

    [Test]
    public void OnlyTheTypeThatRisesAtEveryStepIsCalledSteady()
    {
        var trends = SeriesAnalysis.Trends(Series());

        var leaking = trends.Single(trend => trend.TypeName == "Leaking");
        var noisy = trends.SingleOrDefault(trend => trend.TypeName == "Noisy");

        Assert.That(leaking.GrowsSteadily, Is.True);
        Assert.That(noisy?.GrowsSteadily, Is.Not.True, "a type that goes up and down is being used, not accumulating");
    }

    [Test]
    public void TypesThatDoNotMoveAreNotReported()
    {
        var trends = SeriesAnalysis.Trends(Series());

        Assert.That(trends.Select(trend => trend.TypeName), Has.None.EqualTo("Stable"));
    }

    [Test]
    public void TheHeapTotalIsAvailableAsItsOwnTrend()
    {
        var total = SeriesAnalysis.TotalTrend(Series());

        Assert.That(total.Bytes, Has.Count.EqualTo(4));
        Assert.That(total.FirstBytes, Is.EqualTo(24_000_000));
        Assert.That(total.LastBytes, Is.EqualTo(51_000_000));
    }

    [Test]
    public void ASeriesOfOneHasNoTrend()
    {
        var single = new[] { Summary(DateTime.Now, ("Leaking", 1, 1_000_000)) };

        Assert.That(SeriesAnalysis.Trends(single), Is.Empty);
    }

    [Test]
    public void ComparingTwoSummariesReportsWhatAppearedAndDisappeared()
    {
        var before = Summary(DateTime.Now, ("Gone", 5, 5_000_000), ("Kept", 1, 1_000));
        var after = Summary(DateTime.Now.AddHours(1), ("New", 7, 7_000_000), ("Kept", 1, 1_000));

        var deltas = SeriesAnalysis.Compare(before, after);

        Assert.That(deltas[0].TypeName, Is.EqualTo("New"));
        Assert.That(deltas[0].DeltaBytes, Is.EqualTo(7_000_000));
        Assert.That(deltas[^1].TypeName, Is.EqualTo("Gone"));
        Assert.That(deltas[^1].DeltaBytes, Is.EqualTo(-5_000_000));
    }
}
