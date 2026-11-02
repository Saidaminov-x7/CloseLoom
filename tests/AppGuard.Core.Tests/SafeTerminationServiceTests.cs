using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;
using AppGuard.Core.Policies;
using AppGuard.Core.Services;

namespace AppGuard.Core.Tests;

public sealed class SafeTerminationServiceTests
{
    private static readonly ProcessIdentity Identity = new(71, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), @"C:\Apps\Tool\tool.exe", 9);

    [Fact]
    public async Task DryRunRecordsWouldTerminateAndNeverCallsTerminator()
    {
        var fixture = new Fixture();
        var outcome = await fixture.Service.TryTerminateAsync(Identity, new(true, true, 9), true, false, CancellationToken.None);
        Assert.False(outcome.Allowed);
        Assert.Equal(0, fixture.Terminator.Calls);
        Assert.Contains(fixture.Log.Events, e => e.Result == "Dry run");
    }

    [Fact]
    public async Task ActiveModeCallsTerminatorOnlyAfterFreshValidation()
    {
        var fixture = new Fixture();
        var outcome = await fixture.Service.TryTerminateAsync(Identity, new(false, true, 9), true, false, CancellationToken.None);
        Assert.True(outcome.Allowed);
        Assert.Equal(1, fixture.Terminator.Calls);
    }

    [Fact]
    public async Task ChangedStateIsSkippedWithoutCallingTerminator()
    {
        var fixture = new Fixture();
        var outcome = await fixture.Service.TryTerminateAsync(Identity, new(false, true, 9), true, true, CancellationToken.None);
        Assert.False(outcome.Allowed);
        Assert.Equal(0, fixture.Terminator.Calls);
    }

    private sealed class Fixture
    {
        public FakeLog Log { get; } = new();
        public FakeTerminator Terminator { get; } = new();
        private readonly AppStub _allowlist = new(Identity);
        private readonly ProcessStub _process = new(Identity);
        private readonly WindowStub _windows = new();
        public SafeTerminationService Service { get; }

        public Fixture() => Service = new(_allowlist, _process, _windows, Terminator, Log, new TerminationPolicy());
    }

    private sealed class AppStub(ProcessIdentity identity) : IAllowlist
    {
        public ValueTask<MonitoredApplication?> FindExactExecutableAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult<MonitoredApplication?>(string.Equals(path, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? new MonitoredApplication(Guid.NewGuid(), "Tool", identity.ExecutablePath)
                : null);
    }
    private sealed class ProcessStub(ProcessIdentity identity) : IProcessCatalog
    {
        public ValueTask<ProcessSnapshot?> GetSnapshotAsync(int pid, CancellationToken cancellationToken) => ValueTask.FromResult<ProcessSnapshot?>(pid == identity.Pid ? new(identity, false) : null);
    }
    private sealed class WindowStub : IWindowMonitor
    {
        public ValueTask<IReadOnlyList<WindowSnapshot>> GetWindowsAsync(int pid, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<WindowSnapshot>>([]);
    }
    private sealed class FakeTerminator : IProcessTerminator
    {
        public int Calls { get; private set; }
        public ValueTask TerminateAsync(ProcessIdentity identity, CancellationToken cancellationToken) { Calls++; return ValueTask.CompletedTask; }
    }
    private sealed class FakeLog : IActivityLog
    {
        public List<ActivityEvent> Events { get; } = [];
        public ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken) { Events.Add(activityEvent); return ValueTask.CompletedTask; }
    }
}
