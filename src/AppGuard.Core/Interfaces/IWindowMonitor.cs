using AppGuard.Core.Models;

namespace AppGuard.Core.Interfaces;

public interface IWindowMonitor
{
    ValueTask<IReadOnlyList<WindowSnapshot>> GetWindowsAsync(int pid, CancellationToken cancellationToken);
}
