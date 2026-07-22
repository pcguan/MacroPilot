using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace MacroPilot.Services;

/// <summary>
/// 内置更新日志（嵌入资源 changelog.json）。它同时是发布说明（version.json 的 notes）的唯一来源，
/// 避免"程序里写一套、release 页面写另一套"。新增版本时只改 changelog.json。
/// </summary>
public static class Changelog
{
    public sealed class Entry
    {
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
        public List<string> Notes { get; set; } = new();
    }

    private static List<Entry>? _all;

    /// <summary>全部条目，按文件顺序（新版本在前）。读不到或格式坏时返回空表，不抛。</summary>
    public static IReadOnlyList<Entry> All => _all ??= Load();

    private static List<Entry> Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // 资源名形如 MacroPilot.changelog.json；用后缀匹配，免得改了默认命名空间就找不到。
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("changelog.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return new List<Entry>();
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return new List<Entry>();
            using var r = new StreamReader(s);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<Entry>>(r.ReadToEnd(), opts) ?? new List<Entry>();
        }
        catch { return new List<Entry>(); }
    }

    /// <summary>取指定版本的更新点；没有该版本的条目则返回空表。</summary>
    public static IReadOnlyList<string> NotesFor(string version) =>
        All.FirstOrDefault(e => e.Version == version)?.Notes ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>当前运行版本的条目（可能为 null，例如本地调试版号不在表里）。</summary>
    public static Entry? Current => All.FirstOrDefault(e => e.Version == UpdateService.CurrentVersionText);
}
