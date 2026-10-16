using System;

namespace MacroPilot.Services;

/// <summary>
/// 运行条件的自定义组合表达式：用 "@序号" 引用条件列表里的第 N 条（1 起），支持 &amp;&amp;（与）、
/// ||（或）、!（非）与括号，如 (@1 &amp;&amp; @2) || @3。
/// 存储不加新字段：塞在原 RunConditionLogic / UntilLogic 字符串里，取值三态 "And" / "Or" / "Expr:表达式"，
/// Copy / Snapshot / 序列化天然覆盖；老版本读到未知值按 And 处理，属可接受的降级。
/// 求值 &amp;&amp; / || 正常短路（图片条件很贵，能不判就不判）；同一条件被引用多次由调用方记忆化保证只判一次。
/// </summary>
public static class CondExpr
{
    public const string Prefix = "Expr:";
    internal const int MaxExpressionLength = 16384;
    internal const int MaxNodes = 1024;
    internal const int MaxDepth = 128;
    public static bool IsExpr(string? logic) => logic != null && logic.StartsWith(Prefix, StringComparison.Ordinal);
    public static string Get(string? logic) => IsExpr(logic) ? logic![Prefix.Length..] : "";

    /// <summary>全角容错：中文输入法打出的 （）！＆｜＠ 一律按半角认。</summary>
    public static string Normalize(string? expr) =>
        (expr ?? "").Replace('（', '(').Replace('）', ')').Replace('！', '!').Replace('＆', '&').Replace('｜', '|').Replace('＠', '@');

    /// <summary>校验表达式：合法返回 null，否则返回错误描述（供编辑器直接提示）。itemCount = 条件条数。</summary>
    public static string? Validate(string expr, int itemCount)
    {
        try { Compile(expr, itemCount); return null; }
        catch (FormatException ex) { return ex.Message; }
    }

    /// <summary>求值。item(i)（0 起）给出第 i 条条件的结果，仅在短路未跳过时才被调用；表达式非法抛 FormatException。</summary>
    public static bool Eval(string expr, int itemCount, Func<int, bool> item)
        => Compile(expr, itemCount).Evaluate(item);

    // 编译结果只属于调用方；引擎按一次运行缓存，避免无限全局缓存积累用户表达式。
    internal sealed class Compiled
    {
        internal readonly Node Root;
        public string Text { get; }
        internal Compiled(Node root, string text) { Root = root; Text = text; }
        public bool Evaluate(Func<int, bool> item) => Root.Eval(item);
    }

    internal static Compiled Compile(string expr, int itemCount)
    {
        if (expr.Length > MaxExpressionLength)
            throw new FormatException($"表达式最多允许 {MaxExpressionLength} 个字符，请拆分条件。");
        var normalized = Normalize(expr);
        return new Compiled(Parse(normalized, itemCount), normalized);
    }

    /// <summary>
    /// 条件列表删除第 removed 条（1 起）后改写表达式：对它的引用整段消失（与/或少了一边就取另一边，
    /// !@N 随之消失），其余引用的序号自动前移对准新列表。返回改写结果（空串 = 表达式已无内容）；
    /// 原表达式本身非法时返回 null，调用方保持原文不动。
    /// </summary>
    public static string? RemoveRef(string expr, int removed, int itemCount)
    {
        Node root;
        try { root = Compile(expr, itemCount).Root; }
        catch (FormatException) { return null; }
        var stripped = Strip(root, removed - 1);
        return stripped == null ? "" : Render(stripped, 0);
    }

    private static Node? Strip(Node n, int removed) => n switch
    {
        Ref r => r.I == removed ? null : new Ref { I = r.I > removed ? r.I - 1 : r.I },
        Not t => Strip(t.X, removed) is Node x ? new Not { X = x } : null,
        AndN a => (Strip(a.L, removed), Strip(a.R, removed)) switch
        {
            (null, null) => null,
            (Node l, null) => l,
            (null, Node r) => r,
            (Node l, Node r) => new AndN { L = l, R = r },
        },
        OrN o => (Strip(o.L, removed), Strip(o.R, removed)) switch
        {
            (null, null) => null,
            (Node l, null) => l,
            (null, Node r) => r,
            (Node l, Node r) => new OrN { L = l, R = r },
        },
        _ => n,
    };

    // prec：0=|| 层、1=&& 层、2=原子（! 的操作数）。子表达式优先级低于所处环境时补括号。
    private static string Render(Node n, int prec) => n switch
    {
        Ref r => "@" + (r.I + 1),
        Not t => "!" + Render(t.X, 2),
        AndN a => Wrap(Render(a.L, 1) + " && " + Render(a.R, 1), prec > 1),
        OrN o => Wrap(Render(o.L, 0) + " || " + Render(o.R, 0), prec > 0),
        _ => "",
    };
    private static string Wrap(string s, bool need) => need ? "(" + s + ")" : s;

