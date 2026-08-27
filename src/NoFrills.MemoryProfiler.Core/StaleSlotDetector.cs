using Microsoft.Diagnostics.Runtime;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>A collection whose storage still refers to items it no longer counts as its own.</summary>
public sealed record StaleSlotFinding(
    string HolderType,
    string ArrayField,
    string CountField,
    int InstancesInspected,
    int InstancesWithStaleSlots,
    long StaleSlots,
    long StaleBytes,
    IReadOnlyList<string> WhatIsStuck)
{
    public double ShareOfInstances => InstancesInspected == 0
        ? 0
        : (double)InstancesWithStaleSlots / InstancesInspected;
}

public sealed class StaleSlotOptions
{
    /// <summary>Instances of one type actually read; the rest are counted but not opened.</summary>
    public int SampleSize { get; init; } = 300;

    /// <summary>A guard against one enormous array costing the whole search.</summary>
    public int MaximalSlotsPerInstance { get; init; } = 200_000;

    /// <summary>Types with fewer instances than this are not worth the reading time.</summary>
    public int MinimalInstances { get; init; } = 8;
}

/// <summary>
/// Finds collections that keep what they have given away.
///
/// A list, a queue or a ring buffer that removes an item by moving an index, without clearing the
/// slot the item occupied, still refers to it: the garbage collector cannot reclaim it, and the
/// memory stays until something happens to overwrite that slot. On a quiet channel that can be
/// minutes or never. It is the bug this profiler was written after, and the only way to see it is
/// to read the fields: from the outside such a collection looks perfectly normal.
///
/// The test is deliberately index blind: an item is stale when the storage holds more references
/// than the collection says it has. That holds for a list, for a builder, and for a ring buffer
/// whose live range wraps around the end, without knowing anything about the type.
///
/// Needs a full process dump: a gcdump carries no field values.
/// </summary>
public static class StaleSlotDetector
{
    private static readonly string[] CountNames = { "length", "count", "size", "used", "filled" };

    public static IReadOnlyList<StaleSlotFinding> Find(
        FullDumpSnapshot snapshot,
        StaleSlotOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StaleSlotOptions();

        var heap = snapshot.Runtime.Heap;
        var shapes = new Dictionary<ulong, CollectionShape?>();
        var states = new Dictionary<string, State>();

        progress?.Report("Reading the collections of the heap...");

        foreach (var instance in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var type = instance.Type;
            if (type == null || type.IsArray) continue;

            if (!shapes.TryGetValue(type.MethodTable, out var shape))
            {
                shape = ShapeOf(type);
                shapes[type.MethodTable] = shape;
            }

            if (shape == null) continue;

            if (!states.TryGetValue(type.Name!, out var state))
            {
                state = new State(shape);
                states[type.Name!] = state;
            }

            ++state.Seen;

            if (state.Inspected >= options.SampleSize) continue;

            Inspect(instance, shape, state, options);
        }

        var findings = new List<StaleSlotFinding>();

        foreach (var (typeName, state) in states)
        {
            if (state.Seen < options.MinimalInstances) continue;
            if (state.WithStaleSlots == 0) continue;

            findings.Add(new StaleSlotFinding(
                typeName,
                state.Shape.Array.Name ?? "?",
                state.Shape.Count.Name ?? "?",
                state.Inspected,
                state.WithStaleSlots,
                state.StaleSlots,
                state.StaleBytes,
                state.Stuck.OrderByDescending(entry => entry.Value).Take(5).Select(entry => entry.Key).ToList()));
        }

        return findings
            .OrderByDescending(finding => finding.StaleBytes)
            .ThenByDescending(finding => finding.StaleSlots)
            .ToList();
    }

    private sealed class State(CollectionShape shape)
    {
        public CollectionShape Shape { get; } = shape;
        public int Seen;
        public int Inspected;
        public int WithStaleSlots;
        public long StaleSlots;
        public long StaleBytes;
        public Dictionary<string, int> Stuck { get; } = new();
    }

    private sealed record CollectionShape(ClrInstanceField Array, ClrInstanceField Count);

    /// <summary>
    /// A type is worth reading when it holds a storage array of things that can refer to objects,
    /// together with a number saying how much of it is in use.
    /// </summary>
    private static CollectionShape? ShapeOf(ClrType type)
    {
        ClrInstanceField? array = null;
        ClrInstanceField? count = null;

        foreach (var field in type.Fields)
        {
            var fieldType = field.Type;

            if (fieldType is { IsArray: true } && array == null)
            {
                var element = fieldType.ComponentType;

                // An array of numbers cannot keep anything alive.
                if (element != null && (element.IsObjectReference || element.ContainsPointers)) array = field;

                continue;
            }

            if (count == null && field.ElementType is ClrElementType.Int32 && IsCountName(field.Name)) count = field;
        }

        return array != null && count != null ? new CollectionShape(array, count) : null;
    }

