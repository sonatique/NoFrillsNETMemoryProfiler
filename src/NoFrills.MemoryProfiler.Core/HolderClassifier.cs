namespace NoFrills.MemoryProfiler.Core;

public enum HolderCategory
{
    /// <summary>Something in the application keeps the objects alive. This is what to investigate.</summary>
    Application,

    /// <summary>The objects are free and waiting in their pool.</summary>
    Pooled,

    /// <summary>The objects are unreachable and only wait for their finalizer to return them.</summary>
    AwaitingFinalizer,

    /// <summary>The objects are pinned, typically for a transfer in progress.</summary>
    Pinned,

    /// <summary>A diagnostic structure references the objects; it does not explain their lifetime.</summary>
    Instrumentation
}

/// <summary>Matches a path to a root against a known holder.</summary>
public sealed record HolderRule(string Pattern, HolderCategory Category, string Explanation);

/// <summary>
/// Sorts the paths to a root into the ones that are expected and the ones worth investigating.
///
/// Without this, every report starts with the same manual triage: dead objects queued for
/// finalization and buffers pinned for a transfer look like retention and are normal.
///
/// The defaults know only what every .NET heap has. A buffer pool, a cache, anything of yours
/// that legitimately holds on to objects is recognised by adding a rule for it:
///
/// <code>
/// var classifier = HolderClassifier.Default.With(
///     new HolderRule("MyBufferPool", HolderCategory.Pooled, "Free block waiting in the pool"));
/// </code>
///
/// It is worth doing: without it the pool holding its own free blocks is reported as a leak
/// every time, and a report that has to be triaged by hand is one nobody reads twice.
///
/// The application reads the same rules from <see cref="HolderRuleFile"/>, so this knowledge can
/// be written once in a file rather than compiled in.
/// </summary>
public sealed class HolderClassifier
{
    public static HolderClassifier Default { get; } = new(new[]
    {
        // The pool names of the framework, and of nearly everything written to look like them:
        // System.Buffers.ArrayPool and MemoryPool, and the BufferPool or ObjectPool most
        // applications end up with. A pool holding its own free buffers is the pool working, and
        // reporting that as a leak every time is how a report stops being read.
        new HolderRule(
            "ArrayPool",
            HolderCategory.Pooled,
            "Free buffer waiting in the pool"),

        new HolderRule(
            "BufferPool",
            HolderCategory.Pooled,
            "Free buffer waiting in the pool"),

        new HolderRule(
            "MemoryPool",
            HolderCategory.Pooled,
            "Free buffer waiting in the pool"),

        new HolderRule(
            "ObjectPool",
            HolderCategory.Pooled,
            "Free instance waiting in the pool"),

        new HolderRule(
            "[finalizer Handles]",
            HolderCategory.AwaitingFinalizer,
            "Unreachable, the finalizer will return it to the pool"),

        new HolderRule(
            "[root: FinalizerQueue]",
            HolderCategory.AwaitingFinalizer,
            "Unreachable, the finalizer will return it to the pool"),

        new HolderRule(
            "[pinning Handles]",
            HolderCategory.Pinned,
            "Pinned, typically a transfer in progress"),

        new HolderRule(
            "[root: Pinned",
            HolderCategory.Pinned,
            "Pinned, typically a transfer in progress"),

    });

    private readonly IReadOnlyList<HolderRule> m_rules;

    public HolderClassifier(IReadOnlyList<HolderRule> rules) => m_rules = rules;

    public IReadOnlyList<HolderRule> Rules => m_rules;

    public HolderClassifier With(params HolderRule[] additional) =>
        new(m_rules.Concat(additional).ToList());

    public HolderClassifier With(IEnumerable<HolderRule> additional) =>
        new(m_rules.Concat(additional).ToList());

    public HolderCategory Classify(IReadOnlyList<string> frames)
    {
        foreach (var frame in frames)
        {
            foreach (var rule in m_rules)
            {
                if (frame.Contains(rule.Pattern, StringComparison.Ordinal)) return rule.Category;
            }
        }

        return HolderCategory.Application;
    }

    public string? Explain(IReadOnlyList<string> frames)
    {
        foreach (var frame in frames)
        {
            foreach (var rule in m_rules)
            {
                if (frame.Contains(rule.Pattern, StringComparison.Ordinal)) return rule.Explanation;
            }
        }

        return null;
    }
}
