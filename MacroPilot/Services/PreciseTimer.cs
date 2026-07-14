using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacroPilot.Services;

/// <summary>
/// 高精度等待：支持小数毫秒。粗睡 + 末段自旋补足亚毫秒尾巴；
/// 配合 timeBeginPeriod(1) 让 Thread.Sleep 逼近 1ms 精度。
/// 注意：普通 Windows 无法保证精确到亚毫秒（受调度/其它进程影响，实测误差约 0.1–1ms）。
/// </summary>
public static class PreciseTimer
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    /// <summary>提升系统定时器分辨率到 1ms（进程级）。执行期用，成对调用 [EndHighResolution]。</summary>
    public static void BeginHighResolution() { try { timeBeginPeriod(1); } catch { } }
    public static void EndHighResolution() { try { timeEndPeriod(1); } catch { } }

    /// <summary>高精度睡眠（小数毫秒）。用于按住时长这类短时。</summary>
    public static void Sleep(double ms) => Sleep(ms, CancellationToken.None);

    /// <summary>
    /// 可取消的高精度睡眠：停止/关闭时立即返回，不再等睡完。
    /// 按住时长（HoldMs）可被用户设成分钟级，若不可取消则 F11 急停要等睡完才生效。
    /// 提前返回后，调用方（backend）随后的"抬键/抬鼠标"仍会执行，故不会卡键。
    /// </summary>
    public static void Sleep(double ms, CancellationToken ct)
    {
        if (ms <= 0) return;
        long target = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            double remain = (target - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remain <= 0) return;
            // 粗睡用 WaitHandle，取消时立即唤醒；留 ~1.5ms 给末段自旋补足亚毫秒。
            if (remain > 2) { if (ct.CanBeCanceled) { if (ct.WaitHandle.WaitOne((int)(remain - 1.5))) return; } else Thread.Sleep((int)(remain - 1.5)); }
            else Thread.SpinWait(80);
        }
    }
}
