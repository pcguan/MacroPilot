using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>软件模拟后端（SendInput）。游戏若有反作弊可能屏蔽，普通程序可用。</summary>
public sealed class NativeInputDevice : IInputBackend
{
    private readonly bool _scanCode;
    private InputExecutionContext? _execution;
    public void SetExecutionContext(InputExecutionContext? context) => _execution = context;
    private void Checkpoint(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _execution?.Checkpoint(ct); }

    public NativeInputDevice(string keyMode) { _scanCode = !string.Equals(keyMode, "VirtualKey", StringComparison.OrdinalIgnoreCase); }

    // 记录"我们当前真按着"的键/按钮，ReleaseAll 只抬这些——绝不能无条件发按钮抬起：
    // 裸的 MOUSEEVENTF_RIGHTUP 会被前台应用当成一次右键（弹出上下文菜单）。正常按下即抬起时这些都为 0，
    // 只有 F11 急停恰好卡在"已按下、还没抬起"之间才非 0，那时 ReleaseAll 精确收尾。
    private const uint BtnL = 1, BtnR = 2, BtnM = 4;
    private uint _btnDown;
    private byte _modDown;
    private ushort _keyDown;
    private char? _unicodeDown;

    public bool IsOpen => true;
    public string Describe => $"软件模拟（{(_scanCode ? "ScanCode" : "VirtualKey")}）";
    // SendInput 支持虚拟桌面绝对坐标；拟人化仍沿物理屏幕的可达路径移动。
    public bool ContinuousAcrossScreens => true;
    private NativeMouseObserver? _mouseObserver;
    public bool Open() => true;
    public void Close() { EndMove(); _mouseObserver?.Dispose(); _mouseObserver = null; }
    public void Dispose() { Close(); }

    public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
    {
        var mods = ModifierVks(modifier);
        bool hasKey = KeyMap.TryVk(key, out var k);
        void Press()
        {
            foreach (var (bit, vk) in KeyMap.ModifierVk)
                if ((modifier & bit) != 0) { KeyEvent(vk, true); _modDown |= bit; }
            if (hasKey) { KeyEvent(k, true); _keyDown = k; }
        }
        void Release()
        {
            if (hasKey && _keyDown == k) { KeyEvent(k, false); _keyDown = 0; }
            for (int i = mods.Count - 1; i >= 0; i--) KeyEvent(mods[i], false);
            _modDown &= (byte)~modifier;
        }
        InputExecutionContext.Hold(holdMs, Press, Release, _execution, ct);
    }

    private static (uint Down, uint Up, uint Bit) ButtonFlags(string button) => button switch
    {
        "Right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, BtnR),
        "Middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, BtnM),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, BtnL),
    };

    public void MouseClick(string button, double holdMs, CancellationToken ct = default)
    {
        var (down, up, bit) = ButtonFlags(button);
        InputExecutionContext.Hold(holdMs,
            () => { MouseEvent(down, 0, 0, 0); _btnDown |= bit; },
            () => { MouseEvent(up, 0, 0, 0); _btnDown &= ~bit; }, _execution, ct);
    }

    public void MouseDown(string button)
    {
        var (down, _, bit) = ButtonFlags(button);
        MouseEvent(down, 0, 0, 0); _btnDown |= bit;
    }

    public void MouseUp(string button)
    {
        var (_, up, bit) = ButtonFlags(button);
        MouseEvent(up, 0, 0, 0); _btnDown &= ~bit;
    }

