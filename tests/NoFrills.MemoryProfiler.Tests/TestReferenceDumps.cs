using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// The tests that read real dumps must run on a machine that has them and be ignored, not failed,
/// on one that does not. This checks the resolver both ways.
/// </summary>
[TestFixture]
public sealed class TestReferenceDumps
{
    [Test]
    public void ADumpThatIsNowhereIsReportedAsMissing()
    {
        Assert.That(ReferenceDumps.Find("no-such-dump-4f2a1c.gcdump"), Is.Null);
    }

    [Test]
    public void TheMissingMessageSaysHowToRunTheTest()
    {
        var message = ReferenceDumps.WhereToPutIt(ReferenceDumps.PoolRetention);

        Assert.That(message, Does.Contain(ReferenceDumps.FolderVariable).And.Contain(ReferenceDumps.PoolRetention));
    }

    [Test]
    public void TheConfiguredFolderIsSearchedIncludingBelowIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MemoryProfilerDumpLookup", Guid.NewGuid().ToString("N"));
        var below = Path.Combine(folder, "SomeProcess");

        Directory.CreateDirectory(below);
        File.WriteAllText(Path.Combine(below, ReferenceDumps.PoolRetention), "stand in for a dump");

        var previous = Environment.GetEnvironmentVariable(ReferenceDumps.FolderVariable);
        Environment.SetEnvironmentVariable(ReferenceDumps.FolderVariable, folder);

        try
        {
            var found = ReferenceDumps.Find(ReferenceDumps.PoolRetention);

            Assert.That(found, Is.Not.Null);
            Assert.That(found, Does.StartWith(folder),
                "the configured folder must win over the folders used while the profiler was written");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReferenceDumps.FolderVariable, previous);
            Directory.Delete(folder, recursive: true);
        }
    }
}
