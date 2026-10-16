using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// Records the garbage collector of a running process for a while: every collection, every
/// suspension of the managed threads, and an allocation sample about every 100 KB with the stack
/// that made it. Unlike a heap dump it does not pause the target, so it can watch a process while
/// it misbehaves.
/// </summary>
public static class GcTimelineCollector
{
    /// <summary>
    /// How long the runtime may take to name its methods once the recording stops. The names come
    /// after the stop, and a large application has many of them.
    /// </summary>
    private static readonly TimeSpan RundownTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Records into the store, under the folder of the process name, beside its dumps.</summary>
    public static string Record(
        LiveProcess process,
        DumpStore store,
        TimeSpan duration,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outputPath = store.NewDumpPath(process.Name, process.ProcessId, DateTime.Now, GcTimeline.Extension);

        return RecordTo(process.ProcessId, outputPath, duration, progress, cancellationToken);
    }

    /// <summary>
    /// Records to an exact file path. Cancelling ends the recording early and keeps what was
    /// recorded: a recording cut short still answers the question, and is what Cancel is pressed for.
    /// </summary>
    public static string RecordTo(
        int processId,
        string outputPath,
        TimeSpan duration,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Verbose is what adds the allocation ticks to the GC keyword; the stacks of the events are
        // named by the rundown the runtime sends when the session stops.
        var providers = new[]
        {
            new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Stack))
        };

        // Written aside and moved into place, so a recording that fails half way is not left in
        // the store looking like a complete one.
        var partial = outputPath + ".collecting";

        try
        {
            using (var session = new DiagnosticsClient(processId).StartEventPipeSession(providers, requestRundown: true))
            using (var file = File.Create(partial))
            {
                progress?.Report($"Recording the garbage collector of process {processId} for {duration.TotalSeconds:F0} s...");

                var copy = session.EventStream.CopyToAsync(file);

                Task.WhenAny(copy, Task.Delay(duration, cancellationToken)).Wait();

                // The stream only ends by itself when the target does.
                if (!copy.IsCompleted)
                {
                    progress?.Report("Stopping; the runtime is naming the methods on the stacks...");
                    session.Stop();
                }

                Task.WhenAny(copy, Task.Delay(RundownTimeout)).Wait();

                if (!copy.IsCompleted)
                {
                    throw new HeapCollectionException(
                        $"Process {processId} did not finish the recording within {RundownTimeout.TotalMinutes:F0} minutes of being asked to stop.");
                }

                copy.GetAwaiter().GetResult();
            }

            File.Move(partial, outputPath, overwrite: true);
        }
        catch (Exception exception)
        {
            if (File.Exists(partial)) File.Delete(partial);

            if (exception is ServerNotAvailableException or TimeoutException)
            {
                throw new HeapCollectionException(
                    $"Process {processId} did not answer the recording request; it may have ended or be unresponsive.",
                    exception);
            }

            throw;
        }

        progress?.Report($"Written to {outputPath}");

        return outputPath;
    }
}
