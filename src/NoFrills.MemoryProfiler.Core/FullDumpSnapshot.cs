using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// A full process dump (dotnet-dump / DiagnosticsClient.WriteDump), read with ClrMD.
///
/// Exists because dotnet-gcdump silently truncates heaps at ten million objects, a hardcoded
/// constant of its reader. A full dump has no such cap, and knows the fields: attribution reads
/// "RequestQueue .m_pending" instead of a bare type name.
///
/// Unlike a gcdump, taking a full dump does not induce a collection first, so it also contains
/// dead-but-uncollected objects; what the analysis reports as unreachable is garbage awaiting
/// collection, not retention.
///
/// Reading needs the CLR data access component matching the dumped runtime: analyze on a machine
/// with the same .NET installed, or let ClrMD fetch it from the symbol server.
/// </summary>
public sealed class FullDumpSnapshot : ISnapshot, IDisposable
{
    private readonly DataTarget m_dataTarget;
    private readonly ClrRuntime m_runtime;

    private FullDumpSnapshot(
        string filePath,
        DataTarget dataTarget,
        ClrRuntime runtime,
        long objectCount,
        long totalSize,
        IReadOnlyList<TypeStatistic> typeStatistics)
    {
        FilePath = filePath;
        m_dataTarget = dataTarget;
        m_runtime = runtime;
        ObjectCount = objectCount;
        TotalSize = totalSize;
        TypeStatistics = typeStatistics;
    }

    public string FilePath { get; }

    /// <summary>The runtime of the dumped process, for the checks that have to read fields.</summary>
    internal ClrRuntime Runtime => m_runtime;

    /// <summary>A full dump records no process name, machine or moment; the store's file name carries the moment.</summary>
    public string? ProcessName => null;
    public string? MachineName => null;
    public DateTime? TimeCollected => null;

    public long ObjectCount { get; }
    public long TotalSize { get; }
    public IReadOnlyList<TypeStatistic> TypeStatistics { get; }

    public void Dispose()
    {
        m_runtime.Dispose();
        m_dataTarget.Dispose();
    }

    public static FullDumpSnapshot Load(string filePath, IProgress<string>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        progress?.Report($"Opening {Path.GetFileName(filePath)}...");

        var dataTarget = DataTarget.LoadDump(filePath);

        try
        {
            if (dataTarget.ClrVersions.Length == 0)
            {
                throw new InvalidOperationException("This dump contains no .NET runtime to read.");
            }

            var runtime = dataTarget.ClrVersions[0].CreateRuntime();

            progress?.Report("Walking the heap...");

            long objectCount = 0;
            long totalSize = 0;
            var counts = new Dictionary<string, (int Count, long Bytes)>();

            foreach (var heapObject in runtime.Heap.EnumerateObjects())
            {
                var name = heapObject.Type?.Name;
                if (name == null) continue;

                var size = (long)heapObject.Size;

                ++objectCount;
                totalSize += size;

                counts.TryGetValue(name, out var current);
                counts[name] = (current.Count + 1, current.Bytes + size);
            }

            var statistics = counts
                .Select(entry => new TypeStatistic(entry.Key, entry.Value.Count, entry.Value.Bytes))
                .OrderByDescending(statistic => statistic.TotalBytes)
                .ToList();

            progress?.Report($"Read {objectCount:N0} objects in {watch.Elapsed.TotalSeconds:F1} s");

            return new FullDumpSnapshot(filePath, dataTarget, runtime, objectCount, totalSize, statistics);
        }
        catch
        {
            dataTarget.Dispose();
            throw;
        }
    }

    public HolderReport AnalyzeHolders(
        ObjectFilter filter,
        HolderAnalysisOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HolderAnalysisOptions();
        var heap = m_runtime.Heap;

        progress?.Report("Selecting objects...");

        var targets = new HashSet<ulong>();
        long targetBytes = 0;

        foreach (var heapObject in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = heapObject.Type?.Name;
            if (name == null) continue;

            var size = (long)heapObject.Size;

            if (size < filter.MinimalSize || size > filter.MaximalSize) continue;
            if (!filter.MatchesType(name)) continue;

            targets.Add(heapObject.Address);
            targetBytes += size;
        }

        progress?.Report($"Finding what holds {targets.Count:N0} objects...");

        var staticNames = MapStaticFieldNames();
        var (parent, rootKind, directParents) = TraverseFromRoots(targets, options, cancellationToken);

        var directReferrers = CountDirectReferrers(targets, directParents, staticNames, cancellationToken);
        var (paths, unreachable) = GroupRootPaths(targets, parent, rootKind, staticNames, options);

        return new HolderReport(filter, targets.Count, targetBytes, directReferrers, paths, unreachable);
    }

