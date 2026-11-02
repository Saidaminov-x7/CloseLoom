using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AppGuard.Infrastructure.Monitoring;

/// <summary>Out-of-context WinEvent hooks for top-level window show, hide, and destroy events.</summary>
public sealed class WindowsWindowEventSource : IWindowEventSource
{
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const int ObjectIdWindow = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private static readonly ConcurrentDictionary<IntPtr, WinEventProc> FailedUnhookDelegates = new();

    // A valid SHOW/HIDE event records HWND ownership so DESTROY remains attributable even after Windows
    // invalidates its handle. Unknown owners still fault closed instead of guessing a PID.
    private readonly ConcurrentDictionary<IntPtr, int> _knownWindowOwners = new();
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _initialized = new();
    private readonly List<IntPtr> _hooks = [];
    private Thread? _thread;
    private uint _threadId;
    private Action<int?>? _onWindowChanged;
    private Action<Exception>? _onError;
    private WinEventProc? _callback;
    private bool _poisoned;
    private bool _disposed;

    public void Start(Action<int?> onWindowChanged, Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(onWindowChanged);
        ArgumentNullException.ThrowIfNull(onError);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_poisoned || _hooks.Count != 0) throw new InvalidOperationException("A previous Windows event hook could not be confirmed removed; this source cannot be restarted.");
            if (_thread is not null) throw new InvalidOperationException("Window event source is already running.");
            _knownWindowOwners.Clear();
            _onWindowChanged = onWindowChanged;
            _onError = onError;
            _initialized.Reset();
            _thread = new Thread(HookThreadMain) { IsBackground = true, Name = "AppGuard WinEvent hook" };
            _thread.Start();
        }
        _initialized.Wait();
        lock (_gate)
        {
            if (_hooks.Count != 3)
            {
                throw new InvalidOperationException("Windows window event hooks could not be installed.");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _initialized.Dispose();
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            thread = _thread;
            if (thread is not null && _threadId != 0 && !PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop the Windows event hook.");
        }
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread && !thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The Windows event hook thread did not stop in time.");
        lock (_gate)
        {
            _thread = null;
            if (_hooks.Count != 0) throw new InvalidOperationException("One or more Windows hooks could not be removed; callbacks remain rooted and monitoring must stay stopped.");
            _callback = null;
            _knownWindowOwners.Clear();
        }
    }

    private void HookThreadMain()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _callback = OnWinEvent;
            InstallHook(EventObjectDestroy);
            InstallHook(EventObjectShow);
            InstallHook(EventObjectHide);
            _initialized.Set();

            if (_hooks.Count == 3)
            {
                int result;
                while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                if (result < 0) ReportError(new Win32Exception(Marshal.GetLastWin32Error(), "The Windows window event loop failed."));
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            _initialized.Set();
            lock (_gate)
            {
                foreach (var hook in _hooks.ToArray())
                {
                    if (UnhookWinEvent(hook)) _hooks.Remove(hook);
                    else
                    {
                        _poisoned = true;
                        FailedUnhookDelegates[hook] = _callback!;
                        ReportError(new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove a Windows event hook; monitoring must remain stopped."));
                    }
                }
                _threadId = 0;
            }
        }
    }

    private void InstallHook(uint eventId)
    {
        var hook = SetWinEventHook(eventId, eventId, IntPtr.Zero, _callback!, 0, 0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install a Windows window event hook.");
        lock (_gate) _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (hwnd == IntPtr.Zero || objectId != ObjectIdWindow || childId != 0) return;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid > 0)
            {
                var ownerPid = checked((int)pid);
                if (eventType == EventObjectDestroy)
                    _knownWindowOwners.TryRemove(hwnd, out _);
                else
                    _knownWindowOwners[hwnd] = ownerPid;
                _onWindowChanged?.Invoke(ownerPid);
            }
            else if (_knownWindowOwners.TryGetValue(hwnd, out var rememberedPid))
            {
                if (eventType == EventObjectDestroy)
                    _knownWindowOwners.TryRemove(hwnd, out _);
                _onWindowChanged?.Invoke(rememberedPid);
            }
            else if (eventType == EventObjectDestroy)
            {
                // Windows may invalidate the HWND before delivering DESTROY. If we never saw its owner,
                // do not attribute this event to an allowlisted app or fault all monitoring because of an
                // unrelated window. Reconciliation checks current windows every two seconds. Active
                // termination remains unavailable; this is a best-effort dry-run observation only.
                return;
            }
            else
            {
                // An unidentified SHOW/HIDE event makes window observation unreliable; fail closed.
                _onWindowChanged?.Invoke(null);
            }
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void ReportError(Exception error)
    {
        try { _onError?.Invoke(error); }
        catch { /* Never unwind managed exceptions through a native callback. */ }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeMessage { public IntPtr HWnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; public uint LPrivate; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
