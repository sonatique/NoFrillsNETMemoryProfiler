using System.Diagnostics;
using Graphs;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tools.GCDump;

namespace NoFrills.MemoryProfiler.Core;

public sealed record LiveProcess(int ProcessId, string Name)
{
    public override string ToString() => $"{Name} ({ProcessId})";
}

/// <summary>
/// Takes a snapshot of a running .NET process, the same way the dotnet-gcdump tool does: the
/// runtime is asked over its diagnostic pipe to walk its heap, which forces a blocking collection
/// in the target process. Expect it to pause for a few seconds on a large heap.
/// </summary>
public static class LiveCollector
{
    public static IReadOnlyList<LiveProcess> GetProfilableProcesses()
    {
        var processes = new List<LiveProcess>();

        foreach (var processId in DiagnosticsClient.GetPublishedProcesses())
        {
            try
            {
                var process = Process.GetProcessById(processId);
                processes.Add(new LiveProcess(processId, process.ProcessName));
            }
            catch (ArgumentException)
            {
                // The process ended between the enumeration and the lookup.
            }
        }

        return processes.OrderBy(process => process.Name).ToList();
    }

    /// <summary>Collects into the store, under the folder of the process name.</summary>
    public static string Collect(
        LiveProcess process,
        DumpStore store,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outputPath = store.NewDumpPath(process.Name, process.ProcessId, DateTime.Now);

        return CollectTo(process.ProcessId, outputPath, timeout, progress, cancellationToken);
    }

    /// <summary>Collects to an exact file path. Named apart from Collect so that a caller passing
    /// a folder cannot compile by accident.</summary>
    public static string CollectTo(
        int processId,
        string outputPath,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        progress?.Report($"Collecting from process {processId}, this pauses it...");

        var log = new ProgressWriter(progress);
        var graph = new MemoryGraph(50_000);

        // Two heap dumps of the same process at once confuse the runtime and one of them comes
        // back without its roots, so collections are taken one at a time.
        s_oneAtATime.Wait(cancellationToken);

        bool collected;

        try
        {
            collected = EventPipeDotNetHeapDumper.DumpFromEventPipe(
                cancellationToken,
                processId,
                string.Empty,
                graph,
                log,
                (int)timeout.TotalSeconds,
                new DotNetHeapInfo());
        }
        finally
        {
            s_oneAtATime.Release();
        }

        if (!collected)
        {
            throw new HeapCollectionException(
                $"The runtime of process {processId} did not produce a heap dump. {log.LastLine}");
        }

        Validate(graph, processId, log.LastLine);

        graph.AllowReading();

        // Written aside and moved into place: a dump that fails half way must not be left in the
        // store, where it would later be read as if it were a complete one.
        var partial = outputPath + ".collecting";

        try
        {
            GCHeapDump.WriteMemoryGraph(graph, partial, "NoFrills.MemoryProfiler");
            File.Move(partial, outputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }

        progress?.Report($"Written to {outputPath}");

        return outputPath;
    }

    /// <summary>
    /// Checks the graph really describes a heap before it is written.
    ///
    /// The dumper can report success while the runtime never delivered the root edges, which
    /// leaves a graph that cannot be read back. Caught here, it is a collection that has to be
    /// retried; left alone it becomes "RootIndex not set." from deep inside the graph library, or
    /// worse, a small file that looks like a dump.
    /// </summary>
    internal static void Validate(MemoryGraph graph, int processId, string lastCollectorMessage)
    {
        if (graph.RootIndex == NodeIndex.Invalid)
        {
            throw new HeapCollectionException(
                $"The heap of process {processId} came back without its roots, so the dump would be unreadable. " +
                "The runtime was probably too busy to deliver them; the next collection usually succeeds. " +
                $"Last message from the collector: {lastCollectorMessage}");
        }

        if (graph.NodeIndexLimit == 0)
        {
            throw new HeapCollectionException(
                $"The heap of process {processId} came back empty. Last message from the collector: {lastCollectorMessage}");
        }
    }

    private static readonly SemaphoreSlim s_oneAtATime = new(1, 1);

    /// <summary>Forwards the collector log to the progress display, keeping the last line for errors.</summary>
    private sealed class ProgressWriter(IProgress<string>? progress) : TextWriter
    {
        private readonly System.Text.StringBuilder m_current = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public string LastLine { get; private set; } = string.Empty;

        public override void Write(char value)
        {
            if (value != '\n')
            {
                if (value != '\r') m_current.Append(value);
                return;
            }

            LastLine = m_current.ToString().Trim();
            m_current.Clear();

            if (LastLine.Length != 0) progress?.Report(LastLine);
        }
    }
}
