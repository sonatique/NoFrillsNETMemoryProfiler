using Graphs;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>A type that references the analyzed objects directly.</summary>
public sealed record DirectReferrer(string TypeName, int ReferenceCount);

/// <summary>
/// One way the analyzed objects are kept alive, from the holder up to a garbage collector root,
/// with the number of objects reached that way.
/// </summary>
public sealed record RootPath(
    IReadOnlyList<string> Frames,
    int ObjectCount,
    long Bytes,
    HolderCategory Category)
{
    public string Signature => string.Join(" <- ", Frames);

    public string FirstFrame => Frames.Count == 0 ? "(root)" : Frames[0];
}

public sealed record HolderReport(
    ObjectFilter Filter,
    int ObjectCount,
    long TotalBytes,
    IReadOnlyList<DirectReferrer> DirectReferrers,
    IReadOnlyList<RootPath> RootPaths,
    int UnreachableCount)
{
    public long SuspiciousBytes => RootPaths.Where(p => p.Category == HolderCategory.Application).Sum(p => p.Bytes);
}

public sealed class HolderAnalysisOptions
{
    /// <summary>How many frames of the path to a root are kept in a signature.</summary>
    public int MaximalFrameCount { get; init; } = 14;

    /// <summary>
    /// Referrer types that are not allowed to explain an object, because they reference far too
    /// many of them to be informative. The path search steps over these, so objects are attributed
    /// to the holder that actually keeps them alive.
    ///
    /// The default hides a dictionary keyed by byte arrays, the shape of the bookkeeping a
    /// buffer pool keeps over what it has lent out: it refers to every one of them by design, and
    /// would otherwise be reported as holding them all.
    /// </summary>
    public IReadOnlyList<string> IgnoredReferrerPatterns { get; init; } = new[] { "System.Byte[],System.Int64" };

    public HolderClassifier Classifier { get; init; } = HolderClassifier.Default;
}

public static class HolderAnalysis
{
    public static HolderReport Run(
        HeapSnapshot snapshot,
        ObjectFilter filter,
        HolderAnalysisOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HolderAnalysisOptions();

        progress?.Report("Selecting objects...");
        var selected = snapshot.SelectObjects(filter);

        var isSelected = new bool[snapshot.NodeCount];
        foreach (var index in selected) isSelected[index] = true;

        progress?.Report($"Finding what holds {selected.Count:N0} objects...");

        var directReferrers = FindDirectReferrers(snapshot, selected, cancellationToken);
        var (paths, unreachable) = FindRootPaths(snapshot, isSelected, selected, options, progress, cancellationToken);

        return new HolderReport(
            filter,
            selected.Count,
            snapshot.TotalSizeOf(selected),
            directReferrers,
            paths,
            unreachable);
    }

    /// <summary>
    /// Uses the reverse edges, so every referrer of every object is reported, not only the one
    /// that happens to be on a shortest path.
    /// </summary>
    private static List<DirectReferrer> FindDirectReferrers(
        HeapSnapshot snapshot,
        IReadOnlyList<int> selected,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>();
        var referrers = snapshot.Referrers;
        var refStorage = referrers.AllocNodeStorage();

        foreach (var index in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var refNode = referrers.GetNode((NodeIndex)index, refStorage);

            for (var parent = refNode.GetFirstChildIndex(); parent != NodeIndex.Invalid; parent = refNode.GetNextChildIndex())
            {
                var typeName = snapshot.TypeNameOf((int)parent);
                counts.TryGetValue(typeName, out var count);
                counts[typeName] = count + 1;
            }
        }

        return counts
            .Select(entry => new DirectReferrer(entry.Key, entry.Value))
            .OrderByDescending(referrer => referrer.ReferenceCount)
            .ToList();
    }

    /// <summary>
    /// Walks the graph from the roots, breadth first, and records for each analyzed object the
    /// first path that reached it. Breadth first means the shortest path wins, which is the one
    /// that describes the holder most directly.
    /// </summary>
    private static (List<RootPath> Paths, int Unreachable) FindRootPaths(
        HeapSnapshot snapshot,
        bool[] isSelected,
        IReadOnlyList<int> selected,
        HolderAnalysisOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var graph = snapshot.Graph;
        var nodeCount = snapshot.NodeCount;

        const int Unvisited = -2;
        const int IsRoot = -1;

        var parent = new int[nodeCount];
        Array.Fill(parent, Unvisited);

        var queue = new Queue<int>();
        var root = snapshot.RootIndex;

        parent[root] = IsRoot;
        queue.Enqueue(root);

        var nodeStorage = graph.AllocNodeStorage();
        var typeStorage = graph.AllocTypeNodeStorage();

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = queue.Dequeue();
            var node = graph.GetNode((NodeIndex)current, nodeStorage);
            var currentType = graph.GetType(node.TypeIndex, typeStorage).Name ?? string.Empty;

            if (IsIgnored(currentType, options.IgnoredReferrerPatterns)) continue;

            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
            {
                var childIndex = (int)child;
                if (childIndex < 0 || childIndex >= nodeCount) continue;

                if (parent[childIndex] != Unvisited) continue;

                parent[childIndex] = current;

                // An analyzed object is a leaf of the search: what it references itself does not
                // explain why it is alive.
                if (!isSelected[childIndex]) queue.Enqueue(childIndex);
            }
        }

        progress?.Report("Grouping paths...");

        var grouped = new Dictionary<string, (List<string> Frames, int Count, long Bytes)>();
        var unreachable = 0;

        foreach (var index in selected)
        {
            if (parent[index] == Unvisited)
            {
                ++unreachable;
                continue;
            }

            var frames = new List<string>();

            for (var current = parent[index]; current >= 0 && frames.Count < options.MaximalFrameCount; current = parent[current])
            {
                frames.Add(snapshot.TypeNameOf(current));
            }

            var signature = string.Join(" <- ", frames);

            grouped.TryGetValue(signature, out var entry);
            grouped[signature] = (frames, entry.Count + 1, entry.Bytes + snapshot.SizeOf(index));
        }

        var paths = grouped.Values
            .Select(entry => new RootPath(
                entry.Frames,
                entry.Count,
                entry.Bytes,
                options.Classifier.Classify(entry.Frames)))
            .OrderByDescending(path => path.Bytes)
            .ToList();

        return (paths, unreachable);
    }

    private static bool IsIgnored(string typeName, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (typeName.Contains(pattern, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
