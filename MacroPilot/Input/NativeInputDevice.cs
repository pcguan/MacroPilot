using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>软件模拟后端（SendInput）。游戏若有反作弊可能屏蔽，普通程序可用。</summary>
public sealed class NativeInputDevice : IInputBackend
{
    private readonly bool _scanCode;
    public NativeInputDevice(string keyMode) { _scanCode = !string.Equals(keyMode, "VirtualKey", StringComparison.OrdinalIgnoreCase); }

    // 记录"我们当前真按着"的键/按钮，ReleaseAll 只抬这些——绝不能无条件发按钮抬起：
    // 裸的 MOUSEEVENTF_RIGHTUP 会被前台应用当成一次右键（弹出上下文菜单）。正常按下即抬起时这些都为 0，
    // 只有 F11 急停恰好卡在"已按下、还没抬起"之间才非 0，那时 ReleaseAll 精确收尾。
    private const uint BtnL = 1, BtnR = 2, BtnM = 4;
    private uint _btnDown;
    private byte _modDown;
    private ushort _keyDown;

    public bool IsOpen => true;
    public string Describe => $"软件模拟（{(_scanCode ? "ScanCode" : "VirtualKey")}）";
    // SendInput 绝对坐标带 VIRTUALDESK，一次调用即可落到整个虚拟桌面任意处 → 跨屏也整段连续拟人化。
    public bool ContinuousAcrossScreens => true;
    public bool Open() => true;
    public void Close() { }
    public void Dispose() { }

    public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
    {
        var mods = ModifierVks(modifier);
        foreach (var vk in mods) KeyEvent(vk, true);
        _modDown |= modifier;
        bool hasKey = KeyMap.TryVk(key, out var k);
        if (hasKey) { KeyEvent(k, true); _keyDown = k; }
        Sleep(holdMs, ct);   // 急停会在此抛出 → 下面的抬起被跳过，_modDown/_keyDown 仍置位，交给 ReleaseAll 收尾
        if (hasKey) { KeyEvent(k, false); _keyDown = 0; }
        for (int i = mods.Count - 1; i >= 0; i--) KeyEvent(mods[i], false);
        _modDown &= (byte)~modifier;
    }

    public void MouseClick(string button, double holdMs, CancellationToken ct = default)
    {
        var (down, up, bit) = button switch
        {
            "Right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, BtnR),
            "Middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, BtnM),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, BtnL),
        };
        MouseEvent(down, 0, 0, 0); _btnDown |= bit;
        Sleep(holdMs, ct);
        MouseEvent(up, 0, 0, 0); _btnDown &= ~bit;
    }

    // 参数为"虚拟桌面像素"(可跨屏、可负)。
    public void MouseMove(int vx, int vy) => MoveVirtual(vx, vy);

    /// <summary>把光标移到虚拟桌面绝对像素 (vx,vy)——覆盖全部显示器。CH9329 副屏降级也复用它。</summary>
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
        // 只抬"确实还按着"的键/按钮。无条件发 RIGHTUP/MIDDLEUP 会被前台应用当成一次右键/中键点击。
        if (_keyDown != 0) { KeyEvent(_keyDown, false); _keyDown = 0; }
        foreach (var (bit, vk) in KeyMap.ModifierVk) if ((_modDown & bit) != 0) KeyEvent(vk, false);
        _modDown = 0;
        if ((_btnDown & BtnL) != 0) MouseEvent(MOUSEEVENTF_LEFTUP, 0, 0, 0);
        if ((_btnDown & BtnR) != 0) MouseEvent(MOUSEEVENTF_RIGHTUP, 0, 0, 0);
        if ((_btnDown & BtnM) != 0) MouseEvent(MOUSEEVENTF_MIDDLEUP, 0, 0, 0);
        _btnDown = 0;
    }

    private static List<ushort> ModifierVks(byte modifier)
    {
        var list = new List<ushort>();
        foreach (var (bit, vk) in KeyMap.ModifierVk) if ((modifier & bit) != 0) list.Add(vk);
        return list;
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
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private void MouseEvent(uint flags, int dx, int dy, int data) => MouseEventStatic(flags, dx, dy, data);

    private static void MouseEventStatic(uint flags, int dx, int dy, int data)
    {
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi.dx = dx; inp.U.mi.dy = dy; inp.U.mi.mouseData = (uint)data; inp.U.mi.dwFlags = flags;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void Sleep(double ms, CancellationToken ct) => Services.PreciseTimer.Sleep(ms, ct);

    // ---- P/Invoke ----
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008, KEYEVENTF_EXTENDEDKEY = 0x0001;
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
