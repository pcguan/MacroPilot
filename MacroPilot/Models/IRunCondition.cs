namespace MacroPilot.Models;

/// <summary>
/// 运行条件的公共契约：方案级（<see cref="MacroPlan"/>）与动作级（<see cref="MacroStep"/>）都实现它，
/// 于是编辑界面（BuildRunConditionPanel / RunConditionEditor）与执行判定（MacroRunner.Evaluate）
/// 各自只有一份代码。**以后新增条件类型只需改这里 + 那两处，两级不会再走偏。**
/// 字段名与两个模型原有的 JSON 属性一致，因此不影响已存档的 plans.json。
/// </summary>
public interface IRunCondition
{
    /// <summary>条件列表（v0.4 起的正式表示）。多条之间按 <see cref="RunConditionLogic"/> 组合。</summary>
    System.Collections.Generic.List<ConditionItem> RunConditions { get; set; }
    /// <summary>"And"=全部满足（默认）；"Or"=任一满足。</summary>
    string RunConditionLogic { get; set; }

    // ↓↓ 以下单条字段仅用于读取历史 plans.json：Normalize 会把它们并入 RunConditions 后清空。
    /// <summary>""=无条件；"TimeRange"=时间段；"ImageMatch"=图片出现。</summary>
    string RunConditionType { get; set; }
    /// <summary>true 表示条件不满足时才执行。</summary>
    bool RunConditionInvert { get; set; }

    // TimeRange：一天内的分钟数（0-1439），null 表示该侧不限。
    int? RunConditionStartMinute { get; set; }
    int? RunConditionEndMinute { get; set; }

    // ImageMatch：模板引用 file:<sha256>（或旧的内联 base64）+ 屏内相对矩形 + 相似度阈值。
    string RunConditionImage { get; set; }
    string RunConditionMonitor { get; set; }
    int RunConditionRectX { get; set; }
    int RunConditionRectY { get; set; }
    int RunConditionRectW { get; set; }
    int RunConditionRectH { get; set; }
    double RunConditionThreshold { get; set; }

    // 重复检查：条件不满足时按间隔重新判定，直到满足或达到次数上限。
    // 【方案级】历来就是"空转等到条件满足"，故忽略本开关始终等待（旧行为不变），但间隔/次数上限对它生效。
    // 【动作级/组合级】原本是"不满足即跳过"，勾选后才改为重复检查。
    bool RunConditionRetry { get; set; }
    int RunConditionRetryIntervalMs { get; set; }   // 默认 1000
    int RunConditionRetryMax { get; set; }          // 0 = 不限次数
}

public static class RunCondition
{
    /// <summary>
    /// 把历史存档里的单条字段并入 <see cref="IRunCondition.RunConditions"/>，并清空旧字段。
    /// 幂等：已经是列表表示的直接返回。执行判定与编辑入口都会先调它，因此即便某条加载路径漏调也不会出错。
    /// </summary>
    public static void Normalize(IRunCondition c)
    {
        c.RunConditions ??= new System.Collections.Generic.List<ConditionItem>();
        if (c.RunConditions.Count > 0 || string.IsNullOrEmpty(c.RunConditionType)) { ClearLegacy(c); return; }
        var it = new ConditionItem
        {
            Type = c.RunConditionType,
            Invert = c.RunConditionInvert,
            StartMinute = c.RunConditionStartMinute,
            EndMinute = c.RunConditionEndMinute,
            Image = c.RunConditionImage,
            Monitor = c.RunConditionMonitor,
            RectX = c.RunConditionRectX, RectY = c.RunConditionRectY,
            RectW = c.RunConditionRectW, RectH = c.RunConditionRectH,
            Threshold = c.RunConditionThreshold,
        };
        if (it.IsValid) c.RunConditions.Add(it);
        ClearLegacy(c);
    }

    private static void ClearLegacy(IRunCondition c)
    {
        c.RunConditionType = "";
        c.RunConditionInvert = false;
        c.RunConditionStartMinute = null;
        c.RunConditionEndMinute = null;
        c.RunConditionImage = "";
        c.RunConditionMonitor = "";
        c.RunConditionRectX = c.RunConditionRectY = c.RunConditionRectW = c.RunConditionRectH = 0;
        c.RunConditionThreshold = 0.9;
    }

