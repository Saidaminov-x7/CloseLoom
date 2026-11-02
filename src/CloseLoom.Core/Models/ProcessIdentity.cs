namespace CloseLoom.Core.Models;

/// <summary>Stable process identity. PID alone is deliberately insufficient because Windows reuses PIDs.</summary>
public sealed record ProcessIdentity(int Pid, DateTimeOffset StartedAtUtc, string ExecutablePath, int SessionId);
public sealed record ProcessSnapshot(ProcessIdentity Identity, bool HasExited);
public sealed record WindowSnapshot(int ProcessId, nint Handle, bool IsVisible, bool IsMinimized, bool IsMainWindow);
