using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>
/// CH9329 USB-HID 桥片（串口）。协议帧：0x57 0xAB 0x00 cmd len data... checksum=sum&amp;0xFF。
/// 键盘 cmd 0x02 [modifier,0,k1..k6]；相对鼠标 cmd 0x05 [0x01,buttons,dx,dy,wheel]；绝对鼠标 cmd 0x04。
/// 真实 HID 只进前台窗口（执行前需把目标程序切到前台）。
/// 坑：不要开 dsrdtr（硬件流控会断通信），仅置 DTR/RTS。
/// </summary>
public sealed class Ch9329Device : IInputBackend
{
    private readonly string _port;
    private readonly int _baud;
    private SerialPort? _sp;

    public Ch9329Device(string port, int baud) { _port = port; _baud = baud; }

    public bool IsOpen => _sp is { IsOpen: true };
    public string Describe => $"CH9329 {_port}@{_baud}";

    public bool Open()
    {
        try
        {
            // ReadTimeout 50ms：本类不读 ACK（相对闭环靠"等光标真移动"踩节拍）；仅避免个别读操作长时间阻塞。
            _sp = new SerialPort(_port, _baud) { ReadTimeout = 50, WriteTimeout = 200 };
            _sp.Open();
            _sp.DtrEnable = true;
            _sp.RtsEnable = true;
            _sp.DiscardInBuffer();
            // 握手探测（失败也允许继续，部分桥片不回 info）
            Send(0x01, Array.Empty<byte>());
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public void Close()
    {
        try { if (_sp is { IsOpen: true }) { ReleaseAll(); _sp.Close(); } } catch { }
        _sp?.Dispose();
        _sp = null;
    }

    private static byte[] Packet(byte cmd, byte[] data)
    {
        var frame = new byte[5 + data.Length + 1];
        frame[0] = 0x57; frame[1] = 0xAB; frame[2] = 0x00; frame[3] = cmd; frame[4] = (byte)data.Length;
        Array.Copy(data, 0, frame, 5, data.Length);
        int sum = 0;
        for (int i = 0; i < frame.Length - 1; i++) sum += frame[i];
        frame[^1] = (byte)(sum & 0xFF);
        return frame;
    }

    private int _sendFails;
    private const int MaxConsecutiveSendFails = 3;
    private void Send(byte cmd, byte[] data)
    {
        if (_sp is not { IsOpen: true }) return;
        try
        {
            // 发送前清收缓冲即可顺带丢弃上一帧应答，避免堆积；不再等 ACK——
            // 相对闭环的节拍完全由 MouseMove 里的"等光标真移动"负责（更直接、不冗余）。
            _sp.DiscardInBuffer();
            var p = Packet(cmd, data);
            _sp.Write(p, 0, p.Length);
            _sendFails = 0;
        }
        catch (Exception ex)
        {
            // 单次抖动容忍；连续多次写失败＝设备大概率已掉，抛出让 Runner 以 Error 结束，
            // 别再对所有后续动作"静默假装成功"。（ReleaseAll/Close 里的 Send 在其各自 try 内，抛出会被吞掉，无碍）
            if (++_sendFails >= MaxConsecutiveSendFails)
            {
                _sendFails = 0;
                throw new System.IO.IOException($"CH9329 串口连续写入失败（设备可能已断开）：{ex.Message}", ex);
            }
        }
    }

    private void Keyboard(byte modifier, byte k1)
        => Send(0x02, new byte[] { modifier, 0x00, k1, 0, 0, 0, 0, 0 });

    public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
    {
        byte code = KeyMap.TryHid(key, out var c) ? c : (byte)0;
        if (code == 0 && modifier == 0) return; // 未知键名：不发只含修饰键的空报文
        Keyboard(modifier, code);
        Sleep(holdMs, ct);
        Keyboard(0, 0); // 全松
    }

    private static byte ButtonBit(string button) => button switch
    {
        "Left" => 0x01, "Right" => 0x02, "Middle" => 0x04, _ => 0x01
    };

    // 拖动等场景按住的鼠标键位集合：随所有鼠标帧（相对/绝对/滚轮）一起发送，
    // 否则移动帧的按键字节为 0 会把按住的键顶掉，拖动会中途变成松开。
    private byte _held;

    public void MouseDown(string button)
    {
        _held |= ButtonBit(button);
        Send(0x05, new byte[] { 0x01, _held, 0, 0, 0 });
    }

    public void MouseUp(string button)
    {
        _held = (byte)(_held & ~ButtonBit(button));
        Send(0x05, new byte[] { 0x01, _held, 0, 0, 0 });
    }

    public void MouseClick(string button, double holdMs, CancellationToken ct = default)
    {
        byte b = ButtonBit(button);
        Send(0x05, new byte[] { 0x01, (byte)(_held | b), 0, 0, 0 });   // 按下
        Sleep(holdMs, ct);
        Send(0x05, new byte[] { 0x01, _held, 0, 0, 0 });               // 松开（保留拖动按住的键）
    }

    // 参数为"虚拟桌面像素"。CH9329 的 0x04 绝对坐标 0–4096 只映射【主屏】，副屏只能靠相对移动跨过去：
    // - 目标在主屏 → 0x04 一帧精确到位，不受指针加速。
    // - 光标已在目标副屏内 → 直接屏内相对收敛（小移动不绕路）。
    // - 否则 → 0x04 重锚到主屏，再沿【相邻屏 BFS 路径】逐段跨共享边走到目标屏。
    //   屏幕拓扑全部运行时读取(ScreenInfo.All → EnumDisplayMonitors)，任意布局通用、非硬编码，
    //   不假设"主屏与每块副屏都相邻"——两屏只要在扩展桌面里连通就能走到。
    // ---- 移动会话（拟人化用）：一串连续【同屏】移动前后各调一次，只关一次系统加速、会话内走轻量相对收敛 ----
    private bool _inMoveSession;
    public void BeginMove()
    {
        if (_inMoveSession) return;
        _inMoveSession = true;
        DisableMouseAccel();   // 整段只关一次，会话内相对移动即 1:1
    }
    public void EndMove()
    {
        if (!_inMoveSession) return;
        _inMoveSession = false;
        RestoreMouseAccel();
        if (TimingDiag) FlushTiming();
    }

    public void MouseMove(int vx, int vy)
    {
        // 移动会话中（拟人化）：调用方保证同屏，直接相对收敛——不判主屏 0x04、不 BFS、不逐点开关加速。
        if (_inMoveSession)
        {
            if (TimingDiag)   // 临时：测每个相对闭环路点的真实收敛耗时（调优 CH9329 拟人化路点数）
            {
                var (bx, by) = ScreenInfo.CursorPos();
                var tsw = System.Diagnostics.Stopwatch.StartNew();
                RelativeConverge(vx, vy, null, "会话");
                tsw.Stop();
                double td = Math.Sqrt((double)(vx - bx) * (vx - bx) + (double)(vy - by) * (vy - by));
                lock (_tLock) _tbuf.Append($"{tsw.Elapsed.TotalMilliseconds:F1},{_lastFrames},{td:F0}\n");
            }
            else RelativeConverge(vx, vy, null, "会话");
            return;
        }

        var mons = ScreenInfo.All();
        ScreenInfo.Monitor? primary = null, target = null;
        foreach (var m in mons)
        {
            if (m.Primary) primary = m;
            if (target == null && m.Contains(vx, vy)) target = m;
        }
        primary ??= (mons.Count > 0 ? mons[0] : new ScreenInfo.Monitor("", 0, 0, 1920, 1080, true));

        // 目标在主屏 → 0x04 一发精确到位。
        if (primary.Contains(vx, vy)) { SendAbs04(primary, vx, vy, "主屏"); return; }

        target ??= NearestMonitor(vx, vy, mons, primary);
        var (cx, cy) = ScreenInfo.CursorPos();
        var sb = MoveDiag ? new System.Text.StringBuilder() : null;

        // 光标已在目标副屏内 → 直接屏内相对收敛，不绕主屏。
        if (target.Contains(cx, cy))
        {
            sb?.Append($"MOVE(屏内相对·屏{target.Number}) 目标=({vx},{vy}) 起点=({cx},{cy})\n");
            bool a0 = DisableMouseAccel();
            sb?.Append($"  强制1:1: 加速已关={a0} 原指针速度={_savedSpeed}(移动期临时设为10)\n");
            try { RelativeConverge(vx, vy, sb, "定位"); } finally { RestoreMouseAccel(); }
            FlushMove(sb, vx, vy);
            return;
        }

        // 求主屏→目标屏的相邻屏链（BFS）。正常扩展桌面必连通；不连通才走降级。
        var path = MonitorPath(primary, target, mons);
        if (path == null)
        {
            sb?.Append($"MOVE(降级·与主屏不连通) 目标=({vx},{vy})\n");
            int ex = Math.Clamp(vx, primary.Left, primary.Right - 1), ey = Math.Clamp(vy, primary.Top, primary.Bottom - 1);
            SendAbs04(primary, ex, ey, "降级入口");   // 内部已轮询等到位
            bool a1 = DisableMouseAccel();
            sb?.Append($"  强制1:1: 加速已关={a1}\n");
            try { RelativeConverge(vx, vy, sb, "定位"); } finally { RestoreMouseAccel(); }
            FlushMove(sb, vx, vy);
            return;
        }

        // 0x04 落到主屏上贴近第一段共享边的点，缩短首跳；随后逐段跨屏。
        int entryX, entryY;
        if (path.Count > 1) { var e = CrossPoint(path[1], primary); entryX = e.x; entryY = e.y; }
        else { entryX = Math.Clamp(vx, primary.Left, primary.Right - 1); entryY = Math.Clamp(vy, primary.Top, primary.Bottom - 1); }
        sb?.Append($"MOVE(路径 {PathText(path)}) 目标=({vx},{vy}) 起点=({cx},{cy})\n");
        SendAbs04(primary, entryX, entryY, "枢纽");   // 内部已轮询等到位

        // 整段相对行走期间统一把系统指针置 1:1（比逐跳开关更省），最后精确落到目标点。
        bool accelOff = DisableMouseAccel();
        sb?.Append($"  强制1:1: 加速已关={accelOff} 原指针速度={_savedSpeed}(移动期临时设为10)\n");
        try
        {
            for (int i = 1; i < path.Count; i++)
            {
                var cp = CrossPoint(path[i - 1], path[i]);
                if (!RelativeConverge(cp.x, cp.y, sb, $"跨入屏{path[i].Number}")) { sb?.Append("  跨屏卡住，停止后续路径\n"); break; }   // 止损，别白等后续跳
            }
            RelativeConverge(vx, vy, sb, "定位");
        }
        finally { RestoreMouseAccel(); }
        FlushMove(sb, vx, vy);
    }

    // 用 0x04 绝对定位把光标送到主屏上的 (px,py)（须在主屏范围内）。tag 仅用于日志。
    private void SendAbs04(ScreenInfo.Monitor primary, int px, int py, string tag)
    {
        int ax = Math.Clamp((int)Math.Round(4096.0 * (px - primary.Left) / primary.Width), 0, 4095);
        int ay = Math.Clamp((int)Math.Round(4096.0 * (py - primary.Top) / primary.Height), 0, 4095);
        // 0x04 绝对鼠标帧：data = [0x02, 按键, X低, X高, Y低, Y高, 滚轮]，X/Y 为 0–4096 绝对坐标(小端)。
        Send(0x04, new byte[] { 0x02, _held, (byte)(ax & 0xFF), (byte)((ax >> 8) & 0xFF), (byte)(ay & 0xFF), (byte)((ay >> 8) & 0xFF), 0x00 });
        // 轮询等光标真到锚点附近再返回（替代固定 sleep）：避免 0x04 尚未生效就走相对，导致首帧读到旧位置、冲过头。
        bool arrived = WaitCursorNear(px, py, 4, 150);
        if (MoveDiag)
        {
            var (fx, fy) = ScreenInfo.CursorPos();
            Diag($"MOVE(绝对0x04·{tag}) 目标=({px},{py}) 主屏[{primary.Left},{primary.Top}~{primary.Right},{primary.Bottom}] → abs=({ax},{ay})/4096  到位={arrived} 光标=({fx},{fy}) 残差=({px - fx},{py - fy})");
        }
    }

    // 相对闭环收敛到 (tx,ty)：逐帧读光标→发残差(截 ±127)→等真位移，直到 ≤1px。
    // 调用方须已把系统指针置 1:1（DisableMouseAccel）。返回是否到位。
    private int _lastFrames;   // 上次 RelativeConverge 的收敛帧数（供收敛耗时测量用）
    private bool RelativeConverge(int tx, int ty, System.Text.StringBuilder? sb, string tag)
    {
        const int MaxIter = 200, FrameWaitMs = 150;
        int stuck = 0, prevDx = 0, prevDy = 0, dampX = 1, dampY = 1;
        for (int i = 0; i < MaxIter; i++)
        {
            var (cx, cy) = ScreenInfo.CursorPos();
            int dx = tx - cx, dy = ty - cy;
            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) { _lastFrames = i; sb?.Append($"  [{tag}] 到位@帧{i} ({cx},{cy})\n"); return true; }
            // 冲过头（残差符号翻转）→ 该轴发送量减半、自带阻尼：即便系统加速没关成功(增益>1)也能收敛，不会在目标两侧永久振荡到 200 帧。
            if (prevDx != 0 && Math.Sign(dx) != Math.Sign(prevDx)) dampX = Math.Min(dampX * 2, 64); else if (prevDx != 0) dampX = Math.Max(1, dampX / 2);
            if (prevDy != 0 && Math.Sign(dy) != Math.Sign(prevDy)) dampY = Math.Min(dampY * 2, 64); else if (prevDy != 0) dampY = Math.Max(1, dampY / 2);
            int sendX = DampStep(dx, dampX), sendY = DampStep(dy, dampY);
            Send(0x05, new byte[] { 0x01, _held, Signed(sendX), Signed(sendY), 0x00 });
            bool moved = WaitCursorMoved(cx, cy, FrameWaitMs);
            var (ncx, ncy) = ScreenInfo.CursorPos();
            sb?.Append($"  [{tag}]帧{i}: ({cx},{cy}) 残差({dx},{dy}) 发({sendX},{sendY}) 阻尼({dampX},{dampY}) → ({ncx},{ncy}) 位移=({ncx - cx},{ncy - cy}) moved={moved}\n");
            if (!moved) { if (++stuck >= 4) { _lastFrames = i + 1; sb?.Append($"  [{tag}] 卡住(连续无位移)@帧{i}\n"); return false; } }
            else stuck = 0;
            prevDx = dx; prevDy = dy;
        }
        _lastFrames = MaxIter;
        sb?.Append($"  [{tag}] 到迭代上限\n");
        return false;
    }

    // 阻尼后的单轴发送量：残差截 ±127 再按阻尼因子缩小，但残差非 0 时至少发 ±1，避免缩成 0 卡死。
    private static int DampStep(int d, int damp)
    {
        if (d == 0) return 0;
        int m = Clamp127(d) / damp;
        return m != 0 ? m : (d > 0 ? 1 : -1);
    }

    // 两块屏是否相邻（共享一段正长度的公共边）。左右挨着看竖边、上下挨着看横边，且投影有重叠。
    private static bool Adjacent(ScreenInfo.Monitor a, ScreenInfo.Monitor b)
    {
        bool vShare = (a.Right == b.Left || a.Left == b.Right)
                      && Math.Min(a.Bottom, b.Bottom) > Math.Max(a.Top, b.Top);
        bool hShare = (a.Bottom == b.Top || a.Top == b.Bottom)
                      && Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left);
        return vShare || hShare;
    }

