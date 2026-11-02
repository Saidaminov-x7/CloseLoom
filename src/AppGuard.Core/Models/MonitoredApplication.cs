namespace AppGuard.Core.Models;

public sealed record MonitoredApplication(
    Guid Id,
    string Name,
    string ExecutablePath,
    bool Enabled = true,
    TimeSpan? GracePeriod = null,
    bool MayContinueInBackground = false,
    bool AllowBackgroundTerminationConsent = false,
    bool IsProtected = false)
{
    public TimeSpan EffectiveGracePeriod => GracePeriod ?? TimeSpan.FromSeconds(3);
}
