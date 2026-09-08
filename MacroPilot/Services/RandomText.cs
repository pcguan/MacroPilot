using System;
using System.Collections.Generic;
using System.Text;

namespace MacroPilot.Services;

/// <summary>
/// 「逆向正则」：按正则模式【随机生成】一个符合该模式的字符串（不是用来匹配的）。
/// 例：<c>[0-9a-z]{10}</c> → 10 位随机小写字母数字；<c>1[3-9]\d{9}</c> → 一个随机手机号。
///
/// 支持常用子集：字面量、字符类 <c>[a-z0-9]</c>（含否定 <c>[^abc]</c>）、预定义类 <c>\d \w \s</c>
/// （及大写取反）、分组 <c>(...)</c>、交替 <c>|</c>、量词 <c>{n} {n,m} {n,} ? * +</c>、反斜杠转义。
/// 不支持断言/反向引用（锚点 ^ $ 直接忽略）——生成场景用不到。
/// </summary>
public static class RandomText
{
    private const int OpenEndedExtra = 8;   // * + {n,} 这类无上限量词，最多再重复这么多次
    private const int MaxLength = 4096;     // 结果总长上限，防 {1000}{1000} 之类组合爆炸

    /// <summary>按模式生成一个随机串。模式非法时抛 <see cref="FormatException"/>。</summary>
    public static string Generate(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return "";
        var node = new Parser(pattern).ParseAll();
        var sb = new StringBuilder();
        node.Emit(sb, Random.Shared);
        return sb.ToString();
    }

    // ---------- 语法树 ----------
    private abstract class Node { public abstract void Emit(StringBuilder sb, Random rng); }

    private sealed class LiteralNode : Node
    {
        public string Text = "";
        public override void Emit(StringBuilder sb, Random rng)
        {
            if (sb.Length + Text.Length <= MaxLength) sb.Append(Text);
        }
    }

    private sealed class SetNode : Node
    {
        public char[] Chars = Array.Empty<char>();
        public override void Emit(StringBuilder sb, Random rng)
        {
            if (Chars.Length == 0 || sb.Length >= MaxLength) return;
            sb.Append(Chars[rng.Next(Chars.Length)]);
        }
    }

    private sealed class SeqNode : Node
    {
        public List<Node> Items = new();
        public override void Emit(StringBuilder sb, Random rng)
        {
            foreach (var n in Items) { if (sb.Length >= MaxLength) return; n.Emit(sb, rng); }
        }
    }

    private sealed class AltNode : Node
    {
        public List<Node> Options = new();
        public override void Emit(StringBuilder sb, Random rng)
        {
            if (Options.Count == 0) return;
            Options[rng.Next(Options.Count)].Emit(sb, rng);   // 随机选一个分支
        }
    }

    private sealed class RepeatNode : Node
    {
        public Node Inner = null!;
        public int Min, Max;
        public override void Emit(StringBuilder sb, Random rng)
        {
            int n = Max <= Min ? Min : rng.Next(Min, Max + 1);
            for (int i = 0; i < n; i++) { if (sb.Length >= MaxLength) return; Inner.Emit(sb, rng); }
        }
    }

    // ---------- 解析（递归下降）----------
    // 交替 := 串联 ('|' 串联)* ；串联 := 量词项* ；量词项 := 原子 量词? ；
    // 原子 := '(' 交替 ')' | '[' 字符类 ']' | 转义 | '.' | 字面字符
    private sealed class Parser
    {
        private readonly string _p;
        private int _i;
        public Parser(string pattern) { _p = pattern; }

        public Node ParseAll()
        {
            var n = ParseAlternation();
            if (_i < _p.Length) throw new FormatException($"第 {_i + 1} 个字符处有多余的 '{_p[_i]}'");
            return n;
        }

        private Node ParseAlternation()
        {
            var first = ParseSequence();
            if (_i >= _p.Length || _p[_i] != '|') return first;
            var alt = new AltNode();
            alt.Options.Add(first);
            while (_i < _p.Length && _p[_i] == '|') { _i++; alt.Options.Add(ParseSequence()); }
            return alt;
        }

        private Node ParseSequence()
        {
            var seq = new SeqNode();
            while (_i < _p.Length && _p[_i] != '|' && _p[_i] != ')') seq.Items.Add(ParseRepeat());
            return seq.Items.Count == 1 ? seq.Items[0] : seq;
        }

        private Node ParseRepeat()
        {
            var atom = ParseAtom();
            if (_i >= _p.Length) return atom;
            int min, max;
            switch (_p[_i])
            {
                case '?': _i++; min = 0; max = 1; break;
                case '*': _i++; min = 0; max = OpenEndedExtra; break;
                case '+': _i++; min = 1; max = 1 + OpenEndedExtra; break;
                case '{':
                    int close = _p.IndexOf('}', _i);
                    if (close < 0) return atom;                       // 没闭合就当普通字符，前面已按字面量处理
                    var body = _p.Substring(_i + 1, close - _i - 1);
                    var parts = body.Split(',');
                    if (parts.Length == 1 && int.TryParse(parts[0], out var exact)) { min = max = exact; }
                    else if (parts.Length == 2 && int.TryParse(parts[0], out var lo))
                    {
                        min = lo;
                        max = parts[1].Length == 0 ? lo + OpenEndedExtra
                            : (int.TryParse(parts[1], out var hi) ? hi : throw new FormatException($"量词 {{{body}}} 不合法"));
                    }
                    else throw new FormatException($"量词 {{{body}}} 不合法");
                    if (min < 0 || max < min) throw new FormatException($"量词 {{{body}}} 范围不合法");
                    _i = close + 1;
                    break;
                default: return atom;
            }
            if (_i < _p.Length && (_p[_i] == '?' || _p[_i] == '+')) _i++;   // 懒惰/占有量词后缀：生成场景无意义，跳过
            return new RepeatNode { Inner = atom, Min = min, Max = max };
        }

