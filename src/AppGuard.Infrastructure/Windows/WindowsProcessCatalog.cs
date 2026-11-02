using System.Diagnostics;
using System.Runtime.InteropServices;
using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;

namespace AppGuard.Infrastructure.Windows;

public sealed class WindowsProcessCatalog : IProcessCatalog
{
    public ValueTask<ProcessSnapshot?> GetSnapshotAsync(int pid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return ValueTask.FromResult<ProcessSnapshot?>(null);
            var identity = new ProcessIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                Path.GetFullPath(process.MainModule?.FileName ?? throw new InvalidOperationException("Executable path unavailable.")), process.SessionId);
            return ValueTask.FromResult<ProcessSnapshot?>(new ProcessSnapshot(identity, process.HasExited));
        }
        catch (ArgumentException) { return ValueTask.FromResult<ProcessSnapshot?>(null); }
        catch (InvalidOperationException) { return ValueTask.FromResult<ProcessSnapshot?>(null); }
        catch (System.ComponentModel.Win32Exception) { return ValueTask.FromResult<ProcessSnapshot?>(null); }
    }
}

public sealed class WindowsWindowMonitor : IWindowMonitor
{
    public ValueTask<IReadOnlyList<WindowSnapshot>> GetWindowsAsync(int pid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windows = new List<WindowSnapshot>();
        var cancelled = false;
        var succeeded = EnumWindows((handle, _) =>
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; return false; }
            GetWindowThreadProcessId(handle, out var ownerPid);
            if (ownerPid == (uint)pid)
            {
                var visible = IsWindowVisible(handle);
                var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                var minimized = GetWindowPlacement(handle, ref placement) && placement.ShowCmd == 2;
                // Only top-level, visible windows are eligible. A process with any visible top-level window is conservatively retained.
                // Owned top-level dialogs remain relevant user windows. EnumWindows does not enumerate child controls.
                if (visible) windows.Add(new WindowSnapshot(pid, handle, true, minimized, true));
            }
            return true;
        }, IntPtr.Zero);
        if (cancelled || cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        if (!succeeded) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate top-level windows; window state is unknown.");
        return ValueTask.FromResult<IReadOnlyList<WindowSnapshot>>(windows);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public POINT MinPosition, MaxPosition;
        public RECT NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
}
