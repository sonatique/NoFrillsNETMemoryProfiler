using Microsoft.Diagnostics.NETCore.Client;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// Takes a full process dump, the alternative to the gcdump collection for heaps beyond ten
/// million objects (where dotnet-gcdump silently truncates).
///
/// The trade: no object cap and field precise attribution, against a file the size of the
/// process memory and a somewhat longer pause while it is written.
/// </summary>
public static class FullDumpCollector
{
    public static string Collect(
        LiveProcess process,
        DumpStore store,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outputPath = store.NewDumpPath(process.Name, process.ProcessId, DateTime.Now, Snapshot.FullDumpExtension);

        progress?.Report($"Writing the full memory of process {process.ProcessId}, this pauses it...");

        // Written aside and moved into place, so a dump that fails half way (disk full, process
        // gone) is not left in the store looking like a complete one.
        var partial = outputPath + ".collecting";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            new DiagnosticsClient(process.ProcessId).WriteDump(DumpType.Full, partial, logDumpGeneration: false);

            File.Move(partial, outputPath, overwrite: true);
        }
        catch (Exception exception)
        {
            if (File.Exists(partial)) File.Delete(partial);

            if (exception is ServerNotAvailableException or TimeoutException)
            {
                throw new HeapCollectionException(
                    $"Process {process.ProcessId} did not answer the dump request; it may have ended or be unresponsive.",
                    exception);
            }

            throw;
        }

        progress?.Report($"Written to {outputPath}");

        return outputPath;
    }
}
