using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// What one dump contains, reduced to per type totals.
///
/// A loaded dump holds its whole object graph, one to two gigabytes for a large heap, so a series
/// of them cannot be kept in memory at once. The summary is a few hundred kilobytes and is all the
/// evolution views need; the full graph is only loaded when a single dump is analyzed.
/// </summary>
public sealed class SnapshotSummary
{
    [JsonConstructor]
    public SnapshotSummary(
        string filePath,
        DateTime takenAt,
        int objectCount,
        long totalBytes,
        IReadOnlyList<TypeStatistic> types)
    {
        FilePath = filePath;
        TakenAt = takenAt;
        ObjectCount = objectCount;
        TotalBytes = totalBytes;
        Types = types;
    }

    public string FilePath { get; }
    public DateTime TakenAt { get; }
    public int ObjectCount { get; }
    public long TotalBytes { get; }
    public IReadOnlyList<TypeStatistic> Types { get; }

    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    private Dictionary<string, TypeStatistic>? m_byName;

    public TypeStatistic? Find(string typeName)
    {
        m_byName ??= Types.ToDictionary(type => type.TypeName);

        return m_byName.GetValueOrDefault(typeName);
    }

    /// <summary>
    /// Loads a dump, reduces it, and lets the graph go. Reading a summary again is served from a
    /// cache file next to the dump, because reducing a large dump takes seconds and a series
    /// would otherwise be reduced from scratch on every look.
    /// </summary>
    public static SnapshotSummary For(DumpFile dump, IProgress<string>? progress = null)
    {
        var cached = ReadCache(dump);
        if (cached != null) return cached;

        progress?.Report($"Reducing {dump.FileName}...");

        var snapshot = Snapshot.Load(dump.Path, progress);
        var summary = Extract(snapshot, dump.TakenAt);

        // A full dump snapshot keeps the file open until it is let go.
        (snapshot as IDisposable)?.Dispose();

        // The dump was only needed to produce the summary: let the graph go before the next one.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        WriteCache(dump, summary);

        return summary;
    }

    public static SnapshotSummary Extract(ISnapshot snapshot, DateTime takenAt) => new(
        snapshot.FilePath,
        snapshot.TimeCollected ?? takenAt,
        (int)Math.Min(snapshot.ObjectCount, int.MaxValue),
        snapshot.TotalSize,
        snapshot.TypeStatistics);

    private static string CachePathOf(string dumpPath) => dumpPath + ".summary.json";

    public static void DeleteCache(string dumpPath)
    {
        try
        {
            var path = CachePathOf(dumpPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A stale cache is harmless: it is checked against the dump before being used.
        }
    }

    private static SnapshotSummary? ReadCache(DumpFile dump)
    {
        var path = CachePathOf(dump.Path);

        try
        {
            if (!File.Exists(path)) return null;

            // A cache older than its dump describes something else.
            if (File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(dump.Path)) return null;

            return JsonSerializer.Deserialize<SnapshotSummary>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteCache(DumpFile dump, SnapshotSummary summary)
    {
        try
        {
            File.WriteAllText(CachePathOf(dump.Path), JsonSerializer.Serialize(summary));
        }
        catch (IOException)
        {
            // Not being able to cache only costs time next round.
        }
    }
}