    // ---- 递归下降：expr := term ('||' term)* ；term := factor ('&&' factor)* ；factor := '!' factor | '(' expr ')' | '@' 数字
    internal abstract class Node { public int Depth = 1; public abstract bool Eval(Func<int, bool> item); }
    private sealed class Ref : Node { public int I; public override bool Eval(Func<int, bool> f) => f(I); }
    private sealed class Not : Node { public Node X = null!; public override bool Eval(Func<int, bool> f) => !X.Eval(f); }
    private sealed class AndN : Node { public Node L = null!, R = null!; public override bool Eval(Func<int, bool> f) => L.Eval(f) && R.Eval(f); }
    private sealed class OrN : Node { public Node L = null!, R = null!; public override bool Eval(Func<int, bool> f) => L.Eval(f) || R.Eval(f); }

    private static Node Parse(string s, int itemCount)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new FormatException("请填写表达式，例：(@1 && @2) || @3");
        int p = 0;
        var n = ParseOr(s, ref p, itemCount, new ParseBudget(), 0);
        SkipWs(s, ref p);
        if (p < s.Length) throw new FormatException($"位置 {p + 1} 处有多余内容「{s[p..]}」（两个条件之间要用 && 或 || 连接）");
        return n;
    }

    private sealed class ParseBudget
    {
        private int _nodes;
        public Node Track(Node node, int depth = 1)
        {
            if (++_nodes > MaxNodes) throw new FormatException($"表达式最多允许 {MaxNodes} 个条件和运算项，请拆分条件。");
            CheckDepth(depth);
            node.Depth = depth;
            return node;
        }
        public static void CheckDepth(int depth)
        {
            if (depth > MaxDepth) throw new FormatException($"表达式嵌套不能超过 {MaxDepth} 层，请拆分条件。");
        }
    }

    private static void SkipWs(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

    private static Node ParseOr(string s, ref int p, int n, ParseBudget budget, int depth)
    {
        var l = ParseAnd(s, ref p, n, budget, depth);
        while (true)
        {
            SkipWs(s, ref p);
            if (p + 1 < s.Length && s[p] == '|' && s[p + 1] == '|')
            { p += 2; var r = ParseAnd(s, ref p, n, budget, depth); l = budget.Track(new OrN { L = l, R = r }, 1 + Math.Max(l.Depth, r.Depth)); }
            else if (p < s.Length && s[p] == '|') throw new FormatException("「或」请写成 ||（两个竖线）");
            else return l;
        }
    }

    private static Node ParseAnd(string s, ref int p, int n, ParseBudget budget, int depth)
    {
        var l = ParseFactor(s, ref p, n, budget, depth);
        while (true)
        {
            SkipWs(s, ref p);
            if (p + 1 < s.Length && s[p] == '&' && s[p + 1] == '&')
            { p += 2; var r = ParseFactor(s, ref p, n, budget, depth); l = budget.Track(new AndN { L = l, R = r }, 1 + Math.Max(l.Depth, r.Depth)); }
            else if (p < s.Length && s[p] == '&') throw new FormatException("「与」请写成 &&（两个 & 符号）");
            else return l;
        }
    }

    private static Node ParseFactor(string s, ref int p, int n, ParseBudget budget, int depth)
    {
        ParseBudget.CheckDepth(depth);
        SkipWs(s, ref p);
        if (p >= s.Length) throw new FormatException("表达式不完整（&& / || / ! 后面要跟条件引用）");
        char c = s[p];
        if (c == '!')
        { p++; var x = ParseFactor(s, ref p, n, budget, depth + 1); return budget.Track(new Not { X = x }, x.Depth + 1); }
        if (c == '(')
        {
            p++;
            var inner = ParseOr(s, ref p, n, budget, depth + 1);
            SkipWs(s, ref p);
            if (p >= s.Length || s[p] != ')') throw new FormatException("缺少右括号 )");
            p++;
            return inner;
        }
        if (c == '@')
        {
            p++;
            int st = p;
            while (p < s.Length && char.IsDigit(s[p])) p++;
            if (p == st) throw new FormatException("@ 后面要跟条件序号，如 @1");
            if (!int.TryParse(s[st..p], out int idx) || idx < 1 || idx > n)
                throw new FormatException($"@{s[st..p]} 超出范围：当前共 {n} 条条件");
            return budget.Track(new Ref { I = idx - 1 });
        }
        throw new FormatException($"位置 {p + 1} 处无法识别「{c}」（支持 @序号、&&、||、!、括号）");
    }
}