    // 参数为"虚拟桌面像素"(可跨屏、可负)。
    private AbsoluteMoveController? _moveController;
    private List<ScreenInfo.Monitor>? _moveScreens;
    private AbsoluteMoveController CreateMoveController()
    {
        var context = _execution;
        var observer = _mouseObserver ??= new NativeMouseObserver();
        return new(ScreenInfo.CursorPos, (x, y) =>
            {
                if (_moveScreens != null && !MouseRoutePlanner.SameTopology(_moveScreens, ScreenInfo.All()))
                    throw new InputSafetyException("移动过程中显示器布局发生变化，已安全停止方案。");
                MoveVirtual(x, y);
            }, Checkpoint,
            clock: context == null ? null : () => context.ActiveMilliseconds,
            clockExcludesPauses: context?.HasActiveClock == true, externalEpoch: () => observer.Epoch);
    }
    public void BeginMove()
    {
        if (_moveController != null) throw new InvalidOperationException("鼠标移动会话不能嵌套。");
        _moveScreens = ScreenInfo.All();
        try { _moveController = CreateMoveController(); }
        catch { _moveScreens = null; throw; }
    }
    public void EndMove() { _moveController = null; _moveScreens = null; }
    public void VerifyMousePosition(int x, int y) => MouseRoutePlanner.VerifyPosition(x, y);
    public void MouseMove(int vx, int vy, CancellationToken ct = default)
    {
        Checkpoint(ct);
        var screens = ScreenInfo.All();
        MouseRoutePlanner.RequireScreen(screens, new(vx, vy));
        if (_moveScreens != null && !MouseRoutePlanner.SameTopology(_moveScreens, screens))
            throw new InputSafetyException("移动过程中显示器布局发生变化，已安全停止方案。");
        (_moveController ?? CreateMoveController()).MoveTo(vx, vy, ct);
    }

    /// <summary>把光标移到虚拟桌面绝对像素 (vx,vy)——覆盖全部显示器。</summary>
    public static void MoveVirtual(int vx, int vy)
    {
        int ox = GetSystemMetrics(SM_XVIRTUALSCREEN), oy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0) vw = Math.Max(1, GetSystemMetrics(0));
        if (vh <= 0) vh = Math.Max(1, GetSystemMetrics(1));
        int nx = (int)Math.Round((vx - ox) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((vy - oy) * 65535.0 / Math.Max(1, vh - 1));
        MouseEventStatic(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
    }

    // 存储单位统一为"格"（与 CH9329 一致）：Windows 滚轮一格 = WHEEL_DELTA(120)，故这里 ×120。
    public void MouseWheel(int amount) => MouseEvent(MOUSEEVENTF_WHEEL, 0, 0, amount * 120);

    public void ReleaseAll()
    {
        // 单次抬起失败也继续尝试其他按键；不发送本任务从未按下的按钮。
        Exception? failure = null;
        void Attempt(Action release) { try { release(); } catch (Exception ex) { failure ??= ex; } }
        if (_unicodeDown is char unit) Attempt(() => { UnicodeEvent(unit, false); _unicodeDown = null; });
        if (_keyDown != 0) Attempt(() => { KeyEvent(_keyDown, false); _keyDown = 0; });
        foreach (var (bit, vk) in KeyMap.ModifierVk)
            if ((_modDown & bit) != 0) Attempt(() => { KeyEvent(vk, false); _modDown &= (byte)~bit; });
        foreach (var (bit, flag) in new[] { (BtnL, MOUSEEVENTF_LEFTUP), (BtnR, MOUSEEVENTF_RIGHTUP), (BtnM, MOUSEEVENTF_MIDDLEUP) })
            if ((_btnDown & bit) != 0) Attempt(() => { MouseEvent(flag, 0, 0, 0); _btnDown &= ~bit; });
        if (failure != null) throw failure;
    }

    private static List<ushort> ModifierVks(byte modifier)
    {
        var list = new List<ushort>();
        foreach (var (bit, vk) in KeyMap.ModifierVk) if ((modifier & bit) != 0) list.Add(vk);
        return list;
    }

    // ---- Unicode 文本注入 ----
    // 原理：KEYEVENTF_UNICODE 时 wVk 必须为 0、wScan 放 UTF-16 码元，系统据此给焦点窗口投递 WM_CHAR，
    // 【绕过键盘布局与输入法】，因此中文/emoji/任意符号都能输出。代价是走消息层——用 RawInput/
    // DirectInput 读扫描码的程序（多数游戏）收不到。
    public bool SupportsUnicodeText => true;

    public void TypeText(string text, double charDelayMs, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (int i = 0; i < text.Length; i++)
        {
            Checkpoint(ct);
            char c = text[i];
            // 换行/制表用【真实键】：不少程序不响应 WM_CHAR 的 \n（换行要的是 Enter 这个键事件）。
            if (c == '\n') KeyTap("ENTER", 0, 0, ct);
            else if (c == '\t') KeyTap("TAB", 0, 0, ct);
            else if (c == '\r') continue;                      // CRLF 的 \r 跳过，由上面的 \n 出 Enter
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // BMP 外字符（emoji）是代理对：两个码元连发，系统自行合成一个字符。
                UnicodeTap(c);
                i++;
                UnicodeTap(text[i]);
            }
            else UnicodeTap(c);
            if (charDelayMs > 0) Sleep(charDelayMs, ct);
        }
    }

