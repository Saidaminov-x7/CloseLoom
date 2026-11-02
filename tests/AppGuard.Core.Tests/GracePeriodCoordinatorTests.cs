using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;
using AppGuard.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AppGuard.Core.Tests;

public sealed class GracePeriodCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static MonitoredApplication Application => new(Guid.NewGuid(), "Editor", @"C:\Apps\Editor.exe", GracePeriod: TimeSpan.FromSeconds(3));
    private static ProcessIdentity Identity(int pid, int second = 0) => new(pid, Now.AddSeconds(second), @"C:\Apps\Editor.exe", 1);
    private static TerminationOptions Options => new(DryRun: true, MonitoringEnabled: true, CurrentSessionId: 1);
    private static WindowSnapshot MainWindow(int pid, nint handle) => new(pid, handle, true, false, true);
    private static WindowSnapshot ClosedWindow(int pid, nint handle) => new(pid, handle, false, false, true);

    [Fact]
    public async Task LastMainWindowCloseStartsDelayAndCallsSafetyServiceAfterThreeSeconds()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(120);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);

        fixture.Time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Empty(fixture.Termination.Calls);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        await fixture.Termination.WaitForCallAsync();
        Assert.Equal(id, Assert.Single(fixture.Termination.Calls));
    }

    [Fact]
    public async Task StableClosedWindowRunsDecisionOnlyOnceAcrossRepeatedReconciliations()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(136);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();

        for (var scan = 0; scan < 8; scan++)
        {
            // Windows can report transient invisible/owned window changes after the main window closes.
            await fixture.Coordinator.ObserveWindowsAsync(id, [], stateChangedDuringGrace: scan % 2 == 0);
            fixture.Time.Advance(TimeSpan.FromSeconds(3));
        }

        Assert.Single(fixture.Termination.Calls);
    }

    [Fact]
    public async Task ReopeningWindowAfterCompletedDecisionAllowsOneNewDecision()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(137);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        Assert.Single(fixture.Termination.Calls);

        await fixture.Coordinator.ObserveWindowsAsync(id, [MainWindow(id.Pid, 2)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCountAsync(2);
        Assert.Equal(2, fixture.Termination.Calls.Count);

        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Yield();
        Assert.Equal(2, fixture.Termination.Calls.Count);
    }

    [Fact]
    public async Task SecondOpenWindowPreventsStartingGracePeriod()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(121);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1), MainWindow(id.Pid, 2)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, [MainWindow(id.Pid, 2)]);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();
        Assert.Empty(fixture.Termination.Calls);
    }

    [Fact]
    public async Task ReopenedWindowCancelsPendingDelayAndClosingAgainStartsFreshDelay()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(122);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.ObserveWindowsAsync(id, [MainWindow(id.Pid, 1)]);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.Empty(fixture.Termination.Calls);

        await fixture.Coordinator.ObserveWindowsAsync(id, [ClosedWindow(id.Pid, 1)]);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        Assert.Single(fixture.Termination.Calls);
    }

    [Fact]
    public async Task RawWindowStateChangeRestartsDelayEvenWhenSnapshotIsIdentical()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(130);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.ObserveWindowsAsync(id, [], stateChangedDuringGrace: true);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(fixture.Termination.Calls);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Termination.WaitForCallAsync();
        Assert.Single(fixture.Termination.Calls);
    }

    [Fact]
    public async Task UnreliableWindowObservationCancelsUntilReliableSnapshotReturns()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(131);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.ReportWindowObservationUnreliableAsync(id);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(fixture.Termination.Calls);

        await fixture.Coordinator.ObserveWindowsAsync(id, [], stateChangeObserved: false, observationReliable: true);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        Assert.Single(fixture.Termination.Calls);
    }

    [Fact]
    public async Task UnverifiableProcessCancelsGraceUntilIdentityRefresh()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(134);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.ReportProcessObservationUnreliableAsync(id);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(fixture.Termination.Calls);

        await fixture.Coordinator.ObserveWindowsAsync(id, []); // Fresh verified identity/window snapshot recovers observation.
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        Assert.Single(fixture.Termination.Calls);
    }

    [Fact]
    public async Task ForgettingAllowlistedProcessDoesNotInventExitEvent()
    {
        var fixture = new Fixture();
        var events = new List<ActivityEvent>();
        fixture.Coordinator.ActivityOccurred += (_, e) => events.Add(e);
        await fixture.StartAsync();
        var id = Identity(135);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ForgetProcessAsync(id);
        Assert.DoesNotContain(events, e => e.Type == ActivityEventType.ProcessExited);
        Assert.Equal(0, fixture.Coordinator.TrackedProcessCount);
    }

    [Fact]
    public async Task StopMonitoringCancelsPendingGracePeriod()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(123);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        await fixture.Coordinator.StopAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();
        Assert.Empty(fixture.Termination.Calls);
    }

    [Fact]
    public async Task ProcessExitDuringGraceRemovesOnlyThatInstance()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var exited = Identity(124);
        var other = Identity(125, 1);
        await fixture.TrackAsync(exited, [MainWindow(exited.Pid, 1)]);
        await fixture.TrackAsync(other, [MainWindow(other.Pid, 2)]);
        await fixture.Coordinator.ObserveWindowsAsync(exited, []);
        await fixture.Coordinator.ObserveWindowsAsync(other, []);
        await fixture.Coordinator.ProcessExitedAsync(exited);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        Assert.Equal(other, Assert.Single(fixture.Termination.Calls));
    }

    [Fact]
    public async Task SameExecutableInstancesHaveIndependentGracePeriodsAndPidReuseIdentity()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var first = Identity(126);
        var second = Identity(126, 10); // Same PID, different creation time: a different process instance.
        await fixture.TrackAsync(first, [MainWindow(first.Pid, 1)]);
        await fixture.TrackAsync(second, [MainWindow(second.Pid, 2)]);
        await fixture.Coordinator.ObserveWindowsAsync(first, []);
        await fixture.Coordinator.ObserveWindowsAsync(second, []);
        await fixture.Coordinator.ObserveWindowsAsync(first, [MainWindow(first.Pid, 1)]);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        Assert.Equal(second, Assert.Single(fixture.Termination.Calls));
    }

    [Fact]
    public async Task NoMainWindowEverObservedNeverStartsGracePeriod()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(127);
        await fixture.TrackAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();
        Assert.Empty(fixture.Termination.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GracePeriodBelowThreeSecondsIsRejected(int seconds)
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(129);
        var app = Application with { GracePeriod = TimeSpan.FromSeconds(seconds) };
        await fixture.Coordinator.TrackProcessAsync(app, id, Options, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Yield();
        Assert.Empty(fixture.Termination.Calls);
    }

    [Fact]
    public async Task DisposeCancelsTimersAndReleasesCoordinatorState()
    {
        var fixture = new Fixture();
        await fixture.StartAsync();
        var id = Identity(128);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        await fixture.Coordinator.DisposeAsync();
        await fixture.Coordinator.DisposeAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.Empty(fixture.Termination.Calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.Coordinator.StartAsync());
    }

    [Fact]
    public async Task DisposeWaitsForActiveGraceTaskToFinish()
    {
        var fixture = new Fixture();
        fixture.Termination.BlockCompletion();
        await fixture.StartAsync();
        var id = Identity(132);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Termination.WaitForCallAsync();
        var dispose = fixture.Coordinator.DisposeAsync().AsTask();
        try { Assert.False(dispose.IsCompleted); }
        finally { fixture.Termination.ReleaseCompletion(); }
        await dispose;
    }

    [Fact]
    public async Task CoordinatorPublishesOnlyRealWindowTransitionsAndMonitoringState()
    {
        var fixture = new Fixture();
        var events = new List<ActivityEvent>();
        fixture.Coordinator.ActivityOccurred += (_, activity) => events.Add(activity);
        await fixture.StartAsync();
        Assert.True(fixture.Coordinator.IsMonitoring);
        Assert.DoesNotContain(events, e => e.Type == ActivityEventType.ProcessDetected);

        var id = Identity(133);
        await fixture.TrackAsync(id, [MainWindow(id.Pid, 1)]);
        Assert.Equal(1, fixture.Coordinator.TrackedProcessCount);
        await fixture.Coordinator.ObserveWindowsAsync(id, []);
        Assert.Contains(events, e => e.Type == ActivityEventType.WindowOpened);
        Assert.Contains(events, e => e.Type == ActivityEventType.WindowClosed);
        await fixture.Coordinator.StopAsync();
        Assert.False(fixture.Coordinator.IsMonitoring);
        Assert.Equal(0, fixture.Coordinator.TrackedProcessCount);
    }

    private sealed class Fixture
    {
        public FakeTimeProvider Time { get; } = new(Now);
        public FakeTermination Termination { get; } = new();
        public GracePeriodCoordinator Coordinator { get; }
        private readonly FakeLog _log = new();

        public Fixture() => Coordinator = new(Termination, _log, Time);
        public ValueTask StartAsync() => Coordinator.StartAsync();
        public ValueTask TrackAsync(ProcessIdentity identity, IReadOnlyList<WindowSnapshot> windows) =>
            Coordinator.TrackProcessAsync(Application, identity, Options, windows);
    }

    private sealed class FakeTermination : ISafeTerminationService
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _completion;
        public List<ProcessIdentity> Calls { get; } = [];
        public async ValueTask<TerminationDecision> TryTerminateAsync(ProcessIdentity expected, TerminationOptions options,
            bool hadEligibleWindow, bool stateChangedDuringGrace, CancellationToken cancellationToken)
        {
            Assert.True(hadEligibleWindow);
            Assert.False(stateChangedDuringGrace);
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(expected);
            _called.TrySetResult();
            if (_completion is not null) await _completion.Task.ConfigureAwait(false);
            return TerminationDecision.Permit();
        }
        public Task WaitForCallAsync() => _called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public async Task WaitForCountAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Calls.Count < count)
                await Task.Delay(10, cts.Token);
        }
        public void BlockCompletion() => _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseCompletion() => _completion?.TrySetResult();
    }

    private sealed class FakeLog : IActivityLog
    {
        public ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
