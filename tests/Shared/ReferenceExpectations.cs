using System;
using System.IO;
using System.Text.Json;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// What the tests should find in the reference dumps.
///
/// Those tests are the strongest evidence the profiler works, because they run against a real
/// heap whose answer is known: the leak it was written to find. That answer is a list of type
/// names from the application the dump came from, which has no business being written into a
/// public repository, and would mean nothing to anyone without the dump anyway.
///
/// So the names live beside the dumps rather than here. Put a file named
/// reference-expectations.json in the folder MEMORYPROFILER_TEST_DUMPS points at:
///
/// {
///   "holder":        "Some.Namespace.TheTypeThatHoldsTheBuffers",
///   "secondHolder":  "Some.Namespace.AnotherHolder",
///   "directReferrer":"Some.Namespace.ATypeReferringToThemDirectly",
///   "poolClass":     "TheClassOfYourBufferPool",
///   "typeToInspect": "Some.Namespace.APlentifulType"
/// }
///
/// Without it the tests that need it are ignored, which is what happens on any machine that does
/// not have the dumps either.
/// </summary>
public sealed class ReferenceExpectations
{
    public const string FileName = "reference-expectations.json";

    public string Holder { get; init; } = string.Empty;
    public string SecondHolder { get; init; } = string.Empty;
    public string DirectReferrer { get; init; } = string.Empty;
    public string PoolClass { get; init; } = string.Empty;
    public string TypeToInspect { get; init; } = string.Empty;

    private static ReferenceExpectations? s_loaded;
    private static bool s_looked;

    /// <summary>The expectations, or null when they are not on this machine.</summary>
    public static ReferenceExpectations? Find()
    {
        if (s_looked) return s_loaded;

        s_looked = true;

        var path = ReferenceDumps.Find(FileName);

        if (path == null) return null;

        try
        {
            s_loaded = JsonSerializer.Deserialize<ReferenceExpectations>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            s_loaded = null;
        }

        return s_loaded;
    }

    public static string WhereToPutIt() =>
        $"{FileName} was not found. Put it in the folder {ReferenceDumps.FolderVariable} points at, " +
        "naming what the reference dumps are expected to contain, to run this test.";
}
