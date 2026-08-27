using System.Globalization;
using System.Text;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// Records what the collector did, in a file next to the dumps.
///
/// A schedule runs unattended for hours: when a collection fails at 03:00 the message in the
/// window is gone by morning, replaced by the next one. The log is what is left to read.
/// </summary>
public sealed class CollectionLog
{
    private const long MaximalSize = 4 * 1024 * 1024;

    private readonly object m_locker = new();

    public CollectionLog(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>Raised for every line written, so a window can show what is happening now.</summary>
    public event Action<string>? Written;

    public static CollectionLog For(DumpStore store) => new(Path.Combine(store.RootFolder, "collection.log"));

    public void Info(string message) => Write("INF", message);

    public void Warning(string message) => Write("WRN", message);

    public void Failure(string message, Exception exception)
    {
        var text = new StringBuilder(message);

        // The message of the outer exception is rarely the useful one.
        for (var current = exception; current != null; current = current.InnerException)
        {
            text.Append(" | ").Append(current.GetType().Name).Append(": ").Append(current.Message);
        }

        Write("ERR", text.ToString());

        // The stack goes to the file only: it is noise in the window.
        Append($"      {exception.StackTrace}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {message}";

        Append(line);
        Written?.Invoke(line);
    }

    private void Append(string line)
    {
        lock (m_locker)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                Roll();

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Not being able to log must never stop a collection.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Keeps one previous file so a long schedule cannot fill the disk with its own log.</summary>
    private void Roll()
    {
        var info = new FileInfo(FilePath);

        if (!info.Exists || info.Length < MaximalSize) return;

        var previous = FilePath + ".1";

        if (File.Exists(previous)) File.Delete(previous);

        File.Move(FilePath, previous);
    }

    public IReadOnlyList<string> ReadRecent(int count)
    {
        lock (m_locker)
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<string>();

                return File.ReadLines(FilePath).TakeLast(count).ToList();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
