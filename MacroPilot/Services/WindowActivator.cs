using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MacroPilot.Services;

/// <summary>枚举顶层窗口 + 把指定进程/标题的窗口调到前台。用于"激活窗口"动作。</summary>
public static class WindowActivator
{
    public sealed record WinInfo(IntPtr Hwnd, string Title, string Process, int Pid);

    // "桌面"目标的进程名哨兵：ActivateWindow 动作的 TargetProcess=此值时表示聚焦桌面；
    // TargetTitle 为屏幕设备名（空=不限屏，仅让所有应用失活）。
    public const string DesktopSentinel = "__DESKTOP__";

    /// <summary>聚焦 shell 桌面（所有应用窗口失活）；monitorDevice 非空时把光标停到该屏中心。</summary>
    public static void FocusDesktop(string monitorDevice)
    {
        var shell = GetShellWindow();
        if (shell != IntPtr.Zero) ForceForeground(shell);
        if (!string.IsNullOrEmpty(monitorDevice))
        {
            var m = MacroPilot.Input.ScreenInfo.ByDevice(monitorDevice);
            SetCursorPos(m.Left + m.Width / 2, m.Top + m.Height / 2);
        }
    }

    /// <summary>列出 Alt+Tab 可见的顶层窗口（含无标题的全屏/无边框应用窗口，排除工具窗/子窗口）。</summary>
    public static List<WinInfo> ListTopWindows()
    {
        var list = new List<WinInfo>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsAltTabWindow(h)) return true;
            int len = GetWindowTextLength(h);
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;                 // 排除本程序自身
            string proc = ProcessName(pid);
            if (proc == "explorer" && title == "Program Manager") return true;
            list.Add(new WinInfo(h, title, proc, (int)pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Alt+Tab 判定：可见、非工具窗；有属主的窗口(对话框等)默认排除，除非显式标了 APPWINDOW。
    private static bool IsAltTabWindow(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        IntPtr owner = GetWindow(h, GW_OWNER);
        long ex = GetWindowLongX(h, GWL_EXSTYLE);
        bool tool = (ex & WS_EX_TOOLWINDOW) != 0;
        bool app = (ex & WS_EX_APPWINDOW) != 0;
        if (tool) return false;
        if (owner != IntPtr.Zero && !app) return false;
        return true;
    }

    /// <summary>
    /// 激活目标窗口：优先按选定时记下的 PID 精确命中（同名多开靠它区分）；
    /// PID 已不存在（如目标程序重启）时，回退按进程名 + 标题包含匹配。返回是否成功。
    /// </summary>
    public static bool Activate(int pid, string process, string title, out string matchedTitle)
    {
        matchedTitle = "";
        string proc = NormalizeProcess(process);
        string ttl = (title ?? "").Trim();
        if (pid <= 0 && proc.Length == 0 && ttl.Length == 0) return false;

        var windows = ListTopWindows();
        WinInfo? found = null;

        // 1) PID 精确命中（若同时有进程名，校验名字一致，防 PID 被系统复用）。
        if (pid > 0)
            foreach (var w in windows)
                if (w.Pid == pid && (proc.Length == 0 || string.Equals(NormalizeProcess(w.Process), proc, StringComparison.OrdinalIgnoreCase)))
                { found = w; break; }

        // 2) 回退：进程名 + 标题包含（跨重启/换实例时用）。
        if (found == null)
            foreach (var w in windows)
            {
                bool procOk = proc.Length == 0 || string.Equals(NormalizeProcess(w.Process), proc, StringComparison.OrdinalIgnoreCase);
                bool titleOk = ttl.Length == 0 || w.Title.Contains(ttl, StringComparison.OrdinalIgnoreCase);
                if (procOk && titleOk) { found = w; break; }
            }

        if (found == null) return false;
        matchedTitle = found.Title;
        return ForceForeground(found.Hwnd);
    }

    // 去掉 .exe 后缀、转小写、trim，便于匹配。
    private static string NormalizeProcess(string p)
    {
        p = (p ?? "").Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        return p.ToLowerInvariant();
    }

    private static string ProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return ""; }
    }

    /// <summary>按窗口句柄激活到前台（用于配置时"闪一下"预览某个窗口）。</summary>
    public static bool ActivateHwnd(IntPtr hwnd) => hwnd != IntPtr.Zero && ForceForeground(hwnd);

    // 稳健置前：还原最小化 → 借 AttachThreadInput 绕过前台锁 → BringWindowToTop + SetForegroundWindow。
    private static bool ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);

        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint thisThread = GetCurrentThreadId();

        bool a1 = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
        bool a2 = targetThread != 0 && targetThread != thisThread && targetThread != fgThread && AttachThreadInput(thisThread, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (a1) AttachThreadInput(thisThread, fgThread, false);
            if (a2) AttachThreadInput(thisThread, targetThread, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    private const int SW_RESTORE = 9, SW_SHOW = 5;
    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;
    private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static long GetWindowLongX(IntPtr h, int idx) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(h, idx).ToInt64() : GetWindowLong32(h, idx);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
