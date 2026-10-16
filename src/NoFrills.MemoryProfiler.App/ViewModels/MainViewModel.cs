using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoFrills.MemoryProfiler.Core;

namespace NoFrills.MemoryProfiler.App.ViewModels;

/// <summary>
/// Drives the whole window. Loading a dump and analyzing it take seconds on a large heap, so
/// every such step runs on a worker thread and reports progress; the commands are disabled while
/// one is running.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private ISnapshot? m_snapshot;
    private ISnapshot? m_baseline;
    private HolderReport? m_report;
    private CancellationTokenSource? m_running;

    private readonly ProfilerSettings m_settings = ProfilerSettings.Load();
    private readonly ScheduledCollector m_schedule;

    private HolderClassifier m_classifier = HolderClassifier.Default;

    private readonly List<LiveProcess> m_allProcesses = new();
    private readonly DispatcherTimer m_processWatch;
    private bool m_refreshingProcesses;
    private Task m_pendingProcessRefresh = Task.CompletedTask;

    /// <summary>Index of the tab holding the process list, watched by <see cref="IsWatchingProcesses"/>.</summary>
    public const int LiveProcessesTabIndex = 3;

    /// <summary>
    /// Index of the tab where a set of objects is chosen and what holds it is shown, side by side.
    /// Choosing and answering are one activity and belong in one place.
    /// </summary>
    public const int ExploreTabIndex = 0;

    /// <summary>Index of the tab ranking what is most likely to be leaking.</summary>
    public const int LeakTabIndex = 1;

    /// <summary>Index of the tab showing a recording of the garbage collector.</summary>
    public const int GcTimelineTabIndex = 5;

    /// <summary>Where the process list comes from; replaced in tests.</summary>
    public Func<IReadOnlyList<LiveProcess>> ProcessSource { get; set; } = LiveCollector.GetProfilableProcesses;

    /// <summary>
    /// How "Collect and open" takes a dump into a store, full or not; replaced in tests, which
    /// collect into a folder of their own rather than the one the settings name.
    /// </summary>
    public Func<LiveProcess, DumpStore, bool, IProgress<string>, CancellationToken, string> CollectDump { get; set; } =
        (process, store, fullDump, progress, token) => fullDump
            ? FullDumpCollector.Collect(process, store, progress, token)
            : LiveCollector.Collect(process, store, TimeSpan.FromMinutes(5), progress, token);

    /// <summary>True while the list refreshes on its own, which it only does when it is on screen.</summary>
    public bool IsWatchingProcesses => m_processWatch.IsEnabled;

    /// <summary>Completes when the refresh started on its own has finished; awaited by tests.</summary>
    public Task ProcessRefreshCompleted => m_pendingProcessRefresh;

    public MainViewModel()
    {
        m_store = new DumpStore(m_settings.DumpRootFolder);
        m_schedule = new ScheduledCollector(m_store);

        ReloadHolderRules();

        m_schedule.StateChanged += state => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowScheduleState(state));
        m_schedule.Log.Written += line => Avalonia.Threading.Dispatcher.UIThread.Post(() => AddActivity(line));
        m_schedule.DumpCollected += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Evolution.RefreshSessions(m_store));

        Evolution.OpenDumpRequested += path => _ = LoadDumpAsync(path, asBaseline: false);
        Evolution.RefreshSessions(m_store);

        IntervalMinutes = m_settings.DefaultIntervalMinutes;
        KeepAtMost = m_settings.KeepAtMostPerProcess;

        foreach (var line in m_schedule.Log.ReadRecent(RecentActivityCount)) AddActivity(line);

        UseFullDumps = m_settings.UseFullDumps;
        UseProfilerTheme = m_settings.UseProfilerTheme;

        // Processes come and go while the list is on screen; refreshing it only while it is
        // visible keeps the cost where it is useful.
        m_processWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        m_processWatch.Tick += (_, _) => m_pendingProcessRefresh = RefreshProcessesAsync();
    }

    private DumpStore m_store;

    public EvolutionViewModel Evolution { get; } = new();

    public GcTimelineViewModel Timeline { get; } = new();

    [ObservableProperty] private string m_status = "Open a .gcdump file, or collect one from a running process.";
    [ObservableProperty] private string m_snapshotDescription = "No dump loaded";
    [ObservableProperty] private string m_baselineDescription = "No comparison loaded";
    [ObservableProperty] private bool m_isBusy;
    [ObservableProperty] private string m_typeFilter = string.Empty;
    [ObservableProperty] private TypeRow? m_selectedType;
    [ObservableProperty] private string m_selectionDescription = "Nothing selected";
    [ObservableProperty] private string m_holderSummary = "Choose a type on the left, or one of the presets.";

    /// <summary>What the shown report describes, to tell an answer apart from a leftover one.</summary>
    private string? m_reportedSelection;
    [ObservableProperty] private RootPathRow? m_selectedRootPath;

    public ObservableCollection<TypeRow> Types { get; } = new();
    public ObservableCollection<RootPathRow> RootPaths { get; } = new();
    public ObservableCollection<DirectReferrer> DirectReferrers { get; } = new();
    public ObservableCollection<DeltaRow> Deltas { get; } = new();
    public ObservableCollection<LeakRow> Leaks { get; } = new();
    public ObservableCollection<LiveProcess> Processes { get; } = new();

    /// <summary>What the collector has been doing, most recent first.</summary>
    public ObservableCollection<string> Activity { get; } = new();

    [ObservableProperty] private LiveProcess? m_selectedProcess;
    [ObservableProperty] private string m_processFilter = string.Empty;
    [ObservableProperty] private int m_selectedTabIndex;
    [ObservableProperty] private string m_processCount = string.Empty;
    [ObservableProperty] private string m_scheduleState = "No schedule running";
    [ObservableProperty] private bool m_isScheduleRunning;
    [ObservableProperty] private int m_intervalMinutes = 15;
    [ObservableProperty] private int m_keepAtMost;
    [ObservableProperty] private bool m_useFullDumps;
    [ObservableProperty] private bool m_isTruncated;
    [ObservableProperty] private bool m_useProfilerTheme = true;
    [ObservableProperty] private string m_leakSummary = "Open a dump, then look for leaks.";
    [ObservableProperty] private LeakRow? m_selectedLeak;
    [ObservableProperty] private int m_recordSeconds = 10;

    public string DumpRootFolder
    {
        get => m_settings.DumpRootFolder;
        set
        {
            if (value == m_settings.DumpRootFolder || string.IsNullOrWhiteSpace(value)) return;

            m_settings.DumpRootFolder = value;
            m_settings.Save();
            m_store = new DumpStore(value);

            // The schedule must follow, otherwise it keeps writing to the folder it was born
            // with while the user watches the new one stay empty.
            m_schedule.Store = m_store;
            m_schedule.Log.Info($"Dump folder changed to {value}");

            OnPropertyChanged();
            Evolution.RefreshSessions(m_store);

            Status = $"Dumps are stored under {value}";
        }
    }

    /// <summary>Set by the view, which owns the folder dialog.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    public bool HasSnapshot => m_snapshot != null;

    /// <summary>Set by the view, which owns the file dialogs.</summary>
    public Func<bool, Task<string?>>? PickDumpFile { get; set; }
    public Func<Task<string?>>? PickTraceFile { get; set; }
    public Func<string, Task<string?>>? PickSaveFile { get; set; }

    private IProgress<string> Progress => new Progress<string>(message => Status = message);

    public async Task OpenDumpAsync(bool asBaseline)
    {
        if (PickDumpFile == null) return;

        var path = await PickDumpFile(asBaseline);
        if (path == null) return;

        await LoadDumpAsync(path, asBaseline);
    }

    public async Task LoadDumpAsync(string path, bool asBaseline)
    {
        await RunAsync(async token =>
        {
            var progress = Progress;
            var snapshot = await Task.Run(() => Snapshot.Load(path, progress), token);

            if (asBaseline)
            {
                (m_baseline as IDisposable)?.Dispose();
                m_baseline = snapshot;
                BaselineDescription = Describe(snapshot);
            }
            else
            {
                (m_snapshot as IDisposable)?.Dispose();
                m_snapshot = snapshot;

                Leaks.Clear();
                LeakSummary = "Press \"Find leaks\" to rank what is most likely to be leaking.";
                SnapshotDescription = Describe(snapshot);
                OnPropertyChanged(nameof(HasSnapshot));
                ShowTypes();
            }

            if (m_baseline != null && m_snapshot != null) ShowDeltas();

            Status = $"{System.IO.Path.GetFileName(path)}: {snapshot.ObjectCount:N0} objects, " +
                     $"{ReportWriter.Bytes(snapshot.TotalSize)}";

            IsTruncated = snapshot is HeapSnapshot { IsProbablyTruncated: true };

            if (IsTruncated)
            {
                Status += "  -  this dump hit the ten million object limit of the gcdump collector, " +
                          "so part of the heap is missing; collect a full dump to see all of it.";
            }
        });
    }

    private static string Describe(ISnapshot snapshot)
    {
        var parts = new List<string> { System.IO.Path.GetFileName(snapshot.FilePath) };

        // A .gcdump written by the collector carries no process name nor collection time; only
        // mention them when the file actually recorded them.
        if (snapshot.ProcessName != null) parts.Add(snapshot.ProcessName);
        if (snapshot.MachineName != null) parts.Add($"on {snapshot.MachineName}");
        if (snapshot.TimeCollected != null) parts.Add($"collected {snapshot.TimeCollected:yyyy-MM-dd HH:mm:ss}");

        parts.Add($"{snapshot.ObjectCount:N0} objects");
        parts.Add(ReportWriter.Bytes(snapshot.TotalSize));

        return string.Join("  -  ", parts);
    }

    partial void OnTypeFilterChanged(string value) => ShowTypes();

    partial void OnProcessFilterChanged(string value) => ShowProcesses();

    partial void OnUseFullDumpsChanged(bool value)
    {
        m_settings.UseFullDumps = value;
        m_settings.Save();
    }

    partial void OnUseProfilerThemeChanged(bool value)
    {
        if (value == m_settings.UseProfilerTheme) return;

        m_settings.UseProfilerTheme = value;
        m_settings.Save();

        Status = value
            ? "The profiler's own colours will be used the next time it starts."
            : "The system colours will be used the next time the profiler starts.";
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        var shouldWatch = value == LiveProcessesTabIndex;

        if (shouldWatch == m_processWatch.IsEnabled) return;

        m_processWatch.IsEnabled = shouldWatch;
        OnPropertyChanged(nameof(IsWatchingProcesses));

        if (shouldWatch) m_pendingProcessRefresh = RefreshProcessesAsync();
    }

    partial void OnSelectedTypeChanged(TypeRow? value)
    {
        if (value == null) return;

        SelectionDescription = $"type ~ \"{value.TypeName}\"";

        // The holder view still shows the previous answer: it does not describe this type, so it
        // must go rather than sit under a title that now names something else.
        if (m_reportedSelection != SelectionDescription) ForgetReport();

        AnalyzeSelectionSoon();
    }

    /// <summary>
    /// How long to wait before analyzing a newly selected type. Analyzing a large heap takes a
    /// second or two, so doing it on every keystroke of a cursor moving down the list would make
    /// the list unusable; waiting for the selection to settle keeps it immediate to the eye.
    /// Tests set it to zero.
    /// </summary>
    public TimeSpan SelectionAnalysisDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    private DispatcherTimer? m_selectionSettled;

    private void AnalyzeSelectionSoon()
    {
        if (m_snapshot == null || SelectedType == null) return;

        if (SelectionAnalysisDelay <= TimeSpan.Zero)
        {
            m_pendingAnalysis = AnalyzeSelectionCommand.ExecuteAsync(null);
            return;
        }

        m_selectionSettled ??= CreateSelectionTimer();

        m_selectionSettled.Stop();
        m_selectionSettled.Interval = SelectionAnalysisDelay;
        m_selectionSettled.Start();
    }

    private DispatcherTimer CreateSelectionTimer()
    {
        var timer = new DispatcherTimer();

        timer.Tick += (_, _) =>
        {
            // An analysis of the type left behind is of no use now: drop it and take the new one.
            if (IsBusy)
            {
                m_running?.Cancel();
                return;
            }

            timer.Stop();
            m_pendingAnalysis = AnalyzeSelectionCommand.ExecuteAsync(null);
        };

        return timer;
    }

    private void ForgetReport()
    {
        m_report = null;
        m_reportedSelection = null;

        RootPaths.Clear();
        DirectReferrers.Clear();
        SelectedRootPath = null;

        HolderSummary = SelectedType == null
            ? "Choose a type on the left, or one of the presets."
            : $"Finding what holds {SelectedType.TypeName}...";

        ExportReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Opening the holder tab runs the analysis the title promises. It was possible to select a
    /// type, come here, and read a title naming it above two empty tables, because nothing had
    /// asked for the answer yet.
    /// </summary>
    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        if (newValue != ExploreTabIndex) return;
        if (m_snapshot == null || IsBusy) return;
        if (SelectedType == null) return;
        if (m_reportedSelection == SelectionDescription) return;

        m_pendingAnalysis = AnalyzeSelectionCommand.ExecuteAsync(null);
    }

    private Task m_pendingAnalysis = Task.CompletedTask;

    /// <summary>Completes when the analysis started by opening the tab has finished; awaited by tests.</summary>
    public Task AnalysisCompleted => m_pendingAnalysis;

    private void ShowTypes()
    {
        var selectedTypeName = SelectedType?.TypeName;

        Types.Clear();

        if (m_snapshot == null) return;

        var filter = TypeFilter;

        var rows = m_snapshot.TypeStatistics
            .Where(statistic => string.IsNullOrWhiteSpace(filter)
                                || statistic.TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(2000)
            .Select(statistic => new TypeRow(statistic));

        foreach (var row in rows) Types.Add(row);

        // Rebuilding the list makes the grid drop its selection; restore it while the row is
        // still there, so "Selected type" keeps meaning what the user last picked.
        if (selectedTypeName != null)
        {
            SelectedType = Types.FirstOrDefault(row => row.TypeName == selectedTypeName);
        }
    }

    private void ShowDeltas()
    {
        Deltas.Clear();

        if (m_baseline == null || m_snapshot == null) return;

        foreach (var delta in SnapshotDiff.Compare(m_baseline, m_snapshot).Take(500)) Deltas.Add(new DeltaRow(delta));
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeSelectionAsync()
    {
        // The list rebuild drops the grid selection: falling back to "everything" here once froze
        // the profiler for minutes on a five million object heap. Guidance instead.
        if (SelectedType == null)
        {
            HolderSummary = "Choose a type on the left, or one of the presets.";
            Status = HolderSummary;
            return;
        }

        await AnalyzeAsync(ObjectFilter.ForType(SelectedType.TypeName));
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzePoolBlocksAsync()
    {
        // A preset is a different question from the selected row: clear the row so the title and
        // the list agree on what is being shown.
        SelectedType = null;
        SelectionDescription = "pooled buffers (2 MB blocks)";

        await AnalyzeAsync(ObjectFilter.ForPoolBlocks());
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeLargeObjectsAsync()
    {
        SelectedType = null;
        SelectionDescription = "large object heap (85 KB and above)";

        await AnalyzeAsync(new ObjectFilter { MinimalSize = 85_000 });
    }

    private bool CanAnalyze() => m_snapshot != null && !IsBusy;

    private async Task AnalyzeAsync(ObjectFilter filter)
    {
        var snapshot = m_snapshot;
        if (snapshot == null) return;

        // The selection has been acted on: a pending wait for it would otherwise fire during this
        // analysis and cancel it.
        m_selectionSettled?.Stop();

        // Read once: reloading the rules mid-analysis must not change what this one is classified
        // with, or the paths and the summary would disagree.
        var classifier = m_classifier;

        await RunAsync(async token =>
        {
            var progress = Progress;

            SelectionDescription = filter.ToString();
            HolderSummary = "Analyzing...";
            RootPaths.Clear();
            DirectReferrers.Clear();

            HolderReport report;

            try
            {
                report = await Task.Run(
                    () => snapshot.AnalyzeHolders(
                        filter,
                        new HolderAnalysisOptions { Classifier = classifier },
                        progress,
                        token),
                    token);
            }
            catch (OperationCanceledException)
            {
                HolderSummary = "Analysis cancelled.";
                throw;
            }
            catch (Exception exception)
            {
                // The status bar alone is too easy to miss: an empty view must say why it is empty.
                HolderSummary = $"Analysis failed: {exception.Message}";
                throw;
            }

            m_report = report;
            m_reportedSelection = SelectionDescription;

            foreach (var path in report.RootPaths.Take(500)) RootPaths.Add(new RootPathRow(path, classifier));

            foreach (var referrer in report.DirectReferrers.Take(200)) DirectReferrers.Add(referrer);

            SelectedRootPath = RootPaths.FirstOrDefault();

            HolderSummary =
                $"{report.ObjectCount:N0} objects, {ReportWriter.Bytes(report.TotalBytes)} " +
                $"({ReportWriter.Percent(report.TotalBytes, snapshot.TotalSize)} of the heap). " +
                $"Held by the application: {ReportWriter.Bytes(report.SuspiciousBytes)}." +
                (report.RootPaths.Count > 500 ? $" Showing the 500 largest of {report.RootPaths.Count:N0} paths." : string.Empty);

            Status = HolderSummary;
        });
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task FindLeaksAsync()
    {
        var snapshot = m_snapshot;
        if (snapshot == null) return;

        var classifier = m_classifier;

        await RunAsync(async token =>
        {
            var progress = Progress;
            var series = Evolution.LoadedSeries;

            Leaks.Clear();
            LeakSummary = series.Count >= 2
                ? $"Looking, with {series.Count} dumps of history..."
                : "Looking. Load a series in Evolution first to also use how things grow.";

            IReadOnlyList<LeakFinding> findings;

            try
            {
                findings = await Task.Run(
                    () => LeakDetector.Find(
                        snapshot,
                        series,
                        new LeakDetectorOptions { Classifier = classifier },
                        progress,
                        token),
                    token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LeakSummary = $"Search failed: {exception.Message}";
                throw;
            }

            foreach (var finding in findings) Leaks.Add(new LeakRow(finding));

            SelectedLeak = Leaks.FirstOrDefault();

            LeakSummary = findings.Count == 0
                ? "Nothing stands out: no type is both large and held where it should not be."
                : $"{findings.Count} suspect(s), most likely first." +
                  (series.Count >= 2 ? string.Empty : " Load a series in Evolution to weigh growth as well.") +
                  (IsTruncated ? " The dump is truncated, so a holder outside it cannot be seen." : string.Empty);

            Status = LeakSummary;
        });
    }

    /// <summary>Opens the suspect where objects are explored, with its holders already showing.</summary>
    [RelayCommand]
    private void InspectLeak()
    {
        var leak = SelectedLeak;
        if (leak == null) return;

        TypeFilter = leak.TypeName;
        SelectedType = Types.FirstOrDefault(row => row.TypeName == leak.TypeName);
        SelectedTabIndex = ExploreTabIndex;

        if (SelectedType != null) m_pendingAnalysis = AnalyzeSelectionCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        // A tick that arrives while the previous one is still listing must be dropped, not queued.
        if (m_refreshingProcesses) return;

        m_refreshingProcesses = true;

        try
        {
            var source = ProcessSource;
            var processes = await Task.Run(source);

            m_allProcesses.Clear();
            m_allProcesses.AddRange(processes);

            ShowProcesses();
        }
        catch (Exception exception)
        {
            Status = $"Could not list the processes: {exception.Message}";
        }
        finally
        {
            m_refreshingProcesses = false;
        }
    }

    /// <summary>
    /// Updates the visible list in place rather than rebuilding it: a list that is replaced twice a
    /// second would drop the selection and flicker under the pointer.
    /// </summary>
    private void ShowProcesses()
    {
        var filter = ProcessFilter;

        var matching = m_allProcesses
            .Where(process => string.IsNullOrWhiteSpace(filter)
                              || process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                              || process.ProcessId.ToString().Contains(filter, StringComparison.Ordinal))
            .OrderBy(process => process.Name)
            .ThenBy(process => process.ProcessId)
            .ToList();

        for (var i = Processes.Count - 1; i >= 0; --i)
        {
            if (!matching.Contains(Processes[i])) Processes.RemoveAt(i);
        }

        for (var i = 0; i < matching.Count; ++i)
        {
            if (i >= Processes.Count)
            {
                Processes.Add(matching[i]);
                continue;
            }

            if (!Processes[i].Equals(matching[i])) Processes.Insert(i, matching[i]);
        }

        ProcessCount = matching.Count == m_allProcesses.Count
            ? $"{m_allProcesses.Count} processes"
            : $"{matching.Count} of {m_allProcesses.Count} processes";
    }

    [RelayCommand]
    private async Task CollectAsync()
    {
        var process = SelectedProcess;
        if (process == null)
        {
            Status = "Select a process first.";
            return;
        }

        string? path = null;

        await RunAsync(async token =>
        {
            var progress = Progress;
            var store = m_store;
            var fullDump = UseFullDumps;
            var collect = CollectDump;

            path = await Task.Run(() => collect(process, store, fullDump, progress, token), token);

            Evolution.RefreshSessions(store);
        });

        // Opened once the collection's RunAsync is over, not inside it: opening runs its own, which
        // returned at once while the collection still held the busy state, so nothing was opened.
        if (path != null) await LoadDumpAsync(path, asBaseline: false);
    }

    /// <summary>
    /// Reads the recording once its <see cref="RunAsync"/> is over, not inside it: reading runs its
    /// own, which would return at once while the recording still holds the busy state.
    /// </summary>
    [RelayCommand]
    private async Task RecordGcTimelineAsync()
    {
        var process = SelectedProcess;
        if (process == null)
        {
            Status = "Select a process first.";
            return;
        }

        string? path = null;

        // Cancel ends the recording early; what was recorded is still read.
        await RunAsync(async token =>
        {
            var progress = Progress;
            var store = m_store;
            var duration = TimeSpan.FromSeconds(RecordSeconds);

            path = await Task.Run(() => GcTimelineCollector.Record(process, store, duration, progress, token), CancellationToken.None);
        });

        if (path != null) await LoadTraceAsync(path);
    }

    [RelayCommand]
    private async Task OpenTraceAsync()
    {
        if (PickTraceFile == null) return;

        var path = await PickTraceFile();
        if (path != null) await LoadTraceAsync(path);
    }

    public async Task LoadTraceAsync(string path)
    {
        await RunAsync(async token =>
        {
            var progress = Progress;
            var timeline = await Task.Run(() => GcTimeline.Load(path, progress), token);

            Timeline.Show(timeline);
            SelectedTabIndex = GcTimelineTabIndex;

            Status = Timeline.Description;
        });
    }

    [RelayCommand]
    private async Task ExportGcReportAsync()
    {
        var timeline = Timeline.Loaded;

        if (timeline == null)
        {
            Status = "Record a process or open a recording first.";
            return;
        }

        if (PickSaveFile == null) return;

        var path = await PickSaveFile(System.IO.Path.GetFileNameWithoutExtension(timeline.FilePath) + "-gc.md");
        if (path == null) return;

        await System.IO.File.WriteAllTextAsync(path, ReportWriter.Write(timeline));

        Status = $"Report written to {path}";
    }

    [RelayCommand]
    private async Task ChangeDumpFolderAsync()
    {
        if (PickFolder == null) return;

        var folder = await PickFolder(DumpRootFolder);
        if (folder != null) DumpRootFolder = folder;
    }

    [RelayCommand]
    private void StartSchedule()
    {
        var process = SelectedProcess;
        if (process == null)
        {
            Status = "Select a process first.";
            return;
        }

        try
        {
            m_settings.DefaultIntervalMinutes = IntervalMinutes;
            m_settings.KeepAtMostPerProcess = KeepAtMost;
            m_settings.Save();

            m_schedule.Collect = UseFullDumps
                ? (target, token) => FullDumpCollector.Collect(target, m_schedule.Store, null, token)
                : (target, token) => LiveCollector.Collect(target, m_schedule.Store, TimeSpan.FromMinutes(5), null, token);

            m_schedule.Start(process, TimeSpan.FromMinutes(IntervalMinutes), KeepAtMost);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private void StopSchedule() => m_schedule.Stop();

    private const int RecentActivityCount = 200;

    private void AddActivity(string line)
    {
        // Newest first: a failure that happened overnight must be the first thing on screen.
        Activity.Insert(0, line);

        while (Activity.Count > RecentActivityCount) Activity.RemoveAt(Activity.Count - 1);
    }

    [RelayCommand]
    private void OpenLog()
    {
        var path = m_schedule.Log.FilePath;

        if (!System.IO.File.Exists(path))
        {
            Status = "Nothing has been collected yet, so there is no log.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Status = $"Could not open {path}: {exception.Message}";
        }
    }

    /// <summary>
    /// Where the reader's own holder rules are read from. Settable so a test can point at its own
    /// file instead of the one in the user profile.
    /// </summary>
    public string HolderRulesPath { get; set; } = HolderRuleFile.DefaultPath;

    /// <summary>What the rule file was found to contain, shown so a typo in it is not silent.</summary>
    [ObservableProperty] private string m_holderRulesSummary = string.Empty;

    /// <summary>
    /// Re-reads the rule file. Cheap, and it means editing the file does not need a restart: the
    /// next analysis classifies with what was just saved.
    /// </summary>
    [RelayCommand]
    public void ReloadHolderRules()
    {
        var rules = HolderRuleFile.Load(HolderRulesPath);

        m_classifier = rules.Applied();

        HolderRulesSummary = rules.Rules.Count == 0 && rules.Problems.Count == 0
            ? "No holder rules of your own: only the pools of the framework are recognised."
            : $"{rules.Rules.Count} holder rule(s) of your own." +
              (rules.Problems.Count == 0 ? string.Empty : " " + string.Join(" ", rules.Problems));

        Status = HolderRulesSummary;
    }

    /// <summary>
    /// Opens the rule file, writing one that explains itself if there is none yet. An empty editor
    /// would leave the reader to guess the shape of what to write.
    /// </summary>
    [RelayCommand]
    private void EditHolderRules()
    {
        string path;

        try
        {
            path = HolderRuleFile.WriteTemplateIfMissing(HolderRulesPath);
        }
        catch (Exception exception)
        {
            Status = $"Could not write {HolderRulesPath}: {exception.Message}";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

            Status = $"Editing {path}. Save it, then Reload rules.";
        }
        catch (Exception exception)
        {
            Status = $"The rules are in {path}, which could not be opened: {exception.Message}";
        }
    }

    private void ShowScheduleState(ScheduledCollectorState state)
    {
        IsScheduleRunning = state.IsRunning;

        ScheduleState = state.IsRunning
            ? $"Every {state.Interval.TotalMinutes:F0} min on {state.ProcessName} ({state.ProcessId}): " +
              $"{state.CollectedCount} collected, next at {state.NextCollectionAt:HH:mm:ss}. {state.LastMessage}"
            : state.LastMessage;

        Status = ScheduleState;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportReportAsync()
    {
        if (PickSaveFile == null || m_snapshot == null || m_report == null) return;

        var suggested = System.IO.Path.GetFileNameWithoutExtension(m_snapshot.FilePath) + "-analysis.md";
        var path = await PickSaveFile(suggested);
        if (path == null) return;

        var markdown = ReportWriter.Write(m_snapshot, m_report, m_classifier);
        await System.IO.File.WriteAllTextAsync(path, markdown);

        Status = $"Report written to {path}";
    }

    private bool CanExport() => m_report != null && !IsBusy;

    [RelayCommand]
    private void Cancel() => m_running?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;

        using var cancellation = new CancellationTokenSource();

        m_running = cancellation;
        IsBusy = true;
        NotifyCommands();

        try
        {
            await work(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception exception)
        {
            Status = $"Failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            m_running = null;
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        AnalyzeSelectionCommand.NotifyCanExecuteChanged();
        AnalyzePoolBlocksCommand.NotifyCanExecuteChanged();
        AnalyzeLargeObjectsCommand.NotifyCanExecuteChanged();
        FindLeaksCommand.NotifyCanExecuteChanged();
        ExportReportCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One row of the type list.</summary>
public sealed class TypeRow(TypeStatistic statistic)
{
    public string TypeName { get; } = statistic.TypeName;
    public int Count { get; } = statistic.Count;
    public long TotalBytes { get; } = statistic.TotalBytes;
    public string TotalDisplay { get; } = ReportWriter.Bytes(statistic.TotalBytes);
    public string AverageDisplay { get; } = ReportWriter.Bytes(statistic.AverageBytes);
}

/// <summary>One row of the holder list: a way the selected objects are kept alive.</summary>
public sealed class RootPathRow(RootPath path, HolderClassifier? classifier = null)
{
    public string HeldBy { get; } = path.FirstFrame;
    public int ObjectCount { get; } = path.ObjectCount;
    public string BytesDisplay { get; } = ReportWriter.Bytes(path.Bytes);
    public HolderCategory Category { get; } = path.Category;
    public string CategoryDisplay { get; } = path.Category switch
    {
        HolderCategory.Application => "investigate",
        HolderCategory.Pooled => "free in pool",
        HolderCategory.AwaitingFinalizer => "awaiting finalizer",
        HolderCategory.Pinned => "pinned",
        HolderCategory.Instrumentation => "instrumentation",
        _ => path.Category.ToString()
    };

    /// <summary>
    /// True when the application itself is keeping the objects, rather than the pool, finalization
    /// or pinning. Only that state earns a colour; the expected holders stay neutral.
    /// </summary>
    public bool IsSuspect { get; } = path.Category == HolderCategory.Application;

    public IReadOnlyList<string> Frames { get; } = path.Frames;
    public string PathText { get; } = string.Join(Environment.NewLine, path.Frames);
    public string? Explanation { get; } = (classifier ?? HolderClassifier.Default).Explain(path.Frames);
}
