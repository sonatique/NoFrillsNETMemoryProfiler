namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// A heap at one moment, whichever file format it came from. The views work against this, so a
/// .gcdump and a full process dump behave the same everywhere.
/// </summary>
public interface ISnapshot
{
    string FilePath { get; }

    /// <summary>Null when the file did not record it.</summary>
    string? ProcessName { get; }
    string? MachineName { get; }
    DateTime? TimeCollected { get; }

    long ObjectCount { get; }
    long TotalSize { get; }

    /// <summary>
    /// True when the file holds only part of the heap, so what is not in it cannot be attributed
    /// and absence of a holder proves nothing.
    /// </summary>
    bool IsPartial => false;

    /// <summary>Every type present, largest total size first.</summary>
    IReadOnlyList<TypeStatistic> TypeStatistics { get; }

    /// <summary>What keeps the objects matching the filter alive.</summary>
    HolderReport AnalyzeHolders(
        ObjectFilter filter,
        HolderAnalysisOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Opens a snapshot file by what it is.</summary>
public static class Snapshot
{
    public const string GcDumpExtension = ".gcdump";
    public const string FullDumpExtension = ".dmp";

    public static ISnapshot Load(string filePath, IProgress<string>? progress = null)
    {
        if (filePath.EndsWith(FullDumpExtension, StringComparison.OrdinalIgnoreCase))
        {
            return FullDumpSnapshot.Load(filePath, progress);
        }

        return HeapSnapshot.Load(filePath, progress);
    }
}
