using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MacroPilot.Services;

/// <summary>
/// 鼠标轨迹录制器：装 WH_MOUSE_LL 低级钩子，把【真实物理】鼠标移动/按键/滚轮采样为
/// (相对录制起点的高精度毫秒, 屏幕绝对 x, y, 事件类型) 落盘成 CSV，供离线分析真人手感、
/// 调优拟人化算法。只记录物理输入——本程序自己合成的移动带 LLMHF_INJECTED，直接滤掉。
///
/// 关键：LL 钩子回调受系统 LowLevelHooksTimeout（默认 300ms）约束、且跑在 UI 输入路径上，
/// 必须极轻——回调里只把一个值类型样本入队（无 IO、无字符串格式化）；后台 DispatcherTimer
/// 每 ~400ms 攒批格式化写盘。钩子须装在带消息泵的线程 → 由 UI 线程 Start()。
/// </summary>
public sealed class MouseTraceRecorder
{
    public bool IsRecording { get; private set; }
    public string? CurrentFile { get; private set; }
    /// <summary>攒批落盘时回调（UI 线程）：累计样本数、已录时长（秒）。供状态栏更新。</summary>
    public event Action<int, double>? Progress;

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
        WM_MOUSEWHEEL = 0x020A;
    private const int LLMHF_INJECTED = 0x00000001;

    // evt 字节码 → CSV 文本（在落盘线程展开，回调里只存字节，零字符串分配）。
    private static readonly string[] EvtName = { "M", "LD", "LU", "RD", "RU", "MD", "MU", "W" };

    private readonly struct Sample
    {
        public readonly double T; public readonly int X, Y; public readonly byte E;
        public Sample(double t, int x, int y, byte e) { T = t; X = x; Y = y; E = e; }
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelMouseProc? _proc;              // 必须持有引用防 GC 回收委托
    private IntPtr _hook;
    private readonly Stopwatch _clock = new();
    private readonly ConcurrentQueue<Sample> _buf = new();
    private int _count;
    private DispatcherTimer? _flush;
    private StreamWriter? _writer;

    /// <summary>开始录制到 traceDir 下带时间戳的新 CSV 文件（由 UI 线程调用）。</summary>
    public bool Start(string traceDir, out string error)
    {
        error = "";
        if (IsRecording) return true;
        try
        {
            Directory.CreateDirectory(traceDir);
            CurrentFile = Path.Combine(traceDir, $"mousetrace-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _writer = new StreamWriter(CurrentFile, append: false, new UTF8Encoding(false)) { AutoFlush = false };
            _writer.WriteLine("# MacroPilot 鼠标轨迹  started=" + DateTime.Now.ToString("o"));
            _writer.WriteLine("# t_ms=自录制开始的高精度毫秒；x/y=屏幕绝对像素；evt=M 移动 / LD LU RD RU MD MU 按键 / W 滚轮");
            _writer.WriteLine("t_ms,x,y,evt");
            _writer.Flush();
            while (_buf.TryDequeue(out _)) { }
            _count = 0;
            _clock.Restart();

            _proc = HookCallback;
            using var p = Process.GetCurrentProcess();
            using var m = p.MainModule;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(m?.ModuleName), 0);
            if (_hook == IntPtr.Zero)
            {
                error = "安装鼠标钩子失败（错误码 " + Marshal.GetLastWin32Error() + "）";
                try { _writer.Dispose(); } catch { }
                _writer = null; _proc = null;
                return false;
            }

            _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flush.Tick += (_, __) => Flush();
            _flush.Start();
            IsRecording = true;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { _writer?.Dispose(); } catch { }
            _writer = null; _proc = null;
            return false;
        }
    }

    /// <summary>停止录制：卸钩子、把剩余样本落盘、关文件。幂等。</summary>
    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _flush?.Stop(); _flush = null;
        Flush();                                   // 落最后一批
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
        _proc = null;
    }

    private void Flush()
    {
        var w = _writer;
        if (w == null) return;
        int n = 0;
        while (_buf.TryDequeue(out var s))
        {
            var evt = s.E < EvtName.Length ? EvtName[s.E] : "?";
            w.WriteLine($"{s.T:F2},{s.X},{s.Y},{evt}");
            n++;
        }
        if (n > 0) { try { w.Flush(); } catch { } }
        Progress?.Invoke(_count, _clock.Elapsed.TotalSeconds);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int flags = Marshal.ReadInt32(lParam + 12);            // MSLLHOOKSTRUCT.flags
            if ((flags & LLMHF_INJECTED) == 0)                     // 只留真实物理输入；滤掉本程序合成的移动
            {
                int e = wParam.ToInt32() switch
                {
                    WM_MOUSEMOVE => 0,
                    WM_LBUTTONDOWN => 1, WM_LBUTTONUP => 2,
                    WM_RBUTTONDOWN => 3, WM_RBUTTONUP => 4,
                    WM_MBUTTONDOWN => 5, WM_MBUTTONUP => 6,
                    WM_MOUSEWHEEL => 7,
                    _ => -1,
                };
                if (e >= 0)
                {
                    int x = Marshal.ReadInt32(lParam);             // MSLLHOOKSTRUCT.pt.x
                    int y = Marshal.ReadInt32(lParam + 4);         // MSLLHOOKSTRUCT.pt.y
                    _buf.Enqueue(new Sample(_clock.Elapsed.TotalMilliseconds, x, y, (byte)e));
                    System.Threading.Interlocked.Increment(ref _count);
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
