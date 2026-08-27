using System;
using System.IO;
using System.Linq;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Finds the dumps the tests read.
///
/// Those tests check the profiler still reaches the conclusions of a real investigation, which
/// needs the real dumps; they are far too large for the repository. Set MEMORYPROFILER_TEST_DUMPS
/// to the folder holding them (searched recursively) and the tests run; without it they are
/// ignored rather than failing, so a clone builds and tests green out of the box.
///
/// Several folders can be given, separated the way PATH separates its own, because dumps taken
/// months apart rarely end up in one place.
/// </summary>
public static class ReferenceDumps
{
    public const string FolderVariable = "MEMORYPROFILER_TEST_DUMPS";

    /// <summary>The dump of the retention the analysis tests are calibrated against.</summary>
    public const string PoolRetention = "20260821_133910_175088.gcdump";

    /// <summary>One dump of a scheduled collection, used for the type and holder view tests.</summary>
    public const string AnalyzerSeries = "20260821_173849_49388.gcdump";

    /// <summary>
    /// The folders named by the variable that exist. Empty when it is unset or points nowhere.
    ///
    /// The variable is the only way they are found. Falling back to wherever the dumps happened to
    /// sit on the machine they were collected on is a path that means nothing to anybody else, and
    /// says more about that machine than a public repository should.
    /// </summary>
    public static IReadOnlyList<string> Folders
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(FolderVariable);

            if (string.IsNullOrWhiteSpace(configured)) return Array.Empty<string>();

            return configured
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Directory.Exists)
                .ToList();
        }
    }

    /// <summary>The first configured folder, for the tests that want a store rather than a file.</summary>
    public static string? Folder => Folders.FirstOrDefault();

    /// <summary>The full path of the dump, or null when it is not on this machine.</summary>
    public static string? Find(string fileName)
    {
        foreach (var folder in Folders)
        {
            var direct = Path.Combine(folder, fileName);
            if (File.Exists(direct)) return direct;

            // The dumps of one process sit in their own folder, so look below as well.
            var found = Directory
                .EnumerateFiles(folder, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (found != null) return found;
        }

        return null;
    }

    public static string WhereToPutIt(string fileName) =>
        $"{fileName} was not found. Set {FolderVariable} to the folder holding the reference dumps " +
        $"(or several, separated by '{Path.PathSeparator}') to run this test.";
}
