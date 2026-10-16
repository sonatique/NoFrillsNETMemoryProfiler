using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>One garbage collection. Times are milliseconds from the start of the recording.</summary>
public sealed record GcRun(int Number, int Generation, string Reason, string Type, double StartMs, double? EndMs)
{
    public bool IsBackground => Type == "BackgroundGC";
}

/// <summary>
/// A time the runtime stopped the managed threads, from asking them to stop to their running
/// again, with the collection it was for. A background collection stops them twice.
/// </summary>
public sealed record GcPause(double StartMs, double DurationMs, string SuspendReason, GcRun? Gc)
{
    /// <summary>What the pause is counted under: the kind of collection it was for.</summary>
    public string Generation => Gc == null
        ? $"no GC ({SuspendReason})"
        : $"gen{Gc.Generation}{(Gc.IsBackground ? " background" : string.Empty)}";
}

public sealed record PauseStatistic(
    string Generation,
    int Count,
    double TotalMs,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    string TriggeredBy);

/// <summary>The sampled allocations that share a type, a thread or a stack.</summary>
public sealed record AllocationGroup(string Name, long Bytes, int Samples);

/// <summary>
/// One allocation tick: the runtime reports one about every 100 KB a thread allocates, charged to
/// the object that crossed the mark, so the samples are proportions rather than a full account.
/// </summary>
public sealed record AllocationSample(string TypeName, long Bytes, int ThreadId, string Stack);

/// <summary>
/// What a recording of the garbage collector says: what triggers the collections, how long they
/// stop the threads, and what allocates. A heap snapshot cannot answer any of it: it shows what
/// survived, not what was made and thrown away, nor when the threads stood still.
/// </summary>
public sealed class GcTimeline
{
    public const string Extension = ".nettrace";

    /// <summary>How many frames of the application identify an allocating stack.</summary>
    public const int StackFrameCount = 3;

    public const string NoStack = "(no stack)";
    public const string FrameworkOnly = "(framework only)";

    private static readonly string[] FrameworkPrefixes = { "System.", "Microsoft.", "Internal.", "Interop." };

    private GcTimeline(
        string filePath,
        double durationMs,
        IReadOnlyList<GcRun> collections,
        IReadOnlyList<GcPause> pauses,
        IReadOnlyList<AllocationSample> allocations)
    {
        FilePath = filePath;
        DurationMs = durationMs;
        Collections = collections;
        Pauses = pauses;
        Allocations = allocations;

        PausesByGeneration = pauses
            .GroupBy(pause => pause.Generation)
            .Select(Summarize)
            .OrderBy(statistic => statistic.Generation, StringComparer.Ordinal)
            .ToList();

        AllocationsByType = Group(allocations, sample => sample.TypeName);
        AllocationsByThread = Group(allocations, sample => $"OS thread {sample.ThreadId}");
        AllocationsByStack = Group(allocations, sample => sample.Stack);
    }

    public string FilePath { get; }
    public double DurationMs { get; }
    public IReadOnlyList<GcRun> Collections { get; }
    public IReadOnlyList<GcPause> Pauses { get; }
    public IReadOnlyList<AllocationSample> Allocations { get; }

    public IReadOnlyList<PauseStatistic> PausesByGeneration { get; }
    public IReadOnlyList<AllocationGroup> AllocationsByType { get; }
    public IReadOnlyList<AllocationGroup> AllocationsByThread { get; }

    /// <summary>Keyed by the first application frames of the stack, leaf first, one per line.</summary>
    public IReadOnlyList<AllocationGroup> AllocationsByStack { get; }

    public double TotalPauseMs => Pauses.Sum(pause => pause.DurationMs);
    public long AllocatedBytes => Allocations.Sum(sample => sample.Bytes);

    /// <summary>
    /// Reads a recording. The stacks are only named once the trace is converted to the indexed
    /// form TraceEvent resolves them from, which is written aside and deleted after reading.
    /// </summary>
    public static GcTimeline Load(string path, IProgress<string>? progress = null)
    {
        progress?.Report($"Reading {Path.GetFileName(path)}...");

        var indexed = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.etlx");

        try
        {
            TraceLog.CreateFromEventPipeDataFile(path, indexed);

            using var log = new TraceLog(indexed);

            return Read(path, log);
        }
        finally
        {
            try
            {
                File.Delete(indexed);
            }
            catch (IOException)
            {
                // A file left in the temporary folder is harmless.
            }
        }
    }

