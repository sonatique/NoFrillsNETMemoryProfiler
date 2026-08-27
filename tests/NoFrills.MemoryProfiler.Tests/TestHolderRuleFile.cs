using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Checks the file where the reader names their own pools.
///
/// This is the one piece of knowledge the profiler cannot have: a pool written for one application
/// is recognisable only to whoever wrote it, and left unrecognised it is reported as the largest
/// leak in the heap on every run. So the file has to be forgiving. A typo in it must cost the one
/// rule that has the typo, never the analysis.
/// </summary>
public sealed class TestHolderRuleFile
{
    private string m_folder = string.Empty;

    private string Path => System.IO.Path.Combine(m_folder, HolderRuleFile.FileName);

    [SetUp]
    public void MakeFolder()
    {
        m_folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MemoryProfilerRules", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(m_folder);
    }

    [TearDown]
    public void RemoveFolder()
    {
        if (Directory.Exists(m_folder)) Directory.Delete(m_folder, recursive: true);
    }

    [Test]
    public void NoFileMeansNoRulesAndNoComplaint()
    {
        var rules = HolderRuleFile.Load(Path);

        Assert.That(rules.Rules, Is.Empty);
        Assert.That(rules.Problems, Is.Empty, "a profiler that has never been configured is not misconfigured");
    }

    [Test]
    public void RulesAreRead()
    {
        Save("""
        {
          "rules": [
            { "pattern": "MyRingOfBuffers", "category": "Pooled", "explanation": "Free buffer waiting in the ring" },
            { "pattern": "MyCounters", "category": "instrumentation" }
          ]
        }
        """);

        var rules = HolderRuleFile.Load(Path);

        Assert.That(rules.Problems, Is.Empty);
        Assert.That(rules.Rules, Has.Count.EqualTo(2));

        Assert.That(rules.Rules[0].Pattern, Is.EqualTo("MyRingOfBuffers"));
        Assert.That(rules.Rules[0].Category, Is.EqualTo(HolderCategory.Pooled));
        Assert.That(rules.Rules[0].Explanation, Is.EqualTo("Free buffer waiting in the ring"));

        Assert.That(
            rules.Rules[1].Category,
            Is.EqualTo(HolderCategory.Instrumentation),
            "the category is what it says, whatever case it says it in");

        Assert.That(rules.Rules[1].Explanation, Is.Not.Empty, "a rule without wording still needs something to show");
    }

    [Test]
    public void ThePoolOfTheApplicationIsRecognisedOnceItIsNamed()
    {
        var frames = new[] { "MyApp.Buffers.MyRingOfBuffers", "MyApp.Engine" };

        Assert.That(
            HolderClassifier.Default.Classify(frames),
            Is.EqualTo(HolderCategory.Application),
            "unnamed, the pool looks exactly like a leak, which is the problem this file solves");

        Save("""
        { "rules": [ { "pattern": "MyRingOfBuffers", "category": "Pooled", "explanation": "Free buffer" } ] }
        """);

        var classifier = HolderRuleFile.Classifier(Path);

        Assert.That(classifier.Classify(frames), Is.EqualTo(HolderCategory.Pooled));
        Assert.That(classifier.Explain(frames), Is.EqualTo("Free buffer"));

        Assert.That(
            classifier.Classify(new[] { "System.Buffers.ArrayPool<Byte>+Bucket" }),
            Is.EqualTo(HolderCategory.Pooled),
            "what is added is added to the defaults, it does not replace them");
    }

    [Test]
    public void WhatTheReaderSaysBeatsWhatTheDefaultsGuess()
    {
        var frames = new[] { "MyApp.Buffers.FixedSizeArrayPool+BlockAge" };

        Assert.That(
            HolderClassifier.Default.Explain(frames),
            Is.EqualTo("Free buffer waiting in the pool"),
            "the built-in rule matches on the ArrayPool fragment, which is a guess about a name");

        Save("""
        {
          "rules": [
            { "pattern": "FixedSizeArrayPool", "category": "Pooled", "explanation": "Free block waiting in the pool" }
          ]
        }
        """);

        Assert.That(
            HolderRuleFile.Classifier(Path).Explain(frames),
            Is.EqualTo("Free block waiting in the pool"),
            "the reader named their own type, so their wording is the one shown");
    }

    [Test]
    public void ARuleCanContradictADefault()
    {
        var frames = new[] { "MyApp.MyObjectPoolWrapper" };

        Assert.That(HolderClassifier.Default.Classify(frames), Is.EqualTo(HolderCategory.Pooled));

        Save("""
        { "rules": [ { "pattern": "MyObjectPoolWrapper", "category": "Application" } ] }
        """);

        Assert.That(
            HolderRuleFile.Classifier(Path).Classify(frames),
            Is.EqualTo(HolderCategory.Application),
            "a name that only looks like a pool must be sayable, or the leak in it can never be reported");
    }

    [Test]
    public void AWrongCategoryCostsOnlyItsOwnRule()
    {
        Save("""
        {
          "rules": [
            { "pattern": "MyRingOfBuffers", "category": "Recycled" },
            { "pattern": "MyCache", "category": "Instrumentation" }
          ]
        }
        """);

        var rules = HolderRuleFile.Load(Path);

        Assert.That(rules.Rules.Select(rule => rule.Pattern), Is.EqualTo(new[] { "MyCache" }));
        Assert.That(rules.Problems, Has.Count.EqualTo(1));
        Assert.That(rules.Problems[0], Does.Contain("MyRingOfBuffers").And.Contain("Recycled"));
        Assert.That(rules.Problems[0], Does.Contain("Pooled"), "saying what is wrong is not enough, say what is allowed");
    }

    [Test]
    public void AFileThatIsNotJsonIsReportedRatherThanThrown()
    {
        Save("this is not json at all");

        var rules = HolderRuleFile.Load(Path);

        Assert.That(rules.Rules, Is.Empty);
        Assert.That(rules.Problems, Has.Count.EqualTo(1));
        Assert.That(rules.Problems[0], Does.Contain(HolderRuleFile.FileName));
    }

    [Test]
    public void TheTemplateExplainsItselfAndParses()
    {
        var written = HolderRuleFile.WriteTemplateIfMissing(Path);

        Assert.That(written, Is.EqualTo(Path));

        var text = File.ReadAllText(Path);
        Assert.That(text, Does.Contain("Pooled"), "the categories have to be in the file, nobody will look them up");

        var rules = HolderRuleFile.Load(Path);

        Assert.That(rules.Problems, Is.Empty, "the example the reader is shown must itself be valid");
        Assert.That(rules.Rules, Has.Count.EqualTo(1));
    }

    [Test]
    public void TheTemplateNeverOverwritesWhatTheReaderWrote()
    {
        Save("""
        { "rules": [ { "pattern": "Mine", "category": "Pooled" } ] }
        """);

        HolderRuleFile.WriteTemplateIfMissing(Path);

        Assert.That(HolderRuleFile.Load(Path).Rules.Single().Pattern, Is.EqualTo("Mine"));
    }

    private void Save(string content) => File.WriteAllText(Path, content);
}
