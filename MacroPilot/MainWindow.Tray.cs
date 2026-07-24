using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MacroPilot;

/// <summary>
/// 系统托盘：图标常驻，双击显示主窗口，右键弹主题化菜单（显示 / 选方案直接跑 / 停止 / 退出）。
/// 最小化时按配置收进托盘（隐藏任务栏项）。用 Shell_NotifyIcon P/Invoke，菜单用 WPF ContextMenu 保持一致外观。
/// </summary>
public partial class MainWindow
{
    private const int WM_TRAYICON = 0x0400 + 1;   // WM_APP+1：托盘回调消息
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_LBUTTONUP = 0x0202;
    private bool _trayAdded;
    private IntPtr _trayIcon;

    private void InitTray()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        HwndSource.FromHwnd(h)?.AddHook(TrayHook);   // 常驻钩子（与运行期热键钩子并存，互不影响）
        try
        {
            // 用可执行文件自带的图标作托盘图标
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            _trayIcon = ico?.Handle ?? IntPtr.Zero;
        }
        catch { _trayIcon = IntPtr.Zero; }

        var data = NewData(h);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _trayIcon;
        data.szTip = "键鼠宏助手";
        _trayAdded = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void RemoveTray()
    {
        if (!_trayAdded) return;
        var h = new WindowInteropHelper(this).Handle;
        var data = NewData(h);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayAdded = false;
    }

    private IntPtr TrayHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int ev = lParam.ToInt32();
            if (ev == WM_LBUTTONDBLCLK) { RestoreFromTray(); handled = true; }
            else if (ev == WM_RBUTTONUP) { ShowTrayMenu(); handled = true; }
        }
        return IntPtr.Zero;
    }

    // 从托盘/最小化恢复主窗口到前台。
    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) Services.WindowActivator.ActivateHwnd(h);
    }

    // 最小化到托盘：隐藏窗口 + 撤掉任务栏项，图标仍在托盘。
    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowTrayMenu()
    {
        var cm = new ContextMenu();
        cm.Items.Add(MakeMenuItem("显示主窗口", RestoreFromTray));
        cm.Items.Add(new Separator());

        // 选方案直接跑（子菜单）；运行中则整组禁用
        var runItem = new MenuItem { Header = "运行方案" };
        bool running = _runner is { IsRunning: true };
        if (_plans.Count == 0) runItem.Items.Add(new MenuItem { Header = "（暂无方案）", IsEnabled = false });
        foreach (var p in _plans)
        {
            var plan = p;
            var mi = new MenuItem { Header = plan.Name, IsEnabled = !running && plan.Steps.Count > 0 };
            mi.Click += (_, _) => RunPlanFromTray(plan);
            runItem.Items.Add(mi);
        }
        cm.Items.Add(runItem);
        cm.Items.Add(MakeMenuItem("停止运行", () => _runner?.Stop(), running));
        cm.Items.Add(new Separator());
        cm.Items.Add(MakeMenuItem("退出", () => { _exiting = true; Close(); }));

        // 托盘菜单需先把某个窗口置前，点菜单外才会正常关闭（经典 tray 菜单怪癖）。
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetForegroundWindow(h);
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        cm.IsOpen = true;
    }

    private bool _exiting;   // true = 真正退出（区别于最小化到托盘）

    // 从托盘运行一个方案：运行中则忽略，否则直接跑其克隆快照（不改动当前正在编辑的方案）。
    private void RunPlanFromTray(Models.MacroPlan plan)
    {
        if (_runner is { IsRunning: true } || _startingRun) return;
        if (plan.Steps.Count == 0) return;
        RunPlan(BuildRunPlan(plan), plan.Name);
    }

    private NOTIFYICONDATA NewData(IntPtr h) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = h,
        uID = 1,
        szTip = "",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
}
