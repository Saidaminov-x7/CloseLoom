using CloseLoom.Core.Models;

namespace CloseLoom.Core.Interfaces;

public interface IProcessTerminator
{
    /// <summary>Returns only after the exact verified process instance is observed to have exited.</summary>
    ValueTask TerminateAsync(ProcessIdentity identity, CancellationToken cancellationToken);
}
