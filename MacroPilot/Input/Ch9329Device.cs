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
    private InputExecutionContext? _execution;
    private RelativeMoveController? _moveController;
    public void SetExecutionContext(InputExecutionContext? context) => _execution = context;
    private void Checkpoint(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _execution?.Checkpoint(ct); }
    private RelativeMoveController CreateMoveController()
    {
        var context = _execution;
        return new(ScreenInfo.CursorPos,
            (dx, dy) => Send(0x05, new byte[] { 0x01, _held, Signed(dx), Signed(dy), 0 }), Checkpoint,
            clock: context == null ? null : () => context.ActiveMilliseconds,
            clockExcludesPauses: context?.HasActiveClock == true);
    }


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
        try { EndMove(); RestoreMouseAccel(); } catch { }
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

    private void Send(byte cmd, byte[] data)
    {
        if (_sp is not { IsOpen: true }) throw new InputSafetyException("CH9329 串口未连接，已停止方案。");
        try
        {
            // 发送前清收缓冲即可顺带丢弃上一帧应答，避免堆积；不再等 ACK——
            // 相对闭环的节拍完全由 MouseMove 里的"等光标真移动"负责（更直接、不冗余）。
            _sp.DiscardInBuffer();
            var p = Packet(cmd, data);
            _sp.Write(p, 0, p.Length);
        }
        catch (Exception ex)
        {
            throw new InputSafetyException($"CH9329 串口写入失败，已安全停止方案：{ex.Message}", ex);
        }
    }

    private void Keyboard(byte modifier, byte k1)
        => Send(0x02, new byte[] { modifier, 0x00, k1, 0, 0, 0, 0, 0 });

    public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
    {
        byte code = KeyMap.TryHid(key, out var c) ? c : (byte)0;
        if (code == 0 && modifier == 0) return; // 未知键名：不发只含修饰键的空报文
        InputExecutionContext.Hold(holdMs, () => Keyboard(modifier, code),
            () => Keyboard(0, 0), _execution, ct);
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
        InputExecutionContext.Hold(holdMs,
            () => Send(0x05, new byte[] { 0x01, (byte)(_held | b), 0, 0, 0 }),
            () => Send(0x05, new byte[] { 0x01, _held, 0, 0, 0 }), _execution, ct);
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
        DisableMouseAccel();
        try { _moveController = CreateMoveController(); _inMoveSession = true; }
        catch { RestoreMouseAccel(); throw; }
    }
    public void EndMove()
    {
        if (!_inMoveSession) return;
        _inMoveSession = false;
        _moveController = null;
        RestoreMouseAccel();
        if (TimingDiag) FlushTiming();
    }

    public void MouseMove(int vx, int vy, CancellationToken ct = default)
    {
        Checkpoint(ct);
        if (!_inMoveSession) _moveController = null;
        // 移动会话中（拟人化）：调用方保证同屏，直接相对收敛——不判主屏 0x04、不 BFS、不逐点开关加速。
        if (_inMoveSession)
        {
            if (TimingDiag)   // 临时：测每个相对闭环路点的真实收敛耗时（调优 CH9329 拟人化路点数）
            {
                var (bx, by) = ScreenInfo.CursorPos();
                var tsw = System.Diagnostics.Stopwatch.StartNew();
                RelativeConverge(vx, vy, null, "会话", ct);
                tsw.Stop();
                double td = Math.Sqrt((double)(vx - bx) * (vx - bx) + (double)(vy - by) * (vy - by));
                lock (_tLock) _tbuf.Append(FormattableString.Invariant($"{tsw.Elapsed.TotalMilliseconds:F1},{_lastFrames},{td:F0}\n"));
            }
            else RelativeConverge(vx, vy, null, "会话", ct);
            EnsureArrived(vx, vy);
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
        if (primary.Contains(vx, vy)) { SendAbs04(primary, vx, vy, "主屏", ct); EnsureArrived(vx, vy); return; }

        target ??= NearestMonitor(vx, vy, mons, primary);
        var (cx, cy) = ScreenInfo.CursorPos();
        var sb = MoveDiag ? new System.Text.StringBuilder() : null;

        // 光标已在目标副屏内 → 直接屏内相对收敛，不绕主屏。
        if (target.Contains(cx, cy))
        {
            sb?.Append($"MOVE(屏内相对·屏{target.Number}) 目标=({vx},{vy}) 起点=({cx},{cy})\n");
            bool a0 = DisableMouseAccel();
            sb?.Append($"  强制1:1: 加速已关={a0} 原指针速度={_savedSpeed}(移动期临时设为10)\n");
            try { RelativeConverge(vx, vy, sb, "定位", ct); } finally { RestoreMouseAccel(); }
            FlushMove(sb, vx, vy);
            EnsureArrived(vx, vy);
            return;
        }

        // 求主屏→目标屏的相邻屏链（BFS）。正常扩展桌面必连通；不连通才走降级。
        var path = MonitorPath(primary, target, mons);
        if (path == null)
        {
            sb?.Append($"MOVE(降级·与主屏不连通) 目标=({vx},{vy})\n");
            int ex = Math.Clamp(vx, primary.Left, primary.Right - 1), ey = Math.Clamp(vy, primary.Top, primary.Bottom - 1);
            SendAbs04(primary, ex, ey, "降级入口", ct);   // 内部已轮询等到位
            bool a1 = DisableMouseAccel();
            sb?.Append($"  强制1:1: 加速已关={a1}\n");
            try { RelativeConverge(vx, vy, sb, "定位", ct); } finally { RestoreMouseAccel(); }
            FlushMove(sb, vx, vy);
            EnsureArrived(vx, vy);
            return;
        }

        // 0x04 落到主屏上贴近第一段共享边的点，缩短首跳；随后逐段跨屏。
        int entryX, entryY;
        if (path.Count > 1) { var e = CrossPoint(path[1], primary); entryX = e.x; entryY = e.y; }
        else { entryX = Math.Clamp(vx, primary.Left, primary.Right - 1); entryY = Math.Clamp(vy, primary.Top, primary.Bottom - 1); }
        sb?.Append($"MOVE(路径 {PathText(path)}) 目标=({vx},{vy}) 起点=({cx},{cy})\n");
        SendAbs04(primary, entryX, entryY, "枢纽", ct);   // 内部已轮询等到位

        // 整段相对行走期间统一把系统指针置 1:1（比逐跳开关更省），最后精确落到目标点。
        bool accelOff = DisableMouseAccel();
        sb?.Append($"  强制1:1: 加速已关={accelOff} 原指针速度={_savedSpeed}(移动期临时设为10)\n");
        try
        {
            for (int i = 1; i < path.Count; i++)
            {
                var cp = CrossPoint(path[i - 1], path[i]);
                RelativeConverge(cp.x, cp.y, sb, $"跨入屏{path[i].Number}", ct);
            }
            RelativeConverge(vx, vy, sb, "定位", ct);
        }
        finally { RestoreMouseAccel(); }
        FlushMove(sb, vx, vy);
        EnsureArrived(vx, vy);
    }

    // 用 0x04 绝对定位把光标送到主屏上的 (px,py)（须在主屏范围内）。tag 仅用于日志。
    private void SendAbs04(ScreenInfo.Monitor primary, int px, int py, string tag, CancellationToken ct)
    {
        Checkpoint(ct);
        int ax = Math.Clamp((int)Math.Round(4096.0 * (px - primary.Left) / primary.Width), 0, 4095);
        int ay = Math.Clamp((int)Math.Round(4096.0 * (py - primary.Top) / primary.Height), 0, 4095);
        // 0x04 绝对鼠标帧：data = [0x02, 按键, X低, X高, Y低, Y高, 滚轮]，X/Y 为 0–4096 绝对坐标(小端)。
        Send(0x04, new byte[] { 0x02, _held, (byte)(ax & 0xFF), (byte)((ax >> 8) & 0xFF), (byte)(ay & 0xFF), (byte)((ay >> 8) & 0xFF), 0x00 });
        // 轮询等光标真到锚点附近再返回（替代固定 sleep）：避免 0x04 尚未生效就走相对，导致首帧读到旧位置、冲过头。
        bool arrived = WaitCursorNear(px, py, 4, 150, ct);
        if (!arrived) throw new InputSafetyException("鼠标绝对定位未到达目标，已安全停止方案。");
        if (MoveDiag)
        {
            var (fx, fy) = ScreenInfo.CursorPos();
            Diag($"MOVE(绝对0x04·{tag}) 目标=({px},{py}) 主屏[{primary.Left},{primary.Top}~{primary.Right},{primary.Bottom}] → abs=({ax},{ay})/4096  到位={arrived} 光标=({fx},{fy}) 残差=({px - fx},{py - fy})");
        }
    }

    // 相对闭环须处于 1:1；异常位移、未到位和取消直接穿透调用方，不再静默返回 false。
    private int _lastFrames;   // 上次 RelativeConverge 的收敛帧数（供收敛耗时测量用）
    private void RelativeConverge(int tx, int ty, System.Text.StringBuilder? sb, string tag, CancellationToken ct)
    {
        _moveController ??= CreateMoveController();
        _moveController.MoveTo(tx, ty, ct);
        _lastFrames = _moveController.Frames;
        sb?.Append($"  [{tag}] 到位，帧数={_lastFrames}\n");
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

    // 移动收尾校验：光标没落到目标附近就抛错——静默返回会让紧随其后的点击点在错误位置上，
    // 输入位置不再可信时停止整份方案，不能走失败监听继续输入。
    private static void EnsureArrived(int vx, int vy)
    {
        var (fx, fy) = ScreenInfo.CursorPos();
        if (Math.Abs(vx - fx) <= 6 && Math.Abs(vy - fy) <= 6) return;
        throw new InputSafetyException(
            $"鼠标未能移动到目标位置（目标 ({vx},{vy})，实际 ({fx},{fy})）。前台应用可能锁定或不断重置指针（如游戏的鼠标捕获），已安全停止方案；请确认目标窗口后重新运行。");
    }

    private static void FlushMove(System.Text.StringBuilder? sb, int vx, int vy)
    {
        if (sb == null) return;
        var (fx, fy) = ScreenInfo.CursorPos();
        sb.Append($"MOVE 结束: 终点=({fx},{fy}) 最终残差=({vx - fx},{vy - fy})\n");
        Diag(sb.ToString());
    }

    // 轮询等光标进入 (px,py) 的 ±tol 方块内（0x04 绝对帧异步生效）；到达返回 true，超时返回 false。
    private bool WaitCursorNear(int px, int py, int tol, int timeoutMs, CancellationToken ct)
    {
        var context = _execution;
        long Now() => context?.ActiveMilliseconds ?? Environment.TickCount64;
        long started = Now(), blockedTime = 0;
        while (Now() - started - blockedTime < timeoutMs)
        {
            long before = Now();
            Checkpoint(ct);
            if (context?.HasActiveClock != true) blockedTime += Now() - before;
            var (cx, cy) = ScreenInfo.CursorPos();
            if (Math.Abs(cx - px) <= tol && Math.Abs(cy - py) <= tol) return true;
            if (ct.WaitHandle.WaitOne(1)) ct.ThrowIfCancellationRequested();
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
        string backup = MouseBackupPath();
        if (System.IO.File.Exists(backup) && !RestoreBackup(backup,
            m => SystemParametersInfo(SPI_SETMOUSE, 0, m, 0),
            speed => SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(speed), 0)))
            throw new InputSafetyException("上次鼠标设置尚未恢复，已停止移动，恢复备份仍保留。");
        var mouse = new int[3];
        if (!SystemParametersInfo(SPI_GETMOUSE, 0, mouse, 0) ||
            !SystemParametersInfoGet(SPI_GETMOUSESPEED, 0, out int speed, 0))
            throw new InputSafetyException("无法读取系统鼠标设置，已停止移动。");
        try { SaveMouseBackup(backup, mouse, speed); }
        catch (Exception ex) { throw new InputSafetyException("无法保存鼠标恢复设置，未修改系统参数。", ex); }
        _savedMouse = mouse; _savedSpeed = speed;
        bool accel = SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 0, 0, 0 }, 0);
        bool linear = SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(10), 0);
        if (!accel || !linear)
        {
            RestoreMouseAccel();
            throw new InputSafetyException("无法启用精确鼠标移动，已停止方案并恢复原设置。");
        }
        return true;
    }

    private void RestoreMouseAccel()
    {
        if (_savedMouse == null && _savedSpeed <= 0) return;
        if (!RestoreBackup(MouseBackupPath(),
            m => SystemParametersInfo(SPI_SETMOUSE, 0, m, 0),
            speed => SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(speed), 0)))
            throw new InputSafetyException("鼠标系统设置恢复失败，备份已保留，下次启动将重试恢复。");
        _savedMouse = null; _savedSpeed = -1;
    }

    private static string MouseBackupPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPilot", "mouse_backup.json");

    internal static void SaveMouseBackup(string path, int[] mouse, int speed)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var data = System.Text.Json.JsonSerializer.Serialize(new { m = mouse, s = speed });
        string temp = path + ".tmp";
        try
        {
            using (var stream = new System.IO.FileStream(temp, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            System.IO.File.Move(temp, path, overwrite: true);
        }
        finally { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
    }

    internal static bool RestoreBackup(string path, Func<int[], bool> setMouse, Func<int, bool> setSpeed)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("m", out var m) || m.GetArrayLength() != 3 ||
                !root.TryGetProperty("s", out var sp) || !sp.TryGetInt32(out int speed))
                return false;
            var mouse = new[] { m[0].GetInt32(), m[1].GetInt32(), m[2].GetInt32() };
            bool hasMouse = Array.TrueForAll(mouse, v => v >= 0);
            bool hasSpeed = speed >= 1 && speed <= 20;
            // 兼容旧版只读到一部分原值的备份；-1 表示该项当时没有被读取/修改。
            if ((!hasMouse && !Array.TrueForAll(mouse, v => v == -1)) ||
                (!hasSpeed && speed != -1) || (!hasMouse && !hasSpeed)) return false;
            // 两项均尝试恢复；只有全部成功才允许丢弃恢复记录。
            bool mouseOk = !hasMouse || setMouse(mouse);
            bool speedOk = !hasSpeed || setSpeed(speed);
            if (!mouseOk || !speedOk) return false;
            System.IO.File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public static void RecoverMouseSettingsOnStartup() => RestoreBackup(MouseBackupPath(),
        m => SystemParametersInfo(SPI_SETMOUSE, 0, m, 0),
        speed => SystemParametersInfoSet(SPI_SETMOUSESPEED, 0, new IntPtr(speed), 0));
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
    public static bool TimingDiag = false;
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
            lock (_diagLock)
            {
                if (new System.IO.FileInfo(path) is { Exists: true, Length: > 1_000_000 }) System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, $"# session {DateTime.Now:HH:mm:ss}  elapsed_ms,frames,dist_px\n{data}");
            }
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
        try { Keyboard(0, 0); }
        finally { Send(0x05, new byte[] { 0x01, 0, 0, 0, 0 }); }
    }

    private static byte Signed(int v)
    {
        // 下限取 -127（不是 -128）：CH9329 相对增量里 -128(0x80) 是"死值"，发了那个轴纹丝不动。
        if (v > 127) v = 127; if (v < -127) v = -127;
        return (byte)(v < 0 ? v + 256 : v);
    }


    public void Dispose() => Close();

}
