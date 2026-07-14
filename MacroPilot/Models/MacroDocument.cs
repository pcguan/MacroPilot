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

    // 未知桥片：USB "VID:PID" -> 用户起的名
    public Dictionary<string, string> KnownBridges { get; set; } = new();

    public List<MacroPlan> Plans { get; set; } = new();
}
