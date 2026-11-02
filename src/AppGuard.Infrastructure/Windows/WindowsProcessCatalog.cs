using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;

namespace AppGuard.Infrastructure.Windows;

public sealed class WindowsProcessCatalog : IProcessCatalog
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xFFFFFFFF;

    public ValueTask<ProcessSnapshot?> GetSnapshotAsync(int pid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pid <= 0) return ValueTask.FromResult<ProcessSnapshot?>(null);
        using var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
        if (handle.IsInvalid) return ValueTask.FromResult<ProcessSnapshot?>(null);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetProcessId(handle) != (uint)pid) return ValueTask.FromResult<ProcessSnapshot?>(null);
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) return ValueTask.FromResult<ProcessSnapshot?>(null);
            if (!ProcessIdToSessionId((uint)pid, out var sessionId)) return ValueTask.FromResult<ProcessSnapshot?>(null);
            var buffer = new StringBuilder(32768);
            var capacity = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref capacity)) return ValueTask.FromResult<ProcessSnapshot?>(null);
            var path = Path.GetFullPath(buffer.ToString());
            var startedAt = DateTimeOffset.FromFileTime(creation.ToLong());
            var wait = WaitForSingleObject(handle, 0);
            if (wait == WaitFailed || (wait != WaitObject0 && wait != WaitTimeout)) return ValueTask.FromResult<ProcessSnapshot?>(null);
            return ValueTask.FromResult<ProcessSnapshot?>(new ProcessSnapshot(
                new ProcessIdentity(pid, startedAt, path, checked((int)sessionId)), wait == WaitObject0));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        { return ValueTask.FromResult<ProcessSnapshot?>(null); }
    }

    public ValueTask<IReadOnlyList<ProcessSnapshot>> GetCurrentSessionProcessesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Process.GetProcesses is a local, read-only session snapshot. Access failures for individual entries are
        // intentionally isolated by GetSnapshotAsync; the original enumeration failure is allowed to propagate.
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        var processes = Process.GetProcesses();
        var snapshots = new List<ProcessSnapshot>(processes.Length);
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.SessionId != currentSessionId) continue;
                    var snapshot = GetSnapshotAsync(process.Id, cancellationToken).GetAwaiter().GetResult();
                    if (snapshot is { HasExited: false } && snapshot.Identity.SessionId == currentSessionId)
                        snapshots.Add(snapshot);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A single process disappearing or denying access cannot invalidate the session enumeration.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return ValueTask.FromResult<IReadOnlyList<ProcessSnapshot>>(snapshots);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly long ToLong() => ((long)High << 32) | Low;
    }

    private sealed class SafeProcessHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeProcessHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder executableName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}

public sealed class WindowsWindowMonitor : IWindowMonitor
{
    public ValueTask<IReadOnlyList<WindowSnapshot>> GetWindowsAsync(int pid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windows = new List<WindowSnapshot>();
        var cancelled = false;
        Exception? callbackError = null;
        var succeeded = EnumWindows((handle, _) =>
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; return false; }
            try
            {
                if (GetWindowThreadProcessId(handle, out var ownerPid) == 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not identify a top-level window owner.");
                if (ownerPid != (uint)pid || !IsWindowVisible(handle)) return true;

                var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(handle, ref placement))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect a top-level window.");
                Marshal.SetLastPInvokeError(0);
                var owner = GetWindow(handle, 4 /* GW_OWNER */);
                var ownerError = Marshal.GetLastWin32Error();
                if (owner == IntPtr.Zero && ownerError != 0)
                    throw new System.ComponentModel.Win32Exception(ownerError, "Could not inspect window ownership.");
                Marshal.SetLastPInvokeError(0);
                var extendedStyle = GetWindowLongPtrCompat(handle, -20 /* GWL_EXSTYLE */).ToInt64();
                var styleError = Marshal.GetLastWin32Error();
                if (extendedStyle == 0 && styleError != 0)
                    throw new System.ComponentModel.Win32Exception(styleError, "Could not inspect window styles.");
                var isToolWindow = (extendedStyle & 0x00000080L) != 0; // WS_EX_TOOLWINDOW
                var isMainWindow = owner == IntPtr.Zero && !isToolWindow;
                var minimized = placement.ShowCmd is 2 or 6 or 7;
                windows.Add(new WindowSnapshot(pid, handle, true, minimized, isMainWindow));
            }
            catch (Exception ex) { callbackError = ex; return false; }
            return true;
        }, IntPtr.Zero);
        if (cancelled || cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        if (callbackError is not null) throw callbackError;
        if (!succeeded) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate top-level windows; window state is unknown.");
        return ValueTask.FromResult<IReadOnlyList<WindowSnapshot>>(windows);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(IntPtr hWnd, int index);
    private static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hWnd, index)
        : new IntPtr(GetWindowLong32(hWnd, index));
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