    // BFS 求 src→dst 的相邻屏链（含首尾）；不连通返回 null。屏幕拓扑由 ScreenInfo.All() 运行时读取，非硬编码。
    // 用引用相等（同一 mons 列表里的对象）比对，避免 record 值相等把重编号字段也算进去。
    private static List<ScreenInfo.Monitor>? MonitorPath(ScreenInfo.Monitor src, ScreenInfo.Monitor dst, List<ScreenInfo.Monitor> mons)
    {
        if (ReferenceEquals(src, dst)) return new List<ScreenInfo.Monitor> { src };
        var prev = new Dictionary<ScreenInfo.Monitor, ScreenInfo.Monitor>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<ScreenInfo.Monitor>(ReferenceEqualityComparer.Instance) { src };
        var q = new Queue<ScreenInfo.Monitor>();
        q.Enqueue(src);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            foreach (var m in mons)
            {
                if (seen.Contains(m) || !Adjacent(cur, m)) continue;
                prev[m] = cur;
                if (ReferenceEquals(m, dst))
                {
                    var path = new List<ScreenInfo.Monitor> { m };
                    for (var p = cur; ; p = prev[p]) { path.Add(p); if (ReferenceEquals(p, src)) break; }
                    path.Reverse();
                    return path;
                }
                seen.Add(m);
                q.Enqueue(m);
            }
        }
        return null;
    }

    // 供拟人化跨屏【逐屏分段】用：把从 (sx,sy) 到 (tx,ty) 的相邻屏路径拆成锚点序列。
    // 返回 pts：pts[0]=起点、pts[末]=目标，中间每个跨屏 hop 插入一对"出口点(留在前屏、贴共享边) + 入口点(落在后屏、贴共享边)"。
    // 于是相邻两锚点要么【同屏】(调用方对其 EmitStroke 拟人化)、要么【一次跨缝】(调用方会话内一次相对收敛越过、仅相邻屏)。
    // 不跨屏/找不到屏/拓扑不连通 → 返回 null（调用方走 0x04 兜底）。从【当前光标所在屏】BFS，不再强制 0x04 重锚主屏，
    // 这样起手那一屏的子段也能被拟人化（而非被瞬移吞掉）。
    public List<(int x, int y)>? PlanCrossAnchors(int sx, int sy, int tx, int ty)
    {
        var mons = ScreenInfo.All();
        ScreenInfo.Monitor? src = null, dst = null;
        foreach (var m in mons)
        {
            if (src == null && m.Contains(sx, sy)) src = m;
            if (dst == null && m.Contains(tx, ty)) dst = m;
        }
        if (src == null || dst == null || ReferenceEquals(src, dst)) return null;   // 同屏/越界 → 交上层处理
        var path = MonitorPath(src, dst, mons);
        if (path == null || path.Count < 2) return null;                            // 不连通 → 0x04 兜底
        var pts = new List<(int x, int y)> { (sx, sy) };
        for (int i = 0; i + 1 < path.Count; i++)
        {
            pts.Add(CrossPoint(path[i + 1], path[i]));   // 出口点：留在 path[i] 内、贴与 path[i+1] 的共享边
            pts.Add(CrossPoint(path[i], path[i + 1]));   // 入口点：落在 path[i+1] 内、贴同一条共享边
        }
        pts.Add((tx, ty));
        return pts;
    }

    // 从屏 a 跨入屏 b 时，b 内紧贴共享边中点的落点（严格在 b 内）。
    private static (int x, int y) CrossPoint(ScreenInfo.Monitor a, ScreenInfo.Monitor b)
    {
        int x, y;
        if (a.Right == b.Left || a.Left == b.Right)   // 左右相邻，跨竖边：y 取重叠段中点
        {
            int lo = Math.Max(a.Top, b.Top), hi = Math.Min(a.Bottom, b.Bottom);
            y = (lo + hi) / 2;
            x = a.Right == b.Left ? b.Left + 1 : b.Right - 2;
        }
        else                                          // 上下相邻，跨横边：x 取重叠段中点
        {
            int lo = Math.Max(a.Left, b.Left), hi = Math.Min(a.Right, b.Right);
            x = (lo + hi) / 2;
            y = a.Bottom == b.Top ? b.Top + 1 : b.Bottom - 2;
        }
        return (Math.Clamp(x, b.Left, b.Right - 1), Math.Clamp(y, b.Top, b.Bottom - 1));
    }

    // 点不在任何屏内时，取欧氏距离最近的屏。
    private static ScreenInfo.Monitor NearestMonitor(int vx, int vy, List<ScreenInfo.Monitor> mons, ScreenInfo.Monitor fallback)
    {
        ScreenInfo.Monitor? best = null; long bestD = long.MaxValue;
        foreach (var m in mons)
        {
            int cx = Math.Clamp(vx, m.Left, m.Right - 1), cy = Math.Clamp(vy, m.Top, m.Bottom - 1);
            long d = (long)(vx - cx) * (vx - cx) + (long)(vy - cy) * (vy - cy);
            if (d < bestD) { bestD = d; best = m; }
        }
        return best ?? fallback;
    }

    private static string PathText(List<ScreenInfo.Monitor> path)
    {
        var parts = new List<string>();
        foreach (var m in path) parts.Add(m.Primary ? $"主屏{m.Number}" : $"屏{m.Number}");
        return string.Join("→", parts);
    }

    private static void FlushMove(System.Text.StringBuilder? sb, int vx, int vy)
    {
        if (sb == null) return;
        var (fx, fy) = ScreenInfo.CursorPos();
        sb.Append($"MOVE 结束: 终点=({fx},{fy}) 最终残差=({vx - fx},{vy - fy})\n");
        Diag(sb.ToString());
    }

    private static int Clamp127(int v) => v > 127 ? 127 : (v < -127 ? -127 : v);

    // 相对报文异步生效：轮询到光标真位移即返回 true；等满超时仍无位移返回 false。
    private static bool WaitCursorMoved(int px, int py, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            var (cx, cy) = ScreenInfo.CursorPos();
            if (cx != px || cy != py) return true;
            System.Threading.Thread.Sleep(1);
        }
        return false;
    }

    // 轮询等光标进入 (px,py) 的 ±tol 方块内（0x04 绝对帧异步生效）；到达返回 true，超时返回 false。
    private static bool WaitCursorNear(int px, int py, int tol, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            var (cx, cy) = ScreenInfo.CursorPos();
            if (Math.Abs(cx - px) <= tol && Math.Abs(cy - py) <= tol) return true;
            System.Threading.Thread.Sleep(1);
        }
        return false;
    }

    // 临时把系统鼠标改成"1:1 线性"：关"指针加速"(enhance pointer precision) + 指针速度设为 10(1:1)，
    // 移动结束还原。两者都会缩放相对移动量——只关加速会残留指针速度滑块的线性倍率(如滑块=20 → 3.5x)，
    // 导致相对闭环发散/振荡。返回是否成功。
    private int[]? _savedMouse;
    private int _savedSpeed = -1;
    private const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004, SPI_GETMOUSESPEED = 0x0070, SPI_SETMOUSESPEED = 0x0071;
    private bool DisableMouseAccel()
    {
        bool ok = false;
        try
        {
            var cur = new int[3];
            if (SystemParametersInfo(SPI_GETMOUSE, 0, cur, 0))
            {
                _savedMouse = cur;
                ok = SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 0, 0, 0 }, 0);   // 关加速
            }
        }
        catch { }
        try
        {
            if (SystemParametersInfoGet(SPI_GETMOUSESPEED, 0, out int sp, 0))
            {
                _savedSpeed = sp;
                SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(10), 0);        // 速度设 1:1
            }
        }
        catch { }
        WriteMouseBackup();   // 原值落盘：进程若被杀/崩溃没走到 Restore，下次启动据此还原，不永久改用户设置
        return ok;
    }
    private void RestoreMouseAccel()
    {
        try { if (_savedMouse != null) { SystemParametersInfo(SPI_SETMOUSE, 0, _savedMouse, 0); _savedMouse = null; } } catch { }
        try { if (_savedSpeed > 0) { SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(_savedSpeed), 0); _savedSpeed = -1; } } catch { }
        try { System.IO.File.Delete(MouseBackupPath()); } catch { }   // 正常还原后删备份
    }

    // 系统鼠标设置的崩溃恢复备份（关加速期间存原值；正常还原后删除）。
    private static string MouseBackupPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPilot", "mouse_backup.json");
    private void WriteMouseBackup()
    {
        try
        {
            if (_savedMouse == null && _savedSpeed <= 0) return;
            var m = _savedMouse ?? new[] { -1, -1, -1 };
            var p = MouseBackupPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllText(p, $"{{\"m\":[{m[0]},{m[1]},{m[2]}],\"s\":{_savedSpeed}}}");
        }
        catch { }
    }

    // 程序启动时调用：上次运行若异常退出、没来得及还原被临时改成 1:1 的系统鼠标设置，据备份还原并删除备份。
    public static void RecoverMouseSettingsOnStartup()
    {
        var p = MouseBackupPath();
        try
        {
            if (!System.IO.File.Exists(p)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(p));
            var root = doc.RootElement;
            if (root.TryGetProperty("m", out var mEl) && mEl.ValueKind == System.Text.Json.JsonValueKind.Array && mEl.GetArrayLength() == 3)
            {
                var m = new[] { mEl[0].GetInt32(), mEl[1].GetInt32(), mEl[2].GetInt32() };
                if (m[0] >= 0 || m[1] >= 0 || m[2] >= 0) SystemParametersInfo(SPI_SETMOUSE, 0, m, 0);
            }
            if (root.TryGetProperty("s", out var sEl) && sEl.TryGetInt32(out int s) && s > 0)
                SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(s), 0);
        }
        catch { }
        try { System.IO.File.Delete(p); } catch { }
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    // ---- 移动诊断日志（写 %AppData%\MacroPilot\ch9329_move.log，用整段缓冲一次落盘，不扰动每帧时序）----
    // 默认关闭；排查鼠标移动落点问题时临时置 true。
    public static bool MoveDiag = false;
    private static readonly object _diagLock = new();

    // ---- 临时：CH9329 单路点收敛耗时测量（调优拟人化路点数用；每移动会话攒一批、EndMove 落 ch9329_timing.log）----
    // 缓冲期间不做逐点文件 IO，避免扰动被测的收敛时序。调优定稿后置 false 或删除。
    public static bool TimingDiag = true;
    private static readonly System.Text.StringBuilder _tbuf = new();
    private static readonly object _tLock = new();
    private static void FlushTiming()
    {
        string data;
        lock (_tLock) { if (_tbuf.Length == 0) return; data = _tbuf.ToString(); _tbuf.Clear(); }
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPilot");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "ch9329_timing.log");
            lock (_diagLock) System.IO.File.AppendAllText(path, $"# session {DateTime.Now:HH:mm:ss}  elapsed_ms,frames,dist_px\n{data}");
        }
        catch { }
    }
    private static void Diag(string block)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPilot");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "ch9329_move.log");
            lock (_diagLock)
            {
                // 只追加会无限涨——超过 ~1MB 就重开，避免长期运行占满磁盘。
                try { var fi = new System.IO.FileInfo(path); if (fi.Exists && fi.Length > 1_000_000) System.IO.File.WriteAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] (日志超 1MB 已重置)\n"); } catch { }
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {block}\n");
            }
        }
        catch { }
    }

    public void MouseWheel(int amount)
        => Send(0x05, new byte[] { 0x01, _held, 0x00, 0x00, Signed(amount) });

    public void ReleaseAll()
    {
        _held = 0;
        Keyboard(0, 0);
        Send(0x05, new byte[] { 0x01, 0, 0, 0, 0 });
    }

    private static byte Signed(int v)
    {
        // 下限取 -127（不是 -128）：CH9329 相对增量里 -128(0x80) 是"死值"，发了那个轴纹丝不动。
        if (v > 127) v = 127; if (v < -127) v = -127;
        return (byte)(v < 0 ? v + 256 : v);
    }

    private static void Sleep(double ms, CancellationToken ct) => Services.PreciseTimer.Sleep(ms, ct);

    public void Dispose() => Close();

}
