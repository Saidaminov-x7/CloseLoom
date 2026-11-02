using AppGuard.Core.Models;

namespace AppGuard.Core.Interfaces;

public interface IActivityLog
{
    ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken);
}
