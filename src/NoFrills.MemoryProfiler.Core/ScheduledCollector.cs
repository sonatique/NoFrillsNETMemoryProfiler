using System.Diagnostics;

namespace NoFrills.MemoryProfiler.Core;

public sealed record ScheduledCollectorState(
    bool IsRunning,
    string ProcessName,
    int ProcessId,
    TimeSpan Interval,
    int CollectedCount,
    DateTime? NextCollectionAt,
    string LastMessage);

/// <summary>
/// Takes a dump of one process every so often, unattended, until it is stopped or the process
/// ends. The dumps land in the store, so they form a series that the evolution view can read.
///
/// Collecting pauses the target for the duration of a blocking collection, which is why the
/// interval is in minutes: this is meant to run next to a long capture, not to sample tightly.
/// </summary>
public sealed class ScheduledCollector
{
    private CancellationTokenSource? m_cancellation;
    private Task? m_loop;

    public ScheduledCollector(DumpStore store, CollectionLog? log = null)
    {
        Store = store;
        Log = log ?? CollectionLog.For(store);
        Collect = (process, token) => LiveCollector.Collect(process, Store, TimeSpan.FromMinutes(5), null, token);
    }

    /// <summary>
    /// Where the dumps go, read at every collection. Replaceable, because a schedule holding on
    /// to the store it was built with kept writing to the old folder after the user changed it.
    /// </summary>
    public DumpStore Store { get; set; }

    public CollectionLog Log { get; }

    /// <summary>How a dump is taken; replaced in tests.</summary>
    public Func<LiveProcess, CancellationToken, string> Collect { get; set; }

    /// <summary>
    /// A collection that comes back without its roots is a transient failure, so one retry is
    /// worth more than losing that point of the series.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(20);

    public event Action<ScheduledCollectorState>? StateChanged;

    /// <summary>Raised for every dump written, so the application can open or list it.</summary>
    public event Action<string>? DumpCollected;

    public ScheduledCollectorState State { get; private set; } =
        new(false, string.Empty, 0, TimeSpan.Zero, 0, null, "Not started");

    public bool IsRunning => m_loop is { IsCompleted: false };

    public void Start(LiveProcess process, TimeSpan interval, int keepAtMost = 0, bool collectImmediately = true)
    {
        if (IsRunning) throw new InvalidOperationException("A schedule is already running.");

        if (interval < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "Collecting pauses the target process, so the interval must be at least one minute.");
        }

        m_cancellation = new CancellationTokenSource();
        var token = m_cancellation.Token;

        Publish(State with
        {
            IsRunning = true,
            ProcessName = process.Name,
            ProcessId = process.ProcessId,
            Interval = interval,
            CollectedCount = 0,
            NextCollectionAt = collectImmediately ? DateTime.Now : DateTime.Now + interval,
            LastMessage = "Started"
        });

        Log.Info($"Schedule started on {process.Name} ({process.ProcessId}), every {interval.TotalMinutes:F0} min, " +
                 $"keeping {(keepAtMost > 0 ? keepAtMost.ToString() : "every")} dump(s), into {Store.RootFolder}");

        m_loop = Task.Run(() => Loop(process, interval, keepAtMost, collectImmediately, token), token);
    }

    public void Stop()
    {
        if (IsRunning) Log.Info($"Schedule stopped after {State.CollectedCount} dumps");

        m_cancellation?.Cancel();
        Publish(State with { IsRunning = false, NextCollectionAt = null, LastMessage = "Stopped" });
    }

    private async Task Loop(
        LiveProcess process,
        TimeSpan interval,
        int keepAtMost,
        bool collectImmediately,
        CancellationToken token)
    {
        var first = true;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!first || !collectImmediately)
                {
                    Publish(State with { NextCollectionAt = DateTime.Now + interval });
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }

                first = false;

                if (HasExited(process.ProcessId))
                {
                    Log.Info($"Process {process.Name} ({process.ProcessId}) ended, schedule stopped after " +
                             $"{State.CollectedCount} dumps");

                    Publish(State with
                    {
                        IsRunning = false,
                        NextCollectionAt = null,
                        LastMessage = $"Process {process.Name} ({process.ProcessId}) ended, schedule stopped " +
                                      $"after {State.CollectedCount} dumps"
                    });

                    return;
                }

                if (!CollectOnce(process, keepAtMost, token)) return;
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called.
        }
        catch (Exception exception)
        {
            Log.Failure("Schedule stopped", exception);
            Publish(State with { IsRunning = false, NextCollectionAt = null, LastMessage = $"Failed: {exception.Message}" });
        }
    }

    /// <summary>False when the schedule must end because the target process is gone.</summary>
    private bool CollectOnce(LiveProcess process, int keepAtMost, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 2; ++attempt)
        {
            try
            {
                var started = DateTime.Now;
                var path = Collect(process, token);
                var size = new FileInfo(path).Length;

                Log.Info($"Collected {Path.GetFileName(path)} from {process.Name} ({process.ProcessId}): " +
                         $"{size / (1024 * 1024)} MB in {(DateTime.Now - started).TotalSeconds:F0} s");

                if (keepAtMost > 0)
                {
                    var deleted = Store.Trim(process.Name, keepAtMost);
                    if (deleted != 0) Log.Info($"Deleted {deleted} dump(s) over the limit of {keepAtMost}");
                }

                Publish(State with
                {
                    CollectedCount = State.CollectedCount + 1,
                    LastMessage = $"{DateTime.Now:HH:mm:ss} collected {Path.GetFileName(path)}"
                });

                DumpCollected?.Invoke(path);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A collection that failed because the target is gone ends the schedule: there is
                // nothing left to collect from, and silence would look like it is still working.
                if (HasExited(process.ProcessId))
                {
                    var explanation = $"Process {process.Name} ({process.ProcessId}) is gone, schedule stopped " +
                                      $"after {State.CollectedCount} dumps";

                    Log.Info(explanation);
                    Publish(State with { IsRunning = false, NextCollectionAt = null, LastMessage = explanation });

                    return false;
                }

                // Only a collection that came back unusable is worth retrying; anything else would
                // fail the same way immediately.
                var retrying = attempt == 1 && exception is HeapCollectionException;

                Log.Failure(
                    $"Collection of {process.Name} ({process.ProcessId}) failed" + (retrying ? ", retrying" : string.Empty),
                    exception);

                Publish(State with
                {
                    LastMessage = $"{DateTime.Now:HH:mm:ss} failed: {exception.Message}" +
                                  (retrying ? " (retrying)" : string.Empty)
                });

                if (!retrying) return true;

                if (token.WaitHandle.WaitOne(RetryDelay)) return true;
            }
        }

        return true;
    }

    private static bool HasExited(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private void Publish(ScheduledCollectorState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
