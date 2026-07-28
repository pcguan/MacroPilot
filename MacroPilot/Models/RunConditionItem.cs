using System.Text.Json.Serialization;

namespace MacroPilot.Models;

/// <summary>
/// 单条运行条件的数据（时间段 / 图片出现）。与 <see cref="IRunCondition"/> 上那组
/// RunConditionXxx 旧字段一一对应——旧字段现在只用于读取历史 plans.json，读进来后由
/// <see cref="RunCondition.Normalize"/> 归一成本类的一条，此后内存与新存档里只有列表这一种表示。
/// </summary>
public sealed class ConditionItem : IConditionData
{
    /// <summary>"TimeRange" 或 "ImageMatch"。</summary>
    public string Type { get; set; } = "";
    /// <summary>取反：条件不成立时才算满足。</summary>
    public bool Invert { get; set; }

    // TimeRange：当天分钟数 0-1439，null 表示该侧不限。
    public int? StartMinute { get; set; }
    public int? EndMinute { get; set; }

    // ImageMatch：模板引用 file:<sha256>（或旧的内联 base64）+ 锚定屏 + 屏内相对矩形 + 相似度阈值。
    public string Image { get; set; } = "";
    public string Monitor { get; set; } = "";
    public int RectX { get; set; }
    public int RectY { get; set; }
    public int RectW { get; set; }
    public int RectH { get; set; }
    public double Threshold { get; set; } = 0.9;

    // 截图那一刻的原始区域（用户之后手动改了限制区域还能一键还原回来）。W/H=0 表示没有原始记录。
    public string OrigMonitor { get; set; } = "";
    public int OrigRectX { get; set; }
    public int OrigRectY { get; set; }
    public int OrigRectW { get; set; }
    public int OrigRectH { get; set; }

    /// <summary>本条是否配置完整（判定与 UI 共用一套口径）。</summary>
    [JsonIgnore]
    public bool IsValid =>
        (Type == "TimeRange" && (StartMinute.HasValue || EndMinute.HasValue))
        || (Type == "ImageMatch" && !string.IsNullOrEmpty(Image) && RectW > 0 && RectH > 0);

    public ConditionItem Clone() => new()
    {
        Type = Type, Invert = Invert, StartMinute = StartMinute, EndMinute = EndMinute,
        Image = Image, Monitor = Monitor,
        RectX = RectX, RectY = RectY, RectW = RectW, RectH = RectH, Threshold = Threshold,
        OrigMonitor = OrigMonitor, OrigRectX = OrigRectX, OrigRectY = OrigRectY, OrigRectW = OrigRectW, OrigRectH = OrigRectH,
    };

    /// <summary>一句话描述，供条件列表显示。</summary>
    public override string ToString()
    {
        string body;
        if (Type == "ImageMatch")
        {
            string mon = Monitor;
            int i = mon.LastIndexOf('\\');
            if (i >= 0) mon = mon[(i + 1)..];
            body = RectW > 0 && RectH > 0
                ? $"图片出现（{(string.IsNullOrEmpty(mon) ? "" : mon + " ")}区域 {RectW}×{RectH}，相似度 {Threshold * 100:0}%）"
                : "图片出现（未设置）";
        }
        else if (Type == "TimeRange")
        {
            static string F(int m) { m = ((m % 1440) + 1440) % 1440; return $"{m / 60:00}:{m % 60:00}"; }
            body = (StartMinute, EndMinute) switch
            {
                (int s, int e) => $"时间段 {F(s)}-{F(e)}",
                (int s, null) => $"时间段 {F(s)} 之后",
                (null, int e) => $"时间段 {F(e)} 之前",
                _ => "时间段（未设置）",
            };
        }
        else body = "（未设置）";
        return Invert ? body + " · 取反" : body;
    }
}

/// <summary>
/// 单条条件的读写契约。让「点击图片」的编辑面板（ClickImagePanel）既能编辑动作自身的
/// 点击模板，也能编辑某一条图片条件——两边字段语义一致，只是承载对象不同。
/// </summary>
public interface IConditionData
{
    string Type { get; set; }
    bool Invert { get; set; }
    int? StartMinute { get; set; }
    int? EndMinute { get; set; }
    string Image { get; set; }
    string Monitor { get; set; }
    int RectX { get; set; }
    int RectY { get; set; }
    int RectW { get; set; }
    int RectH { get; set; }
    double Threshold { get; set; }
    string OrigMonitor { get; set; }
    int OrigRectX { get; set; }
    int OrigRectY { get; set; }
    int OrigRectW { get; set; }
    int OrigRectH { get; set; }
}