    private static GcTimeline Read(string path, TraceLog log)
    {
        var collections = new List<GcRun>();
        var running = new Dictionary<int, int>();
        var suspensions = new List<(double Start, double End, string Reason)>();
        var allocations = new List<AllocationSample>();
        var stacks = new Dictionary<CallStackIndex, string>();

        double? suspendedAt = null;
        var suspendReason = string.Empty;

        var source = log.Events.GetSource();

        // A collection is paired by its number, which the start and the end both carry; one that
        // started before the recording has no start and is left out.
        source.Clr.GCStart += data =>
        {
            running[data.Count] = collections.Count;
            collections.Add(new GcRun(data.Count, data.Depth, data.Reason.ToString(), data.Type.ToString(), data.TimeStampRelativeMSec, null));
        };

        source.Clr.GCStop += data =>
        {
            if (!running.Remove(data.Count, out var index)) return;

            collections[index] = collections[index] with { EndMs = data.TimeStampRelativeMSec };
        };

        // One thread suspends the runtime at a time, so a suspension ends with the next restart.
        source.Clr.GCSuspendEEStart += data =>
        {
            suspendedAt = data.TimeStampRelativeMSec;
            suspendReason = data.Reason.ToString();
        };

        source.Clr.GCRestartEEStop += data =>
        {
            if (suspendedAt == null) return;

            suspensions.Add((suspendedAt.Value, data.TimeStampRelativeMSec, suspendReason));
            suspendedAt = null;
        };

        source.Clr.GCAllocationTick += data =>
        {
            allocations.Add(new AllocationSample(
                data.TypeName,
                data.AllocationAmount64,
                data.ThreadID,
                StackOf(data, stacks)));
        };

        source.Process();

        var pauses = suspensions
            .Select(suspension => new GcPause(
                suspension.Start,
                suspension.End - suspension.Start,
                suspension.Reason,
                ServedBy(collections, suspension.Start, suspension.End, suspension.Reason)))
            .ToList();

        return new GcTimeline(path, log.SessionEndTimeRelativeMSec, collections, pauses, allocations);
    }

    /// <summary>
    /// The collection a suspension was for: the one that started during it, or else, for a
    /// suspension the GC asked for, the one still running when it began, which is how the second
    /// pause of a background collection is found. A debugger stopping the threads during a
    /// background collection is not that collection's doing.
    /// </summary>
    private static GcRun? ServedBy(IReadOnlyList<GcRun> collections, double start, double end, string reason) =>
        collections.LastOrDefault(gc => gc.StartMs >= start && gc.StartMs <= end)
        ?? (reason.StartsWith("SuspendForGC", StringComparison.Ordinal)
            ? collections.LastOrDefault(gc => gc.StartMs < start && (gc.EndMs == null || gc.EndMs > start))
            : null);

    /// <summary>
    /// The first frames of the application on the stack of an allocation, leaf first. The frames
    /// of the framework say how memory is allocated, which is always the same; the application
    /// frames above them say why.
    /// </summary>
    private static string StackOf(TraceEvent data, Dictionary<CallStackIndex, string> stacks)
    {
        var index = data.CallStackIndex();
        if (index == CallStackIndex.Invalid) return NoStack;

        if (stacks.TryGetValue(index, out var known)) return known;

        var frames = new List<string>();

        for (var frame = data.CallStack(); frame != null && frames.Count < StackFrameCount; frame = frame.Caller)
        {
            var method = frame.CodeAddress.FullMethodName;

            // A frame the rundown did not name has nothing to group by, and is skipped with the framework.
            if (method.Length == 0 || FrameworkPrefixes.Any(prefix => method.StartsWith(prefix, StringComparison.Ordinal))) continue;

            frames.Add(method);
        }

        var stack = frames.Count == 0 ? FrameworkOnly : string.Join(Environment.NewLine, frames);
        stacks[index] = stack;

        return stack;
    }

    private static PauseStatistic Summarize(IGrouping<string, GcPause> pauses)
    {
        var durations = pauses.Select(pause => pause.DurationMs).Order().ToArray();

        // A background collection pauses twice: its trigger is counted once.
        var triggers = pauses
            .Select(pause => pause.Gc)
            .OfType<GcRun>()
            .Distinct()
            .GroupBy(gc => gc.Reason)
            .OrderByDescending(reason => reason.Count())
            .Select(reason => $"{reason.Key} {reason.Count()}");

        return new PauseStatistic(
            pauses.Key,
            durations.Length,
            durations.Sum(),
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            durations[^1],
            string.Join(", ", triggers));
    }

    /// <summary>Nearest rank, so the value is one that was measured.</summary>
    private static double Percentile(double[] sorted, double fraction) =>
        sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static List<AllocationGroup> Group(IEnumerable<AllocationSample> samples, Func<AllocationSample, string> key) =>
        samples
            .GroupBy(key)
            .Select(group => new AllocationGroup(group.Key, group.Sum(sample => sample.Bytes), group.Count()))
            .OrderByDescending(group => group.Bytes)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();
}
