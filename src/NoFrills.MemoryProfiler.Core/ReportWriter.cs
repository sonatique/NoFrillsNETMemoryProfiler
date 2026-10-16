using System.Text;

namespace NoFrills.MemoryProfiler.Core;

/// <summary>Writes an analysis as markdown, to paste into an issue or a review.</summary>
public static class ReportWriter
{
    public static string Write(ISnapshot snapshot, HolderReport report, HolderClassifier classifier)
    {
        var text = new StringBuilder();

        text.AppendLine($"# Heap analysis - {Path.GetFileName(snapshot.FilePath)}");
        text.AppendLine();
        if (snapshot.ProcessName != null) text.AppendLine($"- Process: {snapshot.ProcessName} on {snapshot.MachineName}");
        if (snapshot.TimeCollected != null) text.AppendLine($"- Collected: {snapshot.TimeCollected:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"- Heap: {snapshot.ObjectCount:N0} objects, {Bytes(snapshot.TotalSize)}");
        text.AppendLine();
        text.AppendLine($"## Selection: {report.Filter}");
        text.AppendLine();
        text.AppendLine($"{report.ObjectCount:N0} objects, {Bytes(report.TotalBytes)} " +
                        $"({Percent(report.TotalBytes, snapshot.TotalSize)} of the heap).");

        if (report.UnreachableCount != 0)
        {
            text.AppendLine();
            text.AppendLine($"{report.UnreachableCount:N0} objects were not reached from any root.");
        }

        text.AppendLine();
        text.AppendLine("## What holds them");
        text.AppendLine();
        text.AppendLine("| Objects | Bytes | Category | Held by |");
        text.AppendLine("|--------:|------:|----------|---------|");

        foreach (var path in report.RootPaths.Take(25))
        {
            text.AppendLine($"| {path.ObjectCount:N0} | {Bytes(path.Bytes)} | {path.Category} | `{path.FirstFrame}` |");
        }

        text.AppendLine();
        text.AppendLine($"Worth investigating: {Bytes(report.SuspiciousBytes)} held by the application.");
        text.AppendLine();
        text.AppendLine("## Paths to a root");
        text.AppendLine();

        foreach (var path in report.RootPaths.Take(15))
        {
            var explanation = classifier.Explain(path.Frames);

            text.AppendLine($"### {path.ObjectCount:N0} objects, {Bytes(path.Bytes)} - {path.Category}");

            if (explanation != null) text.AppendLine($"_{explanation}_");

            text.AppendLine();
            text.AppendLine("```");

            foreach (var frame in path.Frames) text.AppendLine(frame);

            text.AppendLine("```");
            text.AppendLine();
        }

        text.AppendLine("## Direct referrers");
        text.AppendLine();
        text.AppendLine("| References | Type |");
        text.AppendLine("|-----------:|------|");

        foreach (var referrer in report.DirectReferrers.Take(20))
        {
            text.AppendLine($"| {referrer.ReferenceCount:N0} | `{referrer.TypeName}` |");
        }

        return text.ToString();
    }

    public static string Write(GcTimeline timeline)
    {
        var text = new StringBuilder();

        text.AppendLine($"# GC timeline - {Path.GetFileName(timeline.FilePath)}");
        text.AppendLine();
        text.AppendLine($"- Recorded: {timeline.DurationMs / 1000:F1} s");
        text.AppendLine($"- Collections: {timeline.Collections.Count:N0}");
        text.AppendLine($"- Pauses: {timeline.Pauses.Count:N0}, {timeline.TotalPauseMs:F1} ms in all " +
                        $"({(timeline.DurationMs == 0 ? 0 : 100 * timeline.TotalPauseMs / timeline.DurationMs):F2} % of the time)");
        text.AppendLine($"- Allocated: {Bytes(timeline.AllocatedBytes)}, sampled about every 100 KB");
        text.AppendLine();
        text.AppendLine("## Pauses by generation");
        text.AppendLine();
        text.AppendLine("| Generation | Pauses | Total ms | p50 ms | p95 ms | Max ms | Triggered by |");
        text.AppendLine("|------------|-------:|---------:|-------:|-------:|-------:|--------------|");

        foreach (var pauses in timeline.PausesByGeneration)
        {
            text.AppendLine($"| {pauses.Generation} | {pauses.Count:N0} | {pauses.TotalMs:F1} | {pauses.P50Ms:F2} | " +
                            $"{pauses.P95Ms:F2} | {pauses.MaxMs:F2} | {pauses.TriggeredBy} |");
        }

        text.AppendLine();
        text.AppendLine("## Longest pauses");
        text.AppendLine();
        text.AppendLine("| At s | Pause ms | Generation | Trigger |");
        text.AppendLine("|-----:|---------:|------------|---------|");

        foreach (var pause in timeline.Pauses.OrderByDescending(pause => pause.DurationMs).Take(25))
        {
            text.AppendLine($"| {pause.StartMs / 1000:F3} | {pause.DurationMs:F2} | {pause.Generation} | {pause.Gc?.Reason} |");
        }

        AppendAllocations(text, "Allocations by type", "Type", timeline.AllocationsByType, timeline.AllocatedBytes);
        AppendAllocations(text, "Allocations by thread", "Thread", timeline.AllocationsByThread, timeline.AllocatedBytes);
        AppendAllocations(text, "Allocations by stack", "First application frames", timeline.AllocationsByStack, timeline.AllocatedBytes);

        return text.ToString();
    }

    private static void AppendAllocations(StringBuilder text, string title, string column, IReadOnlyList<AllocationGroup> groups, long total)
    {
        text.AppendLine();
        text.AppendLine($"## {title}");
        text.AppendLine();
        text.AppendLine($"| Bytes | Share | Samples | {column} |");
        text.AppendLine("|------:|------:|--------:|---------|");

        foreach (var group in groups.Take(25))
        {
            // A stack is one frame per line, which a table cell can only hold as breaks. The double
            // backticks keep the one of a generic name ("List`1") from ending the code span.
            var name = string.Join("<br>", group.Name.Split(Environment.NewLine).Select(frame => $"`` {frame} ``"));

            text.AppendLine($"| {Bytes(group.Bytes)} | {Percent(group.Bytes, total)} | {group.Samples:N0} | {name} |");
        }
    }

    public static string Bytes(long value)
    {
        if (value >= 1024L * 1024 * 1024) return $"{value / (1024d * 1024 * 1024):F2} GB";
        if (value >= 1024L * 1024) return $"{value / (1024d * 1024):F1} MB";
        if (value >= 1024) return $"{value / 1024d:F1} KB";

        return $"{value} B";
    }

    public static string Percent(long part, long total) => total == 0 ? "0 %" : $"{100d * part / total:F1} %";
}
