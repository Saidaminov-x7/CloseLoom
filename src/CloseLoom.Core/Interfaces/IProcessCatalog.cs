using CloseLoom.Core.Models;

namespace CloseLoom.Core.Interfaces;

public interface IProcessCatalog
{
    ValueTask<ProcessSnapshot?> GetSnapshotAsync(int pid, CancellationToken cancellationToken);

    /// <summary>
    /// Returns only fully identified processes in the current interactive user session. Per-process inaccessible,
    /// exited, or unidentifiable entries may be skipped. A failure to enumerate the session must throw; it must never
    /// be represented as an empty list.
    /// </summary>
    ValueTask<IReadOnlyList<ProcessSnapshot>> GetCurrentSessionProcessesAsync(CancellationToken cancellationToken);
}
