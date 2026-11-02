using CloseLoom.Core.Models;

namespace CloseLoom.Core.Interfaces;

public interface IActivityLog
{
    ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken);
}