    private static bool IsCountName(string? name)
    {
        if (name == null) return false;

        var bare = name.TrimStart('_', 'm').TrimStart('_').ToLowerInvariant();

        return CountNames.Any(candidate => bare == candidate || bare.EndsWith(candidate, StringComparison.Ordinal));
    }

    private static void Inspect(ClrObject instance, CollectionShape shape, State state, StaleSlotOptions options)
    {
        try
        {
            var storage = shape.Array.ReadObject(instance.Address, interior: false);
            if (!storage.IsValid || !storage.IsArray) return;

            var array = storage.AsArray();
            var declared = shape.Count.Read<int>(instance.Address, interior: false);

            if (declared < 0 || declared > array.Length) return;

            ++state.Inspected;

            var slots = Math.Min(array.Length, options.MaximalSlotsPerInstance);
            var occupied = 0;
            var stuck = new List<ClrObject>();

            for (var i = 0; i < slots; ++i)
            {
                if (!ReadSlot(array, i, stuck)) continue;

                ++occupied;
            }

            // Index blind on purpose: more references than the collection admits to holding means
            // the ones it let go are still there, wherever they sit in the storage.
            var stale = occupied - declared;

            if (stale <= 0) return;

            ++state.WithStaleSlots;
            state.StaleSlots += stale;

            // Which of the found items are the stale ones is unknown, so their sizes are averaged:
            // reporting the whole content as wasted would overstate it.
            var averageSize = stuck.Count == 0 ? 0 : stuck.Sum(item => (long)item.Size) / stuck.Count;
            state.StaleBytes += averageSize * stale;

            foreach (var item in stuck.Take(20))
            {
                var name = item.Type?.Name;
                if (name == null) continue;

                state.Stuck.TryGetValue(name, out var seen);
                state.Stuck[name] = seen + 1;
            }
        }
        catch (Exception)
        {
            // A field that cannot be read is one instance not inspected, not a failed search.
        }
    }

    /// <summary>True when the slot refers to something; the references of a struct element count.</summary>
    private static bool ReadSlot(ClrArray array, int index, List<ClrObject> found)
    {
        var element = array.Type.ComponentType;

        if (element is { IsObjectReference: true })
        {
            var item = array.GetObjectValue(index);

            if (item.Address == 0) return false;

            if (found.Count < 32) found.Add(item);

            return true;
        }

        if (element is { ContainsPointers: true })
        {
            var value = array.GetStructValue(index);

            return value.IsValid && ReadStructReferences(value, found, depth: 0);
        }

        return false;
    }

    /// <summary>
    /// The reference that keeps an item alive can sit inside nested structures: the packet holds a
    /// data vector, the data vector holds the pooled buffer.
    /// </summary>
    private static bool ReadStructReferences(ClrValueType value, List<ClrObject> found, int depth)
    {
        if (depth > 3 || value.Type == null) return false;

        var any = false;

        foreach (var field in value.Type.Fields)
        {
            if (field.IsObjectReference)
            {
                var item = value.ReadObjectField(field);

                if (item.Address == 0) continue;

                if (found.Count < 32) found.Add(item);

                any = true;
                continue;
            }

            if (field.Type is { IsValueType: true, ContainsPointers: true })
            {
                var nested = value.ReadValueTypeField(field);

                if (nested.IsValid && ReadStructReferences(nested, found, depth + 1)) any = true;
            }
        }

        return any;
    }

    /// <summary>Turns the findings into the same shape as the rest of the leak list.</summary>
    public static LeakFinding ToLeakFinding(StaleSlotFinding finding)
    {
        var reasons = new List<string>
        {
            $"{finding.InstancesWithStaleSlots} of {finding.InstancesInspected} inspected instances hold more " +
            $"references than they say they contain",
            $"{finding.StaleSlots:N0} slots hold something the collection has already let go"
        };

        if (finding.WhatIsStuck.Count != 0)
        {
            reasons.Add("what is stuck there: " + string.Join(", ", finding.WhatIsStuck));
        }

        // This is read from the fields rather than inferred from shape and size, so it is worth
        // more than the statistical findings and is scored above them.
        var confidence = 0.6 + Math.Min(0.35, finding.ShareOfInstances * 0.35);

        return new LeakFinding(
            finding.HolderType,
            $"{finding.HolderType}.{finding.ArrayField}",
            new[] { $"{finding.HolderType}.{finding.ArrayField} (counted by {finding.CountField})" },
            (int)finding.StaleSlots,
            finding.StaleBytes,
            0,
            finding.ShareOfInstances,
            confidence,
            reasons,
            $"Clear the slot when removing from {finding.HolderType}: moving {finding.CountField} without " +
            $"clearing {finding.ArrayField} leaves the removed item referenced, and the garbage collector " +
            "cannot reclaim it until something overwrites that slot.");
    }
}
