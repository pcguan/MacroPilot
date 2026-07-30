using System;
using System.Collections.Generic;
using MacroPilot.Input;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>原始输入事件（录制期间由低级钩子采集）。坐标为虚拟桌面物理像素；Data 按 Kind 复用：按钮下标 / 滚轮 delta / 虚拟键码。</summary>
public enum RecKind { MouseDown, MouseUp, MouseMove, Wheel, KeyDown, KeyUp }
public readonly record struct RecEvent(long T, RecKind Kind, int X, int Y, int Data);

/// <summary>
/// 宏录制的归纳器：把原始事件流折叠成动作列表。**纯函数、无 UI/屏幕依赖**（坐标→显示器的换算由
/// 调用方注入），所以整套规则可以确定性单测——钩子采集层照项目惯例不测，规则全测。
///
/// 归纳规则（v1）：
///   按下→抬起、位移 &lt; 5px            → 点击坐标（HoldMs=实测，下限 75——低于它 CH9329 会吞后续键）
///   按下→位移 ≥ 5px→抬起               → 拖动（起点/终点；中间轨迹丢弃——回放交给拟人化，录的是"意图"）
///   同位置同键连续点击（间隔 &lt; 500ms）→ 合并为一条，点击次数 = N、重复间隔 = 实测均值
///   连续滚轮（同方向、间隔 &lt; 300ms）  → 合并为一条滚轮，格数求和
///   修饰键按住 + 普通键                  → 按键（Modifier 位 + Key）；修饰键单独按放 → 单独的修饰键按键
///   长按普通键（系统自动重复的 KeyDown） → 一条按键（从首次按下计时）
///   相邻动作间隔 ≥ 200ms                → 插入等待（取整到 50ms）
///   纯移动（没按键没滚轮）               → 不录（噪声）
/// </summary>
public static class RecordingCompiler
{
    public const int ClickSlopPx = 5;       // 位移小于它算点击、大于算拖动（物理像素）
    public const int MergeClickGapMs = 500; // 连击合并窗口（双击/三连击自然归并成"点击次数 N"）
    public const int WheelGapMs = 300;      // 滚轮合并窗口
    public const int MinWaitMs = 200;       // 小于它的间隔视为连贯，不生成等待
    public const int HoldFloorMs = 75;      // 按住时长下限：CH9329 hold<75 部分应用会吞后续键（项目既有结论）

    // VK → 按键名（反查 KeyMap.Vk；"Esc"先于"Escape"入表，反查取先到者）；修饰键 VK → Modifier 位。
    private static readonly Dictionary<ushort, string> VkName = BuildVkNames();
    private static readonly Dictionary<ushort, byte> ModBit = BuildModBits();

    private static Dictionary<ushort, string> BuildVkNames()
    {
        var d = new Dictionary<ushort, string>();
        foreach (var kv in KeyMap.Vk)
            if (!d.ContainsKey(kv.Value)) d[kv.Value] = kv.Key;
        return d;
    }
    private static Dictionary<ushort, byte> BuildModBits()
    {
        var d = new Dictionary<ushort, byte>();
        foreach (var (bit, vk) in KeyMap.ModifierVk) d[vk] = bit;
        return d;
    }

    // 中间产物：一段"完成了的"输入（带起止时间与合并所需的物理坐标），最后统一转 MacroStep。
    private sealed class Prim
    {
        public long StartT, EndT;
        public MacroStep Step = null!;
        public string MergeKey = "";        // 点击合并用：按钮 + 物理坐标；空 = 不参与合并
        public int Px, Py;                  // 点击的物理坐标（合并比位移用，步骤里存的是归一化值）
        public long GapSum; public int GapCount;   // 合并时累计的间隔，最后取均值
    }

    /// <summary>把事件流编译成动作列表。map = 物理坐标 → (显示器设备名, 屏内归一化)。</summary>
    public static List<MacroStep> Compile(IReadOnlyList<RecEvent> events, Func<int, int, (string dev, double nx, double ny)> map)
    {
        var prims = FoldPrimitives(events, map);
        return EmitSteps(prims);
    }

