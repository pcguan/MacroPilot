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

    /// <summary>
    /// 能否直接注入任意 Unicode 字符（中文等）。
    /// 键盘协议传的是【按键位置】而非字符，汉字没有对应键位——只有软件后端能靠 SendInput 的
    /// KEYEVENTF_UNICODE 绕过键盘布局直接投递字符；CH9329 是真实 HID 键盘，物理上做不到（返回 false，
    /// 文本只能走剪贴板粘贴）。
    /// </summary>
    bool SupportsUnicodeText => false;

    /// <summary>逐字符注入文本（仅 <see cref="SupportsUnicodeText"/> 为 true 时可用）。
    /// charDelayMs：每个字符之间的间隔，0=不等待。换行/制表由实现改发真实的回车/Tab 键。</summary>
    void TypeText(string text, double charDelayMs, System.Threading.CancellationToken ct = default) { }

    /// <summary>在当前光标位置点击鼠标，按住 holdMs（可含小数）。button: Left/Right/Middle。ct 取消时提前抬起。</summary>
    void MouseClick(string button, double holdMs, System.Threading.CancellationToken ct = default);

    /// <summary>移动鼠标到屏幕绝对坐标。</summary>
    void MouseMove(int x, int y);

    /// <summary>按下鼠标键并保持（拖动用）：期间的 MouseMove 均处于按住状态，直到 MouseUp。</summary>
    void MouseDown(string button);
    void MouseUp(string button);

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