        private Node ParseAtom()
        {
            char c = _p[_i];
            switch (c)
            {
                case '(':
                    _i++;
                    if (_i + 1 < _p.Length && _p[_i] == '?' && (_p[_i + 1] == ':' || _p[_i + 1] == '=' || _p[_i + 1] == '!')) _i += 2;   // (?: 等前缀
                    var inner = ParseAlternation();
                    if (_i >= _p.Length || _p[_i] != ')') throw new FormatException("缺少右括号 ')'");
                    _i++;
                    return inner;
                case '[': return ParseSet();
                case '\\': return ParseEscape();
                case '.': _i++; return new SetNode { Chars = AnyChars };
                case '^' or '$': _i++; return new LiteralNode();   // 锚点：生成时无意义，忽略
                default: _i++; return new LiteralNode { Text = c.ToString() };
            }
        }

        // [a-z0-9_]、[^abc]、内部可含 \d 之类
        private Node ParseSet()
        {
            _i++;                                    // 跳过 '['
            bool negate = _i < _p.Length && _p[_i] == '^';
            if (negate) _i++;
            var chars = new List<char>();
            bool first = true;
            while (true)
            {
                if (_i >= _p.Length) throw new FormatException("字符类缺少右中括号 ']'");
                char c = _p[_i];
                if (c == ']' && !first) { _i++; break; }
                first = false;
                if (c == '\\')
                {
                    _i++;
                    if (_i >= _p.Length) throw new FormatException("字符类里的 '\\' 后面缺少字符");
                    var cls = PredefinedClass(_p[_i]);
                    if (cls != null) chars.AddRange(cls); else chars.Add(Unescape(_p[_i]));
                    _i++;
                    continue;
                }
                // 区间 a-z：'-' 在首尾时按字面量处理
                if (_i + 2 < _p.Length && _p[_i + 1] == '-' && _p[_i + 2] != ']')
                {
                    char lo = c, hi = _p[_i + 2];
                    if (hi < lo) throw new FormatException($"字符区间 {lo}-{hi} 顺序颠倒");
                    for (char x = lo; x <= hi; x++) chars.Add(x);
                    _i += 3;
                    continue;
                }
                chars.Add(c);
                _i++;
            }
            if (negate)
            {
                var ex = new HashSet<char>(chars);
                var keep = new List<char>();
                foreach (var ch in AnyChars) if (!ex.Contains(ch)) keep.Add(ch);
                chars = keep;
            }
            if (chars.Count == 0) throw new FormatException("字符类为空，生成不出字符");
            return new SetNode { Chars = chars.ToArray() };
        }

        private Node ParseEscape()
        {
            _i++;                                    // 跳过 '\'
            if (_i >= _p.Length) throw new FormatException("末尾的 '\\' 后面缺少字符");
            char c = _p[_i++];
            var cls = PredefinedClass(c);
            return cls != null ? new SetNode { Chars = cls } : new LiteralNode { Text = Unescape(c).ToString() };
        }

        private static char Unescape(char c) => c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c };
    }

    // ---------- 字符集 ----------
    private static readonly char[] AnyChars = Build(0x21, 0x7E);          // '.' 与否定类的取值域：可打印 ASCII（不含空格）
    private static readonly char[] Digits = Build('0', '9');
    private static readonly char[] Words = BuildWords();
    private static readonly char[] Spaces = { ' ' };                     // \s 只给空格：换行会打断文本，生成场景不该出现

    private static char[]? PredefinedClass(char c) => c switch
    {
        'd' => Digits,
        'w' => Words,
        's' => Spaces,
        'D' => Exclude(Digits),
        'W' => Exclude(Words),
        'S' => Exclude(Spaces),
        _ => null,
    };

    private static char[] Build(int lo, int hi)
    {
        var a = new char[hi - lo + 1];
        for (int i = 0; i < a.Length; i++) a[i] = (char)(lo + i);
        return a;
    }

    private static char[] BuildWords()
    {
        var list = new List<char>();
        for (char c = 'a'; c <= 'z'; c++) list.Add(c);
        for (char c = 'A'; c <= 'Z'; c++) list.Add(c);
        for (char c = '0'; c <= '9'; c++) list.Add(c);
        list.Add('_');
        return list.ToArray();
    }

    private static char[] Exclude(char[] removed)
    {
        var ex = new HashSet<char>(removed);
        var keep = new List<char>();
        foreach (var c in AnyChars) if (!ex.Contains(c)) keep.Add(c);
        return keep.ToArray();
    }
}
