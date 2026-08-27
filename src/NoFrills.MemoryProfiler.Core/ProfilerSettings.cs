using System.Text.Json;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// The few choices that must survive a restart, kept in the user profile so a scheduled session
/// picks up where it was configured.
/// </summary>
public sealed class ProfilerSettings
{
    public string DumpRootFolder { get; set; } = DefaultDumpRootFolder;

    public int DefaultIntervalMinutes { get; set; } = 15;

    /// <summary>How many dumps a scheduled session keeps; older ones are deleted. Zero keeps all.</summary>
    public int KeepAtMostPerProcess { get; set; } = 0;

    /// <summary>Full process dumps instead of gcdumps: no ten million object cap, larger files.</summary>
    public bool UseFullDumps { get; set; }

    /// <summary>
    /// The profiler's own colours rather than the system ones. Read once at startup: the look
    /// cannot be swapped under a running window, so changing it takes effect on the next start.
    /// </summary>
    public bool UseProfilerTheme { get; set; } = true;

    public static string DefaultDumpRootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "MemoryProfiler");

    public static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NoFrills.MemoryProfiler",
        "settings.json");

    public static ProfilerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return new ProfilerSettings();

            var settings = JsonSerializer.Deserialize<ProfilerSettings>(File.ReadAllText(SettingsFilePath));

            return settings ?? new ProfilerSettings();
        }
        catch (Exception)
        {
            // Unreadable settings must not stop the profiler from starting.
            return new ProfilerSettings();
        }
    }

    public void Save()
    {
        var folder = Path.GetDirectoryName(SettingsFilePath)!;
        Directory.CreateDirectory(folder);

        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, s_writeOptions));
    }

    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };
}
