using CloseLoom.Core.Interfaces;
using CloseLoom.Core.Models;

namespace CloseLoom.Infrastructure.Logging;

/// <summary>Decorates the durable activity log and counts entries successfully written during this instance.</summary>
public sealed class CountingActivityLog(IActivityLog inner) : IActivityLog
{
    private long _count;
    public int Count => checked((int)Math.Min(int.MaxValue, Interlocked.Read(ref _count)));
    public event EventHandler<int>? CountChanged;

    public async ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(activityEvent, cancellationToken).ConfigureAwait(false);
        var count = Interlocked.Increment(ref _count);
        if (count > int.MaxValue) Interlocked.Exchange(ref _count, int.MaxValue);
        try { CountChanged?.Invoke(this, Count); }
        catch { /* Observer failures must not affect durable logging. */ }
    }
}
