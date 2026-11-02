using AppGuard.Core.Models;

namespace AppGuard.Core.Interfaces;

public interface IProcessCatalog
{
    ValueTask<ProcessSnapshot?> GetSnapshotAsync(int pid, CancellationToken cancellationToken);
}
