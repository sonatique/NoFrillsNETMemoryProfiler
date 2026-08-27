using Avalonia.Headless.NUnit;
using NoFrills.MemoryProfiler.App.ViewModels;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The reported empty "What holds them" view, replayed as the user drove it: open a dump, filter
/// the type list, select a row, press "Selected type".
/// </summary>
public sealed class TestHolderViewFromTypesTab
{
    /// <summary>What the reference dumps hold, kept beside them rather than written down here.</summary>
    private static NoFrills.MemoryProfiler.Tests.ReferenceExpectations Expected =>
        NoFrills.MemoryProfiler.Tests.ReferenceExpectations.Find()
        ?? throw new IgnoreException(NoFrills.MemoryProfiler.Tests.ReferenceExpectations.WhereToPutIt());

    [AvaloniaTest]
    public async Task PickingATypeAndAnalyzingFillsTheHolderView()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries);

        if (path == null) Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries));

        var model = new MainViewModel { ProcessSource = () => Array.Empty<NoFrills.MemoryProfiler.Core.LiveProcess>() };

        await model.LoadDumpAsync(path!, asBaseline: false);

        model.TypeFilter = Expected.TypeToInspect;
        Assert.That(model.Types, Is.Not.Empty, "the filtered list must offer the type");

        model.SelectedType = model.Types.First(row => row.TypeName == Expected.TypeToInspect);

        await model.AnalyzeSelectionCommand.ExecuteAsync(null);

        Assert.That(model.HolderSummary, Is.Not.Empty, "the summary must always say what happened");
        Assert.That(model.RootPaths, Is.Not.Empty, "the holders of an existing type cannot be empty");
        Assert.That(model.DirectReferrers, Is.Not.Empty);
    }

    [AvaloniaTest]
    public async Task AnalyzingWithoutASelectedTypeExplainsInsteadOfScanningEverything()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries);

        if (path == null) Assert.Ignore(NoFrills.MemoryProfiler.Tests.ReferenceDumps.WhereToPutIt(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.AnalyzerSeries));

        var model = new MainViewModel { ProcessSource = () => Array.Empty<NoFrills.MemoryProfiler.Core.LiveProcess>() };

        await model.LoadDumpAsync(path!, asBaseline: false);

        // In the real window the grid drops its selection whenever the list rebuilds; the view
        // model restores it while the row still matches, and this is the case where it cannot.
        model.TypeFilter = Expected.TypeToInspect;
        model.SelectedType = model.Types.First();
        model.TypeFilter = "something else entirely";
        model.SelectedType = null;

        await model.AnalyzeSelectionCommand.ExecuteAsync(null);

        Assert.That(model.HolderSummary, Does.Contain("Choose a type"),
            "no selection must produce guidance, not an analysis of the whole heap");
    }
}
