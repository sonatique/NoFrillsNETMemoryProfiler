using Avalonia.Headless.NUnit;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The reported empty view: a type was selected in the Types tab, which writes the title of the
/// holder tab, but nothing ran the analysis. The title named the type above two empty tables and
/// no message said why.
/// </summary>
public sealed class TestHolderTabBehaviour
{
    /// <summary>What the reference dumps hold, kept beside them rather than written down here.</summary>
    private static NoFrills.MemoryProfiler.Tests.ReferenceExpectations Expected =>
        NoFrills.MemoryProfiler.Tests.ReferenceExpectations.Find()
        ?? throw new IgnoreException(NoFrills.MemoryProfiler.Tests.ReferenceExpectations.WhereToPutIt());

    private static async Task<MainViewModel> Loaded()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries);

        if (path == null)
        {
            Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
                NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries));
        }

        var model = new MainViewModel { ProcessSource = () => Array.Empty<LiveProcess>() };
        await model.LoadDumpAsync(path!, asBaseline: false);

        return model;
    }

    private static void Select(MainViewModel model, string typeNamePart)
    {
        model.TypeFilter = typeNamePart;
        model.SelectedType = model.Types.First(row => row.TypeName.Contains(typeNamePart));
    }

    [AvaloniaTest]
    public async Task ChoosingATypeShowsWhatHoldsItWithoutLeavingTheView()
    {
        var model = await Loaded();
        model.SelectionAnalysisDelay = TimeSpan.Zero;

        Select(model, Expected.TypeToInspect);
        await model.AnalysisCompleted;

        Assert.That(model.RootPaths, Is.Not.Empty, "the answer must appear next to the list, not a tab away");
        Assert.That(model.DirectReferrers, Is.Not.Empty);
        Assert.That(model.HolderSummary, Does.Contain("objects"));
        Assert.That(model.SelectedTabIndex, Is.EqualTo(MainViewModel.ExploreTabIndex),
            "and without moving the user anywhere");
    }

    [AvaloniaTest]
    public async Task ChoosingAnotherTypeDropsTheAnswerThatDescribedThePreviousOne()
    {
        var model = await Loaded();

        model.SelectionAnalysisDelay = TimeSpan.Zero;

        Select(model, Expected.TypeToInspect);
        await model.AnalysisCompleted;

        var first = model.RootPaths.Count;
        Assert.That(first, Is.Not.Zero);

        Select(model, "System.String");

        // The tables must never describe one type under the title of another, not even for the
        // moment it takes to compute the new answer.
        Assert.That(model.SelectionDescription, Does.Contain("System.String"));

        await model.AnalysisCompleted;

        Assert.That(model.HolderSummary, Does.Contain("objects"));
    }

    [AvaloniaTest]
    public async Task ReopeningTheTabDoesNotAnalyzeTheSameThingAgain()
    {
        var model = await Loaded();

        model.SelectionAnalysisDelay = TimeSpan.Zero;

        Select(model, Expected.TypeToInspect);
        await model.AnalysisCompleted;

        var summary = model.HolderSummary;

        model.SelectedTabIndex = 2;
        model.SelectedTabIndex = MainViewModel.ExploreTabIndex;
        await model.AnalysisCompleted;

        Assert.That(model.HolderSummary, Is.EqualTo(summary), "the answer is already there");
    }

    [AvaloniaTest]
    public async Task ADumpAtTheCollectorLimitIsFlagged()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find("20260821_171602_49388.gcdump");

        if (path == null) Assert.Ignore("truncated reference dump not available");

        var model = new MainViewModel { ProcessSource = () => Array.Empty<LiveProcess>() };
        await model.LoadDumpAsync(path!, asBaseline: false);

        Assert.That(model.IsTruncated, Is.True,
            "a dump stopped at ten million objects must not look like a complete one");
        Assert.That(model.Status, Does.Contain("full dump"), "and must say what to do about it");
    }
}
