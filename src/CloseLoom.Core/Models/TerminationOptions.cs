namespace CloseLoom.Core.Models;

public sealed record TerminationOptions(bool DryRun = true, bool MonitoringEnabled = true,
    int CurrentSessionId = -1, IReadOnlySet<string>? ProtectedExecutablePaths = null);

public sealed record TerminationDecision(bool Allowed, string Reason)
{
    public static TerminationDecision Permit() => new(true, "All safety checks passed.");
    public static TerminationDecision Skip(string reason) => new(false, reason);
}
