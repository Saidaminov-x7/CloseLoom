using AppGuard.Core.Models;

namespace AppGuard.Core.Interfaces;

public interface ISafeTerminationService
{
    ValueTask<TerminationDecision> TryTerminateAsync(ProcessIdentity expected, TerminationOptions options,
        bool hadEligibleWindow, bool stateChangedDuringGrace, CancellationToken cancellationToken);
}
