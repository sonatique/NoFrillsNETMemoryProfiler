using System.Globalization;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>One dump file on disk, with the moment it was taken.</summary>
public sealed record DumpFile(string Path, DateTime TakenAt, long FileSize)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Display => $"{TakenAt:yyyy-MM-dd HH:mm:ss}  ({FileSize / (1024 * 1024)} MB)";
}

/// <summary>All the dumps taken of one process name, oldest first.</summary>
public sealed record DumpSession(string ProcessName, IReadOnlyList<DumpFile> Dumps)
{
    public string Display => Dumps.Count == 0
        ? ProcessName
        : $"{ProcessName}  -  {Dumps.Count} dumps, {Dumps[0].TakenAt:yyyy-MM-dd HH:mm} to {Dumps[^1].TakenAt:HH:mm}";
}

/// <summary>
/// Where the dumps live: one folder per process name under a configurable root, so the dumps of a
/// process are a series by construction and can be compared without collecting them by hand.
/// </summary>
public sealed class DumpStore(string rootFolder)
{
    private static readonly string[] Extensions = { Snapshot.GcDumpExtension, Snapshot.FullDumpExtension };

    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    public string RootFolder { get; } = rootFolder;

    public string FolderFor(string processName) => Path.Combine(RootFolder, Sanitize(processName));

    public string NewDumpPath(string processName, int processId, DateTime takenAt, string extension = Snapshot.GcDumpExtension)
    {
        var folder = FolderFor(processName);
        Directory.CreateDirectory(folder);

        return Path.Combine(
            folder,
            $"{takenAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)}_{processId}{extension}");
    }

    public IReadOnlyList<DumpSession> EnumerateSessions()
    {
        if (!Directory.Exists(RootFolder)) return Array.Empty<DumpSession>();

        var sessions = new List<DumpSession>();

        foreach (var folder in Directory.EnumerateDirectories(RootFolder))
        {
            var dumps = EnumerateDumps(Path.GetFileName(folder));
            if (dumps.Count != 0) sessions.Add(new DumpSession(Path.GetFileName(folder), dumps));
        }

        // Loose dumps directly under the root are still worth listing.
        var loose = ReadFolder(RootFolder);
        if (loose.Count != 0) sessions.Add(new DumpSession("(not sorted by process)", loose));

        return sessions.OrderBy(session => session.ProcessName).ToList();
    }

    public IReadOnlyList<DumpFile> EnumerateDumps(string processName) => ReadFolder(FolderFor(processName));

    private static List<DumpFile> ReadFolder(string folder)
    {
        if (!Directory.Exists(folder)) return new List<DumpFile>();

        return Extensions
            .SelectMany(extension => Directory.EnumerateFiles(folder, "*" + extension))
            .Select(Describe)
            .OrderBy(dump => dump.TakenAt)
            .ToList();
    }

    public static DumpFile Describe(string path)
    {
        var info = new FileInfo(path);

        return new DumpFile(path, TakenAtOf(path, info), info.Length);
    }

    /// <summary>
    /// A .gcdump does not record when it was collected, so the time comes from the file name the
    /// profiler writes, and falls back to the file date for dumps collected by other tools.
    /// </summary>
    private static DateTime TakenAtOf(string path, FileInfo info)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('_');

        if (separator > 0
            && DateTime.TryParseExact(
                name[..separator],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var stamp))
        {
            return stamp;
        }

        return info.LastWriteTime;
    }

    /// <summary>Deletes the oldest dumps of a process, keeping the most recent ones.</summary>
    public int Trim(string processName, int keepAtMost)
    {
        if (keepAtMost <= 0) return 0;

        var dumps = EnumerateDumps(processName);
        if (dumps.Count <= keepAtMost) return 0;

        var deleted = 0;

        foreach (var dump in dumps.Take(dumps.Count - keepAtMost))
        {
            try
            {
                File.Delete(dump.Path);
                SnapshotSummary.DeleteCache(dump.Path);
                ++deleted;
            }
            catch (IOException)
            {
                // A dump being read stays; it will be trimmed next time.
            }
        }

        return deleted;
    }

    private static string Sanitize(string name)
    {
        var clean = name;

        foreach (var invalid in Path.GetInvalidFileNameChars()) clean = clean.Replace(invalid, '_');

        return clean.Length == 0 ? "unknown" : clean;
    }
}
