namespace NoFrills.MemoryProfiler.Core;

/// <summary>One suspected leak, with the evidence that produced it.</summary>
public sealed record LeakFinding(
    string TypeName,
    string HeldBy,
    IReadOnlyList<string> HolderPath,
    int ObjectCount,
    long Bytes,
    double BytesPerHour,
    double HolderShare,
    double Confidence,
    IReadOnlyList<string> Reasons,
    string Recommendation)
{
    public string ConfidenceDisplay => Confidence switch
    {
        >= 0.7 => "likely",
        >= 0.45 => "possible",
        _ => "weak"
    };
}

public sealed class LeakDetectorOptions
{
    /// <summary>How many types are taken through the holder analysis, which is the expensive part.</summary>
    public int CandidateCount { get; init; } = 8;

    /// <summary>Types smaller than this are not worth a finding.</summary>
    public long MinimalBytes { get; init; } = 8 * 1024 * 1024;

    public HolderClassifier Classifier { get; init; } = HolderClassifier.Default;

    /// <summary>Findings below this are not shown; they are noise at the bottom of any heap.</summary>
    public double MinimalConfidence { get; init; } = 0.3;

    /// <summary>
    /// Read the fields of the collections to find the ones keeping what they removed. Only a full
    /// dump carries field values, and reading them walks the heap again, so it costs a pass.
    /// </summary>
    public bool ReadCollectionFields { get; init; } = true;
}

/// <summary>
/// Ranks what is most likely to be leaking.
///
/// Two independent kinds of evidence, because either alone is misleading. A type that grows
/// across a series may simply be the capture being recorded; a type that is large in one dump may
/// simply be the working set. What identifies a leak is growth that is accumulating *somewhere*:
/// a single holder in the application collecting instances it never lets go.
///
/// Every finding carries the reasons that produced its score, because a detector that cannot be
/// argued with is a detector nobody trusts.
/// </summary>
public static class LeakDetector
{
    public static IReadOnlyList<LeakFinding> Find(
        ISnapshot latest,
        IReadOnlyList<SnapshotSummary>? series = null,
        LeakDetectorOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LeakDetectorOptions();

        var trends = series is { Count: >= 2 }
            ? SeriesAnalysis.Trends(series, options.MinimalBytes / 8).ToDictionary(trend => trend.TypeName)
            : new Dictionary<string, TypeTrend>();

        var candidates = ChooseCandidates(latest, trends, options);
        var findings = new List<LeakFinding>();

        for (var i = 0; i < candidates.Count; ++i)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = candidates[i];
            progress?.Report($"Checking {i + 1} of {candidates.Count}: {candidate.TypeName}");

            trends.TryGetValue(candidate.TypeName, out var trend);

            var finding = Examine(latest, candidate, trend, options, cancellationToken);

            if (finding != null && finding.Confidence >= options.MinimalConfidence) findings.Add(finding);
        }

        // A collection holding more than it admits to is read from the fields rather than inferred,
        // so it belongs in the same list and above the findings that are only a shape and a size.
        if (options.ReadCollectionFields && latest is FullDumpSnapshot fullDump)
        {
            progress?.Report("Reading the fields of the collections...");

            foreach (var stale in StaleSlotDetector.Find(fullDump, null, progress, cancellationToken))
            {
                findings.Add(StaleSlotDetector.ToLeakFinding(stale));
            }
        }

