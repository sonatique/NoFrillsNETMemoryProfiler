namespace NoFrills.MemoryProfiler.Core;

/// <summary>How one type changed between two snapshots.</summary>
public sealed record TypeDelta(
    string TypeName,
    int BaselineCount,
    int CurrentCount,
    long BaselineBytes,
    long CurrentBytes)
{
    public int DeltaCount => CurrentCount - BaselineCount;
    public long DeltaBytes => CurrentBytes - BaselineBytes;
}

/// <summary>
/// Compares two snapshots of the same process taken at different times. What grew between them
/// is what accumulates, which is a more direct way to find a leak than reading either snapshot.
/// </summary>
public static class SnapshotDiff
{
    public static IReadOnlyList<TypeDelta> Compare(ISnapshot baseline, ISnapshot current)
    {
        var baselineByType = baseline.TypeStatistics.ToDictionary(statistic => statistic.TypeName);
        var currentByType = current.TypeStatistics.ToDictionary(statistic => statistic.TypeName);

        var deltas = new List<TypeDelta>();

        foreach (var typeName in baselineByType.Keys.Union(currentByType.Keys))
        {
            baselineByType.TryGetValue(typeName, out var before);
            currentByType.TryGetValue(typeName, out var after);

            deltas.Add(new TypeDelta(
                typeName,
                before?.Count ?? 0,
                after?.Count ?? 0,
                before?.TotalBytes ?? 0,
                after?.TotalBytes ?? 0));
        }

        return deltas.OrderByDescending(delta => delta.DeltaBytes).ToList();
    }
}
