using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text.Json;
using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;
using AppGuard.Core.Services;
using AppGuard.Infrastructure.Persistence;
using AppGuard.Infrastructure.Logging;

namespace AppGuard.Infrastructure.Monitoring;

/// <summary>
/// Discovers only current-session processes whose full executable path is explicitly allowlisted. Window changes
/// come from WinEvent hooks; a bounded reconciliation scan detects new/exited process instances. Any loss of
/// observation stops the run and cancels all pending grace periods.
/// </summary>
public sealed class WindowsApplicationMonitor : IApplicationMonitor
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(2);
    private const int EventQueueCapacity = 2048;
    private readonly IProcessCatalog _processes;
    private readonly IWindowMonitor _windows;
    private readonly IApplicationStore _applications;
    private readonly IUserSettingsStore _settings;
    private readonly GracePeriodCoordinator _coordinator;
    private readonly IWindowEventSource _windowEvents;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int> _currentSessionId;
    private readonly CountingActivityLog? _countingLog;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly SemaphoreSlim _statusNotifyGate = new(1, 1);
    private readonly object _sync = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ProcessIdentity> _tracked = new();
    private Channel<MonitorSignal>? _signals;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private Task? _tickTask;
    private int _eventFaulted;
    private long _statusGeneration;
    private string? _configurationFingerprint;
    private ApplicationMonitorStatus _status = new(false, false);
    private bool _disposed;

    public WindowsApplicationMonitor(IProcessCatalog processes, IWindowMonitor windows, IApplicationStore applications,
        IUserSettingsStore settings, GracePeriodCoordinator coordinator, IWindowEventSource windowEvents,
        TimeProvider? timeProvider = null, Func<int>? currentSessionId = null, CountingActivityLog? countingLog = null)
    {
        _processes = processes;
        _windows = windows;
        _applications = applications;
        _settings = settings;
        _coordinator = coordinator;
        _windowEvents = windowEvents;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentSessionId = currentSessionId ?? GetCurrentSessionId;
        _countingLog = countingLog;
        if (_countingLog is not null) _countingLog.CountChanged += OnActivityCountChanged;
    }

    public ApplicationMonitorStatus Status { get { lock (_sync) return _status; } }
    public event EventHandler<ApplicationMonitorStatus>? StatusChanged;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status.IsRunning) return;
            await ReapStoppedGenerationAsync(cancellationToken).ConfigureAwait(false);
            var cancellation = new CancellationTokenSource();
            var signals = Channel.CreateBounded<MonitorSignal>(new BoundedChannelOptions(EventQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            lock (_sync)
            {
                _runCancellation = cancellation;
                _signals = signals;
            }

            Interlocked.Exchange(ref _eventFaulted, 0);
            _windowEvents.Start(
                pid =>
                {
                    if (pid is null)
                        SignalEventFault(signals, new InvalidOperationException("Window event owner could not be identified."));
                    else if (!signals.Writer.TryWrite(new MonitorSignal(pid, null, false)))
                        SignalEventFault(signals, new InvalidOperationException("Window event queue overflowed."));
                },
                error => SignalEventFault(signals, error));
            await _coordinator.StartAsync(cancellation.Token).ConfigureAwait(false);
            await ReconcileAsync(cancellation.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _eventFaulted) != 0) throw new InvalidOperationException("Window event observation failed during startup.");
            PublishStatus(new ApplicationMonitorStatus(true, false, null, _tracked.Count,
                Status.TotalTerminatedProcesses, _countingLog?.Count ?? 0, true));
            _tickTask = WriteTicksAsync(signals.Writer, cancellation.Token);
            _runTask = RunAsync(signals.Reader, cancellation.Token);
        }
        catch
        {
            try { _windowEvents.Stop(); } catch { }
            try { await _coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            lock (_sync)
            {
                _runCancellation?.Cancel();
                _runCancellation?.Dispose();
                _runCancellation = null;
                _signals = null;
                _tracked.Clear();
            }
            PublishStatus(new ApplicationMonitorStatus(false, false, "Windows monitoring could not be started."));
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Status.IsRunning || Status.IsPaused) return;
            // Pause tears down hooks and cancels every in-flight grace period. Resume begins with a fresh snapshot.
            try { _windowEvents.Stop(); }
            catch
            {
                await FaultAsync("Windows event hooks could not be stopped; pending actions were cancelled.").ConfigureAwait(false);
                throw;
            }
            // Once native hooks stop, finish cancellation regardless of the UI command token.
            await _workGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _tracked.Clear();
            }
            finally { _workGate.Release(); }
            PublishStatus(Status with { IsPaused = true, TrackedProcesses = 0 });
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Status.IsRunning || !Status.IsPaused) return;
            var signals = _signals ?? throw new InvalidOperationException("Monitoring signal queue is unavailable.");
            await _workGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _eventFaulted, 0);
                _windowEvents.Start(
                pid =>
                {
                    if (pid is null)
                        SignalEventFault(signals, new InvalidOperationException("Window event owner could not be identified."));
                    else if (!signals.Writer.TryWrite(new MonitorSignal(pid, null, false)))
                        SignalEventFault(signals, new InvalidOperationException("Window event queue overflowed."));
                },
                error => SignalEventFault(signals, error));
                PublishStatus(Status with { IsPaused = false, LastError = null });
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _eventFaulted) != 0) throw new InvalidOperationException("Window event observation failed during resume.");
            }
            finally { _workGate.Release(); }
            PublishStatus(Status with { TrackedProcesses = _tracked.Count });
        }
        catch
        {
            await FaultAsync("Window monitoring could not resume.").ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            try { _windowEvents.Dispose(); }
            finally
            {
                if (_countingLog is not null) _countingLog.CountChanged -= OnActivityCountChanged;
                _disposed = true;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        var wasRunning = Status.IsRunning;
        var cancellation = _runCancellation;
        var runTask = _runTask;
        var tickTask = _tickTask;
        cancellation?.Cancel();
        try { _windowEvents.Stop(); }
        catch (Exception) { PublishStatus(Status with { LastError = "Windows event hooks did not stop cleanly." }); }
        await _workGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (wasRunning) await _coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _tracked.Clear();
        }
        finally { _workGate.Release(); }
        _signals?.Writer.TryComplete();
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        }
        if (tickTask is not null)
        {
            try { await tickTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        }
        cancellation?.Dispose();
        lock (_sync)
        {
            _runCancellation = null;
            _signals = null;
            _runTask = null;
            _tickTask = null;
            _tracked.Clear();
        }
        if (wasRunning) PublishStatus(Status with { IsRunning = false, IsPaused = false, TrackedProcesses = 0 });
    }

    private async Task RunAsync(ChannelReader<MonitorSignal> signals, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in signals.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Status.IsPaused) continue;
                if (Volatile.Read(ref _eventFaulted) != 0 || signal.Error is not null)
                {
                    await FaultAsync("Windows event observation became unreliable.").ConfigureAwait(false);
                    return;
                }
                await _workGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (Status.IsPaused || Volatile.Read(ref _eventFaulted) != 0) continue;
                    if (signal.IsTick) await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                    else if (signal.ProcessId is { } pid) await RefreshProcessAsync(pid, stateChangeObserved: true, cancellationToken).ConfigureAwait(false);
                }
                finally { _workGate.Release(); }
                PublishStatus(Status with { TrackedProcesses = _tracked.Count, ActivityEventCount = _countingLog?.Count ?? Status.ActivityEventCount, DryRunEnabled = true });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _eventFaulted) != 0)
                await FaultAsync("Windows event observation became unreliable; pending actions were cancelled.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FaultAsync("Process or window state could not be verified; pending actions were cancelled.").ConfigureAwait(false);
        }
    }

    private async Task WriteTicksAsync(ChannelWriter<MonitorSignal> writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReconciliationInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await writer.WriteAsync(new MonitorSignal(null, null, true), cancellationToken).ConfigureAwait(false);
    }

    private void SignalEventFault(Channel<MonitorSignal> signals, Exception error)
    {
        Interlocked.Exchange(ref _eventFaulted, 1);
        // Start the coordinator stop on this callback thread. Its synchronous prefix cancels every grace CTS
        // before it awaits background completions, so a lost native event invalidates decisions immediately.
        try
        {
            var stopTask = _coordinator.StopAsync(CancellationToken.None).AsTask();
            _ = stopTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception) { }
        try { _runCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        // The bounded queue may be full; the flag remains observable independently of queue capacity.
        signals.Writer.TryWrite(new MonitorSignal(null, error, false));
    }

    private async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var applications = (await _applications.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(app => app.Enabled && !app.IsProtected).ToArray();
        var fingerprint = ComputeConfigurationFingerprint(settings, applications);
        if (_configurationFingerprint is not null && !StringComparer.Ordinal.Equals(_configurationFingerprint, fingerprint))
        {
            await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
            _tracked.Clear();
            await _coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        _configurationFingerprint = fingerprint;
        var processSnapshots = await _processes.GetCurrentSessionProcessesAsync(cancellationToken).ConfigureAwait(false);
        var sessionId = _currentSessionId();
        var current = new Dictionary<int, ProcessIdentity>();

        foreach (var snapshot in processSnapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _eventFaulted) != 0) throw new InvalidOperationException("Window event observation failed.");
            if (snapshot.HasExited || snapshot.Identity.SessionId != sessionId) continue;
            current[snapshot.Identity.Pid] = snapshot.Identity;
            var application = applications.FirstOrDefault(app => PathEquals(app.ExecutablePath, snapshot.Identity.ExecutablePath));
            if (application is null) continue;
            if (_tracked.TryGetValue(snapshot.Identity.Pid, out var previous) && previous != snapshot.Identity)
            {
                await _coordinator.ProcessExitedAsync(previous, cancellationToken).ConfigureAwait(false);
                _tracked.TryRemove(snapshot.Identity.Pid, out _);
            }
            var windows = await _windows.GetWindowsAsync(snapshot.Identity.Pid, cancellationToken).ConfigureAwait(false);
            if (!_tracked.ContainsKey(snapshot.Identity.Pid))
            {
                application = application with { GracePeriod = application.GracePeriod ?? TimeSpan.FromSeconds(Math.Clamp(settings.GracePeriodSeconds, 3, 300)) };
                var options = new TerminationOptions(DryRun: true, MonitoringEnabled: true,
                    CurrentSessionId: sessionId, ProtectedExecutablePaths: new HashSet<string>(settings.ProtectedExecutablePaths, StringComparer.OrdinalIgnoreCase));
                await _coordinator.TrackProcessAsync(application, snapshot.Identity, options, windows, cancellationToken).ConfigureAwait(false);
                _tracked[snapshot.Identity.Pid] = snapshot.Identity;
            }
            else
            {
                await _coordinator.ObserveWindowsAsync(snapshot.Identity, windows, stateChangeObserved: false,
                    observationReliable: true, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var pair in _tracked.ToArray())
        {
            if (current.TryGetValue(pair.Key, out var identity))
            {
                if (identity == pair.Value) continue;
                await _coordinator.ProcessExitedAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                _tracked.TryRemove(pair.Key, out _);
                continue;
            }

            var lookup = await _processes.GetSnapshotAsync(pair.Key, cancellationToken).ConfigureAwait(false);
            if (lookup is { } process && (process.HasExited || process.Identity != pair.Value))
            {
                await _coordinator.ProcessExitedAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                _tracked.TryRemove(pair.Key, out _);
            }
            else if (IsProcessConfirmedAbsent(pair.Key))
            {
                // A vanished PID can be confirmed independently of OpenProcess. Inaccessibility alone is NOT
                // evidence of exit, and a reused PID is handled above when its identity can be verified.
                await _coordinator.ProcessExitedAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                _tracked.TryRemove(pair.Key, out _);
            }
            else
            {
                // An inaccessible-but-existing process is unknown, not proof of exit.
                await _coordinator.ReportProcessObservationUnreliableAsync(pair.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask RefreshProcessAsync(int pid, bool stateChangeObserved, CancellationToken cancellationToken)
    {
        if (!_tracked.TryGetValue(pid, out var expected)) return;
        var snapshot = await _processes.GetSnapshotAsync(pid, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            await _coordinator.ReportProcessObservationUnreliableAsync(expected, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (snapshot.HasExited || snapshot.Identity != expected)
        {
            await _coordinator.ProcessExitedAsync(expected, cancellationToken).ConfigureAwait(false);
            _tracked.TryRemove(pid, out _);
            return;
        }
        var windows = await _windows.GetWindowsAsync(pid, cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _eventFaulted) != 0) throw new InvalidOperationException("Window event observation failed.");
        await _coordinator.ObserveWindowsAsync(expected, windows, stateChangeObserved, observationReliable: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FaultAsync(string message)
    {
        try { _windowEvents.Stop(); } catch (Exception) { }
        try { await _coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        try { _runCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        lock (_sync) _tracked.Clear();
        var previous = Status;
        PublishStatus(new ApplicationMonitorStatus(false, false, message,
            TotalTerminatedProcesses: previous.TotalTerminatedProcesses,
            ActivityEventCount: _countingLog?.Count ?? previous.ActivityEventCount, DryRunEnabled: true));
    }

    private void PublishStatus(ApplicationMonitorStatus status)
    {
        long generation;
        lock (_sync)
        {
            _status = status;
            generation = ++_statusGeneration;
        }
        ThreadPool.QueueUserWorkItem(_ => _ = NotifyStatusAsync(generation, status));
    }

    private async Task NotifyStatusAsync(long generation, ApplicationMonitorStatus status)
    {
        await _statusNotifyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) if (generation != _statusGeneration) return;
            try { StatusChanged?.Invoke(this, status); }
            catch { /* Observer failures must not compromise process observation. */ }
        }
        finally { _statusNotifyGate.Release(); }
    }

    private void OnActivityCountChanged(object? sender, int count) =>
        PublishStatus(Status with { ActivityEventCount = count });

    private async ValueTask ReapStoppedGenerationAsync(CancellationToken cancellationToken)
    {
        var cancellation = _runCancellation;
        var runTask = _runTask;
        var tickTask = _tickTask;
        if (runTask is null && tickTask is null && cancellation is null) return;
        cancellation?.Cancel();
        _signals?.Writer.TryComplete();
        if (runTask is not null)
        {
            try { await runTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        }
        if (tickTask is not null)
        {
            try { await tickTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        }
        cancellation?.Dispose();
        _runCancellation = null;
        _runTask = null;
        _tickTask = null;
        _signals = null;
        _tracked.Clear();
    }

    private static bool IsProcessConfirmedAbsent(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById only throws ArgumentException for an ID that is not currently present.
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Access denied or an inconclusive query MUST NOT be treated as an exit.
            return false;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string ComputeConfigurationFingerprint(UserSettings settings, IReadOnlyList<MonitoredApplication> applications)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings.GracePeriodSeconds,
            settings.ProtectedExecutablePaths,
            Applications = applications.OrderBy(app => app.Id).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static int GetCurrentSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }
    private sealed record MonitorSignal(int? ProcessId, Exception? Error, bool IsTick);
}
