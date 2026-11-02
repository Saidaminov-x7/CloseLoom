using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;

namespace AppGuard.Core.Services;

/// <summary>
/// Coordinates per-process grace periods. It only begins timing after a visible main window was observed,
/// restarts the full delay after any window-state change, and delegates all final decisions to the safety service.
/// </summary>
public sealed class GracePeriodCoordinator : IAsyncDisposable
{
    private readonly ISafeTerminationService _termination;
    private readonly IActivityLog _activityLog;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<ProcessIdentity, TrackedProcess> _tracked = [];
    private readonly HashSet<Task> _backgroundTasks = [];
    private bool _monitoring;
    private bool _disposed;

    public GracePeriodCoordinator(ISafeTerminationService termination, IActivityLog activityLog, TimeProvider? timeProvider = null)
    {
        _termination = termination;
        _activityLog = activityLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            _monitoring = true;
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask TrackProcessAsync(MonitoredApplication application, ProcessIdentity identity,
        TerminationOptions options, IReadOnlyList<WindowSnapshot> initialWindows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(initialWindows);
        if (application.EffectiveGracePeriod < TimeSpan.FromSeconds(3) || application.EffectiveGracePeriod > TimeSpan.FromMinutes(5))
        {
            await WriteAsync(application.Name, ActivityEventType.ProcessSkipped, "Skipped",
                "Configured grace period is outside the supported 3 to 300 second range.", cancellationToken, identity.Pid).ConfigureAwait(false);
            return;
        }
        TrackedProcess tracked;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_monitoring || !options.MonitoringEnabled || !application.Enabled) return;
            if (!_tracked.TryGetValue(identity, out tracked!))
            {
                tracked = new TrackedProcess(application, identity, options);
                _tracked.Add(identity, tracked);
            }
        }
        await WriteAsync(application.Name, ActivityEventType.ProcessDetected, "Detected", null, cancellationToken, identity.Pid).ConfigureAwait(false);
        await ObserveWindowsAsync(identity, initialWindows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reports a fresh snapshot for this exact process instance; PID alone is never used as its key.</summary>
    public async ValueTask ObserveWindowsAsync(ProcessIdentity identity, IReadOnlyList<WindowSnapshot> windows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(windows);
        TrackedProcess? tracked;
        CancellationTokenSource? cancelled = null;
        Task? cancelledTask = null;
        bool schedule = false;
        bool opened = false;
        bool graceCancelled = false;
        bool windowOpened = false;
        bool windowClosed = false;
        CancellationTokenSource? scheduledSource = null;
        long scheduledGeneration = 0;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_monitoring || !_tracked.TryGetValue(identity, out tracked)) return;
            var previousHadMainWindow = tracked.LastWindowSignature is not null && HasVisibleMainWindow(tracked.LastWindows);
            var currentHasMainWindow = HasVisibleMainWindow(windows);
            windowOpened = currentHasMainWindow && !previousHadMainWindow;
            windowClosed = previousHadMainWindow && !currentHasMainWindow;
            var newSignature = Signature(windows);
            if (tracked.LastWindowSignature is not null && tracked.LastWindowSignature != newSignature)
            {
                cancelled = tracked.GraceCancellation;
                graceCancelled = cancelled is not null;
                cancelledTask = tracked.GraceTask;
                tracked.GraceCancellation = null;
                tracked.GraceTask = null;
                tracked.Generation++;
                opened = cancelled is not null && HasVisibleMainWindow(windows);
            }
            tracked.LastWindowSignature = newSignature;
            tracked.LastWindows = windows.ToArray();
            tracked.HadEligibleWindow |= HasVisibleMainWindow(windows);
            schedule = tracked.HadEligibleWindow && !HasVisibleMainWindow(windows) && tracked.GraceCancellation is null;
            if (schedule)
            {
                tracked.GraceCancellation = new CancellationTokenSource();
                tracked.Generation++;
                scheduledSource = tracked.GraceCancellation;
                scheduledGeneration = tracked.Generation;
            }
        }
        cancelled?.Cancel();
        cancelled?.Dispose();
        if (cancelledTask is not null) await cancelledTask.ConfigureAwait(false);

