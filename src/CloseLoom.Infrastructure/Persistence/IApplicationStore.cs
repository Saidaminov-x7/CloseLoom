using CloseLoom.Core.Models;

namespace CloseLoom.Infrastructure.Persistence;

public interface IApplicationStore
{
    ValueTask<IReadOnlyList<MonitoredApplication>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask AddOrUpdateAsync(MonitoredApplication application, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
