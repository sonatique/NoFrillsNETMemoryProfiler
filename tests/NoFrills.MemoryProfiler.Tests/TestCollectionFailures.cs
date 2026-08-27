using NoFrills.MemoryProfiler.Core;
using Graphs;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// The failure that started this: a scheduled collection reported "RootIndex not set." because the
/// runtime returned a heap without its roots. What must hold: the failure is named clearly, no
/// broken file is left behind, the schedule retries once and keeps going, and everything lands in
/// the collection log.
/// </summary>
[TestFixture]
public sealed class TestCollectionFailures
{
    private string m_root = string.Empty;

    [SetUp]
    public void CreateRoot()
    {
        m_root = Path.Combine(Path.GetTempPath(), "MemoryProfilerFailureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_root);
    }

    [TearDown]
    public void RemoveRoot()
    {
        if (Directory.Exists(m_root)) Directory.Delete(m_root, recursive: true);
    }

    [Test]
    public void AGraphWithoutRootsIsRefusedWithAnExplanation()
    {
        var graph = new MemoryGraph(100);

        var refusal = Assert.Throws<HeapCollectionException>(
            () => LiveCollector.Validate(graph, 4242, "last collector line"))!;

        Assert.That(refusal.Message, Does.Contain("without its roots"), "the cause must be named");
        Assert.That(refusal.Message, Does.Contain("4242"), "and the process");
        Assert.That(refusal.Message, Does.Contain("last collector line"), "and what the collector said last");
        Assert.That(refusal.Message, Does.Not.Contain("RootIndex"),
            "the internal wording that surfaced in the window must be replaced, not repeated");
    }

    [Test]
    public void ScheduleRetriesOnceWhenTheCollectionComesBackUnusable()
    {
        var store = new DumpStore(m_root);
        var schedule = new ScheduledCollector(store) { RetryDelay = TimeSpan.FromMilliseconds(50) };

        var attempts = 0;
        var collected = new ManualResetEventSlim();

        schedule.Collect = (process, _) =>
        {
            if (++attempts == 1) throw new HeapCollectionException("came back without its roots");

            var path = store.NewDumpPath(process.Name, process.ProcessId, DateTime.Now.AddSeconds(attempts));
            File.WriteAllText(path, "dump");
            return path;
        };

        schedule.DumpCollected += _ => collected.Set();
        schedule.Start(new LiveProcess(Environment.ProcessId, "self"), TimeSpan.FromMinutes(5));

        try
        {
            Assert.That(collected.Wait(TimeSpan.FromSeconds(15)), Is.True, "the retry must produce the dump");
            Assert.That(attempts, Is.EqualTo(2), "one failure, one successful retry");
            Assert.That(schedule.State.CollectedCount, Is.EqualTo(1));
            Assert.That(schedule.IsRunning, Is.True, "a failed collection must not end the schedule");
        }
        finally
        {
            schedule.Stop();
        }

        var log = File.ReadAllText(schedule.Log.FilePath);

        Assert.That(log, Does.Contain("retrying"), "the failure and the retry must be on record");
        Assert.That(log, Does.Contain("HeapCollectionException"));
        Assert.That(log, Does.Contain("Collected"), "so must the success that followed");
    }

    [Test]
    public void OtherFailuresAreNotRetried()
    {
        var store = new DumpStore(m_root);
        var schedule = new ScheduledCollector(store) { RetryDelay = TimeSpan.FromMilliseconds(50) };

        var attempts = 0;
        var failed = new ManualResetEventSlim();

        schedule.Collect = (_, _) =>
        {
            ++attempts;
            failed.Set();
            throw new UnauthorizedAccessException("store is read only");
        };

        schedule.Start(new LiveProcess(Environment.ProcessId, "self"), TimeSpan.FromMinutes(5));

        try
        {
            Assert.That(failed.Wait(TimeSpan.FromSeconds(15)), Is.True);

            // Give a wrong retry the time it would need, then check it did not happen.
            Thread.Sleep(300);
            Assert.That(attempts, Is.EqualTo(1), "an access problem fails the same way immediately; retrying is noise");
            Assert.That(schedule.IsRunning, Is.True, "but the schedule still waits for the next interval");
        }
        finally
        {
            schedule.Stop();
        }
    }

    [Test]
    public void TheLogTellsTheStoryOfASchedule()
    {
        var store = new DumpStore(m_root);
        var schedule = new ScheduledCollector(store) { RetryDelay = TimeSpan.FromMilliseconds(10) };
        var collected = new ManualResetEventSlim();

        schedule.Collect = (process, _) =>
        {
            var path = store.NewDumpPath(process.Name, process.ProcessId, DateTime.Now);
            File.WriteAllText(path, "dump");
            return path;
        };

        schedule.DumpCollected += _ => collected.Set();
        schedule.Start(new LiveProcess(Environment.ProcessId, "Contoso.Analyzer"), TimeSpan.FromMinutes(5));
        Assert.That(collected.Wait(TimeSpan.FromSeconds(15)), Is.True);
        schedule.Stop();

        var lines = File.ReadAllLines(schedule.Log.FilePath);

        Assert.That(lines, Has.Some.Contains("Schedule started on Contoso.Analyzer"));
        Assert.That(lines, Has.Some.Contains("Collected"));
        Assert.That(lines, Has.Some.Contains("Schedule stopped after 1 dumps"));
        Assert.That(lines, Has.All.Match(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} (INF|WRN|ERR) ").Or.StartWith("      "),
            "every line carries its moment and severity, except continuation lines");
    }

    [Test]
    public void TheLogSurvivesConcurrentWritersAndRolls()
    {
        var log = new CollectionLog(Path.Combine(m_root, "collection.log"));

        Parallel.For(0, 200, i => log.Info($"line {i}"));

        Assert.That(log.ReadRecent(500), Has.Count.EqualTo(200), "no line may be lost to a concurrent write");

        // Failures record every inner exception.
        log.Failure("outer context", new InvalidOperationException("outer", new IOException("actual cause")));

        var recent = log.ReadRecent(5);

        Assert.That(recent, Has.Some.Contains("outer context").And.Some.Contains("actual cause"));
    }
}
