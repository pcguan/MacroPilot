using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows;
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
            Services.ThemedDialog.Show("MacroPilot 已在运行，请勿重复启动。", "MacroPilot",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
