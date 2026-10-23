using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>只计数外部移动事件，不保存轨迹、不拦截输入。独立消息线程避免 UI 阻塞导致漏检。</summary>
internal sealed class NativeMouseObserver : IDisposable
{
    internal static readonly IntPtr OwnInputTag = new(0x4D505630);
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly HookProc _callback;
    private uint _threadId;
    private long _epoch;
    private Exception? _error;
    private volatile bool _stopping;
    private int _disposed;

    internal NativeMouseObserver()
    {
        _callback = Observe;
        _thread = new Thread(Run) { IsBackground = true, Name = "MacroPilot mouse observer" };
        _thread.Start();
        if (!_ready.Wait(2000) || _error != null)
        {
            Dispose();
            throw new InputSafetyException("无法启动鼠标干预监听，已停止软件输入。", _error);
        }
    }

    internal long Epoch
    {
        get
        {
            if (!_thread.IsAlive || _error != null) throw new InputSafetyException("鼠标干预监听已结束，已停止软件输入。", _error);
            return Interlocked.Read(ref _epoch);
        }
    }

    private void Run()
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create the queue before allowing Dispose to post WM_QUIT.
            hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _ready.Set();
            while (!_stopping && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception ex) { _error = ex; }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            _ready.Set();
        }
    }

    private IntPtr Observe(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() == 0x200)
        {
            var input = Marshal.PtrToStructure<MouseHook>(data);
            if ((input.Flags & 1) == 0 || input.ExtraInfo != OwnInputTag) Interlocked.Increment(ref _epoch);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        uint id = Volatile.Read(ref _threadId);
        if (id != 0) PostThreadMessage(id, 0x12, IntPtr.Zero, IntPtr.Zero);
        if (_thread.Join(1000)) _ready.Dispose();
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook
    { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
