using CloseLoom.Core.Models;

namespace CloseLoom.Core.Interfaces;

public interface ISafeTerminationService
{
    ValueTask<TerminationDecision> TryTerminateAsync(ProcessIdentity expected, TerminationOptions options,
        bool hadEligibleWindow, bool stateChangedDuringGrace, CancellationToken cancellationToken);
}
