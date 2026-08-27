using System.Diagnostics;
using Graphs;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// A loaded .gcdump file: the object graph plus the per object data the views need.
///
/// Loading walks the graph once and keeps the type and size of every node in flat arrays, so
/// selecting objects afterwards costs one pass over an integer array instead of a graph walk.
/// The reverse edges (<see cref="RefGraph"/>) are built on demand because they cost about as
/// much memory again as the graph itself.
/// </summary>
public sealed class HeapSnapshot : ISnapshot
{
    private readonly MemoryGraph m_graph;
    private readonly int[] m_typeOfNode;
    private readonly int[] m_sizeOfNode;
    private readonly string[] m_typeNames;

    private RefGraph? m_referrers;

    private HeapSnapshot(
        string filePath,
        GCHeapDump dump,
        MemoryGraph graph,
        int[] typeOfNode,
        int[] sizeOfNode,
        string[] typeNames,
        IReadOnlyList<TypeStatistic> typeStatistics)
    {
        FilePath = filePath;
        ProcessName = string.IsNullOrWhiteSpace(dump.ProcessName) ? null : dump.ProcessName;
        MachineName = string.IsNullOrWhiteSpace(dump.MachineName) ? null : dump.MachineName;
        TimeCollected = dump.TimeCollected == default ? null : dump.TimeCollected;
        m_graph = graph;
        m_typeOfNode = typeOfNode;
        m_sizeOfNode = sizeOfNode;
        m_typeNames = typeNames;
        TypeStatistics = typeStatistics;
        TotalSize = typeStatistics.Sum(t => t.TotalBytes);
    }

    public string FilePath { get; }
    /// <summary>Null when the dump file did not record it.</summary>
    public string? ProcessName { get; }
    public string? MachineName { get; }
    public DateTime? TimeCollected { get; }

    public int NodeCount => m_typeOfNode.Length;
    long ISnapshot.ObjectCount => NodeCount;

    /// <summary>
    /// True when the dump sits on the ten million object limit of the gcdump collector, which
    /// stops there without saying so. What is missing cannot be analyzed, and a holder that lives
    /// in the missing part is simply not reported: take a full dump instead.
    /// </summary>
    public bool IsProbablyTruncated => NodeCount >= GcDumpObjectLimit;

    bool ISnapshot.IsPartial => IsProbablyTruncated;

    private const int GcDumpObjectLimit = 10_000_000;
    public long TotalSize { get; }

    /// <summary>Every type present in the dump, largest total size first.</summary>
    public IReadOnlyList<TypeStatistic> TypeStatistics { get; }

    internal MemoryGraph Graph => m_graph;
    internal int RootIndex => (int)m_graph.RootIndex;

    internal string TypeNameOf(int nodeIndex) => m_typeNames[m_typeOfNode[nodeIndex]];
    internal int SizeOf(int nodeIndex) => m_sizeOfNode[nodeIndex];

    /// <summary>Reverse edges, built on first use: tells which nodes reference a given node.</summary>
    internal RefGraph Referrers => m_referrers ??= new RefGraph(m_graph);

    public static HeapSnapshot Load(string filePath, IProgress<string>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        progress?.Report($"Reading {Path.GetFileName(filePath)}...");

        var dump = new GCHeapDump(filePath);
        var graph = dump.MemoryGraph;
        var nodeCount = (int)graph.NodeIndexLimit;

        progress?.Report($"Indexing {nodeCount:N0} objects...");

        var typeOfNode = new int[nodeCount];
        var sizeOfNode = new int[nodeCount];
        var counts = new Dictionary<int, (int Count, long Bytes)>();

        var nodeStorage = graph.AllocNodeStorage();

        for (var i = 0; i < nodeCount; ++i)
        {
            var node = graph.GetNode((NodeIndex)i, nodeStorage);
            var typeIndex = (int)node.TypeIndex;

            typeOfNode[i] = typeIndex;
            sizeOfNode[i] = node.Size;

            counts.TryGetValue(typeIndex, out var current);
            counts[typeIndex] = (current.Count + 1, current.Bytes + node.Size);
        }

        progress?.Report("Aggregating types...");

        var typeStorage = graph.AllocTypeNodeStorage();
        var typeNames = new string[(int)graph.NodeTypeIndexLimit];

        for (var i = 0; i < typeNames.Length; ++i)
        {
            typeNames[i] = graph.GetType((NodeTypeIndex)i, typeStorage).Name ?? "(no type)";
        }

        // Several type indexes can share a name, so the rows are merged by name: one row per
        // type is what the views and the comparison expect.
        var statistics = counts
            .GroupBy(entry => typeNames[entry.Key])
            .Select(group => new TypeStatistic(
                group.Key,
                group.Sum(entry => entry.Value.Count),
                group.Sum(entry => entry.Value.Bytes)))
            .OrderByDescending(statistic => statistic.TotalBytes)
            .ToList();

        progress?.Report($"Loaded in {watch.Elapsed.TotalSeconds:F1} s");

        return new HeapSnapshot(filePath, dump, graph, typeOfNode, sizeOfNode, typeNames, statistics);
    }

    public HolderReport AnalyzeHolders(
        ObjectFilter filter,
        HolderAnalysisOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return HolderAnalysis.Run(this, filter, options, progress, cancellationToken);
    }

    /// <summary>Indexes of the objects matching the filter.</summary>
    public IReadOnlyList<int> SelectObjects(ObjectFilter filter)
    {
        var selected = new List<int>();

        for (var i = 0; i < m_typeOfNode.Length; ++i)
        {
            var size = m_sizeOfNode[i];

            if (size < filter.MinimalSize || size > filter.MaximalSize) continue;
            if (!filter.MatchesType(m_typeNames[m_typeOfNode[i]])) continue;

            selected.Add(i);
        }

        return selected;
    }

    public long TotalSizeOf(IReadOnlyList<int> objects)
    {
        long total = 0;
        foreach (var index in objects) total += m_sizeOfNode[index];
        return total;
    }
}
