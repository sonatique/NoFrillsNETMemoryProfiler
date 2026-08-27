namespace NoFrills.MemoryProfiler.Core;

/// <summary>How one type behaved across a series of dumps.</summary>
public sealed record TypeTrend(
    string TypeName,
    IReadOnlyList<long> Bytes,
    IReadOnlyList<int> Counts,
    long GrowthBytes,
    double BytesPerHour,
    bool GrowsSteadily)
{
    public long FirstBytes => Bytes.Count == 0 ? 0 : Bytes[0];
    public long LastBytes => Bytes.Count == 0 ? 0 : Bytes[^1];
    public int LastCount => Counts.Count == 0 ? 0 : Counts[^1];
}

/// <summary>
/// Reads a series of dumps of the same process as a time series.
///
/// The point is to separate what merely fluctuates from what accumulates: a type that ends larger
/// than it started may just have been caught at a bad moment, while a type that grows at nearly
/// every step is the one to look at.
/// </summary>
public static class SeriesAnalysis
{
    /// <param name="minimalGrowthBytes">Types growing less than this are not worth reporting.</param>
    public static IReadOnlyList<TypeTrend> Trends(
        IReadOnlyList<SnapshotSummary> series,
        long minimalGrowthBytes = 1024 * 1024)
    {
        if (series.Count < 2) return Array.Empty<TypeTrend>();

        var names = new HashSet<string>();
        foreach (var summary in series)
        {
            foreach (var type in summary.Types) names.Add(type.TypeName);
        }

        var hours = (series[^1].TakenAt - series[0].TakenAt).TotalHours;
        var trends = new List<TypeTrend>();

        foreach (var name in names)
        {
            var bytes = new long[series.Count];
            var counts = new int[series.Count];

            for (var i = 0; i < series.Count; ++i)
            {
                var statistic = series[i].Find(name);
                bytes[i] = statistic?.TotalBytes ?? 0;
                counts[i] = statistic?.Count ?? 0;
            }

            var growth = bytes[^1] - bytes[0];

            if (Math.Abs(growth) < minimalGrowthBytes) continue;

            trends.Add(new TypeTrend(
                name,
                bytes,
                counts,
                growth,
                hours > 0 ? growth / hours : 0,
                GrowsSteadily(bytes)));
        }

        return trends.OrderByDescending(trend => trend.GrowthBytes).ToList();
    }

    /// <summary>
    /// True when the type gets larger at most steps. A type that only ever grows is accumulating;
    /// one that goes up and down is being used and released.
    /// </summary>
    private static bool GrowsSteadily(IReadOnlyList<long> bytes)
    {
        if (bytes.Count < 3) return false;

        var rises = 0;

        for (var i = 1; i < bytes.Count; ++i)
        {
            if (bytes[i] > bytes[i - 1]) ++rises;
        }

        return rises >= (bytes.Count - 1) * 0.8;
    }

    /// <summary>The heap total over the series, to plot next to the types.</summary>
    public static TypeTrend TotalTrend(IReadOnlyList<SnapshotSummary> series)
    {
        var bytes = series.Select(summary => summary.TotalBytes).ToArray();
        var counts = series.Select(summary => summary.ObjectCount).ToArray();
        var hours = series.Count < 2 ? 0 : (series[^1].TakenAt - series[0].TakenAt).TotalHours;
        var growth = bytes.Length == 0 ? 0 : bytes[^1] - bytes[0];

        return new TypeTrend(
            "(whole heap)",
            bytes,
            counts,
            growth,
            hours > 0 ? growth / hours : 0,
            GrowsSteadily(bytes));
    }

    /// <summary>What changed between two dumps of the series, largest growth first.</summary>
    public static IReadOnlyList<TypeDelta> Compare(SnapshotSummary baseline, SnapshotSummary current)
    {
        var names = new HashSet<string>(baseline.Types.Select(type => type.TypeName));
        names.UnionWith(current.Types.Select(type => type.TypeName));

        var deltas = new List<TypeDelta>();

        foreach (var name in names)
        {
            var before = baseline.Find(name);
            var after = current.Find(name);

            deltas.Add(new TypeDelta(
                name,
                before?.Count ?? 0,
                after?.Count ?? 0,
                before?.TotalBytes ?? 0,
                after?.TotalBytes ?? 0));
        }

        return deltas.OrderByDescending(delta => delta.DeltaBytes).ToList();
    }
}
