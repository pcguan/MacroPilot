using System;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Input;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>
/// 执行引擎：后台线程跑一个方案。支持方案/动作级循环、组合(递归)、等待、跳转(goto)、
/// 监听动作(成功/结束/失败)，以及暂停/继续/停止与进度上报。
/// </summary>
public sealed class MacroRunner
{
    private readonly IInputBackend _backend;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _gate = new(true); // set=运行，reset=暂停
    private Task? _task;
    private readonly Random _rng = new();
    private double _jitterMs;   // 拟人化：定时值的 ± 随机偏移上限（可小数，0=关闭）

    // 给定时值加 [-_jitterMs, +_jitterMs] 的连续随机偏移（仅对 >0 的值，避免给瞬时动作凭空加延迟；结果不为负）。
    private double Jitter(double baseMs)
    {
        if (_jitterMs <= 0 || baseMs <= 0) return baseMs;
        return Math.Max(0, baseMs + (_rng.NextDouble() * 2 - 1) * _jitterMs);
    }

    public bool IsRunning => _task is { IsCompleted: false };
    public bool IsPaused { get; private set; }

    public event Action<MacroStep, bool>? StepStateChanged;  // (step, isExecuting)
    public event Action<string, string>? Log;                // (level, message) 普通日志行
    public event Action<string>? ActBegin;                   // 开始一条动作日志行（状态=执行中）
    public event Action<string, string>? ActEnd;             // 结束动作日志行 (状态文字, 状态种类)
    public event Action<int, string>? Progress;              // (percent, 状态串)
    public event Action<bool>? PausedChanged;                // isPaused
    public event Action<string>? Finished;                   // Done/Stopped/Error
    public event Action<string>? PlanLoopChanged;            // 方案循环文本"第 N/总 轮"（每轮开始变；属方案级，与动作级状态分开显示）

    public MacroRunner(IInputBackend backend) => _backend = backend;

