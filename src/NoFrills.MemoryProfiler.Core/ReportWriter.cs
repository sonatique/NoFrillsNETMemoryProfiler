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

    public static string Bytes(long value)
    {
        if (value >= 1024L * 1024 * 1024) return $"{value / (1024d * 1024 * 1024):F2} GB";
        if (value >= 1024L * 1024) return $"{value / (1024d * 1024):F1} MB";
        if (value >= 1024) return $"{value / 1024d:F1} KB";

        return $"{value} B";
    }

    public static string Percent(long part, long total) => total == 0 ? "0 %" : $"{100d * part / total:F1} %";
}