    private void UnicodeTap(char unit)
    {
        UnicodeEvent(unit, true);
        _unicodeDown = unit;
        UnicodeEvent(unit, false);
        _unicodeDown = null; // 抬起失败时保留码元，交由运行器 finally 的 ReleaseAll 重试。
    }

    private static void UnicodeEvent(char unit, bool down)
    {
        var inp = new INPUT { type = INPUT_KEYBOARD };
        inp.U.ki.wVk = 0;                       // 必须为 0，否则系统按虚拟键解释
        inp.U.ki.wScan = unit;
        inp.U.ki.dwFlags = KEYEVENTF_UNICODE | (down ? 0u : KEYEVENTF_KEYUP);
        ValidateSent(SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>()), 1);
    }

    // 扩展键：其扫描码与小键盘键重叠，必须置 KEYEVENTF_EXTENDEDKEY 区分。
    // 否则 ScanCode 模式下方向键/Home/End/PgUp/PgDn/Ins/Del/右 Ctrl/右 Alt 等会被当成小键盘键
    // （如"左方向"VK_LEFT→扫描码 0x4B，NumLock 开时变成输入字符 "4"）。
    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0xA3 or 0xA5 or                       // 右 Ctrl / 右 Alt
        0x21 or 0x22 or 0x23 or 0x24 or       // PgUp / PgDn / End / Home
        0x25 or 0x26 or 0x27 or 0x28 or       // ← ↑ → ↓
        0x2D or 0x2E or                       // Insert / Delete
        0x5B or 0x5C or 0x5D or               // 左 Win / 右 Win / Apps
        0x6F or 0x90 or 0x2C => true,         // 小键盘 / 、NumLock、PrintScreen
        _ => false,
    };

    private void KeyEvent(ushort vk, bool down)
    {
        var inp = new INPUT { type = INPUT_KEYBOARD };
        inp.U.ki.wVk = _scanCode ? (ushort)0 : vk;
        inp.U.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        inp.U.ki.dwFlags = (down ? 0u : KEYEVENTF_KEYUP)
                         | (_scanCode ? KEYEVENTF_SCANCODE : 0u)
                         | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
        ValidateSent(SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>()), 1);
    }

    private void MouseEvent(uint flags, int dx, int dy, int data) => MouseEventStatic(flags, dx, dy, data);

    private static void MouseEventStatic(uint flags, int dx, int dy, int data)
    {
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi.dx = dx; inp.U.mi.dy = dy; inp.U.mi.mouseData = (uint)data; inp.U.mi.dwFlags = flags;
        inp.U.mi.dwExtraInfo = NativeMouseObserver.OwnInputTag;
        ValidateSent(SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>()), 1);
    }

    internal static void ValidateSent(uint actual, uint expected)
    {
        if (actual != expected)
            throw new InputSafetyException($"软件输入未成功发送（{actual}/{expected}）。请检查目标窗口权限和桌面状态，方案已安全停止。");
    }

    private void Sleep(double ms, CancellationToken ct)
    {
        if (_execution != null) _execution.Delay(ms, ct);
        else Services.PreciseTimer.Sleep(ms, ct);
    }

    // ---- P/Invoke ----
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008, KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
}
