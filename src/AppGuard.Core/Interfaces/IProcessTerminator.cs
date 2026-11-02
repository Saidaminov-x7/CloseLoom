using AppGuard.Core.Models;

namespace AppGuard.Core.Interfaces;

public interface IProcessTerminator
{
    ValueTask TerminateAsync(ProcessIdentity identity, CancellationToken cancellationToken);
}