    /// <summary>Static object fields by target address, to label edges out of the pinned static arrays.</summary>
    private Dictionary<ulong, string> MapStaticFieldNames()
    {
        var names = new Dictionary<ulong, string>();

        foreach (var domain in m_runtime.AppDomains)
        {
            foreach (var module in domain.Modules)
            {
                foreach (var (methodTable, _) in module.EnumerateTypeDefToMethodTableMap())
                {
                    var type = m_runtime.GetTypeByMethodTable(methodTable);
                    if (type == null) continue;

                    foreach (var field in type.StaticFields)
                    {
                        try
                        {
                            if (!field.IsObjectReference) continue;

                            var value = field.ReadObject(domain);

                            if (value.Address != 0)
                            {
                                names.TryAdd(value.Address, $"[static var {type.Name}.{field.Name}]");
                            }
                        }
                        catch (Exception)
                        {
                            // An unresolvable static only loses its label, not its path.
                        }
                    }
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Breadth first from the roots, recording the first parent that reached each object. The
    /// stack roots are seeded in a second phase, so a transient local variable cannot shadow the
    /// durable holder of an object both can reach.
    /// </summary>
    private (Dictionary<ulong, ulong> Parent, Dictionary<ulong, string> RootKind, List<ulong> DirectParents)
        TraverseFromRoots(HashSet<ulong> targets, HolderAnalysisOptions options, CancellationToken cancellationToken)
    {
        var heap = m_runtime.Heap;
        var parent = new Dictionary<ulong, ulong>();
        var rootKind = new Dictionary<ulong, string>();
        var directParents = new List<ulong>();
        var queue = new Queue<ulong>();

        void Traverse()
        {
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var address = queue.Dequeue();
                var heapObject = heap.GetObject(address);
                var typeName = heapObject.Type?.Name;

                if (typeName == null) continue;
                if (IsIgnored(typeName, options.IgnoredReferrerPatterns)) continue;

                var referencesTarget = false;

                foreach (var child in heapObject.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: true))
                {
                    if (child == 0) continue;
                    if (targets.Contains(child)) referencesTarget = true;

                    if (!parent.ContainsKey(child))
                    {
                        parent[child] = address;

                        // A selected object is a leaf: what it references does not explain its lifetime.
                        if (!targets.Contains(child)) queue.Enqueue(child);
                    }
                }

                if (referencesTarget) directParents.Add(address);
            }
        }

        var roots = heap.EnumerateRoots().ToList();

        foreach (var stackPhase in new[] { false, true })
        {
            foreach (var root in roots)
            {
                if ((root.RootKind == ClrRootKind.Stack) != stackPhase) continue;

                var address = root.Object.Address;
                if (address == 0 || parent.ContainsKey(address)) continue;

                parent[address] = 0;
                rootKind[address] = root.RootKind.ToString();
                queue.Enqueue(address);
            }

            Traverse();
        }

        return (parent, rootKind, directParents);
    }

    private List<DirectReferrer> CountDirectReferrers(
        HashSet<ulong> targets,
        List<ulong> directParents,
        Dictionary<ulong, string> staticNames,
        CancellationToken cancellationToken)
    {
        var heap = m_runtime.Heap;
        var counts = new Dictionary<string, int>();

        foreach (var parentAddress in directParents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var heapObject = heap.GetObject(parentAddress);

            foreach (var reference in heapObject.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
            {
                if (!targets.Contains(reference.Object.Address)) continue;

                var label = EdgeLabel(heapObject, reference, staticNames);

                counts.TryGetValue(label, out var count);
                counts[label] = count + 1;
            }
        }

        return counts
            .Select(entry => new DirectReferrer(entry.Key, entry.Value))
            .OrderByDescending(referrer => referrer.ReferenceCount)
            .ToList();
    }

    private (List<RootPath> Paths, int Unreachable) GroupRootPaths(
        HashSet<ulong> targets,
        Dictionary<ulong, ulong> parent,
        Dictionary<ulong, string> rootKind,
        Dictionary<ulong, string> staticNames,
        HolderAnalysisOptions options)
    {
        var heap = m_runtime.Heap;
        var grouped = new Dictionary<string, (List<string> Frames, int Count, long Bytes)>();
        var unreachable = 0;

        foreach (var target in targets)
        {
            if (!parent.TryGetValue(target, out var current))
            {
                // With no collection before the dump, unreachable means dead and awaiting one.
                ++unreachable;
                continue;
            }

            var frames = new List<string>();
            var child = target;

            while (current != 0 && frames.Count < options.MaximalFrameCount)
            {
                frames.Add(EdgeLabelBetween(current, child, staticNames));
                child = current;

                if (!parent.TryGetValue(current, out current)) break;
            }

            if (rootKind.TryGetValue(child, out var kind)) frames.Add($"[root: {kind}]");

            var signature = string.Join(" <- ", frames);

            grouped.TryGetValue(signature, out var entry);
            grouped[signature] = (frames, entry.Count + 1, entry.Bytes + (long)heap.GetObject(target).Size);
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

    private string EdgeLabelBetween(ulong parentAddress, ulong childAddress, Dictionary<ulong, string> staticNames)
    {
        var heapObject = m_runtime.Heap.GetObject(parentAddress);

        foreach (var reference in heapObject.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
        {
            if (reference.Object.Address != childAddress) continue;

            return EdgeLabel(heapObject, reference, staticNames);
        }

        return heapObject.Type?.Name ?? "?";
    }

    private static string EdgeLabel(ClrObject parentObject, ClrReference reference, Dictionary<ulong, string> staticNames)
    {
        var typeName = parentObject.Type?.Name ?? "?";

        // Static variables live in pinned object arrays; the variable name says more than that.
        if (typeName == "System.Object[]" && staticNames.TryGetValue(reference.Object.Address, out var staticName))
        {
            return staticName;
        }

        if (reference.Field is { } field) return $"{typeName} .{field.Name}";
        if (reference.IsArrayElement) return $"{typeName} [item]";

        return typeName;
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
