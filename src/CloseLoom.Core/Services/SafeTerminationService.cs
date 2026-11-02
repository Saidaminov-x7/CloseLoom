using CloseLoom.Core.Interfaces;
using CloseLoom.Core.Models;
using CloseLoom.Core.Policies;

namespace CloseLoom.Core.Services;

/// <summary>Performs a fresh fail-closed validation immediately before dry-run reporting or termination.</summary>
public sealed class SafeTerminationService(
    IAllowlist allowlist,
    IProcessCatalog processes,
    IWindowMonitor windows,
    IProcessTerminator terminator,
    IActivityLog activityLog,
    TerminationPolicy policy) : ISafeTerminationService
{
    public async ValueTask<TerminationDecision> TryTerminateAsync(
        ProcessIdentity expected,
        TerminationOptions options,
        bool hadEligibleWindow,
        bool stateChangedDuringGrace,
        CancellationToken cancellationToken)
    {
        MonitoredApplication? application = null;
        try
        {
            application = await allowlist.FindExactExecutableAsync(expected.ExecutablePath, cancellationToken).ConfigureAwait(false);
            ProcessSnapshot? process = await processes.GetSnapshotAsync(expected.Pid, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<WindowSnapshot>? openWindows = process is null || process.HasExited
                ? []
                : await windows.GetWindowsAsync(expected.Pid, cancellationToken).ConfigureAwait(false);
            var decision = policy.Evaluate(application, expected, process, openWindows, options, hadEligibleWindow, stateChangedDuringGrace);
            if (!decision.Allowed)
            {
                await Log(ActivityEventType.ProcessSkipped, "Skipped", decision.Reason).ConfigureAwait(false);
                return decision;
            }

            if (options.DryRun)
            {
                await Log(ActivityEventType.ProcessSkipped, "Dry run", "Would terminate this verified process; Dry Run is enabled.").ConfigureAwait(false);
                return TerminationDecision.Skip("Dry Run: process was not terminated.");
            }

            await terminator.TerminateAsync(expected, cancellationToken).ConfigureAwait(false);
            await Log(ActivityEventType.ProcessTerminated, "Process exit confirmed").ConfigureAwait(false);
            return TerminationDecision.Permit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Log(ActivityEventType.ProcessSkipped, "Cancelled", "Termination check was cancelled.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Do not include exception messages: implementations may expose user paths or other private data.
            await activityLog.WriteAsync(new ActivityEvent(DateTimeOffset.UtcNow, application?.Name,
                ActivityEventType.Error, "Error", ex.GetType().Name, expected.Pid), CancellationToken.None).ConfigureAwait(false);
            return TerminationDecision.Skip("A safety check failed; action was skipped.");
        }

        ValueTask Log(ActivityEventType type, string result, string? reason = null) =>
            activityLog.WriteAsync(new ActivityEvent(DateTimeOffset.UtcNow, application?.Name, type, result, reason, expected.Pid), cancellationToken);
    }
}
