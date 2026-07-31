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
    public event Action<double, string>? Progress;           // (percent 0~100，double——整数百分比只有 100 个台阶，等待类动作的进度条会一顿一顿)
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
                // 方案级条件的等待参数：历来是硬编码"每秒复检、无限等"，现改由配置驱动（默认值与旧行为一致）。
                // 间隔 0 = 判定失败立刻重判（不等待）；次数上限与时长上限同时生效，先到者结束。
                int planWaitMs = Math.Max(0, plan.RunConditionRetryIntervalMs);
                int planWaitMax = Math.Max(0, plan.RunConditionRetryMax);
                int planWaitTimeout = Math.Max(0, plan.RunConditionRetryTimeoutMs);
                int planWaited = 0;
                var planWaitClock = new System.Diagnostics.Stopwatch();
                while (!ct.IsCancellationRequested)
                {
                    // 方案级运行条件：不满足时整方案空转等待时间窗开启（每秒复检、可即时停止），不消耗循环次数。
                    if (!Evaluate(plan, out var planCond, ct))
                    {
                        // 条件类型现已不止时间段（还有图片出现），日志报【实际观测状态】而不是写死"不在时间段内"。
                        if (!waitingWindow)
                        {
                            waitingWindow = true; planWaitClock.Restart();
                            Log?.Invoke("Warning", $"⏸ 方案运行条件未满足（{planCond}），{RetryDesc(planWaitMs, planWaitMax, planWaitTimeout)}…");
                            Progress?.Invoke(0, "等待运行条件…");
                        }
                        if (planWaitMax > 0 && planWaited >= planWaitMax)
                        {
                            Log?.Invoke("Warning", $"方案运行条件重复检查 {planWaited} 次仍未满足，结束运行。");
                            break;
                        }
                        if (planWaitTimeout > 0 && planWaitClock.ElapsedMilliseconds >= planWaitTimeout)
                        {
                            Log?.Invoke("Warning", $"方案运行条件重复检查已超过 {planWaitTimeout} 毫秒仍未满足，结束运行。");
                            break;
                        }
                        planWaited++;
                        if (planWaitMs > 0 && ct.WaitHandle.WaitOne(planWaitMs)) break;
                        continue;
                    }
                    if (waitingWindow) { waitingWindow = false; planWaited = 0; planWaitClock.Reset(); Log?.Invoke("Info", "▶ 运行条件已满足，开始执行。"); }
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
    // 重复检查的参数描述（日志用）：间隔 0 说明是"立刻重判"，两个上限都可能为 0（不限）。
    private static string RetryDesc(int intervalMs, int max, int timeoutMs)
    {
        string head = intervalMs > 0 ? $"每 {intervalMs} 毫秒重新检查" : "失败后立刻重新检查";
        if (max > 0) head += $"，最多 {max} 次";
        if (timeoutMs > 0) head += $"，最多 {timeoutMs} 毫秒";
        return head;
    }

    // 循环"当前/总"里的"/总"部分：0=无限→"/∞"，1=不循环→空，N→"/N"
    private static string LoopTot(int total) => total == 0 ? "/∞" : (total == 1 ? "" : $"/{total}");

    private void Wait(double ms, CancellationToken ct, bool report = false)
    {
        // 高精度等待（支持小数毫秒）：粗睡分段（≤50ms，便于暂停/停止即时响应）+ 末段自旋补足亚毫秒尾巴。
        // report 时按已过时间推进进度并在状态栏显示等待百分比。
        if (ms <= 0) return;
        double total = ms;
        double lastReported = -100;   // 上次上报的已过毫秒：末段自旋每圈只有微秒级，不节流会在收尾几毫秒里疯狂拼串上报
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            // 暂停时停表、继续时重新计时——否则暂停期间秒表照走，恢复后 remain 变负、等待被"跳过"。
            if (!_gate.IsSet) { sw.Stop(); _gate.Wait(ct); sw.Start(); }
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
                if (done - lastReported >= 16 || done >= total)   // ~一帧一报足够（UI 端 80ms 才取一次）
                {
                    lastReported = done;
                    Progress?.Invoke(done / total * 100.0, StatusLine(done, total));
                }
            }
        }
    }

    // Jump 动作执行时上报到这里（无论它在顶层、组合内还是监听里），由 RunTop 在当前顶层步骤结束后统一消费。
    // 生效与否在【执行点】就裁决完毕（见 RunOne 的 Jump 分支）：_pendingJump 非空 ⇔ 必然跳出。
    private MacroStep? _pendingJump;
    private System.Collections.Generic.IList<MacroStep> _topSteps = System.Array.Empty<MacroStep>();
    private readonly System.Collections.Generic.Dictionary<MacroStep, int> _jumpUsed = new();   // 已跳次数按 Jump 实例计（本轮内）

    // 顶层动作序列
    private void RunTop(System.Collections.Generic.IList<MacroStep> steps, CancellationToken ct)
    {
        _topSteps = steps;      // 跳转执行点解析目标/上限要用
        _jumpUsed.Clear();      // 上限按【轮】计（与历史一致：每轮重新给额度）
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
            // 先亮起来再判条件：条件的"重复检查"可能等很久，此时界面上必须能看出卡在哪一步
            // （RunLeaf/RunGroup 内部随后还会再发一次，同一对象，无副作用）。
            StepStateChanged?.Invoke(step, true);
            if (step.IsGroup)
            {
                RepeatCycle(step, ct, () =>
                {
                    if (!GateHooks(step, out var conditionText, ct))
                    {
                        Log?.Invoke("Info", prefix + $"{step.Display}，条件不满足，已跳过（{conditionText}）");
                        CompleteHook(step, ct);   // 条件跳过也算一次"结束"
                    }
                    else
                    {
                        Log?.Invoke("Info", prefix + $"组合（{step.Children.Count} 个动作）");
                        RunGroup(step, ct);
                        Log?.Invoke("Success", prefix + "组合执行完成");
                    }
                });
            }
            else
            {
                RunLeaf(step, prefix + step.Display, ct);
            }

            // 消费跳转：Jump = goto，执行到就跳（顶层 / 组合内 / 监听里执行都会上报）。
            // JumpTimes =「最大重复次数」，仅作防死循环上限（0=不限）：本轮内已跳次数达到上限后
            // 该跳转失效、顺序往下走。旧格式挂在其它动作上的 JumpTarget 一律忽略。
            var jumpSrc = _pendingJump; _pendingJump = null;
            if (jumpSrc != null)
            {
                // 生效性（目标存在/未达上限）已在执行点裁决并记过日志，这里只负责真正跳
                int ti = JumpIndex(jumpSrc, steps);
                if (ti >= 0) { i = ti; continue; }
            }
            i++;
        }
    }

    // 跳转目标定位：JumpTargetId（绑定动作本身，增删排序都不会指错）优先；
    // 只有序号的旧存档回退用 JumpTarget。目标不在顶层（被删/移入组合）返回 -1，即不跳。
    // 解析跳转目标在顶层的下标：新格式按【别名】（首个同名者）；旧存档回退身份 Id、再回退序号。
    private static int JumpIndex(MacroStep jump, System.Collections.Generic.IList<MacroStep> steps)
    {
        if (jump.JumpTargetAlias.Length > 0)
        {
            for (int k = 0; k < steps.Count; k++)
                if (steps[k].Alias == jump.JumpTargetAlias) return k;
            return -1;   // 目标别名不存在（被改名/删除）→ 跳转不生效
        }
        if (jump.JumpTargetId.Length > 0)
        {
            for (int k = 0; k < steps.Count; k++)
                if (steps[k].Id == jump.JumpTargetId) return k;
            return -1;
        }
        int ti = jump.JumpTarget - 1;
        return ti >= 0 && ti < steps.Count ? ti : -1;
    }

    // 组合：高亮整组，逐个子动作记日志(执行中→成功/失败)，支持组合自身循环。
    private void RunGroup(MacroStep group, CancellationToken ct, int depth = 0)
    {
        string indent = new string(' ', 4 * (depth + 1));   // 逐层缩进：嵌套子组合的子动作日志层级正确，不再只有一层
        RunHook(group.PreRunAction, "运行前", ct);          // 组合整体开跑前（不随组合自身循环重复）
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
                        RepeatCycle(child, ct, () =>
                        {
                            if (GateHooks(child, out var reason, ct))
                            {
                                Log?.Invoke("Info", $"{indent}└ 子 {k + 1}/{group.Children.Count}：组合（{child.Children.Count} 个动作）");
                                RunGroup(child, ct, depth + 1);
                            }
                            else
                            {
                                Log?.Invoke("Info", $"{indent}└ 子 {k + 1}/{group.Children.Count}：组合条件不满足，已跳过（{reason}）");
                                CompleteHook(child, ct);   // 条件跳过也算一次"结束"
                            }
                        });
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
        RunHook(group.SuccessAction, "运行成功后", ct);
        CompleteHook(group, ct);
    }

    /// <summary>
    /// 【整趟重复】：把"判定运行条件 → 监听 → 动作本体"当作一趟，按 RepeatCount 重复若干趟。
    /// 与动作自身的【执行次数 LoopCount】是两回事——后者只重复动作本体（条件判一次、监听走一遍），
    /// 前者每一趟都重新判条件、重新触发监听。中途产生跳转就不再重复（跳转优先，否则永远跳不出去）。
    /// </summary>
    /// <summary>
    /// 「运行结束后」的统一入口（语义已定）：动作运行结束后触发；正常结束、失败结束、
    /// 条件不满足被跳过——都算结束、都触发；已登记将生效的跳转（跳出）——不触发。
    /// 设置了重复的动作趟内一律不触发，由 RepeatCycle 在全部趟结束后统一触发一次。
    /// </summary>
    private void CompleteHook(MacroStep step, CancellationToken ct)
    {
        if (_completeDeferred.Contains(step)) return;
        if (_pendingJump != null) return;
        RunHook(step.CompleteAction, "运行结束后", ct);
    }

    // 重复执行时的监听语义：每一趟都算一次「运行成功 / 失败」（照常每趟触发），
    // 但「运行结束后」＝这个动作的所有趟数全部结束，只触发一次（含被跳转提前结束；停止/取消不触发）。
    // 实现：趟内挂起该步骤的 CompleteAction（RunLeafOnce / RunGroup 见此集合就跳过），循环收尾统一补一次。
    private readonly System.Collections.Generic.HashSet<MacroStep> _completeDeferred = new();

    private void RepeatCycle(MacroStep step, CancellationToken ct, Action once)
    {
        bool multi = step.HasUntilCondition || step.RepeatCount != 1;
        if (!multi) { Gate(ct); once(); return; }   // 单趟：结束监听由趟内正常触发，行为与历史一致

        _completeDeferred.Add(step);
        try
        {
            if (step.HasUntilCondition) RepeatUntilCycle(step, ct, once);
            else
            {
                int reps = Math.Max(0, step.RepeatCount);
                int done = 0;
                while (!ct.IsCancellationRequested)
                {
                    Gate(ct);
                    once();
                    done++;
                    if (_pendingJump != null) break;
                    if (reps != 0 && done >= reps) break;
                    if (step.RepeatDelayMs > 0) Wait(Jitter(step.RepeatDelayMs), ct);
                }
            }
        }
        finally { _completeDeferred.Remove(step); }
        ct.ThrowIfCancellationRequested();   // 循环因取消退出时不触发结束监听（与单趟被停止时一致）
        CompleteHook(step, ct);
    }

    /// <summary>
    /// 【重复直到条件满足】＝do-while：每趟照常"判运行条件 → 监听 → 本体"，趟末判定一次停止条件
    /// （结构同运行条件：多条与/或），满足即结束该动作；不满足按"每趟间隔"等待后再来一趟。
    /// 次数/时长上限 0=不限、同时生效先到者停——到上限仍未满足只记警告并继续后续动作（不算执行失败）。
    /// 跳转仍然优先：趟内产生跳转就立刻结束重复（与固定趟数一致，否则永远跳不出去）。
    /// </summary>
    private void RepeatUntilCycle(MacroStep step, CancellationToken ct, Action once)
    {
        int interval = Math.Max(0, step.RepeatDelayMs);
        int max = Math.Max(0, step.UntilMaxCount);
        int timeout = Math.Max(0, step.UntilTimeoutMs);
        Log?.Invoke("Info", $"重复执行，直到停止条件满足（{RetryDesc(interval, max, timeout)}）…");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // 日志节流与重复检查同款：间隔很小（尤其 0）时每趟都记会灌满 UI 线程。
        bool verboseLog = interval >= 200;
        var logClock = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;
        while (!ct.IsCancellationRequested)
        {
            Gate(ct);
            once();
            done++;
            if (_pendingJump != null) return;
            bool ok = EvaluateItems(step.UntilConditions, step.UntilLogic, out var text, ct);
            if (verboseLog || ok || done <= 5 || logClock.ElapsedMilliseconds >= 500)
            {
                Log?.Invoke("Info", $"　停止条件检查 第 {done} 趟：{text}");
                logClock.Restart();
            }
            if (ok) { Log?.Invoke("Info", $"停止条件已满足，结束重复（共执行 {done} 趟）。"); return; }
            if (max > 0 && done >= max) { Log?.Invoke("Warning", $"已执行 {done} 趟仍未满足停止条件（已达 {max} 趟上限），结束重复。"); return; }
            if (timeout > 0 && clock.ElapsedMilliseconds >= timeout) { Log?.Invoke("Warning", $"已执行 {done} 趟仍未满足停止条件（已达 {timeout} 毫秒时限），结束重复。"); return; }
            if (interval > 0) Wait(Jitter(interval), ct);
        }
    }

    // 叶子动作：写一条"执行中"日志行 → 执行(含自身循环) → 改为执行成功/失败/已停止；并高亮该行 + 跑监听。
    // 外层再套一层"整趟重复"（RepeatCount），每趟都会重新判条件、重新走监听。
    private bool RunLeaf(MacroStep step, string body, CancellationToken ct)
    {
        bool any = false;
        RepeatCycle(step, ct, () => any |= RunLeafOnce(step, body, ct));
        return any;
    }

    private bool RunLeafOnce(MacroStep step, string body, CancellationToken ct)
    {
        if (!GateHooks(step, out var conditionText, ct))
        {
            ActBegin?.Invoke(body);
            ActEnd?.Invoke("已跳过", "Warning");
            Log?.Invoke("Info", $"条件不满足，跳过动作：{conditionText}");
            CompleteHook(step, ct);   // 条件跳过也算一次"结束"（语义已定：正常/失败/跳过都进结束监听）
            return false;
        }

        RunHook(step.PreRunAction, "运行前", ct);
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
            RunHook(step.FailAction, "运行失败后", ct);
        }
        if (ok)
        {
            StepStateChanged?.Invoke(step, false);
            Progress?.Invoke(100, StatusLine());   // 当前动作完成 → 100%
            ActEnd?.Invoke("执行成功", "Success");
            RunHook(step.SuccessAction, "运行成功后", ct);
        }
        CompleteHook(step, ct);
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
                if (GateHooks(hook, out var reason, ct)) { Log?.Invoke("Info", $"    ↳ 监听（{kind}）：组合（{hook.Children.Count} 个动作）"); RunGroup(hook, ct); }
                else
                {
                    Log?.Invoke("Info", $"    ↳ 监听（{kind}）：组合条件不满足，跳过（{reason}）");
                    CompleteHook(hook, ct);   // 条件跳过也算一次"结束"
                }
            }
            else RunLeaf(hook, $"    ↳ 监听（{kind}）：{hook.Display}", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // 图片模板缓存：键=图片引用字符串（file:hash / 旧内联 base64）——内容寻址，引用变了键就变，天然无失效问题。
    // 运行条件与「点击图片/移动图片」共用；同一 Bitmap 实例复用还让 ScreenMatch 的模板预处理缓存（按实例）持续命中。
    // 一次运行结束在 finally 里统一释放。
    private readonly System.Collections.Generic.Dictionary<string, System.Drawing.Bitmap> _tplCache = new();
    private System.Drawing.Bitmap? TemplateBitmap(string img)
    {
        if (string.IsNullOrEmpty(img)) return null;
        if (_tplCache.TryGetValue(img, out var bmp)) return bmp;
        var bytes = ImageStore.Bytes(img);   // 引用(file:hash)读文件 / 旧内联 base64 都支持
        if (bytes == null) return null;
        try { bmp = ScreenMatch.FromPng(bytes); _tplCache[img] = bmp; return bmp; }
        catch { return null; }
    }
    private System.Drawing.Bitmap? TemplateFor(ConditionItem item) => TemplateBitmap(item.Image);
    private void DisposeTemplateCache()
    {
        foreach (var kv in _tplCache) { try { kv.Value.Dispose(); } catch { } }
        _tplCache.Clear();
    }

    // ---- 一轮判定内共享抓屏 ----
    // 多条图片条件常盯着同一块屏幕区域（与/或组合）：同一轮判定里对同一（区域）只抓一次屏，
    // 其余条件复用（几十毫秒内画面视为同帧）。轮与轮之间必须重抓——画面会变，这正是轮询的意义。
    private System.Collections.Generic.Dictionary<(int, int, int, int), System.Drawing.Bitmap?>? _roundShots;
    private System.Drawing.Bitmap? RoundCapture(int rx, int ry, int rw, int rh)
    {
        if (_roundShots == null)   // 不在判定轮里（点击图片单次定位）：直接抓
        {
            try { return ScreenCapture.Capture(rx, ry, rw, rh); } catch { return null; }
        }
        var key = (rx, ry, rw, rh);
        if (!_roundShots.TryGetValue(key, out var bmp))
        {
            try { bmp = ScreenCapture.Capture(rx, ry, rw, rh); } catch { bmp = null; }
            _roundShots[key] = bmp;
        }
        return bmp;
    }

    // 运行条件门 + 条件类监听：条件判断前 → 判定 → 判断成功后/判断失败后。
    // 条件类监听只在动作【确实设置了】运行条件时触发——没设条件时判定恒过，触发只会刷屏。
    private bool GateHooks(MacroStep step, out string conditionText, CancellationToken ct)
    {
        bool hasCond = RunCondition.Has(step);
        if (hasCond) RunHook(step.PreCondAction, "条件判断前", ct);
        bool ok = Evaluate(step, out conditionText, ct);
        // 重复检查：不满足时按间隔重判，直到满足 / 到次数上限 / 到时长上限（Wait 内已处理暂停/停止）。
        // 间隔 0 = 立刻重判（不等待）；次数与时长两个上限【同时生效】，先到者结束。
        if (hasCond && !ok && step.RunConditionRetry)
        {
            int interval = Math.Max(0, step.RunConditionRetryIntervalMs);
            int max = Math.Max(0, step.RunConditionRetryMax);
            int timeout = Math.Max(0, step.RunConditionRetryTimeoutMs);
            Log?.Invoke("Info", $"条件未满足（{conditionText}），{RetryDesc(interval, max, timeout)}…");
            int tries = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // 日志节流：间隔很小（尤其 0=连着判）时每次都记会把 UI 线程灌满——日志刷不过来，
            // 连 F9/F11 这些要在 UI 线程上派发的热键都会被拖慢。慢速重试仍然每次都记（要看每次的相似度）。
            bool verboseLog = interval >= 200;
            var logClock = System.Diagnostics.Stopwatch.StartNew();
            string stopReason = "";
            while (!ok)
            {
                if (max > 0 && tries >= max) { stopReason = $"已达 {max} 次上限"; break; }
                if (timeout > 0 && clock.ElapsedMilliseconds >= timeout) { stopReason = $"已达 {timeout} 毫秒时限"; break; }
                if (interval > 0) Wait(interval, ct);
                else { Gate(ct); ct.ThrowIfCancellationRequested(); }   // 0 间隔也必须给暂停/停止留出响应点
                tries++;
                ok = Evaluate(step, out conditionText, ct);
                // 每次判定都留痕：图片类条件会带上本次的实际相似度，
                // 便于回答"为什么这次没匹配上"（是画面真的变了，还是只差一点点）。
                if (verboseLog || ok || tries <= 5 || logClock.ElapsedMilliseconds >= 500)
                {
                    Log?.Invoke("Info", $"　重复检查 第 {tries} 次：{conditionText}");
                    logClock.Restart();
                }
            }
            Log?.Invoke(ok ? "Info" : "Warning", ok
                ? $"条件已满足（重复检查 {tries} 次）。"
                : $"条件重复检查 {tries} 次仍未满足（{stopReason}，{conditionText}）。");
        }
        if (hasCond) RunHook(ok ? step.CondSuccessAction : step.CondFailAction, ok ? "判断成功后" : "判断失败后", ct);
        return ok;
    }

    /// <summary>判定一条运行条件是否放行。方案级与动作级共用，conditionText 仅在【跳过】时用于打日志。</summary>
    /// <summary>
    /// 判定整组运行条件：多条按 And（全部满足）/ Or（任一满足）组合。
    /// conditionText 汇总各条的实际观测结果，仅在【跳过】时打日志，便于分辨是"没出现"还是"阈值太严"。
    /// </summary>
    private bool Evaluate(IRunCondition step, out string conditionText, CancellationToken ct = default)
    {
        conditionText = "";
        RunCondition.Normalize(step);            // 幂等：历史存档的单条字段在这里并入列表
        if (!RunCondition.Has(step)) return true;
        return EvaluateItems(step.RunConditions, step.RunConditionLogic, out conditionText, ct);
    }

    // 判定一组条件：运行条件与「直到条件满足」的停止条件共用（同一套与/或/表达式、短路与轮内共享抓屏）。
    private bool EvaluateItems(System.Collections.Generic.List<ConditionItem> items, string logic,
                               out string conditionText, CancellationToken ct)
    {
        // 自定义表达式（"Expr:(@1 && @2) || @3"）：编辑器保存前已校验；这里再兜一次底——
        // 表达式失效（如旧档手改）就退回"全部满足"，别让方案卡死在一条判不动的条件上。
        if (CondExpr.IsExpr(logic) && CondExpr.Validate(CondExpr.Get(logic), items.Count) == null)
            return EvaluateExpr(items, CondExpr.Get(logic), out conditionText, ct);

        bool or = string.Equals(logic, "Or", StringComparison.OrdinalIgnoreCase);
        bool acc = !or;                          // And 从 true 起累积；Or 从 false 起累积
        var parts = new System.Collections.Generic.List<string>();
        _roundShots = new();                     // 本轮判定内共享抓屏（多条同区域的图片条件只抓一次）
        try
        {
            foreach (var item in items)
            {
                if (!item.IsValid) continue;         // 半成品条目不参与判定，避免误判为不满足
                bool one = EvaluateOne(item, out var text, ct);
                parts.Add(text);
                acc = or ? (acc || one) : (acc && one);
                // 短路：And 遇假、Or 遇真即可停——图片条件要抓屏搜索，能省一次是一次。
                if (or ? acc : !acc) break;
            }
        }
        finally
        {
            foreach (var kv in _roundShots) kv.Value?.Dispose();
            _roundShots = null;
        }
        conditionText = string.Join(or ? " 或 " : " 且 ", parts);
        return acc;
    }

    // 按自定义表达式判定：@N 引用第 N 条（1 起）。逐条记忆化——同一条件被引用多次只判一次；
    // && / || 由表达式树正常短路，被跳过的条件完全不抓屏不搜索。
    private bool EvaluateExpr(System.Collections.Generic.List<ConditionItem> items, string expr,
                              out string conditionText, CancellationToken ct)
    {
        var parts = new System.Collections.Generic.List<string>();
        var memo = new bool?[items.Count];
        bool result;
        _roundShots = new();                     // 本轮判定内共享抓屏（与 And/Or 路径同一机制）
        try
        {
            bool ItemVal(int i)
            {
                if (memo[i] is bool b) return b;
                var it = items[i];
                string t = "条件未配置完整";
                bool v = it.IsValid && EvaluateOne(it, out t, ct);
                parts.Add($"@{i + 1} {t}");
                memo[i] = v;
                return v;
            }
            result = CondExpr.Eval(expr, items.Count, ItemVal);
        }
        finally
        {
            foreach (var kv in _roundShots) kv.Value?.Dispose();
            _roundShots = null;
        }
        conditionText = string.Join("；", parts) + $"　⇒ {expr} {(result ? "成立" : "不成立")}";
        return result;
    }

    /// <summary>判定单条条件。</summary>
    private bool EvaluateOne(ConditionItem c, out string text, CancellationToken ct = default)
    {
        if (c.Type == "ImageMatch")
        {
            // 与「点击图片」同一套搜索：限制区域内结构加权滑窗（v0.2.20 起不再要求图片出现在截取时的
            // 原位置——区域内任意处出现即算满足）。旧数据的区域＝当年截图的原位置，恰好退化为只检查该处。
            var tpl = TemplateFor(c);
            if (tpl == null) { text = "目标图片未出现（无模板）"; return c.Invert; }
            var mon = ScreenInfo.ByDevice(c.Monitor);
            int rx, ry, rw, rh;
            if (c.RectW > 0 && c.RectH > 0)
            {
                rx = mon.Left + c.RectX; ry = mon.Top + c.RectY;
                int right = Math.Min(rx + c.RectW, mon.Right), bottom = Math.Min(ry + c.RectH, mon.Bottom);
                rx = Math.Max(rx, mon.Left); ry = Math.Max(ry, mon.Top);
                rw = right - rx; rh = bottom - ry;   // 与当前屏求交集（跨主机导入/换分辨率防越界）
                if (rw <= 0 || rh <= 0) { text = "限制区域不在当前屏幕范围内"; return c.Invert; }
            }
            else { rx = mon.Left; ry = mon.Top; rw = mon.Width; rh = mon.Height; }
            double thr = Math.Clamp(c.Threshold, 0.5, 1.0);
            // 计时含抓屏+搜索（同轮共享抓屏时第二条起自然不含抓屏——记录的就是真实开销）。
            // 用户凭它判断"该不该缩小限制区域/换更有结构的模板"，也能直接看出上面那类慢格子。
            var swMatch = System.Diagnostics.Stopwatch.StartNew();
            var shot = RoundCapture(rx, ry, rw, rh);
            if (shot == null) { text = "抓屏失败，视为未出现"; return c.Invert; }
            var hits = ScreenMatch.FindIn(shot, tpl, thr, out double best, ct, rx, ry);
            swMatch.Stop();
            string cost = $"，耗时 {swMatch.ElapsedMilliseconds}ms";
            bool found = hits.Count > 0;
            text = found
                ? $"目标图片已出现（命中 {hits.Count} 个，最高相似度 {best:0.00} / 阈值 {thr:0.00}{cost}）"
                : best > 0
                    ? $"目标图片未出现（区域内最高相似度 {best:0.00} / 阈值 {thr:0.00}{cost}）"
                    : $"目标图片未出现（区域内最高相似度低于 {Math.Max(0, thr - 0.15):0.00}，阈值 {thr:0.00}{cost}）";
            return c.Invert ? !found : found;
        }
        if (c.Type != "TimeRange") { text = ""; return true; }

        int now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        bool match = IsInTimeRange(now, c.StartMinute, c.EndMinute);
        text = FormatCondition(c, match);
        return c.Invert ? !match : match;
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

    private static string FormatCondition(ConditionItem c, bool inRange)
    {
        string range = (c.StartMinute, c.EndMinute) switch
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
            case "MouseDrag" when step.DragMode == "Window":
                DragWindow(step, ct);
                break;
            case "MouseDrag":
                MoveToStepTarget(step, ct);          // 起点（MoveMonitor/MoveNormX/Y）
                ct.ThrowIfCancellationRequested();
                _backend.MouseDown(step.Button);
                try
                {
                    Wait(60, ct);
                    var (ex, ey) = ScreenInfo.Resolve(step.DragEndMonitor, step.DragEndNormX, step.DragEndNormY);
                    (ex, ey) = ApplyOffset(ex, ey, step.ClickOffset);   // 终点同样带偏移（起点在 MoveToStepTarget 里已偏）
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
            // 点击图片 = 在限制区域内搜模板 → 取第 N 个命中 → 移到其中心 → 点击。找不到/不足 N 个则本步失败（走失败监听、不中断方案）。
            case "MouseClickImage": ClickImage(step, ct); break;
            case "MouseMoveImage": MoveToImage(step, ct); break;
            case "TextInput": TypeText(step, ct); break;
            case "MouseWheel": _backend.MouseWheel(step.Wheel); break;
            // 跳转动作：上报给 RunTop，在当前顶层步骤结束后跳到目标序号（在组合内/监听里执行也生效）。
            case "Jump":
            {
                // 生效与否当场裁决、当场留痕——目标恰好是顺序下一步时"失效"与"生效"流程一模一样，
                // 不记日志用户无法察觉上限有没有起作用（corp-win 实排查过一次）。
                // 也因此 _pendingJump 非空 ⇔ 必然跳出，「运行结束后」的"跳出不触发"才能精确成立。
                string jname = step.JumpTargetAlias.Length > 0 ? $"跳转到「{step.JumpTargetAlias}」" : "跳转";
                if (JumpIndex(step, _topSteps) < 0)
                {
                    Log?.Invoke("Warning", $"{jname}的目标不存在（可能已被删除或改名），跳转不生效，按顺序继续。");
                    break;
                }
                _jumpUsed.TryGetValue(step, out var used);
                if (step.JumpTimes > 0 && used >= step.JumpTimes)
                {
                    Log?.Invoke("Warning", $"{jname}已达 {step.JumpTimes} 次上限，本轮内不再生效，按顺序继续。");
                    break;
                }
                _jumpUsed[step] = used + 1;
                _pendingJump = step;
                break;
            }
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
        (vx, vy) = ApplyOffset(vx, vy, step.ClickOffset);               // 落点偏移（每次调用各自随机）
        if (step.Humanize) MoveHumanized(vx, vy, ct);   // 动作级：每个移动动作各自决定
        else _backend.MouseMove(vx, vy);
    }

    // 文本输入：自动分流 —— 软件后端走 Unicode 注入（能出中文），CH9329 是真实 HID 键盘、
    // 物理上发不了汉字，只能走剪贴板 + Ctrl+V。用户也可在动作里强制指定其中一条路径。
    private void TypeText(MacroStep step, CancellationToken ct)
    {
        var text = step.Text ?? "";
        if (text.Length == 0) throw new InvalidOperationException("文本输入：内容为空。");
        if (step.TextRandom)   // 正则随机：每次执行都重新生成，循环 N 次即得 N 个不同结果
        {
            try { text = RandomText.Generate(text); }
            catch (Exception ex) { throw new InvalidOperationException($"文本输入：随机模式不合法（{ex.Message}）。"); }
            if (text.Length == 0) throw new InvalidOperationException("文本输入：按该模式生成的内容为空。");
            Log?.Invoke("Info", $"文本输入：随机生成「{text}」。");
        }

        bool canInject = _backend.SupportsUnicodeText;
        string mode = step.TextMode switch
        {
            "Unicode" => canInject ? "Unicode" : "Clipboard",   // 硬件后端注入不了，降级并在下面记日志
            "Clipboard" => "Clipboard",
            _ => canInject ? "Unicode" : "Clipboard",           // 自动
        };
        if (step.TextMode == "Unicode" && !canInject)
            Log?.Invoke("Warning", "当前输出方式为 CH9329 硬件键盘，无法直接注入字符，已改用剪贴板粘贴。");

        if (mode == "Unicode")
        {
            _backend.TypeText(text, Jitter(step.TextCharDelayMs), ct);
            return;
        }

        // ---- 剪贴板路径 ----
        // 剪贴板是 STA 独占资源：必须切到 UI 线程访问，且常被别的程序短暂占用，要重试。
        string? backup = ClipboardGetText();
        if (!ClipboardSetText(text)) throw new InvalidOperationException("文本输入：写入剪贴板失败（可能被其它程序占用）。");
        try
        {
            Wait(60, ct);                                   // 给目标程序留出感知剪贴板变化的时间
            _backend.KeyTap("V", 0x01, Math.Max(30, Jitter(step.HoldMs)), ct);   // 0x01 = 左 Ctrl
            Wait(120, ct);                                  // 粘贴是异步的，还原太早目标程序会读到旧内容
        }
        finally
        {
            if (backup != null) ClipboardSetText(backup);    // 尽量还原用户原有剪贴板内容
        }
    }

    // 剪贴板读写：切 UI 线程（STA）+ 重试；失败不抛（还原失败不该影响主流程）。
    private static string? ClipboardGetText()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return null;
        string? r = null;
        try { app.Dispatcher.Invoke(() => { try { if (System.Windows.Clipboard.ContainsText()) r = System.Windows.Clipboard.GetText(); } catch { } }); }
        catch { }
        return r;
    }

    private static bool ClipboardSetText(string text)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return false;
        bool ok = false;
        try
        {
            app.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < 5 && !ok; i++)
                {
                    try { System.Windows.Clipboard.SetText(text); ok = true; }
                    catch { Thread.Sleep(40); }   // CLIPBRD_E_CANT_OPEN：别的程序正占着，稍后重试
                }
            });
        }
        catch { }
        return ok;
    }

    /// <summary>
    /// 拖动窗口：先激活目标窗口 → 按住它的标题栏 → 拖到"窗口左上角落在终点坐标"的位置 → 松开。
    /// 走真实鼠标拖拽（而不是 SetWindowPos 直接搬），这样对方程序收到的是正常的拖窗手势，
    /// 贴边吸附、多显示器 DPI 切换等系统行为都能照常发生。
    /// </summary>
    private void DragWindow(MacroStep step, CancellationToken ct)
    {
        var win = WindowActivator.Find(step.TargetPid, step.TargetProcess, step.TargetTitle)
                  ?? throw new InvalidOperationException($"拖动窗口：没找到目标窗口（{step.TargetProcess}.exe PID {step.TargetPid}）。");
        WindowActivator.ActivateHwnd(win.Hwnd);
        Wait(120, ct);   // 等激活动画/重绘落定，否则按下时窗口可能还没到前台

        if (!WindowActivator.TryGetFrameRect(win.Hwnd, out int wx, out int wy, out int ww, out int wh))
            throw new InvalidOperationException("拖动窗口：读不到窗口位置。");
        // 抓点取标题栏中部偏左：正中央常被标签页/工具栏占着，太靠右又容易压到关闭按钮
        int grabX = wx + Math.Min(Math.Max(40, ww / 4), Math.Max(40, ww - 80));
        int grabY = wy + Math.Min(16, Math.Max(6, wh / 20));

        var (ex, ey) = ScreenInfo.Resolve(step.DragEndMonitor, step.DragEndNormX, step.DragEndNormY);
        (ex, ey) = ApplyOffset(ex, ey, step.ClickOffset);
        // 终点坐标对应窗口的哪个点由 DragAnchor 决定（3×3）：先反推出窗口左上角该落在哪儿
        int ax = Math.Clamp(step.DragAnchor, 0, 8) % 3, ay = Math.Clamp(step.DragAnchor, 0, 8) / 3;
        int targetLeft = ex - (int)Math.Round(ww * ax / 2.0);
        int targetTop = ey - (int)Math.Round(wh * ay / 2.0);
        int tx = grabX + (targetLeft - wx), ty = grabY + (targetTop - wy);   // 抓点的位移 = 窗口左上角的位移

        _backend.MouseMove(grabX, grabY);
        ct.ThrowIfCancellationRequested();
        _backend.MouseDown(step.Button);
        try
        {
            Wait(60, ct);
            if (step.Humanize) MoveHumanized(tx, ty, ct); else _backend.MouseMove(tx, ty);
            Wait(60, ct);
        }
        finally { _backend.MouseUp(step.Button); }
        Log?.Invoke("Info", $"拖动窗口：左上角 ({wx}, {wy}) → ({targetLeft}, {targetTop})（{MacroStep.AnchorCn(step.DragAnchor)}对齐到 {ex}, {ey}）。");
    }

    // 点击图片：定位 → 移到中心 → 点击。移动图片：只定位 + 移动，不点击（两者共用 LocateImage）。
    private void ClickImage(MacroStep step, CancellationToken ct)
    {
        var (cx, cy) = LocateImage(step, "点击图片", ct);
        ct.ThrowIfCancellationRequested();
        if (step.Humanize) MoveHumanized(cx, cy, ct); else _backend.MouseMove(cx, cy);
        ct.ThrowIfCancellationRequested();
        _backend.MouseClick(step.Button, Jitter(step.HoldMs), ct);
    }

    private void MoveToImage(MacroStep step, CancellationToken ct)
    {
        var (cx, cy) = LocateImage(step, "移动图片", ct);
        ct.ThrowIfCancellationRequested();
        if (step.Humanize) MoveHumanized(cx, cy, ct); else _backend.MouseMove(cx, cy);
    }

    // 区域内搜模板 → 返回第 N 个命中的中心点。label 只用于日志/报错措辞。
    private (int cx, int cy) LocateImage(MacroStep step, string label, CancellationToken ct)
    {
        var tpl = TemplateBitmap(step.ClickImage)
                  ?? throw new InvalidOperationException($"{label}：未设置模板图片。");

        // 限制区域：屏内相对像素 → 按该屏当前位置还原绝对区域；未设区域则搜整块绑定屏（默认主屏；
        // 绑定屏在本机不存在时 ByDevice 已回退主屏——常见于方案导入自别的主机）。
        var mon = ScreenInfo.ByDevice(step.ClickImageMonitor);
        int rx, ry, rw, rh;
        if (step.ClickImageRectW > 0 && step.ClickImageRectH > 0)
        {
            rx = mon.Left + step.ClickImageRectX; ry = mon.Top + step.ClickImageRectY; rw = step.ClickImageRectW; rh = step.ClickImageRectH;
            // 与当前屏求交集：区域可能来自分辨率不同的主机（导入）或换过分辨率，越界部分抓屏是未定义内容。
            int right = Math.Min(rx + rw, mon.Right), bottom = Math.Min(ry + rh, mon.Bottom);
            rx = Math.Max(rx, mon.Left); ry = Math.Max(ry, mon.Top);
            rw = right - rx; rh = bottom - ry;
            if (rw <= 0 || rh <= 0)
                throw new InvalidOperationException($"{label}：限制区域不在当前屏幕范围内（方案可能导入自分辨率不同的主机），请重新设置限制区域。");
        }
        else { rx = mon.Left; ry = mon.Top; rw = mon.Width; rh = mon.Height; }

        double thr = Math.Clamp(step.ClickImageThreshold, 0.5, 1.0);
        var swMatch = System.Diagnostics.Stopwatch.StartNew();
        var shot = RoundCapture(rx, ry, rw, rh) ?? throw new InvalidOperationException($"{label}：抓屏失败。");
        System.Collections.Generic.List<(int cx, int cy, double score)> hits;
        double best;
        try { hits = ScreenMatch.FindIn(shot, tpl, thr, out best, ct, rx, ry); }
        finally { if (_roundShots == null) shot.Dispose(); }   // 共享轮里的由 Evaluate 统一释放
        swMatch.Stop();
        string cost = $"，耗时 {swMatch.ElapsedMilliseconds}ms";

        if (hits.Count == 0)
            throw new InvalidOperationException(best > 0
                ? $"{label}：区域内未找到匹配（最高相似度 {best:0.00} / 阈值 {thr:0.00}{cost}）。"
                : $"{label}：区域内未找到匹配（最高相似度低于 {Math.Max(0, thr - 0.15):0.00}，阈值 {thr:0.00}{cost}）。");
        int idx = Math.Max(1, step.ClickImageIndex);
        if (idx > hits.Count)
            throw new InvalidOperationException($"{label}：只找到 {hits.Count} 个匹配，不足第 {idx} 个。");

        var (cx, cy, score) = hits[idx - 1];
        // 多命中时把各处坐标列出来（最多 5 个）——"点错了第几个"一眼能对出来。
        string detail = hits.Count > 1
            ? "（" + string.Join("、", hits.GetRange(0, Math.Min(5, hits.Count)).ConvertAll(h => $"({h.cx},{h.cy})")) + (hits.Count > 5 ? "…" : "") + "）"
            : "";
        Log?.Invoke("Info", $"{label}：命中 {hits.Count} 个{detail}，取第 {idx} 个 ({cx}, {cy})，相似度 {score:0.00}{cost}。");
        return (cx, cy);
    }

    // 落点偏移：在目标点周围半径 radius 像素的圆盘内均匀随机取一点（clamp 回目标所在屏），0=精确命中。
    // 与拟人化轨迹配套：轨迹拟人了、落点却每次分毫不差反而露馅。
    private (int x, int y) ApplyOffset(int x, int y, int radius)
    {
        if (radius <= 0) return (x, y);
        double ang = _rng.NextDouble() * Math.PI * 2;
        double r = radius * Math.Sqrt(_rng.NextDouble());               // sqrt：使落点在圆盘内面积均匀（否则堆在圆心）
        int nx = (int)Math.Round(x + Math.Cos(ang) * r);
        int ny = (int)Math.Round(y + Math.Sin(ang) * r);
        var m = ScreenInfo.ByDevice(ScreenInfo.FromPoint(x, y).device); // 落点所在屏，clamp 防偏出屏
        return (Math.Clamp(nx, m.Left, m.Right - 1), Math.Clamp(ny, m.Top, m.Bottom - 1));
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
