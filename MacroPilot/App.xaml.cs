using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MacroPilot.Models;
using MacroPilot.Services;

namespace MacroPilot;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时主动释放并销毁单实例锁，使下一个实例能立即获得（不必等进程完全终结）。
        try { if (_ownsMutex) { _mutex?.ReleaseMutex(); _ownsMutex = false; } _mutex?.Dispose(); } catch { }
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    /// <summary>把已在运行的那个实例唤到前台（取代"已在运行"模态提示）。找不到就静默退出。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("MacroPilot"))
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
        catch { }
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPilot");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:O}\n{ex}\n\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局统一悬停提示的时序：WPF 默认各控件继承系统值，实测有的按钮要悬停很久才弹。
        // 这里一次性覆盖 FrameworkElement 的默认元数据，所有控件（含代码里 new 出来的）都生效。
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(350));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(350));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
        ToolTipService.ShowOnDisabledProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(true));

        DispatcherUnhandledException += (_, ev) => { LogCrash(ev.Exception); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogCrash(ev.ExceptionObject as Exception);

        try { StartupCore(); } catch (Exception ex) { LogCrash(ex); throw; }
    }

    private void StartupCore()
    {
        // 上次运行若异常退出、没来得及还原被临时改成 1:1 的系统鼠标设置（关加速/速度=10），这里据备份还原。
        Input.Ch9329Device.RecoverMouseSettingsOnStartup();

        var doc = Storage.Load();

        // 按需提权：配置了 RunAsAdmin 且当前非管理员则 runas 重启
        if (TryElevate(doc)) { Shutdown(); return; }

        // 单实例：用独立锁名（避免和参考版 MacroPilot_dist 抢锁，两者可并存对比）。
        // 不抢初始所有权，改用 WaitOne 带超时——上一个实例正在退出时短暂还持锁，这里最多等 3 秒，
        // 等它释放后即可继续，从而消除“关闭后立刻重开提示已运行”的释放竞态。
        _mutex = new Mutex(false, "MacroPilot_Rewrite_SingleInstance");
        try { _ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(3), false); }
        catch (AbandonedMutexException) { _ownsMutex = true; } // 上个实例异常退出遗弃了锁 = 我们已获得
        if (!_ownsMutex)
        {
            // 【不能弹模态框】更新助手在失败/重试时也会拉起本体；若旧实例还没退干净，
            // 这个新实例就会带着一个（很可能在后台、用户看不见的）模态框常驻，占住安装目录，
            // 导致下次更新"改名失败：目录仍被占用"；而任务管理器「结束任务」发的 WM_CLOSE
            // 又被该模态框吃掉，表现为"进程杀不掉"。改成：把已有窗口唤到前台，然后立刻退出。
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        ThemeManager.Apply(doc.Theme);

        // 显式创建并显示主窗口（不依赖 StartupUri）
        var win = new MainWindow(doc);
        win.Show();
    }

    private static bool TryElevate(MacroDocument doc)
    {
        if (!doc.RunAsAdmin || IsAdministrator()) return false;
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return false;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // 用户取消 UAC 等 → 继续非提权运行
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsRunningAsAdmin() => IsAdministrator();

    /// <summary>以管理员身份重启本程序（runas，触发 UAC）。成功发起返回 true，用户取消/失败返回 false。</summary>
    public static bool RelaunchAsAdmin()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return false;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // 用户在 UAC 取消等
    }
}