        return findings.OrderByDescending(finding => finding.Confidence).ThenByDescending(finding => finding.Bytes).ToList();
    }

    /// <summary>
    /// What grows comes first: a type rising at every step is a better lead than a big one. When
    /// there is no series to compare, size is all there is to go on.
    /// </summary>
    private static List<TypeStatistic> ChooseCandidates(
        ISnapshot latest,
        Dictionary<string, TypeTrend> trends,
        LeakDetectorOptions options)
    {
        var byName = latest.TypeStatistics.ToDictionary(statistic => statistic.TypeName);
        var chosen = new List<TypeStatistic>();

        foreach (var trend in trends.Values
                     .Where(trend => trend.GrowthBytes > 0)
                     .OrderByDescending(trend => trend.GrowsSteadily)
                     .ThenByDescending(trend => trend.GrowthBytes))
        {
            if (!byName.TryGetValue(trend.TypeName, out var statistic)) continue;
            if (statistic.TotalBytes < options.MinimalBytes) continue;

            chosen.Add(statistic);

            if (chosen.Count >= options.CandidateCount) return chosen;
        }

        foreach (var statistic in latest.TypeStatistics)
        {
            if (statistic.TotalBytes < options.MinimalBytes) break;
            if (chosen.Any(already => already.TypeName == statistic.TypeName)) continue;

            chosen.Add(statistic);

            if (chosen.Count >= options.CandidateCount) break;
        }

        return chosen;
    }

    private static LeakFinding? Examine(
        ISnapshot latest,
        TypeStatistic candidate,
        TypeTrend? trend,
        LeakDetectorOptions options,
        CancellationToken cancellationToken)
    {
        var report = latest.AnalyzeHolders(
            new ObjectFilter { TypeNamePattern = candidate.TypeName, UseRegularExpression = false },
            new HolderAnalysisOptions { Classifier = options.Classifier },
            null,
            cancellationToken);

        if (report.ObjectCount == 0) return null;

        var applicationPaths = report.RootPaths
            .Where(path => path.Category == HolderCategory.Application)
            .ToList();

        // One holder usually shows up under several paths, because instances reach it through
        // different fields. Summing per holder is what makes the real accumulator visible; taking
        // the largest single path made a leak spread over a dozen routes look like a dozen small
        // ones, which is how the first version of this missed the leak it was written for.
        var byHolder = applicationPaths
            .GroupBy(HolderOf)
            .Select(group => (Holder: group.Key, Bytes: group.Sum(path => path.Bytes),
                              Path: group.MaxBy(path => path.Bytes)!.Frames))
            .OrderByDescending(entry => entry.Bytes)
            .ToList();

        // Three separate quantities, and confusing them is how a detector lies: what the
        // application holds, what the pool or finalization or pinning holds, and what no path
        // reached at all. The last one is not evidence of anything, least of all of the pool.
        var applicationBytes = applicationPaths.Sum(path => path.Bytes);
        var expected = report.RootPaths.Sum(path => path.Bytes) - applicationBytes;
        var attributed = applicationBytes + expected;
        var unattributed = Math.Max(0, report.TotalBytes - attributed);

        var dominant = byHolder.FirstOrDefault();
        var applicationShare = attributed == 0 ? 0 : (double)applicationBytes / attributed;
        var share = attributed == 0 ? 0 : (double)dominant.Bytes / attributed;

        var reasons = new List<string>();
        var confidence = 0d;

        if (trend != null)
        {
            if (trend.GrowsSteadily)
            {
                confidence += 0.35;
                reasons.Add($"grows at every step of the series, {ReportWriter.Bytes((long)trend.BytesPerHour)} per hour");
            }
            else if (trend.GrowthBytes > 0)
            {
                confidence += 0.15;
                reasons.Add($"larger than at the start of the series by {ReportWriter.Bytes(trend.GrowthBytes)}, but not at every step");
            }
        }

        // How much of it the application itself is keeping is the signal; through how many routes
        // it does so is not.
        if (applicationShare >= 0.5)
        {
            confidence += 0.3;
            reasons.Add($"{applicationShare:P0} is held by the application, not by the pool, finalization or pinning");
        }
        else if (applicationShare >= 0.25)
        {
            confidence += 0.15;
            reasons.Add($"{applicationShare:P0} is held by the application, not by the pool, finalization or pinning");
        }

        if (dominant.Holder != null && share >= 0.2)
        {
            confidence += 0.15;
            reasons.Add($"{share:P0} of it hangs from {dominant.Holder} alone");
        }

        if (dominant.Holder != null && LooksLikeAContainer(dominant.Holder))
        {
            confidence += 0.1;
            reasons.Add("the holder is a collection, which is where instances pile up");
        }

        if (attributed > 0 && applicationShare < 0.2)
        {
            // Free pool blocks, objects queued for finalization and pinned buffers all look like
            // retention and are none of it.
            confidence -= 0.3;
            reasons.Add($"{ReportWriter.Bytes(expected)} of it is accounted for by the pool, finalization or pinning");
        }

        // Size decides which of several suspects to look at first, so it is graded rather than a
        // single step: a type that is most of the heap is not the same lead as one that is a fiftieth.
        var shareOfHeap = latest.TotalSize == 0 ? 0 : (double)candidate.TotalBytes / latest.TotalSize;

        var forSize = shareOfHeap switch
        {
            >= 0.5 => 0.3,
            >= 0.2 => 0.2,
            >= 0.1 => 0.15,
            >= 0.05 => 0.1,
            _ => 0
        };

        if (forSize > 0)
        {
            confidence += forSize;
            reasons.Add($"{shareOfHeap:P0} of the whole heap, {ReportWriter.Bytes(candidate.TotalBytes)}");
        }

        if (unattributed > report.TotalBytes / 2)
        {
            if (latest.IsPartial)
            {
                // The heap is cut short, so no holder was found for most of it. That is a limit of
                // the file, not a fact about the objects, and must not count against them.
                reasons.Add($"the dump is truncated: no holder could be found for {ReportWriter.Bytes(unattributed)} of it");
            }
            else
            {
                confidence -= 0.2;
                reasons.Add("most of it is unreachable already and only waiting for a collection");
            }
        }

        confidence = Math.Clamp(confidence, 0, 1);

        return new LeakFinding(
            candidate.TypeName,
            dominant.Holder ?? "(no single holder)",
            dominant.Path ?? Array.Empty<string>(),
            report.ObjectCount,
            report.TotalBytes,
            trend?.BytesPerHour ?? 0,
            share,
            confidence,
            reasons,
            Recommend(dominant.Holder, trend));
    }

    /// <summary>
    /// The object that owns the collection the instances sit in, which is the thing to look at.
    ///
    /// A path starts at whatever refers to the object directly, and that is usually an array or a
    /// per instance wrapper: naming those says nothing, since there is one of them per leaked
    /// object. The first ordinary object *after* the collections is the one that has many of them.
    /// </summary>
    private static string HolderOf(RootPath path)
    {
        var frames = path.Frames;
        var sawCollection = false;

        foreach (var frame in frames)
        {
            var isCollection = LooksLikeAContainer(frame);

            if (isCollection)
            {
                sawCollection = true;
                continue;
            }

            if (sawCollection) return frame;
        }

        return path.FirstFrame;
    }

    private static bool LooksLikeAContainer(string frame)
    {
        string[] containers =
        {
            "List<", "Dictionary<", "HashSet<", "Queue<", "Stack<", "RingBuffer", "ArrayBuilder",
            "Cache", "ConcurrentDictionary<", "ConcurrentQueue<", "ConcurrentBag<", "[]"
        };

        return containers.Any(container => frame.Contains(container, StringComparison.Ordinal));
    }

    private static string Recommend(string? holder, TypeTrend? trend)
    {
        if (holder == null)
        {
            return "No single holder stands out: look at the paths to a root for this type.";
        }

        if (LooksLikeAContainer(holder))
        {
            return $"Check what removes from {holder}: a collection that only ever grows, or one that " +
                   "moves an index without clearing the slot it left, keeps every instance it ever held.";
        }

        if (trend is { GrowsSteadily: true })
        {
            return $"Check the lifetime of {holder}: it keeps more at every step, so something it owns is never released.";
        }

        return $"Check why {holder} still refers to these objects.";
    }
}
