using System.Collections.Generic;

namespace MacroPilot.Models;

/// <summary>全局文档/设置。字段与参考版 plans.json 对齐。</summary>
public sealed class MacroDocument
{
    public string Backend { get; set; } = "Serial";        // Serial(CH9329) / Native(软件模拟)
    public string Theme { get; set; } = "Dark";            // Light / Dark / System
    public string NativeKeyMode { get; set; } = "ScanCode"; // 软件模拟按键编码方式
    public string Port { get; set; } = "";                 // 串口，如 COM9
    public int BaudRate { get; set; } = 9600;
    public int DefaultHoldMs { get; set; } = 75;   // 默认按住时长：低于 75ms 部分应用会吞掉后续按键
    public int DefaultWaitMs { get; set; } = 1000;
    public int ExecutionDelayMs { get; set; } = 1000;      // 点运行后、真正执行前的缓冲（切窗口用）
    public bool TimingJitterEnabled { get; set; }          // 是否启用拟人化时间抖动
    public double TimingJitterMs { get; set; } = 5;        // 抖动区间 ± 毫秒(可小数)，独立保存，关闭抖动也保留
    public bool RunAsAdmin { get; set; }
    // 自动更新：启动时立即检查新版并直接更新；关闭则不做启动检查，仅靠 30s 轮询在状态栏提醒。
    public bool AutoUpdate { get; set; }
    // 方案结束后是否把本体自动激活到前台（默认是）。关则结束后仍停在后台，等用户手动点亮。
    public bool ActivateOnFinish { get; set; } = true;

    // 未知桥片：USB "VID:PID" -> 用户起的名
    public Dictionary<string, string> KnownBridges { get; set; } = new();

    // 窗口几何记忆：key -> 位置/大小/最大化。主窗口 key = "Main"，各对话框 key = 标题。
    // 见 Services/WindowMemory.cs；主窗口与所有对话框共用这一套。
    public Dictionary<string, WindowGeometry> Windows { get; set; } = new();

    // 0.0.18 的旧字段（只有主窗口）。保留仅为把老数据迁进 Windows["Main"]，迁完清零，勿再直接使用。
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public List<MacroPlan> Plans { get; set; } = new();
}

/// <summary>一个窗口记住的几何信息（WPF DIP 坐标）。</summary>
public sealed class WindowGeometry
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public bool Equals(WindowGeometry o) =>
        Left == o.Left && Top == o.Top && Width == o.Width && Height == o.Height && Maximized == o.Maximized;
}
