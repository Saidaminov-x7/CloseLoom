using AppGuard.Core.Models;

namespace AppGuard.Infrastructure.Persistence;

public interface IApplicationStore
{
    ValueTask<IReadOnlyList<MonitoredApplication>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask AddOrUpdateAsync(MonitoredApplication application, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
