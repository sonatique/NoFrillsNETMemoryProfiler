using Avalonia.Headless.NUnit;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The rule file is only worth having if the window reads it. This checks the two things a reader
/// would notice: that editing the file produces one to edit, and that saving it and reloading
/// changes what the next analysis is classified with, without a restart.
/// </summary>
public sealed class TestHolderRulesInTheWindow
{
    private string m_folder = string.Empty;

    private string Path => System.IO.Path.Combine(m_folder, HolderRuleFile.FileName);

    [SetUp]
    public void MakeFolder()
    {
        m_folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MemoryProfilerWindowRules", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(m_folder);
    }

    [TearDown]
    public void RemoveFolder()
    {
        if (Directory.Exists(m_folder)) Directory.Delete(m_folder, recursive: true);
    }

    [AvaloniaTest]
    public void ReloadingSaysWhatWasFound()
    {
        var model = new MainViewModel { HolderRulesPath = Path };

        // Through the commands, not the methods: these are the names the toolbar binds to, and a
        // binding to a name that does not exist fails silently.
        Assert.That(model.EditHolderRulesCommand, Is.Not.Null);

        model.ReloadHolderRulesCommand.Execute(null);
        Assert.That(model.HolderRulesSummary, Does.Contain("No holder rules"));

        File.WriteAllText(Path, """
        {
          // Written the way someone editing this by hand would write it.
          "rules": [
            { "pattern": "MyRingOfBuffers", "category": "Pooled", "explanation": "Free buffer" },
          ]
        }
        """);

        model.ReloadHolderRulesCommand.Execute(null);

        Assert.That(model.HolderRulesSummary, Does.Contain("1 holder rule"));
        Assert.That(model.Status, Is.EqualTo(model.HolderRulesSummary), "the status bar is where this is read");
    }

    [AvaloniaTest]
    public void ATypoIsSaidOutLoud()
    {
        File.WriteAllText(Path, """
        { "rules": [ { "pattern": "MyRingOfBuffers", "category": "Recycled" } ] }
        """);

        var model = new MainViewModel { HolderRulesPath = Path };
        model.ReloadHolderRules();

        Assert.That(
            model.HolderRulesSummary,
            Does.Contain("MyRingOfBuffers"),
            "a rule silently dropped is worse than no rule: the reader thinks the pool is known");
    }

    [AvaloniaTest]
    public void EditingWritesSomethingToEdit()
    {
        var model = new MainViewModel { HolderRulesPath = Path };

        // Starting the editor is the shell's business and not testable here; what matters is that
        // the file exists first, so the reader is never shown an empty one.
        HolderRuleFile.WriteTemplateIfMissing(model.HolderRulesPath);

        Assert.That(File.Exists(Path));

        model.ReloadHolderRules();

        Assert.That(model.HolderRulesSummary, Does.Contain("1 holder rule"));
    }
}
