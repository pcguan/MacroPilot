using System.Collections.Generic;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

/// <summary>条件组合的自定义表达式：语法、求值、短路，以及运行期与条件列表的联动。</summary>
public class CondExprTests
{
    private static bool Eval(string expr, params bool[] vals)
        => CondExpr.Eval(expr, vals.Length, i => vals[i]);

    [Theory]
    [InlineData("@1 && @2", true, new[] { true, true })]
    [InlineData("@1 && @2", false, new[] { true, false })]
    [InlineData("@1 || @2", true, new[] { false, true })]
    [InlineData("@1 || @2", false, new[] { false, false })]
    [InlineData("(@1 && @2) || @3", true, new[] { false, true, true })]
    [InlineData("(@1 && @2) || @3", false, new[] { true, false, false })]
    [InlineData("!@1", false, new[] { true })]
    [InlineData("!(@1 || @2)", true, new[] { false, false })]
    [InlineData("@1 || @2 && @3", true, new[] { true, false, false })]     // && 优先级高于 ||
    [InlineData("@1 || @2 && @3", false, new[] { false, true, false })]
    [InlineData("@1 && !@2", true, new[] { true, false })]
    public void 表达式求值(string expr, bool expected, bool[] vals)
        => Assert.Equal(expected, Eval(expr, vals));

    [Fact]
    public void 全角符号一律按半角认()
        => Assert.True(Eval("（@1 ＆＆ ！@2） ｜｜ @3", true, false, false));

    [Fact]
    public void 短路时不求值被跳过的条件()
    {
        var called = new List<int>();
        bool F(int i) { called.Add(i); return i == 0; }   // @1 真，@2 假
        Assert.True(CondExpr.Eval("@1 || @2", 2, F));
        Assert.Equal(new[] { 0 }, called);                 // @2 被短路跳过
        called.Clear();
        Assert.False(CondExpr.Eval("!@1 && @2", 2, F));
        Assert.Equal(new[] { 0 }, called);
    }

    [Theory]
    [InlineData("@1 && @2", 2, true)]
    [InlineData("", 2, false)]
    [InlineData("   ", 2, false)]
    [InlineData("@0", 2, false)]        // 序号从 1 起
    [InlineData("@3", 2, false)]        // 越界
    [InlineData("@1 &", 2, false)]      // 单个 &
    [InlineData("@1 | @2", 2, false)]   // 单个 |
    [InlineData("(@1 && @2", 2, false)] // 缺右括号
    [InlineData("@1 @2", 2, false)]     // 缺连接符
    [InlineData("abc", 2, false)]
    [InlineData("@x", 2, false)]
    [InlineData("!(!@1)", 1, true)]
    public void 语法校验(string expr, int n, bool ok)
        => Assert.Equal(ok, CondExpr.Validate(expr, n) == null);

    [Theory]
    [InlineData("(@1 && @2) || @3", 2, 3, "@1 || @2")]        // @2 消失，@3 前移成 @2
    [InlineData("@1 && @2", 1, 2, "@1")]                       // 原 @2 前移
    [InlineData("!@2 && @1", 2, 2, "@1")]                      // !@2 整段消失
    [InlineData("@1", 1, 1, "")]                               // 删光
    [InlineData("!(@1 || @2) && @3", 3, 3, "!(@1 || @2)")]     // 括号该保留时保留
    [InlineData("(@1 || @2) && @3", 1, 3, "@1 && @2")]         // 剩单边时多余括号去掉
    [InlineData("(@1 || @2) && @3", 4, 4, "(@1 || @2) && @3")] // 删除未被引用的条件：表达式原样保留（括号也在）
    [InlineData("@2 || (@1 && @3)", 1, 3, "@1 || @2")]         // 删 @1：@2→@1，(@1 && @3) 只剩 @3（前移为 @2）
    public void 删除条件后表达式自动改写(string expr, int removed, int n, string expected)
        => Assert.Equal(expected, CondExpr.RemoveRef(expr, removed, n));

    [Fact]
    public void 原表达式非法时改写返回_null_保持原文()
        => Assert.Null(CondExpr.RemoveRef("@1 &&", 1, 2));

    // ---- 运行期联动：Expr 模式的运行条件 / 停止条件 ----

    [Fact]
    public void 运行条件按表达式判定()
    {
        var s = Key("a").Cond("Expr:@1 || @2", Never(), Always());
        Assert.Equal(new[] { "a" }, Run(Plan(s)).Calls);

        var t = Key("b").Cond("Expr:@1 && @2", Never(), Always());
        Assert.Empty(Run(Plan(t)).Calls);

        var u = Key("c").Cond("Expr:(@1 && @2) || !@1", Never(), Always());   // !@1 = !Never = 满足
        Assert.Equal(new[] { "c" }, Run(Plan(u)).Calls);
    }

    [Fact]
    public void 表达式无效时按全部满足回退()
    {
        // 旧档手改出的坏表达式：不该让方案卡死，退回 And（Never 且 Always → 不满足 → 跳过）
        var s = Key("a").Cond("Expr:@9 &&", Never(), Always());
        Assert.Empty(Run(Plan(s)).Calls);
    }

    [Fact]
    public void 停止条件按表达式判定()
    {
        var s = Key("a");
        s.RepeatUntil = true;
        s.UntilConditions.Add(Never());
        s.UntilConditions.Add(Always());
        s.UntilLogic = "Expr:@1 || @2";
        s.UntilMaxCount = 5; s.RepeatDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);   // 第一趟末表达式即成立
        Assert.True(r.LogHas("停止条件已满足"));
    }
}
