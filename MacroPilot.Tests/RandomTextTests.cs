using System;
using System.Text.RegularExpressions;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>
/// 「正则随机生成」：用 .NET 真正的 Regex 反向校验——生成的串必须能被同一模式完整匹配。
/// 这套断言此前是在 corp-win 上临时跑的一次性 harness，现在固化成常驻用例。
/// </summary>
public class RandomTextTests
{
    [Theory]
    [InlineData("[0-9a-z]{10}")]
    [InlineData(@"\d{6}")]
    [InlineData(@"1[3-9]\d{9}")]
    [InlineData(@"(abc|xyz)-\w{4}")]
    [InlineData("[A-Z]{2,4}")]
    [InlineData("a?b+c*")]
    [InlineData("[^0-9a-zA-Z]{3}")]
    [InlineData(@"user_\d{1,3}@test\.com")]
    [InlineData("[0-0]{4}")]
    [InlineData("(ab){3}")]
    [InlineData(@"[a-zA-Z]\w{5,11}")]
    public void 生成结果必须匹配同一模式(string pattern)
    {
        var re = new Regex("^(?:" + pattern + ")$");
        for (int i = 0; i < 200; i++)
        {
            var s = RandomText.Generate(pattern);
            Assert.True(re.IsMatch(s), $"模式 {pattern} 生成的 \"{s}\" 不符合该模式");
        }
    }

    [Fact]
    public void 中文与字面量原样输出()
    {
        for (int i = 0; i < 50; i++)
        {
            var s = RandomText.Generate("颜色(红|绿|蓝)");
            Assert.StartsWith("颜色", s);
            Assert.Contains(s[2..], new[] { "红", "绿", "蓝" });
        }
    }

    [Fact]
    public void 固定长度模式生成固定长度()
    {
        for (int i = 0; i < 50; i++) Assert.Equal(10, RandomText.Generate("[0-9a-z]{10}").Length);
    }

    [Fact]
    public void 重复生成会产生不同结果()
    {
        var set = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < 60; i++) set.Add(RandomText.Generate("[0-9a-z]{10}"));
        Assert.True(set.Count > 50, "随机性不足：60 次生成里去重后只剩 " + set.Count);
    }

    [Theory]
    [InlineData("[a-")]        // 字符类未闭合
    [InlineData("(abc")]       // 括号未闭合
    [InlineData("a{2,1}")]     // 量词范围颠倒
    [InlineData("[]")]         // 空字符类
    public void 非法模式抛出可读的异常(string bad)
    {
        var ex = Assert.ThrowsAny<Exception>(() => RandomText.Generate(bad));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void 无上限量词有长度保护不会爆炸()
    {
        for (int i = 0; i < 50; i++)
        {
            Assert.True(RandomText.Generate("a*").Length <= 8);
            Assert.InRange(RandomText.Generate("a+").Length, 1, 9);
            Assert.InRange(RandomText.Generate("a{2,}").Length, 2, 10);
        }
    }

    [Fact]
    public void 空模式生成空串()
    {
        Assert.Equal("", RandomText.Generate(""));
    }

    [Fact]
    public void 锚点被忽略而不是当作字面量()
    {
        for (int i = 0; i < 20; i++)
        {
            var s = RandomText.Generate(@"^\d{3}$");
            Assert.Equal(3, s.Length);
            Assert.DoesNotContain("^", s);
            Assert.DoesNotContain("$", s);
        }
    }
}