    /// <summary>是否配置了有效的运行条件（三级共用同一判定，避免一边认为有、一边认为无）。</summary>
    public static bool Has(IRunCondition c)
    {
        if (c.RunConditions != null)
            foreach (var it in c.RunConditions) if (it.IsValid) return true;
        // 尚未 Normalize 的历史数据也要认（保险：任何入口漏调 Normalize 都不至于把条件当成没有）
        return (c.RunConditionType == "TimeRange" && (c.RunConditionStartMinute.HasValue || c.RunConditionEndMinute.HasValue))
            || (c.RunConditionType == "ImageMatch" && !string.IsNullOrEmpty(c.RunConditionImage)
                && c.RunConditionRectW > 0 && c.RunConditionRectH > 0);
    }

    /// <summary>把 src 的运行条件整体拷到 dst（跨级别通用）。</summary>
    public static void Copy(IRunCondition src, IRunCondition dst)
    {
        dst.RunConditions = new System.Collections.Generic.List<ConditionItem>();
        if (src.RunConditions != null)
            foreach (var it in src.RunConditions) dst.RunConditions.Add(it.Clone());   // 深拷贝，别让两份共享同一条
        dst.RunConditionLogic = src.RunConditionLogic;
        dst.RunConditionType = src.RunConditionType;
        dst.RunConditionInvert = src.RunConditionInvert;
        dst.RunConditionStartMinute = src.RunConditionStartMinute;
        dst.RunConditionEndMinute = src.RunConditionEndMinute;
        dst.RunConditionImage = src.RunConditionImage;
        dst.RunConditionMonitor = src.RunConditionMonitor;
        dst.RunConditionRectX = src.RunConditionRectX;
        dst.RunConditionRectY = src.RunConditionRectY;
        dst.RunConditionRectW = src.RunConditionRectW;
        dst.RunConditionRectH = src.RunConditionRectH;
        dst.RunConditionThreshold = src.RunConditionThreshold;
        dst.RunConditionRetry = src.RunConditionRetry;
        dst.RunConditionRetryIntervalMs = src.RunConditionRetryIntervalMs;
        dst.RunConditionRetryMax = src.RunConditionRetryMax;
    }

    /// <summary>
    /// 运行条件的内容指纹：用于"有没有改过"的比较。整体序列化，天然覆盖以后新增的字段，
    /// 不必再维护一份手写的逐字段比较（那种写法漏一个字段就会导致改动不落盘）。
    /// </summary>
    public static string Snapshot(IRunCondition c)
    {
        Normalize(c);
        var sb = new System.Text.StringBuilder();
        sb.Append(c.RunConditionLogic).Append('|')
          .Append(c.RunConditionRetry).Append('|')
          .Append(c.RunConditionRetryIntervalMs).Append('|')
          .Append(c.RunConditionRetryMax).Append('|');
        foreach (var it in c.RunConditions)
            sb.Append(it.Type).Append(',').Append(it.Invert).Append(',')
              .Append(it.StartMinute).Append(',').Append(it.EndMinute).Append(',')
              .Append(it.Image).Append(',').Append(it.Monitor).Append(',')
              .Append(it.RectX).Append(',').Append(it.RectY).Append(',')
              .Append(it.RectW).Append(',').Append(it.RectH).Append(',')
              .Append(it.Threshold.ToString("0.####")).Append(',')
              .Append(it.OrigMonitor).Append(',').Append(it.OrigRectX).Append(',').Append(it.OrigRectY).Append(',')
              .Append(it.OrigRectW).Append(',').Append(it.OrigRectH).Append(';');
        return sb.ToString();
    }

    /// <summary>清空运行条件。</summary>
    public static void Clear(IRunCondition c)
    {
        c.RunConditions = new System.Collections.Generic.List<ConditionItem>();
        c.RunConditionLogic = "And";
        c.RunConditionType = "";
        c.RunConditionInvert = false;
        c.RunConditionStartMinute = null;
        c.RunConditionEndMinute = null;
        c.RunConditionImage = "";
        c.RunConditionMonitor = "";
        c.RunConditionRectX = c.RunConditionRectY = c.RunConditionRectW = c.RunConditionRectH = 0;
        c.RunConditionThreshold = 0.9;
        c.RunConditionRetry = false;
        c.RunConditionRetryIntervalMs = 1000;
        c.RunConditionRetryMax = 0;
    }
}
