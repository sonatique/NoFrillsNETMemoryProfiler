using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>What was read from the rule file, and what could not be.</summary>
public sealed record HolderRuleSet(
    IReadOnlyList<HolderRule> Rules,
    IReadOnlyList<string> Problems)
{
    public static readonly HolderRuleSet Empty = new(Array.Empty<HolderRule>(), Array.Empty<string>());

    /// <summary>
    /// The classifier to analyze with: these rules, and the built-in ones behind them.
    ///
    /// Ahead, not behind. The defaults recognise a pool by a fragment of its name, which is a
    /// guess; the reader naming their own type is not. A pool called MyObjectPoolWrapper that is
    /// really a cache has to be sayable, and the wording someone chose for their own heap has to
    /// be the wording they see.
    /// </summary>
    public HolderClassifier Applied() => Rules.Count == 0
        ? HolderClassifier.Default
        : new HolderClassifier(Rules.Concat(HolderClassifier.Default.Rules).ToList());
}

/// <summary>
/// Rules the reader adds for their own application, kept in a file rather than in the code.
///
/// The classifier knows the pools of the framework by name, which is all it can know: a pool
/// written for one application is recognisable only to whoever wrote it. Left unrecognised it is
/// reported as the largest leak in the heap on every run, since a pool holding its own free
/// buffers looks exactly like a leak from the outside. Naming it here is what stops that, and the
/// same goes for a cache, a ring of reusable objects, anything that holds on legitimately.
/// </summary>
public static class HolderRuleFile
{
    public const string FileName = "holder-rules.json";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NoFrills.MemoryProfiler",
        FileName);

    /// <summary>Reads the rules, reporting what it could not read rather than throwing.</summary>
    public static HolderRuleSet Load(string? path = null)
    {
        path ??= DefaultPath;

        if (!File.Exists(path)) return HolderRuleSet.Empty;

        try
        {
            var document = JsonSerializer.Deserialize<Document>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,

                    // This file is edited by hand. A trailing comma or a note to self must cost
                    // nothing; refusing every rule over one of them is how a feature goes unused.
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

            if (document?.Rules == null) return HolderRuleSet.Empty;

            var rules = new List<HolderRule>();
            var problems = new List<string>();

            foreach (var entry in document.Rules)
            {
                if (string.IsNullOrWhiteSpace(entry.Pattern))
                {
                    problems.Add("a rule without a pattern was ignored");
                    continue;
                }

                if (!Enum.TryParse<HolderCategory>(entry.Category, ignoreCase: true, out var category))
                {
                    problems.Add(
                        $"\"{entry.Pattern}\" names the category \"{entry.Category}\", which is not one of " +
                        string.Join(", ", Enum.GetNames<HolderCategory>()));

                    continue;
                }

                rules.Add(new HolderRule(
                    entry.Pattern,
                    category,
                    string.IsNullOrWhiteSpace(entry.Explanation) ? "Known holder" : entry.Explanation));
            }

            return new HolderRuleSet(rules, problems);
        }
        catch (Exception exception)
        {
            return new HolderRuleSet(
                Array.Empty<HolderRule>(),
                new[] { $"{Path.GetFileName(path)} could not be read: {exception.Message}" });
        }
    }

    /// <summary>The classifier with whatever the file adds, or just the defaults when it has nothing.</summary>
    public static HolderClassifier Classifier(string? path = null) => Load(path).Applied();

    /// <summary>
    /// Writes a file explaining itself, so that opening it is enough to know what to write. Does
    /// nothing when one is already there.
    /// </summary>
    public static string WriteTemplateIfMissing(string? path = null)
    {
        path ??= DefaultPath;

        if (File.Exists(path)) return path;

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var template = new Document
        {
            Help = new[]
            {
                "Each pattern is looked for in the names along a path to a root. When one matches, the",
                "objects held that way are given that verdict instead of being reported as a leak.",
                "Categories: " + string.Join(", ", Enum.GetNames<HolderCategory>()) + ".",
                "Pooled, AwaitingFinalizer, Pinned and Instrumentation are all treated as expected;",
                "Application is what the profiler reports as worth investigating.",
                "The pools of the framework are already known: ArrayPool, BufferPool, MemoryPool,",
                "ObjectPool. Name yours here if it is called something else.",
                "Comments (// ...) and trailing commas are allowed; this is a file to edit by hand.",
                "Remove the example below once you have written your own."
            },
            Rules = new[]
            {
                new Entry
                {
                    Pattern = "MyBufferPool",
                    Category = nameof(HolderCategory.Pooled),
                    Explanation = "Free buffer waiting in the pool"
                }
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));

        return path;
    }

    private sealed class Document
    {
        public string[]? Help { get; set; }
        public Entry[]? Rules { get; set; }
    }

    private sealed class Entry
    {
        public string? Pattern { get; set; }
        public string? Category { get; set; }
        public string? Explanation { get; set; }
    }
}
