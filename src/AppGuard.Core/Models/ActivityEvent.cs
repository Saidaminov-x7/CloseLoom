namespace AppGuard.Core.Models;

public enum ActivityEventType
{
    ProcessDetected, WindowOpened, WindowClosed, GracePeriodStarted,
    GracePeriodCancelled, ProcessSkipped, TerminationRequested, ProcessTerminated, ProcessExited, Error
}

public sealed record ActivityEvent(DateTimeOffset TimestampUtc, string? ApplicationName,
    ActivityEventType Type, string Result, string? Reason = null, int? ProcessId = null);
