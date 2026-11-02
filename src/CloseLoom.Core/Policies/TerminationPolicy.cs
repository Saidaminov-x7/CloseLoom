using CloseLoom.Core.Models;

namespace CloseLoom.Core.Policies;

/// <summary>Fail-closed policy. Every property is checked against fresh data immediately before a termination request.</summary>
public sealed class TerminationPolicy
{
    public TerminationDecision Evaluate(MonitoredApplication? application, ProcessIdentity expected,
        ProcessSnapshot? current, IReadOnlyList<WindowSnapshot>? windows, TerminationOptions options,
        bool hadEligibleWindow, bool stateChangedDuringGrace)
    {
        if (!options.MonitoringEnabled) return TerminationDecision.Skip("Monitoring is stopped.");
        if (application is null || !application.Enabled) return TerminationDecision.Skip("Application is not enabled in the allowlist.");
        if (application.IsProtected) return TerminationDecision.Skip("Application is protected.");
        if (!PathEquals(application.ExecutablePath, expected.ExecutablePath)) return TerminationDecision.Skip("Executable does not match the allowlist entry.");
        if (options.ProtectedExecutablePaths?.Any(p => PathEquals(p, expected.ExecutablePath)) == true) return TerminationDecision.Skip("Executable is in the protected list.");
        if (!hadEligibleWindow) return TerminationDecision.Skip("No eligible user window was observed for this process.");
        if (stateChangedDuringGrace) return TerminationDecision.Skip("Process or window state changed during the grace period.");
        if (current is null) return TerminationDecision.Skip("Process is inaccessible or no longer exists.");
        if (current.HasExited) return TerminationDecision.Skip("Process has already exited.");
        if (current.Identity.Pid != expected.Pid || current.Identity.StartedAtUtc != expected.StartedAtUtc)
            return TerminationDecision.Skip("Process identity changed; PID may have been reused.");
        if (!PathEquals(current.Identity.ExecutablePath, expected.ExecutablePath)) return TerminationDecision.Skip("Current executable path does not match the selected application.");
        if (options.CurrentSessionId < 0 || current.Identity.SessionId != options.CurrentSessionId)
            return TerminationDecision.Skip("Process is not proven to belong to the current user session.");
        if (windows is null) return TerminationDecision.Skip("Window state could not be verified.");
        if (windows.Any(IsEligibleWindow)) return TerminationDecision.Skip("A visible top-level window is still open.");
        if (!options.DryRun && !application.AllowBackgroundTerminationConsent)
            return TerminationDecision.Skip("Explicit per-application consent is required before any real termination.");
        return TerminationDecision.Permit();
    }

    // Any visible top-level window, including owned dialogs, blocks termination. Minimized windows remain visible
    // through IsWindowVisible and are therefore retained; only EnumWindows results should be passed here.
    private static bool IsEligibleWindow(WindowSnapshot window) => window.IsVisible;
    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
