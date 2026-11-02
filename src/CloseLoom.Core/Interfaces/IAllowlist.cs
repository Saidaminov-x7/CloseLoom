using CloseLoom.Core.Models;

namespace CloseLoom.Core.Interfaces;

public interface IAllowlist
{
    ValueTask<MonitoredApplication?> FindExactExecutableAsync(string executablePath, CancellationToken cancellationToken);
}
