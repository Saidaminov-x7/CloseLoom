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
        public List<ProcessIdentity> Calls { get; } = [];
        public ValueTask<TerminationDecision> TryTerminateAsync(ProcessIdentity expected, TerminationOptions options,
            bool hadEligibleWindow, bool stateChangedDuringGrace, CancellationToken cancellationToken)
        {
            Assert.True(hadEligibleWindow);
            Assert.False(stateChangedDuringGrace);
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(expected);
            _called.TrySetResult();
            return ValueTask.FromResult(TerminationDecision.Permit());
        }
        public Task WaitForCallAsync() => _called.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FakeLog : IActivityLog
    {
        public ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
