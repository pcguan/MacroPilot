using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroPilot.Services;

/// <summary>
/// 宏录制的采集层：WH_MOUSE_LL + WH_KEYBOARD_LL 低级钩子，把真实物理输入按时间戳入队，
/// 停止时一次性取出交给 <see cref="RecordingCompiler"/> 归纳。只采集、不理解——所有规则都在归纳器里。
///
/// 沿用 MouseTraceRecorder 的既有结论：
///   · 回调受 LowLevelHooksTimeout(300ms) 约束且在系统输入路径上，必须极轻——只入队值类型，零分配零 IO；
///   · 过滤 LLMHF_INJECTED / LLKHF_INJECTED（本程序自己合成的输入不录）；
///   · 钩子必须装在带消息泵的线程 → 由 UI 线程 Start()。
///
/// 排除区（录制悬浮条）：落在区内的鼠标按下连同配对的抬起一起丢弃——点「完成」这一下不能录进方案。
/// 悬浮条因此必须是固定位置（不可拖动），排除矩形才在整场录制里稳定成立。
/// </summary>
public sealed class InputRecorder
{
    public bool IsRecording { get; private set; }
    /// <summary>已录的"动作级"事件数（按下/滚轮/键按下），供悬浮条显示。移动不计——那是噪声级别的量。</summary>
    public int ActionCount => _actionCount;

    private const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
        WM_MOUSEWHEEL = 0x020A;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int LLMHF_INJECTED = 0x1, LLKHF_INJECTED = 0x10;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private HookProc? _mouseProc, _keyProc;        // 必须持有引用防 GC 回收委托
    private IntPtr _mouseHook, _keyHook;
    private readonly Stopwatch _clock = new();
    private readonly ConcurrentQueue<RecEvent> _buf = new();
    private volatile int _actionCount;

    private (int l, int t, int r, int b)[] _exclude = Array.Empty<(int, int, int, int)>();
    private readonly bool[] _excludedDown = new bool[3];   // 排除区里按下的按钮：配对的抬起也要丢
    private readonly bool[] _keyIsDown = new bool[256];    // 长按的系统自动重复：只录第一次按下（省队列，计数也不虚高）

    /// <summary>开始录制（UI 线程调用）。excludePhysicalRects：物理像素矩形，落在其中的鼠标事件不录（悬浮条自身）。</summary>
    public bool Start(IEnumerable<(int l, int t, int r, int b)> excludePhysicalRects, out string error)
    {
        error = "";
        if (IsRecording) return true;
        _exclude = new List<(int, int, int, int)>(excludePhysicalRects).ToArray();
        Array.Clear(_excludedDown, 0, _excludedDown.Length);
        Array.Clear(_keyIsDown, 0, _keyIsDown.Length);
        while (_buf.TryDequeue(out _)) { }
        _actionCount = 0;
        try
        {
            _mouseProc = MouseCallback;
            _keyProc = KeyCallback;
            using var proc = Process.GetCurrentProcess();
            using var mod = proc.MainModule;
            var h = GetModuleHandle(mod?.ModuleName);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, h, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, h, 0);
            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            {
                Cleanup();
                error = "输入钩子安装失败（目标程序若以管理员运行，请在配置中开启管理员模式）。";
                return false;
            }
            _clock.Restart();
            IsRecording = true;
            return true;
        }
        catch (Exception ex) { Cleanup(); error = ex.Message; return false; }
    }

    /// <summary>停止并取回全部事件（按时间有序——单一队列天然有序）。</summary>
    public List<RecEvent> Stop()
    {
        if (!IsRecording) return new List<RecEvent>();
        IsRecording = false;
        Cleanup();
        var list = new List<RecEvent>(_buf.Count);
        while (_buf.TryDequeue(out var ev)) list.Add(ev);
        return list;
    }

    private void Cleanup()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        _mouseProc = null; _keyProc = null;
    }

    private bool InExclude(int x, int y)
    {
        foreach (var (l, t, r, b) in _exclude)
            if (x >= l && x < r && y >= t && y < b) return true;
        return false;
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int x = Marshal.ReadInt32(lParam);           // MSLLHOOKSTRUCT.pt.x
            int y = Marshal.ReadInt32(lParam + 4);       // .pt.y
            int data = Marshal.ReadInt32(lParam + 8);    // .mouseData（滚轮 delta 在高 16 位）
            int flags = Marshal.ReadInt32(lParam + 12);  // .flags
            if ((flags & LLMHF_INJECTED) == 0)
            {
                long t = _clock.ElapsedMilliseconds;
                switch (wParam.ToInt32())
                {
                    case WM_MOUSEMOVE:
                        _buf.Enqueue(new RecEvent(t, RecKind.MouseMove, x, y, 0));
                        break;
                    case WM_LBUTTONDOWN: Down(t, x, y, 0); break;
                    case WM_LBUTTONUP: Up(t, x, y, 0); break;
                    case WM_RBUTTONDOWN: Down(t, x, y, 1); break;
                    case WM_RBUTTONUP: Up(t, x, y, 1); break;
                    case WM_MBUTTONDOWN: Down(t, x, y, 2); break;
                    case WM_MBUTTONUP: Up(t, x, y, 2); break;
                    case WM_MOUSEWHEEL:
                        if (!InExclude(x, y))
                        {
                            _buf.Enqueue(new RecEvent(t, RecKind.Wheel, x, y, (short)(data >> 16)));
                            _actionCount++;
                        }
                        break;
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        void Down(long t, int x, int y, int b)
        {
            if (InExclude(x, y)) { _excludedDown[b] = true; return; }   // 点在悬浮条上：连同配对抬起一起丢
            _excludedDown[b] = false;
            _buf.Enqueue(new RecEvent(t, RecKind.MouseDown, x, y, b));
            _actionCount++;
        }
        void Up(long t, int x, int y, int b)
        {
            if (_excludedDown[b]) { _excludedDown[b] = false; return; }
            _buf.Enqueue(new RecEvent(t, RecKind.MouseUp, x, y, b));
        }
    }

    private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);          // KBDLLHOOKSTRUCT.vkCode
            int flags = Marshal.ReadInt32(lParam + 8);   // .flags
            if ((flags & LLKHF_INJECTED) == 0)
            {
                long t = _clock.ElapsedMilliseconds;
                switch (wParam.ToInt32())
                {
                    case WM_KEYDOWN: case WM_SYSKEYDOWN:
                        if (vk is >= 0 and < 256 && !_keyIsDown[vk])
                        {
                            _keyIsDown[vk] = true;
                            _buf.Enqueue(new RecEvent(t, RecKind.KeyDown, 0, 0, vk));
                            _actionCount++;
                        }
                        break;
                    case WM_KEYUP: case WM_SYSKEYUP:
                        if (vk is >= 0 and < 256) _keyIsDown[vk] = false;
                        _buf.Enqueue(new RecEvent(t, RecKind.KeyUp, 0, 0, vk));
                        break;
                }
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
