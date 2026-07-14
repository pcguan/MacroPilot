using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacroPilot.Input;

/// <summary>
/// 显示器枚举与坐标换算。位置以"显示器设备名 + 屏内归一化 (0~1)"定义，
/// 运行时解析成虚拟桌面像素（可跨屏，副屏坐标可为负）。
/// </summary>
public static class ScreenInfo
{
    public sealed record Monitor(string Device, int Left, int Top, int Right, int Bottom, bool Primary)
    {
        public int Width => Math.Max(1, Right - Left);
        public int Height => Math.Max(1, Bottom - Top);
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        // 自定义屏幕编号：由 All() 按位置从左到右、从上到下重新赋值（1 起、连续），不依赖 \\.\DISPLAYn。
        public int Number { get; set; }
        public string Label => $"屏幕 {(Number > 0 ? Number.ToString() : "?")}" + (Primary ? "（主）" : "") + $"  {Width}×{Height}";
    }

    public static List<Monitor> All()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT __, IntPtr ___) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
                list.Add(new Monitor(mi.szDevice, mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right, mi.rcMonitor.Bottom, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        // 按位置从左到右、从上到下重新编号（1 起、连续），标签与"标识"浮层一致。
        list.Sort((a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));
        for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        return list;
    }

    /// <summary>所有显示器的并集包围盒（虚拟桌面矩形；右/下为独占边界，与 Monitor.Right/Bottom 同义）。</summary>
    public static (int Left, int Top, int Right, int Bottom) VirtualBounds()
    {
        var all = All();
        if (all.Count == 0) { var p = Primary(); return (p.Left, p.Top, p.Right, p.Bottom); }
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var m in all) { l = Math.Min(l, m.Left); t = Math.Min(t, m.Top); r = Math.Max(r, m.Right); b = Math.Max(b, m.Bottom); }
        return (l, t, r, b);
    }

    public static Monitor Primary()
    {
        foreach (var m in All()) if (m.Primary) return m;
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        return new Monitor("", 0, 0, w > 0 ? w : 1920, h > 0 ? h : 1080, true);
    }

    public static Monitor ByDevice(string device)
    {
        if (!string.IsNullOrEmpty(device))
            foreach (var m in All())
                if (string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase)) return m;
        return Primary(); // 找不到（如显示器被拔）→ 回退主屏
    }

    /// <summary>虚拟桌面像素点 → (显示器设备名, 归一化 nx,ny)。点不在任何屏内则归到主屏。</summary>
    public static (string device, double nx, double ny) FromPoint(int vx, int vy)
    {
        foreach (var m in All())
            if (m.Contains(vx, vy))
                return (m.Device, (vx - m.Left) / (double)m.Width, (vy - m.Top) / (double)m.Height);
        var p = Primary();
        return (p.Device, Clamp01((vx - p.Left) / (double)p.Width), Clamp01((vy - p.Top) / (double)p.Height));
    }

    /// <summary>(显示器, nx, ny) → 虚拟桌面像素。</summary>
    public static (int vx, int vy) Resolve(string device, double nx, double ny)
    {
        var m = ByDevice(device);
        int vx = m.Left + (int)Math.Round(Clamp01(nx) * m.Width);
        int vy = m.Top + (int)Math.Round(Clamp01(ny) * m.Height);
        return (vx, vy);
    }

    public static bool IsOnPrimary(int vx, int vy) => Primary().Contains(vx, vy);

    public static (int vx, int vy) CursorPos() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    // ---- P/Invoke ----
    private const uint MONITORINFOF_PRIMARY = 1;
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
}