        if (tracked is null) return;
        if (windowOpened)
            await WriteAsync(tracked.Application.Name, ActivityEventType.WindowOpened, "Observed", null, cancellationToken, identity.Pid).ConfigureAwait(false);
        if (windowClosed)
            await WriteAsync(tracked.Application.Name, ActivityEventType.WindowClosed, "Observed", null, cancellationToken, identity.Pid).ConfigureAwait(false);
        if (graceCancelled)
            await WriteAsync(tracked.Application.Name, ActivityEventType.GracePeriodCancelled, "Cancelled",
                opened ? "A suitable window reopened during the grace period." : "Window state changed during the grace period.",
                cancellationToken, identity.Pid).ConfigureAwait(false);
        if (schedule)
        {
            await WriteAsync(tracked.Application.Name, ActivityEventType.GracePeriodStarted, "Started",
                $"Waiting {tracked.Application.EffectiveGracePeriod.TotalSeconds:0.###} seconds before rechecking.", cancellationToken, identity.Pid).ConfigureAwait(false);
            lock (_sync)
            {
                if (!_monitoring || !_tracked.TryGetValue(identity, out var current) ||
                    !ReferenceEquals(current.GraceCancellation, scheduledSource) || current.Generation != scheduledGeneration) return;
                var task = CompleteGracePeriodAsync(identity, scheduledGeneration, scheduledSource!);
                current.GraceTask = task;
                _backgroundTasks.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (_sync) _backgroundTasks.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    public async ValueTask ProcessExitedAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        TrackedProcess? tracked;
        Task? pendingTask;
        lock (_sync)
        {
            if (!_tracked.Remove(identity, out tracked)) return;
            tracked.GraceCancellation?.Cancel();
            tracked.GraceCancellation?.Dispose();
            tracked.GraceCancellation = null;
            pendingTask = tracked.GraceTask;
            tracked.GraceTask = null;
        }
        if (pendingTask is not null) await pendingTask.ConfigureAwait(false);
        await WriteAsync(tracked.Application.Name, ActivityEventType.ProcessSkipped, "Process exited", "The process exited during monitoring.", cancellationToken, identity.Pid).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task[] pendingTasks;
        bool wasMonitoring;
        lock (_sync)
        {
            ThrowIfDisposed();
            wasMonitoring = _monitoring;
            _monitoring = false;
            pendingTasks = _backgroundTasks.ToArray();
            var processes = _tracked.Values.ToArray();
            _tracked.Clear();
            foreach (var process in processes)
            {
                process.Generation++;
                process.GraceCancellation?.Cancel();
                process.GraceCancellation?.Dispose();
                process.GraceCancellation = null;
            }
        }
        if (pendingTasks.Length > 0) await Task.WhenAll(pendingTasks).ConfigureAwait(false);
        if (wasMonitoring)
            await WriteAsync(null, ActivityEventType.GracePeriodCancelled, "Monitoring stopped", "All pending grace periods were cancelled.", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pendingTasks;
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _monitoring = false;
                foreach (var process in _tracked.Values)
                {
                    process.GraceCancellation?.Cancel();
                    process.GraceCancellation?.Dispose();
                }
                _tracked.Clear();
            }
            pendingTasks = _backgroundTasks.ToArray();
        }
        if (pendingTasks.Length > 0) await Task.WhenAll(pendingTasks).ConfigureAwait(false);
    }

    private async Task CompleteGracePeriodAsync(ProcessIdentity identity, long generation, CancellationTokenSource tokenSource)
    {
        try
        {
            var application = GetCurrent(identity, generation, tokenSource);
            if (application is null) return;
            await Task.Delay(application.EffectiveGracePeriod, _timeProvider, tokenSource.Token).ConfigureAwait(false);
            TrackedProcess? current;
            lock (_sync)
            {
                if (!_monitoring || !_tracked.TryGetValue(identity, out current) || current.Generation != generation ||
                    !ReferenceEquals(current.GraceCancellation, tokenSource) || HasVisibleMainWindow(current.LastWindows)) return;
            }
            var outcome = await _termination.TryTerminateAsync(identity, current.Options, current.HadEligibleWindow,
                stateChangedDuringGrace: false, tokenSource.Token).ConfigureAwait(false);
            await WriteAsync(current.Application.Name,
                outcome.Allowed ? ActivityEventType.ProcessTerminated : ActivityEventType.ProcessSkipped,
                outcome.Allowed ? "Completed" : "Skipped", outcome.Reason, CancellationToken.None, identity.Pid).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (tokenSource.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await WriteAsync(null, ActivityEventType.Error, "Error", ex.GetType().Name, CancellationToken.None, identity.Pid).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (_tracked.TryGetValue(identity, out var current) && ReferenceEquals(current.GraceCancellation, tokenSource))
                {
                    current.GraceCancellation = null;
                    tokenSource.Dispose();
                }
            }
        }
    }

    private MonitoredApplication? GetCurrent(ProcessIdentity identity, long generation, CancellationTokenSource source)
    {
        lock (_sync)
            return _monitoring && _tracked.TryGetValue(identity, out var tracked) && tracked.Generation == generation &&
                ReferenceEquals(tracked.GraceCancellation, source) ? tracked.Application : null;
    }

    private ValueTask WriteAsync(string? app, ActivityEventType type, string result, string? reason,
        CancellationToken cancellationToken, int? pid = null) =>
        _activityLog.WriteAsync(new ActivityEvent(_timeProvider.GetUtcNow(), app, type, result, reason, pid), cancellationToken);

    private static bool HasVisibleMainWindow(IReadOnlyList<WindowSnapshot> windows) => windows.Any(w => w.IsMainWindow && w.IsVisible);
    private static string Signature(IReadOnlyList<WindowSnapshot> windows) => string.Join(";", windows
        .OrderBy(w => w.Handle.ToInt64()).Select(w => $"{w.ProcessId}:{w.Handle.ToInt64()}:{w.IsVisible}:{w.IsMinimized}:{w.IsMainWindow}"));
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(GracePeriodCoordinator)); }

    private sealed class TrackedProcess(MonitoredApplication application, ProcessIdentity identity, TerminationOptions options)
    {
        public MonitoredApplication Application { get; } = application;
        public ProcessIdentity Identity { get; } = identity;
        public TerminationOptions Options { get; } = options;
        public bool HadEligibleWindow { get; set; }
        public string? LastWindowSignature { get; set; }
        public IReadOnlyList<WindowSnapshot> LastWindows { get; set; } = [];
        public long Generation { get; set; }
        public CancellationTokenSource? GraceCancellation { get; set; }
        public Task? GraceTask { get; set; }
    }
}
