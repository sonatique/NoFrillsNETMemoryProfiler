using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The process list filters as one types and refreshes itself while it is on screen. The list of
/// processes is replaced here so the behaviour can be checked without depending on what happens to
/// be running on the machine.
/// </summary>
public sealed class TestProcessList
{
    private static readonly LiveProcess[] Running =
    {
        new(100, "Contoso.Analyzer"),
        new(200, "Contoso.Daemon"),
        new(300, "devenv"),
        new(400, "dotnet"),
    };

    private static MainViewModel ModelWith(params LiveProcess[] processes)
    {
        var model = new MainViewModel { ProcessSource = () => processes };

        return model;
    }

    private static async Task<MainViewModel> LoadedModel(params LiveProcess[] processes)
    {
        var model = ModelWith(processes);
        await model.RefreshProcessesCommand.ExecuteAsync(null);

        return model;
    }

    [AvaloniaTest]
    public async Task EverythingIsListedWhenNothingIsTyped()
    {
        var model = await LoadedModel(Running);

        Assert.That(model.Processes, Has.Count.EqualTo(4));
        Assert.That(model.ProcessCount, Is.EqualTo("4 processes"));
        Assert.That(model.Processes.Select(process => process.Name), Is.Ordered, "sorted by name");
    }

    [AvaloniaTest]
    public async Task TypingNarrowsTheListToMatchingNames()
    {
        var model = await LoadedModel(Running);

        model.ProcessFilter = "contoso";

        Assert.That(model.Processes.Select(process => process.ProcessId), Is.EqualTo(new[] { 100, 200 }),
            "matching is case insensitive");
        Assert.That(model.ProcessCount, Is.EqualTo("2 of 4 processes"));

        model.ProcessFilter = "daemon";
        Assert.That(model.Processes.Single().ProcessId, Is.EqualTo(200));

        model.ProcessFilter = string.Empty;
        Assert.That(model.Processes, Has.Count.EqualTo(4), "clearing the box brings everything back");
    }

    [AvaloniaTest]
    public async Task AProcessCanAlsoBeFoundByItsIdentifier()
    {
        var model = await LoadedModel(Running);

        model.ProcessFilter = "300";

        Assert.That(model.Processes.Single().Name, Is.EqualTo("devenv"));
    }

    [AvaloniaTest]
    public async Task NothingMatchingLeavesAnEmptyList()
    {
        var model = await LoadedModel(Running);

        model.ProcessFilter = "no such process";

        Assert.That(model.Processes, Is.Empty);
        Assert.That(model.ProcessCount, Is.EqualTo("0 of 4 processes"));
    }

    [AvaloniaTest]
    public async Task RefreshingKeepsTheSelectionAndTheRowsThatDidNotChange()
    {
        var processes = new List<LiveProcess>(Running);
        var model = new MainViewModel { ProcessSource = () => processes };

        await model.RefreshProcessesCommand.ExecuteAsync(null);

        var selected = model.Processes.Single(process => process.ProcessId == 200);
        model.SelectedProcess = selected;

        var rowBefore = model.Processes.Single(process => process.ProcessId == 400);

        // One process ends, another starts, between two refreshes.
        processes.RemoveAll(process => process.ProcessId == 300);
        processes.Add(new LiveProcess(500, "notepad"));

        await model.RefreshProcessesCommand.ExecuteAsync(null);

        Assert.That(model.SelectedProcess, Is.EqualTo(selected),
            "a list that refreshes twice a second must not drop what the user picked");
        Assert.That(model.Processes.Single(process => process.ProcessId == 400), Is.SameAs(rowBefore),
            "rows that did not change must be kept, not replaced");
        Assert.That(model.Processes.Select(process => process.ProcessId), Does.Not.Contain(300));
        Assert.That(model.Processes.Select(process => process.ProcessId), Does.Contain(500));
        Assert.That(model.Processes.Select(process => process.Name), Is.Ordered, "still sorted after the update");
    }

    [AvaloniaTest]
    public async Task ANewProcessAppearsThroughTheFilterOnlyWhenItMatches()
    {
        var processes = new List<LiveProcess>(Running);
        var model = new MainViewModel { ProcessSource = () => processes };

        await model.RefreshProcessesCommand.ExecuteAsync(null);
        model.ProcessFilter = "contoso";

        processes.Add(new LiveProcess(600, "notepad"));
        processes.Add(new LiveProcess(700, "Contoso.Grid"));

        await model.RefreshProcessesCommand.ExecuteAsync(null);

        Assert.That(model.Processes.Select(process => process.ProcessId), Is.EqualTo(new[] { 100, 200, 700 }),
            "only the matching newcomer shows up, in its place");
        Assert.That(model.Processes.Select(process => process.Name), Is.Ordered);
    }

    [AvaloniaTest]
    public void TheListWatchesItselfOnlyWhileItsTabIsShown()
    {
        var model = ModelWith(Running);

        Assert.That(model.IsWatchingProcesses, Is.False, "nothing to watch before the tab is opened");

        model.SelectedTabIndex = MainViewModel.LiveProcessesTabIndex;
        Assert.That(model.IsWatchingProcesses, Is.True);

        model.SelectedTabIndex = 0;
        Assert.That(model.IsWatchingProcesses, Is.False, "watching a hidden list costs for nothing");
    }

    [AvaloniaTest]
    public async Task ShowingTheTabListsTheProcessesWithoutWaitingForTheFirstTick()
    {
        var asked = 0;
        var model = new MainViewModel { ProcessSource = () => { ++asked; return Running; } };

        model.SelectedTabIndex = MainViewModel.LiveProcessesTabIndex;
        await model.ProcessRefreshCompleted;

        Assert.That(asked, Is.GreaterThanOrEqualTo(1), "the list must be filled as soon as it is shown");
        Assert.That(model.Processes, Is.Not.Empty);
    }

    [AvaloniaTest]
    public void TheWatchedTabIndexIsTheProcessTab()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow();
        window.Show();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var items = tabs.GetVisualDescendants().OfType<TabItem>().ToList();

        Assert.That(
            items[MainViewModel.LiveProcessesTabIndex].Header?.ToString(),
            Is.EqualTo("Live processes"),
            "the index the view model watches must be the tab holding the list");
    }
}