    public void Start(MacroPlan plan, int execDelayMs, double jitterMs = 0)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _gate.Set();
        IsPaused = false;
        _jitterMs = Math.Max(0, jitterMs);
        var ct = _cts.Token;
        _task = Task.Run(() =>
        {
            string reason = "Done";
            _total = plan.Steps.Count;
            _planLoops = plan.LoopCount;
            PreciseTimer.BeginHighResolution();  // 执行期把系统定时器分辨率提到 1ms，配合亚毫秒自旋
            try
            {
                if (execDelayMs > 0 && ct.WaitHandle.WaitOne(execDelayMs)) { reason = "Stopped"; return; }
                int laps = 0;
                bool waitingWindow = false;
                while (!ct.IsCancellationRequested)
                {
                    // 方案级运行条件：不满足时整方案空转等待时间窗开启（每秒复检、可即时停止），不消耗循环次数。
                    if (!Evaluate(plan, out var planCond))
                    {
                        // 条件类型现已不止时间段（还有图片出现），日志报【实际观测状态】而不是写死"不在时间段内"。
                        if (!waitingWindow) { waitingWindow = true; Log?.Invoke("Warning", $"⏸ 方案运行条件未满足（{planCond}），等待条件满足…"); Progress?.Invoke(0, "等待运行条件…"); }
                        if (ct.WaitHandle.WaitOne(1000)) break;
                        continue;
                    }
                    if (waitingWindow) { waitingWindow = false; Log?.Invoke("Info", "▶ 运行条件已满足，开始执行。"); }
                    _lap = laps + 1;
                    PlanLoopChanged?.Invoke($"第 {_lap}{LoopTot(_planLoops)} 轮");
                    Log?.Invoke("Info", $"— 第 {_lap}{LoopTot(_planLoops)} 轮 —");
                    RunTop(plan.Steps, ct);
                    laps++;
                    if (plan.LoopCount != 0 && laps >= plan.LoopCount) break;
                    if (ct.IsCancellationRequested) break;
                    if (plan.LoopDelayMs > 0) Wait(Jitter(plan.LoopDelayMs), ct);
                }
                if (ct.IsCancellationRequested) reason = "Stopped";
            }
            catch (OperationCanceledException) { reason = "Stopped"; }
            catch (Exception ex) { reason = "Error"; Log?.Invoke("Error", $"执行异常：{ex.Message}"); }
            finally
            {
                PreciseTimer.EndHighResolution();
                DisposeTemplateCache();
                try { _backend.ReleaseAll(); } catch { }
                Log?.Invoke(reason == "Done" ? "Success" : (reason == "Stopped" ? "Warning" : "Error"),
                    reason switch { "Done" => "✔ 方案运行完成。", "Stopped" => "⏹ 方案已停止。", _ => "方案运行出错。" });
                Finished?.Invoke(reason);
            }
        });   // 不把 ct 传给 Task.Run：否则 Start 后立刻 Stop 会让任务在调度前即 Canceled，
              // finally（ReleaseAll / Finished）都不执行——热键不释放、运行页卡在“运行中”。内部已自查取消。
    }

    public void Stop() { _cts?.Cancel(); _gate.Set(); }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        IsPaused = true; _gate.Reset(); PausedChanged?.Invoke(true);
        Log?.Invoke("Warning", "已暂停");
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused) return;
        IsPaused = false; _gate.Set(); PausedChanged?.Invoke(false);
        Log?.Invoke("Info", "已继续");
    }

    private void Gate(CancellationToken ct)
    {
        if (!_gate.IsSet) _gate.Wait(ct);
    }

    // 状态上下文：当前轮 _lap、顶层 _si/_total、组合子项 _ci/_cn。进度条表示"当前动作"的完成度（等待=倒计时，瞬时动作=100%）。
    private int _lap, _total, _si, _ci, _cn;
    private int _planLoops;                 // 方案循环总数：0=无限，1=单次，N=N 次
    private int _grpLoop, _grpLoopTotal;    // 当前组合自身循环：当前次 / 总次（0=无限，1=不循环）
    private int _stepLoop, _stepLoopTotal;  // 当前动作自身循环：当前次 / 总次（0=无限，1=不循环）

    // 动作级状态文本：动作 i/总 (· 子 k/n) (· 组循环 i/n) (· 循环 i/n) (· 等待 Xs / Ys)。
    // 方案级"第 N/总 轮"不在这里——它经 PlanLoopChanged 事件显示在方案名后面（层次分开）。
    private string StatusLine(double waitElapsedMs = -1, double waitTotalMs = -1)
    {
        string s = $"动作 {_si}/{_total}";
        if (_cn > 0) s += $" · 子 {_ci}/{_cn}";
        if (_cn > 0 && _grpLoopTotal != 1) s += $" · 组循环 {_grpLoop}{LoopTot(_grpLoopTotal)}";
        if (_stepLoopTotal != 1) s += $" · 循环 {_stepLoop}{LoopTot(_stepLoopTotal)}";
        if (waitTotalMs >= 0) s += $" · 等待 {waitElapsedMs / 1000.0:0.0}s / {waitTotalMs / 1000.0:0.0}s";
        return s;
    }
    // 循环"当前/总"里的"/总"部分：0=无限→"/∞"，1=不循环→空，N→"/N"
    private static string LoopTot(int total) => total == 0 ? "/∞" : (total == 1 ? "" : $"/{total}");

    private void Wait(double ms, CancellationToken ct, bool report = false)
    {
        // 高精度等待（支持小数毫秒）：粗睡分段（≤50ms，便于暂停/停止即时响应）+ 末段自旋补足亚毫秒尾巴。
        // report 时按已过时间推进进度并在状态栏显示等待百分比。
        if (ms <= 0) return;
        double total = ms;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Gate(ct);
            double remain = total - sw.Elapsed.TotalMilliseconds;
            if (remain <= 0) break;
            if (remain > 2)
            {
                int slice = (int)Math.Min(50, remain - 1.5);
                if (slice > 0 && ct.WaitHandle.WaitOne(slice)) throw new OperationCanceledException();
            }
            else
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                Thread.SpinWait(80);
            }
            if (report)
            {
                double done = Math.Min(total, sw.Elapsed.TotalMilliseconds);
                Progress?.Invoke((int)(done / total * 100), StatusLine(done, total));
            }
        }
    }

    // Jump 动作执行时上报到这里（无论它在顶层、组合内还是监听里），由 RunTop 在当前顶层步骤结束后统一消费。
    private MacroStep? _pendingJump;

    // 顶层动作序列
    private void RunTop(System.Collections.Generic.IList<MacroStep> steps, CancellationToken ct)
    {
        var jumpUsed = new System.Collections.Generic.Dictionary<MacroStep, int>();   // 已跳次数按 Jump 实例计（本轮内）
        _pendingJump = null;
        int i = 0;
        while (i < steps.Count)
        {
            ct.ThrowIfCancellationRequested();
            Gate(ct);
            var step = steps[i];
            _si = i + 1; _ci = 0; _cn = 0;
            string prefix = $"[{i + 1}/{steps.Count}] ";
            if (step.Disabled)   // 已禁用：整步跳过（含组合/嵌套组合），既不执行也不触发其跳转
            {
                Log?.Invoke("Info", prefix + $"{step.Display}（已禁用，跳过）");
                i++; continue;
            }
            if (step.IsGroup)
            {
                if (!ShouldRun(step, out var conditionText))
                {
                    Log?.Invoke("Info", prefix + $"{step.Display}，条件不满足，已跳过（{conditionText}）");
                }
                else
                {
                    Log?.Invoke("Info", prefix + $"组合（{step.Children.Count} 个动作）");
                    RunGroup(step, ct);
                    Log?.Invoke("Success", prefix + "组合执行完成");
                }
            }
            else
            {
                RunLeaf(step, prefix + step.Display, ct);
            }

            // 消费跳转：Jump = goto，执行到就跳（顶层 / 组合内 / 监听里执行都会上报）。
            // JumpTimes =「最大重复次数」，仅作防死循环上限（0=不限）：本轮内已跳次数达到上限后
            // 该跳转失效、顺序往下走。旧格式挂在其它动作上的 JumpTarget 一律忽略。
            var jumpSrc = _pendingJump; _pendingJump = null;
            if (jumpSrc != null && jumpSrc.JumpTarget >= 1 && jumpSrc.JumpTarget <= steps.Count)
            {
                jumpUsed.TryGetValue(jumpSrc, out var used);
                if (jumpSrc.JumpTimes <= 0 || used < jumpSrc.JumpTimes) { jumpUsed[jumpSrc] = used + 1; i = jumpSrc.JumpTarget - 1; continue; }
            }
            i++;
        }
    }

    // 组合：高亮整组，逐个子动作记日志(执行中→成功/失败)，支持组合自身循环。
    private void RunGroup(MacroStep group, CancellationToken ct, int depth = 0)
    {
        string indent = new string(' ', 4 * (depth + 1));   // 逐层缩进：嵌套子组合的子动作日志层级正确，不再只有一层
        int loops = 0;
        while (!ct.IsCancellationRequested)
        {
            Gate(ct);
            StepStateChanged?.Invoke(group, true);
            try
            {
                for (int k = 0; k < group.Children.Count; k++)
                {
                    _ci = k + 1; _cn = group.Children.Count;
                    _grpLoop = loops + 1; _grpLoopTotal = group.LoopCount;   // 组合自身循环上下文（每子动作前重置，防嵌套组合覆盖）
                    var child = group.Children[k];
                    if (child.Disabled) { Log?.Invoke("Info", $"{indent}└ 子 {k + 1}/{group.Children.Count}：{child.Display}（已禁用，跳过）"); continue; }
                    if (child.IsGroup)   // 嵌套子组合：递归执行，其自身的循环/监听/运行条件都照常生效
                    {
                        if (ShouldRun(child, out var reason))
                        {
                            Log?.Invoke("Info", $"{indent}└ 子 {k + 1}/{group.Children.Count}：组合（{child.Children.Count} 个动作）");
                            RunGroup(child, ct, depth + 1);
                        }
                        else Log?.Invoke("Info", $"{indent}└ 子 {k + 1}/{group.Children.Count}：组合条件不满足，已跳过（{reason}）");
                    }
                    else RunLeaf(child, $"{indent}└ 子 {k + 1}/{group.Children.Count}：{child.Display}", ct);
                }
            }
            finally { StepStateChanged?.Invoke(group, false); _ci = 0; _cn = 0; }
            loops++;
            if (group.LoopCount == 1) break;
            if (group.LoopCount != 0 && loops >= group.LoopCount) break;
            if (group.LoopDelayMs > 0) Wait(Jitter(group.LoopDelayMs), ct);   // 重复间隔：仅在还要再跑一轮时等
        }
        RunHook(group.SuccessAction, "成功后", ct);
        RunHook(group.CompleteAction, "结束后", ct);
    }

    // 叶子动作：写一条"执行中"日志行 → 执行(含自身循环) → 改为执行成功/失败/已停止；并高亮该行 + 跑监听。
    private bool RunLeaf(MacroStep step, string body, CancellationToken ct)
    {
        if (!ShouldRun(step, out var conditionText))
        {
            ActBegin?.Invoke(body);
            ActEnd?.Invoke("已跳过", "Warning");
            Log?.Invoke("Info", $"条件不满足，跳过动作：{conditionText}");
            return false;
        }

        ActBegin?.Invoke(body);
        StepStateChanged?.Invoke(step, true);
        _stepLoop = 1; _stepLoopTotal = step.LoopCount;   // 当前动作自身循环上下文
        Progress?.Invoke(0, StatusLine());   // 新动作进度归零（等待动作随后 0→100，瞬时动作下方置 100）
        bool ok = true;
        try
        {
            int loops = 0;
            while (!ct.IsCancellationRequested)
            {
                Gate(ct);
                _stepLoop = loops + 1;
                if (loops > 0) Progress?.Invoke(0, StatusLine());   // 后续每圈刷新"循环 i/n"（首圈上面已刷）
                RunOne(step, ct);
                loops++;
                if (step.LoopCount == 1) break;
                if (step.LoopCount != 0 && loops >= step.LoopCount) break;
                if (step.LoopDelayMs > 0) Wait(Jitter(step.LoopDelayMs), ct);   // 重复间隔：仅在还要再跑一轮时等
            }
        }
        catch (OperationCanceledException) { StepStateChanged?.Invoke(step, false); ActEnd?.Invoke("已停止", "Stopped"); throw; }
        catch (Exception ex)
        {
            ok = false;
            StepStateChanged?.Invoke(step, false);
            ActEnd?.Invoke("执行失败", "Fail");
            Log?.Invoke("Error", $"执行失败：{ex.Message}");
            RunHook(step.FailAction, "失败后", ct);
        }
        if (ok)
        {
            StepStateChanged?.Invoke(step, false);
            Progress?.Invoke(100, StatusLine());   // 当前动作完成 → 100%
            ActEnd?.Invoke("执行成功", "Success");
            RunHook(step.SuccessAction, "成功后", ct);
        }
        RunHook(step.CompleteAction, "结束后", ct);
        return true;
    }

    // 监听动作＝完整动作：支持自身循环 / 运行条件 / 组合 / 再嵌套监听（递归）——走 RunLeaf/RunGroup 与外层动作同款，
    // 不再是"叶子级一次性执行"的阉割版。急停(取消)要穿透；监听自身失败不影响主流程(RunLeaf 内部已处理其 FailAction/日志)。
    private void RunHook(MacroStep? hook, string kind, CancellationToken ct)
    {
        if (hook is null) return;
        try
        {
            if (hook.IsGroup)
            {
                if (ShouldRun(hook, out var reason)) { Log?.Invoke("Info", $"    ↳ 监听（{kind}）：组合（{hook.Children.Count} 个动作）"); RunGroup(hook, ct); }
                else Log?.Invoke("Info", $"    ↳ 监听（{kind}）：组合条件不满足，跳过（{reason}）");
            }
            else RunLeaf(hook, $"    ↳ 监听（{kind}）：{hook.Display}", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // 图片条件模板缓存：base64+PNG 解码一次即缓存 Bitmap（键=图片内容，内容变了自动失效）；一次运行结束在 finally 里释放。
    private readonly System.Collections.Generic.Dictionary<IRunCondition, (string key, System.Drawing.Bitmap bmp)> _tplCache = new();
    private System.Drawing.Bitmap? TemplateFor(IRunCondition step)
    {
        var img = step.RunConditionImage;
        if (string.IsNullOrEmpty(img)) return null;
        if (_tplCache.TryGetValue(step, out var c))
        {
            if (c.key == img) return c.bmp;
            c.bmp.Dispose(); _tplCache.Remove(step);   // 图片被换过 → 旧缓存作废
        }
        var bytes = ImageStore.Bytes(img);   // 引用(file:hash)读文件 / 旧内联 base64 都支持
        if (bytes == null) return null;
        try { var bmp = ScreenMatch.FromPng(bytes); _tplCache[step] = (img, bmp); return bmp; }
        catch { return null; }
    }
    private void DisposeTemplateCache()
    {
        foreach (var kv in _tplCache) { try { kv.Value.bmp.Dispose(); } catch { } }
        _tplCache.Clear();
    }

    private bool ShouldRun(MacroStep step, out string conditionText) => Evaluate(step, out conditionText);

    /// <summary>判定一条运行条件是否放行。方案级与动作级共用，conditionText 仅在【跳过】时用于打日志。</summary>
    private bool Evaluate(IRunCondition step, out string conditionText)
    {
        conditionText = "";
        if (!RunCondition.Has(step)) return true;
        if (step.RunConditionType == "ImageMatch")
        {
            // 屏内相对坐标 → 按该屏当前位置还原绝对区域（挪动显示器后区域跟着屏走）。
            var mon = ScreenInfo.ByDevice(step.RunConditionMonitor);
            int ax = mon.Left + step.RunConditionRectX, ay = mon.Top + step.RunConditionRectY;
            double score = ScreenMatch.MatchScore(TemplateFor(step), ax, ay);
            bool found = score >= step.RunConditionThreshold;
            // conditionText 只在【跳过】时打日志，报【实际观测状态 + 匹配度】——便于判断是"没匹配上"还是"阈值太严"。
            string pct = score < 0 ? "无模板" : $"匹配度 {score:0.00}/阈值 {step.RunConditionThreshold:0.00}";
            conditionText = (found ? "目标图片已出现" : "目标图片未出现") + $"（{pct}）";
            return step.RunConditionInvert ? !found : found;
        }
        if (step.RunConditionType != "TimeRange") return true;

        int now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        bool match = IsInTimeRange(now, step.RunConditionStartMinute, step.RunConditionEndMinute);
        bool result = step.RunConditionInvert ? !match : match;
        conditionText = FormatCondition(step, match);   // 同理：报当前"在/不在"时段的实际结果
        return result;
    }

    private static bool IsInTimeRange(int now, int? start, int? end)
    {
        if (start.HasValue && end.HasValue)
        {
            int s = NormalizeMinute(start.Value);
            int e = NormalizeMinute(end.Value);
            return s <= e ? now >= s && now <= e : now >= s || now <= e;
        }
        if (start.HasValue) return now >= NormalizeMinute(start.Value);
        if (end.HasValue) return now <= NormalizeMinute(end.Value);
        return true;
    }

    private static string FormatCondition(IRunCondition step, bool inRange)
    {
        string range = (step.RunConditionStartMinute, step.RunConditionEndMinute) switch
        {
            (int s, int e) => $"{FormatMinute(s)}-{FormatMinute(e)}",
            (int s, null) => $"{FormatMinute(s)}之后",
            (null, int e) => $"{FormatMinute(e)}之前",
            _ => "未设置"
        };
        return inRange ? $"当前在 {range}" : $"当前不在 {range}";
    }

    private static int NormalizeMinute(int minute) => ((minute % 1440) + 1440) % 1440;
    private static string FormatMinute(int minute)
    {
        minute = NormalizeMinute(minute);
        return $"{minute / 60:00}:{minute % 60:00}";
    }

    // 纯执行 IO（日志由 RunLeaf/RunGroup 负责）。组合在此仅作兜底（正常走 RunGroup）。
    private void RunOne(MacroStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Gate(ct);
        switch (step.Type)
        {
            case "Group": foreach (var c in step.Children) RunOne(c, ct); break;
            case "Wait": Wait(Jitter(step.DurationMs), ct, report: true); break;
            case "KeyTap":
                // 键名在两张键码表里都查不到 → 该键在任一后端都发不出，先告警（不静默“假装成功”）。仅本步首次循环记一条，别每圈刷屏。
                if (_stepLoop == 1 && !string.IsNullOrEmpty(step.Key) && !KeyMap.Hid.ContainsKey(step.Key) && !KeyMap.Vk.ContainsKey(step.Key))
                    Log?.Invoke("Warning", $"按键「{step.Key}」无对应键码，无法发送（仅发出修饰键，若有）。");
                _backend.KeyTap(step.Key, step.Modifier, Jitter(step.HoldMs), ct);
                break;
            case "MouseClick": _backend.MouseClick(step.Button, Jitter(step.HoldMs), ct); break;
            case "MouseMove": MoveToStepTarget(step, ct); break;
            // 拖动 = 移到起点 → 按下 → 移到终点（可拟人化）→ 松开。
            // 按下后、松开前各留一小段，太快会被目标程序识别成一次点击而不是拖动。
            case "MouseDrag":
                MoveToStepTarget(step, ct);          // 起点（MoveMonitor/MoveNormX/Y）
                ct.ThrowIfCancellationRequested();
                _backend.MouseDown(step.Button);
                try
                {
                    Wait(60, ct);
                    var (ex, ey) = ScreenInfo.Resolve(step.DragEndMonitor, step.DragEndNormX, step.DragEndNormY);
                    if (step.Humanize) MoveHumanized(ex, ey, ct);
                    else _backend.MouseMove(ex, ey);
                    Wait(60, ct);
                }
                finally { _backend.MouseUp(step.Button); }
                break;
            // 点击坐标 = 移动 + 点击。移动段与 MouseMove 完全一致（含拟人化），到位后再按 MouseClick 那套点。
            case "MouseClickAt":
                MoveToStepTarget(step, ct);
                ct.ThrowIfCancellationRequested();
                _backend.MouseClick(step.Button, Jitter(step.HoldMs), ct);
                break;
            case "MouseWheel": _backend.MouseWheel(step.Wheel); break;
            // 跳转动作：上报给 RunTop，在当前顶层步骤结束后跳到目标序号（在组合内/监听里执行也生效）。
            case "Jump": _pendingJump = step; break;
            case "ActivateWindow":
                if (step.TargetProcess == WindowActivator.DesktopSentinel)
                {
                    WindowActivator.FocusDesktop(step.TargetTitle);
                    Log?.Invoke("Info", string.IsNullOrEmpty(step.TargetTitle) ? "已聚焦桌面（所有应用失活）" : $"已聚焦桌面 · {ScreenInfo.ByDevice(step.TargetTitle).Label}");
                    break;
                }
                if (!WindowActivator.Activate(step.TargetPid, step.TargetProcess, step.TargetTitle, out var matched))
                    throw new InvalidOperationException($"未找到目标窗口（{step.TargetProcess}.exe PID {step.TargetPid}）");
                Log?.Invoke("Info", $"已激活窗口：{matched}");
                break;
        }
    }

    // 把光标移到该动作配置的目标点（MouseMove / MouseClickAt 共用）。
    private void MoveToStepTarget(MacroStep step, CancellationToken ct)
    {
        var (vx, vy) = string.IsNullOrEmpty(step.MoveMonitor)
            ? (step.X, step.Y)                                            // 旧数据：主屏像素
            : ScreenInfo.Resolve(step.MoveMonitor, step.MoveNormX, step.MoveNormY);
        if (step.Humanize) MoveHumanized(vx, vy, ct);   // 动作级：每个移动动作各自决定
        else _backend.MouseMove(vx, vy);
    }

    // 拟人化移动。同屏：直接 HumanizeWithin。跨屏按后端能力分流：
    //  · native（VIRTUALDESK 绝对，可一次落到虚拟桌面任意处）→ 整段【连续】拟人化，路点 clamp 到虚拟桌面并集；
    //  · CH9329（0x04 只映主屏、屏间靠相邻相对路由）→ 把相邻屏路径拆成【逐屏子段】各自拟人化，子段之间会话内
    //    一次相对收敛越过共享边（仅相邻屏可越）。这样跨屏也是逐屏可见轨迹，而非"整段瞬移只剩一小截收尾"。
    private void MoveHumanized(int tx, int ty, CancellationToken ct)
    {
        var (sx, sy) = ScreenInfo.CursorPos();
        double dist = Math.Sqrt((double)(tx - sx) * (tx - sx) + (double)(ty - sy) * (ty - sy));
        if (dist < 3) { _backend.MouseMove(tx, ty); return; }

        string tDev = ScreenInfo.FromPoint(tx, ty).device;
        string sDev = ScreenInfo.FromPoint(sx, sy).device;

        // 同屏。
        if (sDev == tDev) { HumanizeWithin(sx, sy, tx, ty, ScreenInfo.ByDevice(tDev), ct); return; }

        // 跨屏 · native/软件：整段连续拟人化（clamp 到虚拟桌面并集）。
        if (_backend.ContinuousAcrossScreens)
        {
            var vb = ScreenInfo.VirtualBounds();
            _backend.BeginMove();
            try
            {
                EmitStroke(sx, sy, tx, ty, MoveDuration(dist), vb.Left, vb.Top, vb.Right - 1, vb.Bottom - 1, false, ct);
                _backend.MouseMove(tx, ty);
            }
            finally { _backend.EndMove(); }
            return;
        }

        // 跨屏 · CH9329：逐屏分段拟人化。锚点序列相邻两点要么同屏（EmitStroke）、要么一次跨缝（相对收敛越过共享边）。
        var anchors = (_backend as Ch9329Device)?.PlanCrossAnchors(sx, sy, tx, ty);
        if (anchors is { Count: >= 2 })
        {
            _backend.BeginMove();
            try
            {
                for (int i = 0; i + 1 < anchors.Count; i++)
                {
                    var (ax, ay) = anchors[i];
                    var (bx, by) = anchors[i + 1];
                    string da = ScreenInfo.FromPoint(ax, ay).device, db = ScreenInfo.FromPoint(bx, by).device;
                    if (da == db)   // 同屏子段 → 逐屏拟人化
                    {
                        var m = ScreenInfo.ByDevice(da);
                        double legDist = Math.Sqrt((double)(bx - ax) * (bx - ax) + (double)(by - ay) * (by - ay));
                        EmitStroke(ax, ay, bx, by, MoveDuration(legDist), m.Left, m.Top, m.Right - 1, m.Bottom - 1, true, ct);
                    }
                    else            // 跨缝：会话内一次相对收敛越过共享边（短、无法平滑）
                    {
                        ct.ThrowIfCancellationRequested(); Gate(ct);
                        _backend.MouseMove(bx, by);
                    }
                }
                _backend.MouseMove(tx, ty);   // 精确到位
            }
            finally { _backend.EndMove(); }
            return;
        }

        // 兜底（拓扑不连通/异常）：老路——0x04 转移到目标屏入口点 + 屏内拟人化收尾。
        var tMon = ScreenInfo.ByDevice(tDev);
        double ux = (sx - tx) / dist, uy = (sy - ty) / dist;
        double L = Math.Clamp(Math.Min(tMon.Width, tMon.Height) * 0.35, 120, 260);
        int ex = Math.Clamp((int)Math.Round(tx + ux * L), tMon.Left + 4, tMon.Right - 5);
        int ey = Math.Clamp((int)Math.Round(ty + uy * L), tMon.Top + 4, tMon.Bottom - 5);
        _backend.MouseMove(ex, ey);
        HumanizeWithin(ex, ey, tx, ty, tMon, ct);
    }

    // 单块屏 mon 内从 (sx,sy) 拟人化移动到 (tx,ty)。数据驱动（实测真人轨迹 CSV 得出，见 [[project_ch9329_closed_loop_move]]）：
    //  ① 时长随距离【次线性】增长 + 高地板 + 每次随机 → 10px≈440ms、100px≈525ms、1000px≈800ms，同距离每次不同；
    //  ② ~120Hz 连续发点（native 每帧 ~8ms），不再大步"打点"；
    //  ③ 速度剖面【前快后长减速】——峰值在约 1/4 处（真人 tpeakfrac≈0.26）；
    //  ④ 轻微弧线 + 相关抖动，控制点/抖动每次随机 → 同起终点轨迹也不同；
    //  ⑤ 大距离偶发过冲后短促回正。
    private void HumanizeWithin(int sx, int sy, int tx, int ty, ScreenInfo.Monitor mon, CancellationToken ct)
    {
        double dist = Math.Sqrt((double)(tx - sx) * (tx - sx) + (double)(ty - sy) * (ty - sy));
        if (dist < 3) { _backend.MouseMove(tx, ty); return; }
        int L = mon.Left, T = mon.Top, R = mon.Right - 1, B = mon.Bottom - 1;
        bool ch9329 = _backend is Ch9329Device;

        double dur = MoveDuration(dist);                                     // ① 主段时长（次线性+高地板+随机）

        // ⑤ 过冲：仅较大距离偶发（真人强过冲约 6%）。冲到目标外一点点，随后短促回正——弱修正很常见。
        double ex = tx, ey = ty;
        bool overshoot = dist > 140 && _rng.NextDouble() < 0.32;
        if (overshoot)
        {
            double ux = (tx - sx) / dist, uy = (ty - sy) / dist;
            double amt = 4 + _rng.NextDouble() * Math.Min(dist * 0.06, 16);
            double perp = (_rng.NextDouble() * 2 - 1) * amt * 0.6;
            ex = Math.Clamp(tx + ux * amt - uy * perp, L, R);
            ey = Math.Clamp(ty + uy * amt + ux * perp, T, B);
        }
        _backend.BeginMove();
        try
        {
            EmitStroke(sx, sy, ex, ey, dur, L, T, R, B, ch9329, ct);
            if (overshoot)
            {
                Wait(40 + _rng.NextDouble() * 70, ct);                       // 冲过后短暂迟疑
                EmitStroke(ex, ey, tx, ty, 90 + _rng.NextDouble() * 90, L, T, R, B, ch9329, ct); // 收手回正（短、近乎直线）
            }
            _backend.MouseMove(Math.Clamp(tx, L, R), Math.Clamp(ty, T, B)); // 精确到位
        }
        finally { _backend.EndMove(); }
    }

    // 移动时长（毫秒）：次线性于距离 + 高地板 + 每次随机。锚点取自真人录制/反馈：10→~440,100→~525,1000→~800。
    // 偶发"快挥"进一步拉开变异，保证同一距离多次移动时长不同。
    private double MoveDuration(double dist)
    {
        double baseMs = 400 + 12.6 * Math.Sqrt(dist);
        double f = 0.80 + _rng.NextDouble() * 0.40;                          // ±20% 个体变异
        if (_rng.NextDouble() < 0.14) f *= 0.62;                             // 偶发快挥
        return Math.Clamp(baseMs * f, 110, 3000);
    }

    // 沿"随机轻弧的三次贝塞尔"从 (sx,sy) 到 (ex,ey)，按【前快后慢】速度剖面在 dur 毫秒内分帧走完，
    // 叠加相关抖动（近终点淡出以精确落点）。native 每帧 ~8ms 即时发点（~120Hz 连续，不打点）；
    // CH9329 硬件无法高频——沿同一路径按累计位移降采样（少而大的相对闭环路点），靠收敛自身节拍。
    private void EmitStroke(double sx, double sy, double ex, double ey, double dur,
                            int L, int T, int R, int B, bool ch9329, CancellationToken ct)
    {
        double D = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
        if (D < 1.5) { _backend.MouseMove(Math.Clamp((int)Math.Round(ex), L, R), Math.Clamp((int)Math.Round(ey), T, B)); return; }
        double ux = (ex - sx) / D, uy = (ey - sy) / D, pxn = -uy, pyn = ux;  // 沿/垂直单位向量

        // ④ 控制点：沿轴约 1/3、2/3 处，各自独立的垂直偏移（同号=C 形弧，异号=S 形）→ 每次轨迹不同。
        double arc = D * (0.03 + _rng.NextDouble() * 0.09);
        double o1 = (_rng.NextDouble() * 2 - 1) * arc, o2 = (_rng.NextDouble() * 2 - 1) * arc;
        double f1 = 0.28 + _rng.NextDouble() * 0.12, f2 = 0.60 + _rng.NextDouble() * 0.14;
        double c1x = sx + ux * D * f1 + pxn * o1, c1y = sy + uy * D * f1 + pyn * o1;
        double c2x = sx + ux * D * f2 + pxn * o2, c2y = sy + uy * D * f2 + pyn * o2;

        // ③ 速度剖面 v(u)=u^a·(1-u)^b（起于 0、止于 0），峰值在 a/(a+b)。取峰值分数 ~0.22–0.34（真人≈0.26）→ 前快后长减速。
        double peak = 0.22 + _rng.NextDouble() * 0.12;
        double a = 1.2, b = a * (1 - peak) / peak;
        const int K = 64;
        var cdf = new double[K + 1];
        for (int j = 1; j <= K; j++)
        {
            double u = (double)j / K, up = (double)(j - 1) / K;
            double vj = Math.Pow(u, a) * Math.Pow(1 - u, b);
            double vp = Math.Pow(up, a) * Math.Pow(1 - up, b);
            cdf[j] = cdf[j - 1] + (vj + vp) * 0.5;                            // 梯形积分累积
        }
        double tot = cdf[K] <= 0 ? 1 : cdf[K];
        for (int j = 0; j <= K; j++) cdf[j] /= tot;                          // 归一到 [0,1]

        // 每个路点：贝塞尔取点 + 相关抖动（近终点 (1-s²) 淡出以精确落点）。
        double wx = 0, wy = 0, amp = Math.Min(D * 0.018, 3.2);
        (double fx, double fy) At(double s)
        {
            double mt = 1 - s;
            double bx = mt * mt * mt * sx + 3 * mt * mt * s * c1x + 3 * mt * s * s * c2x + s * s * s * ex;
            double by = mt * mt * mt * sy + 3 * mt * mt * s * c1y + 3 * mt * s * s * c2y + s * s * s * ey;
            wx = wx * 0.86 + (_rng.NextDouble() * 2 - 1) * amp * 0.5;
            wy = wy * 0.86 + (_rng.NextDouble() * 2 - 1) * amp * 0.5;
            double env = 1 - s * s;
            return (bx + wx * env, by + wy * env);
        }
        void Emit(double fx, double fy)
        {
            ct.ThrowIfCancellationRequested(); Gate(ct);
            _backend.MouseMove(Math.Clamp((int)Math.Round(fx), L, R), Math.Clamp((int)Math.Round(fy), T, B));
        }

        if (ch9329)
        {
            // 硬件后端：每个路点=一次相对闭环收敛（阻塞、每点数十 ms），物理上做不到 120Hz。
            // 但仍让它带【前快后慢】手感——按【时间均匀】取 K 个路点（不是按位移均匀），
            // 于是路点在空间上【前疏后密】：起手大跳、末段小步逼近，收敛次数≈D/18 不变（速度不退化）。
            int steps = Math.Clamp((int)Math.Round(D / 18.0), 4, 64);
            for (int k = 1; k <= steps; k++)
            {
                var (fx, fy) = At(CdfLerp(cdf, K, (double)k / steps));
                Emit(fx, fy);
            }
        }
        else
        {
            // 软件后端：~8ms/帧即时发点（~120Hz 连续，不打点），每帧等待合计≈dur。
            int N = Math.Max(8, (int)Math.Round(dur / 8.0));
            double dtNative = dur / N;
            for (int i = 1; i <= N; i++)
            {
                var (fx, fy) = At(CdfLerp(cdf, K, (double)i / N));
                Emit(fx, fy);
                Wait(dtNative, ct);
            }
        }
    }

    // 在归一化 CDF（K 段梯形积分）上线性插值：给定时间分数 u∈[0,1]，返回已走弧长分数。
    private static double CdfLerp(double[] cdf, int K, double u)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;
        double g = u * K; int lo = (int)g; double fr = g - lo;
        if (lo >= K) return 1;
        return cdf[lo] + (cdf[lo + 1] - cdf[lo]) * fr;
    }
}
