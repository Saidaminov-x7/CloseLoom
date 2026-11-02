using CloseLoom.Core.Models;

namespace CloseLoom.Core.Interfaces;

public interface IWindowMonitor
{
    ValueTask<IReadOnlyList<WindowSnapshot>> GetWindowsAsync(int pid, CancellationToken cancellationToken);
}
