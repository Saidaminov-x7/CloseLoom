using System.Reflection;
using System.Runtime.InteropServices;
using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;
using AppGuard.Core.Services;
using AppGuard.Infrastructure.Monitoring;
using AppGuard.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AppGuard.Infrastructure.Tests;

public sealed class WindowsApplicationMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity Identity = new(420, Now, @"C:\Selected\Editor.exe", 1);
    private static readonly MonitoredApplication Application = new(Guid.NewGuid(), "Editor", Identity.ExecutablePath,
        GracePeriod: TimeSpan.FromSeconds(3));
    private static WindowSnapshot MainWindow(bool visible = true) => new(Identity.Pid, 10, visible, false, true);

    [Fact]
    public void NativeMessageLayoutMatchesWindowsMsgWithoutCallingWin32()
    {
        var type = typeof(WindowsWindowEventSource).GetNestedType("NativeMessage", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, Marshal.SizeOf(type));
    }

    [Fact]
    public async Task StartupOnlyReadsWindowsForExplicitlyAllowlistedExactPath()
    {
        var selected = Identity;
        var other = selected with { Pid = 421, ExecutablePath = @"C:\Other\Other.exe" };
        var fixture = new Fixture([new(selected, false), new(other, false)], [Application]);
        await fixture.StartAsync();

        Assert.Equal([selected.Pid], fixture.Windows.Calls.ToArray());
        Assert.Equal(1, fixture.Monitor.Status.TrackedProcesses);
        Assert.True(fixture.Monitor.Status.DryRunEnabled);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task MissingOwnerPidFailsClosedAndStopsPendingGracePeriod()
    {
        var fixture = new Fixture([new(Identity, false)], [Application], [MainWindow()]);
        await fixture.StartAsync();
        fixture.Windows.Current = [];
        fixture.Events.Fire(null);
        await WaitUntilAsync(() => !fixture.Monitor.Status.IsRunning);

        Assert.Equal(0, fixture.SafeTermination.Calls);
        Assert.Contains("unreliable", fixture.Monitor.Status.LastError!, StringComparison.OrdinalIgnoreCase);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task HookErrorCancelsPendingGraceAndStopsMonitoring()
    {
        var fixture = new Fixture([new(Identity, false)], [Application], [MainWindow()]);
        await fixture.StartAsync();
        fixture.Windows.Current = [];
        fixture.Events.Fire(Identity.Pid);
        await fixture.Windows.WaitForCallsAsync(2);
        fixture.Events.Fail(new InvalidOperationException("synthetic hook failure"));
        await WaitUntilAsync(() => !fixture.Monitor.Status.IsRunning);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();

        Assert.Equal(0, fixture.SafeTermination.Calls);
        Assert.Equal(1, fixture.Events.StopCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task FastHideThenShowInvalidatesFullGraceEvenWhenSnapshotsRace()
    {
        var fixture = new Fixture([new(Identity, false)], [Application], [MainWindow()]);
        await fixture.StartAsync();
        fixture.Windows.Current = [];
        fixture.Events.Fire(Identity.Pid);
        await WaitForLogAsync(fixture, ActivityEventType.GracePeriodStarted, 1);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));

        fixture.Windows.Current = [MainWindow()];
        fixture.Events.Fire(Identity.Pid);
        await WaitForLogAsync(fixture, ActivityEventType.GracePeriodCancelled, 1);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.Equal(0, fixture.SafeTermination.Calls);

        fixture.Windows.Current = [];
        fixture.Events.Fire(Identity.Pid);
        await WaitForLogAsync(fixture, ActivityEventType.GracePeriodStarted, 2);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => fixture.SafeTermination.Calls == 1);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task QueueOverflowImmediatelyStopsCoordinatorAndMonitor()
    {
        var fixture = new Fixture([new(Identity, false)], [Application], [MainWindow()]);
        fixture.Windows.BlockAtCall = 2;
        await fixture.StartAsync();
        fixture.Events.Fire(Identity.Pid);
        await fixture.Windows.WaitUntilBlockedAsync();
        for (var i = 0; i < 3000; i++) fixture.Events.Fire(Identity.Pid);
        fixture.Windows.ReleaseBlock();
        await WaitUntilAsync(() => !fixture.Monitor.Status.IsRunning);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, fixture.SafeTermination.Calls);
        Assert.Equal(0, fixture.Monitor.Status.TrackedProcesses);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task EnumerationFailureStopsRatherThanTreatingFailureAsNoProcesses()
    {
        var fixture = new Fixture([new(Identity, false)], [Application]);
        await fixture.StartAsync();
        fixture.Processes.EnumerationError = new InvalidOperationException("synthetic enumeration failure");
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !fixture.Monitor.Status.IsRunning);

        Assert.True(fixture.Processes.EnumerationCount >= 2);
        Assert.Equal(0, fixture.SafeTermination.Calls);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task PauseCompletesCleanupEvenIfCallerCancelsAfterHooksStop()
    {
        var fixture = new Fixture([new(Identity, false)], [Application], [MainWindow()]);
        fixture.Windows.BlockAtCall = 2;
        await fixture.StartAsync();
        fixture.Windows.Current = [];
        fixture.Events.Fire(Identity.Pid);
        await fixture.Windows.WaitUntilBlockedAsync();
        using var cancellation = new CancellationTokenSource();
        var pause = fixture.Monitor.PauseAsync(cancellation.Token).AsTask();
        await fixture.Events.WaitForStopAsync();
        cancellation.Cancel();
        fixture.Windows.ReleaseBlock();
        await pause;
        fixture.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(fixture.Monitor.Status.IsPaused);
        Assert.Equal(0, fixture.SafeTermination.Calls);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task StopAndRestartAfterObservationFaultReapsPreviousTasks()
    {
        var fixture = new Fixture([new(Identity, false)], [Application]);
        await fixture.StartAsync();
        fixture.Events.Fail(new InvalidOperationException("first failure"));
        await WaitUntilAsync(() => !fixture.Monitor.Status.IsRunning);

        await fixture.Monitor.StartAsync();
        Assert.True(fixture.Monitor.Status.IsRunning);
        Assert.Equal(2, fixture.Events.StartCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task PidReuseRetiresOldIdentityBeforeTrackingReplacement()
    {
        var fixture = new Fixture([new(Identity, false)], [Application]);
        await fixture.StartAsync();
        var replacement = Identity with { StartedAtUtc = Now.AddSeconds(1) };
        fixture.Processes.Snapshots = [new(replacement, false)];
        fixture.Processes.ByPid[Identity.Pid] = new(replacement, false);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Windows.WaitForCallsAsync(2);
        await WaitUntilAsync(() => fixture.Monitor.Status.TrackedProcesses == 1);

        Assert.Contains(fixture.Log.Events, entry => entry.Type == ActivityEventType.ProcessExited && entry.ProcessId == Identity.Pid);
        await fixture.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Expected monitor state was not observed.");
            await Task.Delay(5);
        }
    }

    private static Task WaitForLogAsync(Fixture fixture, ActivityEventType type, int count) =>
        WaitUntilAsync(() => fixture.Log.Events.Count(entry => entry.Type == type) >= count);

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = new(Now);
        public FakeProcessCatalog Processes { get; }
        public FakeWindowMonitor Windows { get; }
        public FakeWindowEventSource Events { get; } = new();
        public FakeLog Log { get; } = new();
        public FakeSafeTermination SafeTermination { get; } = new();
        public WindowsApplicationMonitor Monitor { get; }

        public Fixture(IReadOnlyList<ProcessSnapshot> snapshots, IReadOnlyList<MonitoredApplication> applications,
            IReadOnlyList<WindowSnapshot>? windows = null)
        {
            Processes = new FakeProcessCatalog(snapshots);
            Windows = new FakeWindowMonitor(windows ?? []);
            var coordinator = new GracePeriodCoordinator(SafeTermination, Log, Time);
            Monitor = new WindowsApplicationMonitor(Processes, Windows, new FakeApplicationStore(applications),
                new FakeSettingsStore(), coordinator, Events, Time, () => 1);
        }

        public ValueTask StartAsync() => Monitor.StartAsync();
        public async ValueTask DisposeAsync() { await Monitor.DisposeAsync(); }
    }

    private sealed class FakeWindowEventSource : IWindowEventSource
    {
        private Action<int?>? _changed;
        private Action<Exception>? _error;
        private TaskCompletionSource _stopped = NewSignal();
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public void Start(Action<int?> onWindowChanged, Action<Exception> onError) { _changed = onWindowChanged; _error = onError; StartCount++; _stopped = NewSignal(); }
        public void Stop() { StopCount++; _stopped.TrySetResult(); }
        public void Dispose() => Stop();
        public void Fire(int? pid) => _changed?.Invoke(pid);
        public void Fail(Exception exception) => _error?.Invoke(exception);
        public Task WaitForStopAsync() => _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FakeProcessCatalog(IReadOnlyList<ProcessSnapshot> snapshots) : IProcessCatalog
    {
        public IReadOnlyList<ProcessSnapshot> Snapshots { get; set; } = snapshots;
        public Dictionary<int, ProcessSnapshot?> ByPid { get; } = snapshots.ToDictionary(s => s.Identity.Pid, s => (ProcessSnapshot?)s);
        public Exception? EnumerationError { get; set; }
        public int EnumerationCount { get; private set; }
        public ValueTask<ProcessSnapshot?> GetSnapshotAsync(int pid, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ByPid.TryGetValue(pid, out var snapshot) ? snapshot : null);
        public ValueTask<IReadOnlyList<ProcessSnapshot>> GetCurrentSessionProcessesAsync(CancellationToken cancellationToken)
        {
            EnumerationCount++;
            if (EnumerationError is not null) throw EnumerationError;
            return ValueTask.FromResult(Snapshots);
        }
    }

    private sealed class FakeWindowMonitor(IReadOnlyList<WindowSnapshot> initial) : IWindowMonitor
    {
        private readonly TaskCompletionSource _blocked = NewSignal();
        private readonly TaskCompletionSource _release = NewSignal();
        public IReadOnlyList<WindowSnapshot> Current { get; set; } = initial;
        public System.Collections.Concurrent.ConcurrentQueue<int> Calls { get; } = new();
        public int? BlockAtCall { get; set; }
        public async ValueTask<IReadOnlyList<WindowSnapshot>> GetWindowsAsync(int pid, CancellationToken cancellationToken)
        {
            Calls.Enqueue(pid);
            var call = Calls.Count;
            if (BlockAtCall == call)
            {
                _blocked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return Current;
        }
        public Task WaitForCallsAsync(int count) => WaitCallsCoreAsync(count);
        private async Task WaitCallsCoreAsync(int count)
        {
            var end = DateTime.UtcNow.AddSeconds(3);
            while (Calls.Count < count)
            {
                if (DateTime.UtcNow >= end) throw new TimeoutException("Expected window snapshot was not requested.");
                await Task.Delay(5);
            }
        }
        public Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public void ReleaseBlock() => _release.TrySetResult();
    }

    private sealed class FakeApplicationStore(IReadOnlyList<MonitoredApplication> applications) : IApplicationStore
    {
        public ValueTask<IReadOnlyList<MonitoredApplication>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(applications);
        public ValueTask AddOrUpdateAsync(MonitoredApplication application, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class FakeSettingsStore : IUserSettingsStore
    {
        public ValueTask<UserSettings> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new UserSettings());
        public ValueTask SaveAsync(UserSettings settings, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeSafeTermination : ISafeTerminationService
    {
        public int Calls { get; private set; }
        public ValueTask<TerminationDecision> TryTerminateAsync(ProcessIdentity expected, TerminationOptions options,
            bool hadEligibleWindow, bool stateChangedDuringGrace, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(TerminationDecision.Skip("Fake only.")); }
    }

    private sealed class FakeLog : IActivityLog
    {
        public List<ActivityEvent> Events { get; } = [];
        public ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken) { Events.Add(activityEvent); return ValueTask.CompletedTask; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