    // ---- 第一遍：事件流 → 原语（点击/拖动/滚轮段/按键），按结束时间有序 ----
    private static List<Prim> FoldPrimitives(IReadOnlyList<RecEvent> events, Func<int, int, (string dev, double nx, double ny)> map)
    {
        var prims = new List<Prim>();

        // 鼠标：每个按钮一份按下状态（0=左 1=右 2=中）
        var mDown = new bool[3];
        var mDownT = new long[3];
        var mDownX = new int[3];
        var mDownY = new int[3];
        var mMaxDisp = new int[3];
        var mLastX = new int[3];
        var mLastY = new int[3];

        // 键盘：普通键按下表（vk → 按下时刻 + 按下瞬间的修饰键快照）；修饰键持有表（vk → 按下时刻 + 是否已被组合用掉）
        var kDown = new Dictionary<int, (long t, byte mods)>();
        var modHeld = new Dictionary<int, (long t, bool used)>();

        byte HeldMods()
        {
            byte m = 0;
            foreach (var vk in modHeld.Keys) m |= ModBit[(ushort)vk];
            return m;
        }
        void MarkModsUsed()
        {
            foreach (var vk in new List<int>(modHeld.Keys))
                modHeld[vk] = (modHeld[vk].t, true);
        }
        static string ButtonName(int b) => b switch { 1 => "Right", 2 => "Middle", _ => "Left" };

        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case RecKind.MouseDown:
                {
                    int b = Math.Clamp(ev.Data, 0, 2);
                    if (mDown[b]) break;                       // 异常的重复按下，忽略
                    mDown[b] = true; mDownT[b] = ev.T;
                    mDownX[b] = ev.X; mDownY[b] = ev.Y; mMaxDisp[b] = 0;
                    mLastX[b] = ev.X; mLastY[b] = ev.Y;
                    break;
                }
                case RecKind.MouseMove:
                {
                    for (int b = 0; b < 3; b++)
                        if (mDown[b])
                        {
                            mMaxDisp[b] = Math.Max(mMaxDisp[b], Math.Max(Math.Abs(ev.X - mDownX[b]), Math.Abs(ev.Y - mDownY[b])));
                            mLastX[b] = ev.X; mLastY[b] = ev.Y;
                        }
                    break;
                }
                case RecKind.MouseUp:
                {
                    int b = Math.Clamp(ev.Data, 0, 2);
                    if (!mDown[b]) break;                      // 没配对的抬起（比如按下发生在录制开始前），忽略
                    mDown[b] = false;
                    int disp = Math.Max(mMaxDisp[b], Math.Max(Math.Abs(ev.X - mDownX[b]), Math.Abs(ev.Y - mDownY[b])));
                    if (disp < ClickSlopPx)
                    {
                        var (dev, nx, ny) = map(mDownX[b], mDownY[b]);
                        prims.Add(new Prim
                        {
                            StartT = mDownT[b], EndT = ev.T,
                            MergeKey = $"click:{b}", Px = mDownX[b], Py = mDownY[b],
                            Step = new MacroStep
                            {
                                Type = "MouseClickAt", Button = ButtonName(b),
                                MoveMonitor = dev, MoveNormX = nx, MoveNormY = ny,
                                HoldMs = (int)Math.Max(HoldFloorMs, ev.T - mDownT[b]), HoldUnit = 0,
                                Humanize = true,
                            },
                        });
                    }
                    else
                    {
                        var (ds, nxs, nys) = map(mDownX[b], mDownY[b]);
                        var (de, nxe, nye) = map(ev.X, ev.Y);
                        prims.Add(new Prim
                        {
                            StartT = mDownT[b], EndT = ev.T,
                            Step = new MacroStep
                            {
                                Type = "MouseDrag", Button = ButtonName(b),
                                MoveMonitor = ds, MoveNormX = nxs, MoveNormY = nys,
                                DragEndMonitor = de, DragEndNormX = nxe, DragEndNormY = nye,
                                Humanize = true,
                            },
                        });
                    }
                    break;
                }
                case RecKind.Wheel:
                {
                    // 滚轮先按事件原样入原语（带 delta），合并放到发射阶段（要看相邻原语的间隔与方向）
                    prims.Add(new Prim
                    {
                        StartT = ev.T, EndT = ev.T,
                        MergeKey = "wheel", Px = ev.Data,       // Px 借放 delta
                        Step = new MacroStep { Type = "MouseWheel", Wheel = ev.Data / 120 },
                    });
                    break;
                }
                case RecKind.KeyDown:
                {
                    ushort vk = (ushort)ev.Data;
                    if (ModBit.ContainsKey(vk))
                    {
                        if (!modHeld.ContainsKey(vk)) modHeld[vk] = (ev.T, false);
                        break;
                    }
                    if (kDown.ContainsKey(vk)) break;          // 长按的系统自动重复：只认第一次按下
                    if (!VkName.ContainsKey(vk)) break;        // 不认识的键（媒体键等）整对丢弃
                    kDown[vk] = (ev.T, HeldMods());
                    if (modHeld.Count > 0) MarkModsUsed();     // 修饰键被组合用掉，抬起时不再单独成键
                    break;
                }
                case RecKind.KeyUp:
                {
                    ushort vk = (ushort)ev.Data;
                    if (ModBit.TryGetValue(vk, out var bit))
                    {
                        if (modHeld.TryGetValue(vk, out var h))
                        {
                            modHeld.Remove(vk);
                            if (!h.used)                       // 单独按了一下修饰键（如只按 Alt）
                                prims.Add(new Prim
                                {
                                    StartT = h.t, EndT = ev.T,
                                    Step = new MacroStep { Type = "KeyTap", Key = "", Modifier = bit, HoldMs = (int)Math.Max(HoldFloorMs, ev.T - h.t), HoldUnit = 0 },
                                });
                        }
                        break;
                    }
                    if (!kDown.TryGetValue(vk, out var d)) break;
                    kDown.Remove(vk);
                    prims.Add(new Prim
                    {
                        StartT = d.t, EndT = ev.T,
                        Step = new MacroStep { Type = "KeyTap", Key = VkName[vk], Modifier = d.mods, HoldMs = (int)Math.Max(HoldFloorMs, ev.T - d.t), HoldUnit = 0 },
                    });
                    break;
                }
            }
        }

        prims.Sort((a, b) => a.EndT.CompareTo(b.EndT));   // 交错输入（拖动中按键）按完成先后排
        return prims;
    }

    // ---- 第二遍：合并连击/滚轮，插入等待 ----
    private static List<MacroStep> EmitSteps(List<Prim> prims)
    {
        var steps = new List<MacroStep>();
        Prim? last = null;

        foreach (var p in prims)
        {
            // 连击合并：同键同位置、与上一条点击间隔够近 → 变成"点击次数 N"
            if (last != null && p.MergeKey.StartsWith("click:") && p.MergeKey == last.MergeKey
                && Math.Abs(p.Px - last.Px) < ClickSlopPx && Math.Abs(p.Py - last.Py) < ClickSlopPx
                && p.StartT - last.EndT < MergeClickGapMs)
            {
                last.Step.LoopCount++;
                last.GapSum += p.StartT - last.EndT; last.GapCount++;
                last.Step.LoopDelayMs = (int)(last.GapSum / last.GapCount);
                last.Step.LoopDelayUnit = 0;
                last.EndT = p.EndT;
                continue;
            }
            // 滚轮合并：同方向且间隔够近 → 格数求和
            if (last != null && p.MergeKey == "wheel" && last.MergeKey == "wheel"
                && Math.Sign(p.Px) == Math.Sign(last.Px)
                && p.StartT - last.EndT < WheelGapMs)
            {
                last.Step.Wheel += p.Step.Wheel;
                last.EndT = p.EndT;
                continue;
            }

            // 等待：与上一条动作的间隔够大才生成（取整到 50ms）
            if (last != null)
            {
                long gap = p.StartT - last.EndT;
                if (gap >= MinWaitMs)
                    steps.Add(new MacroStep { Type = "Wait", DurationMs = (int)(Math.Round(gap / 50.0) * 50), DurationUnit = 0 });
            }
            steps.Add(p.Step);
            last = p;
        }

        // 滚轮格数在合并后可能相互抵消为 0（快速上下滚），删掉这类空动作
        steps.RemoveAll(s => s.Type == "MouseWheel" && s.Wheel == 0);
        return steps;
    }
}
