using System;

namespace MacroPilot.Input;

/// <summary>键鼠输入后端抽象：CH9329 硬件 或 软件模拟。</summary>
public interface IInputBackend : IDisposable
{
    bool IsOpen { get; }
    string Describe { get; }

    /// <summary>能否一次调用把光标放到整个虚拟桌面的任意位置（native 绝对 VIRTUALDESK 可；CH9329 只能主屏 0x04 + 相邻屏相对路由，不可）。
    /// true → 跨屏也整段连续拟人化；false → 跨屏须逐屏分段拟人化。</summary>
    bool ContinuousAcrossScreens => false;

    /// <summary>打开/连接，成功返回 true。</summary>
    bool Open();
    void Close();

    /// <summary>按下并保持 holdMs（可含小数）后松开一个键（含修饰键）。ct 取消时提前结束按住并立即抬键。</summary>
    void KeyTap(string key, byte modifier, double holdMs, System.Threading.CancellationToken ct = default);

    /// <summary>在当前光标位置点击鼠标，按住 holdMs（可含小数）。button: Left/Right/Middle。ct 取消时提前抬起。</summary>
    void MouseClick(string button, double holdMs, System.Threading.CancellationToken ct = default);

    /// <summary>移动鼠标到屏幕绝对坐标。</summary>
    void MouseMove(int x, int y);

    /// <summary>移动会话：拟人化等"一串连续移动"前后各调一次。硬件后端借此只关一次系统加速、跳过逐点判屏/路由；
    /// 软件后端无需实现（默认空）。会话内 MouseMove 由调用方保证落在同屏，或落在【相邻屏】（逐屏分段拟人化跨屏时，
    /// 段与段之间用一次相对收敛越过共享边——仅相邻屏可越）。</summary>
    void BeginMove() { }
    void EndMove() { }

    /// <summary>滚轮（正上负下）。</summary>
    void MouseWheel(int amount);

    /// <summary>松开所有键鼠（收尾/急停用）。</summary>
    void ReleaseAll();
}
