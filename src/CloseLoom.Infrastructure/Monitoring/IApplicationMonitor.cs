namespace CloseLoom.Infrastructure.Monitoring;

public sealed record ApplicationMonitorStatus(
    bool IsRunning,
    bool IsPaused,
    string? LastError = null,
    int TrackedProcesses = 0,
    int TotalTerminatedProcesses = 0,
    int ActivityEventCount = 0,
    bool DryRunEnabled = true);

/// <summary>Lifecycle boundary for the explicitly allowlisted Windows monitor.</summary>
public interface IApplicationMonitor : IAsyncDisposable
{
    ApplicationMonitorStatus Status { get; }
    event EventHandler<ApplicationMonitorStatus>? StatusChanged;
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
    ValueTask PauseAsync(CancellationToken cancellationToken = default);
    ValueTask ResumeAsync(CancellationToken cancellationToken = default);
}
