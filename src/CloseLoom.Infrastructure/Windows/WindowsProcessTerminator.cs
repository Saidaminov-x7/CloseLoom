using CloseLoom.Core.Interfaces;
using CloseLoom.Core.Models;

namespace CloseLoom.Infrastructure.Windows;

/// <summary>
/// Active termination is deliberately unavailable until CloseLoom can reliably prove that a target is not
/// a security product or another protected process. Dry Run remains available through the Core service.
/// </summary>
public sealed class WindowsProcessTerminator : IProcessTerminator
{
    public ValueTask TerminateAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        throw new InvalidOperationException(
            "Active process termination is disabled: CloseLoom cannot reliably identify every antivirus or protected application. Use Dry Run.");
    }
}
