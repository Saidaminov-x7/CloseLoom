using AppGuard.Core.Models;

namespace AppGuard.Core.Interfaces;

public interface IAllowlist
{
    ValueTask<MonitoredApplication?> FindExactExecutableAsync(string executablePath, CancellationToken cancellationToken);
}
