using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// On a full dump the leak list gains what the fields say, and what the fields say outranks what
/// size and shape suggest.
/// </summary>
[TestFixture]
public sealed class TestLeakDetectorOnFullDump
{
    private static string SleeperDirectory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "NoFrills.MemoryProfiler.Sleeper"));

    [Test]
    public void TheCollectionKeepingWhatItRemovedIsListedFirst()
    {
        if (!Directory.Exists(SleeperDirectory)) Assert.Ignore($"sleeper project not found: {SleeperDirectory}");

        var store = new DumpStore(Path.Combine(Path.GetTempPath(), "MemoryProfilerFullLeak", Guid.NewGuid().ToString("N")));
        string dumpPath;

        using (var handle = Process.Start(new ProcessStartInfo("dotnet", "run --project .")
               {
                   WorkingDirectory = SleeperDirectory,
                   RedirectStandardOutput = true,
                   UseShellExecute = false
               })!)
        {
            var target = TestTarget.Announced(handle);

            try
            {
                dumpPath = FullDumpCollector.Collect(target, store);
            }
            finally
            {
                if (!handle.HasExited) handle.Kill(entireProcessTree: true);
            }
        }

        try
        {
            using var snapshot = FullDumpSnapshot.Load(dumpPath);

            var findings = LeakDetector.Find(
                snapshot,
                null,
                new LeakDetectorOptions { MinimalBytes = 1024 * 1024 });

            foreach (var finding in findings.Take(6))
            {
                TestContext.Out.WriteLine($"{finding.Confidence:F2} {finding.TypeName} held by {finding.HeldBy}");

                foreach (var reason in finding.Reasons) TestContext.Out.WriteLine($"      - {reason}");
            }

            Assert.That(findings, Is.Not.Empty);
            Assert.That(findings[0].TypeName, Does.Contain("LeakyQueue"),
                "evidence read from the fields must come before evidence guessed from size");
            Assert.That(findings[0].Recommendation, Does.Contain("Clear the slot"));
            Assert.That(
                findings.Select(finding => finding.TypeName),
                Has.None.Contains("TidyQueue"),
                "and the healthy queue must still not be accused");
        }
        finally
        {
            var folder = Path.GetDirectoryName(Path.GetDirectoryName(dumpPath));
            if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
