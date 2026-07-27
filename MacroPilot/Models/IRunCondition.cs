namespace MacroPilot.Models;

/// <summary>
/// 运行条件的公共契约：方案级（<see cref="MacroPlan"/>）与动作级（<see cref="MacroStep"/>）都实现它，
/// 于是编辑界面（BuildRunConditionPanel / RunConditionEditor）与执行判定（MacroRunner.Evaluate）
/// 各自只有一份代码。**以后新增条件类型只需改这里 + 那两处，两级不会再走偏。**
/// 字段名与两个模型原有的 JSON 属性一致，因此不影响已存档的 plans.json。
/// </summary>
public interface IRunCondition
{
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
    /// <summary>是否配置了有效的运行条件（两级共用同一判定，避免一边认为有、一边认为无）。</summary>
    public static bool Has(IRunCondition c) =>
        (c.RunConditionType == "TimeRange" && (c.RunConditionStartMinute.HasValue || c.RunConditionEndMinute.HasValue))
        || (c.RunConditionType == "ImageMatch" && !string.IsNullOrEmpty(c.RunConditionImage)
            && c.RunConditionRectW > 0 && c.RunConditionRectH > 0);

    /// <summary>把 src 的运行条件整体拷到 dst（跨级别通用）。</summary>
    public static void Copy(IRunCondition src, IRunCondition dst)
    {
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

    /// <summary>清空运行条件。</summary>
    public static void Clear(IRunCondition c)
    {
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
