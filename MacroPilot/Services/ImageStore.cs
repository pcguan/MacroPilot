using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>
/// 条件图片外置存储：字节按内容(SHA-256)存到【数据目录\images\&lt;hash&gt;.png】，方案里只存引用 "file:&lt;hash&gt;"。
/// 兼容旧格式：RunConditionImage 若是内联 base64 也能解析（保存时就地外置成引用）。
/// 导出保持自包含：导出前把引用解析回 base64 内联，导入的 base64 保存时再外置。
/// </summary>
public static class ImageStore
{
    private const string Prefix = "file:";
    public static string ImagesDir => Path.Combine(Storage.DataDir, "images");

    private static bool IsRef(string img) => img.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>解析成字节：引用→读文件；否则当作 base64。空/失败返回 null。</summary>
    public static byte[]? Bytes(string? img)
    {
        if (string.IsNullOrEmpty(img)) return null;
        try
        {
            if (IsRef(img))
            {
                var f = Path.Combine(ImagesDir, img[Prefix.Length..] + ".png");
                return File.Exists(f) ? File.ReadAllBytes(f) : null;
            }
            return Convert.FromBase64String(img);
        }
        catch { return null; }
    }

    /// <summary>存字节 → "file:&lt;hash&gt;"（内容寻址，同内容不重复写、天然去重）。写失败则退回内联 base64，保证不丢图。</summary>
    public static string Ref(byte[] png)
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            Directory.CreateDirectory(ImagesDir);
            var f = Path.Combine(ImagesDir, hash + ".png");
            if (!File.Exists(f)) File.WriteAllBytes(f, png);
            return Prefix + hash;
        }
        catch { return Convert.ToBase64String(png); }
    }

    /// <summary>解析成 base64（导出自包含用）：引用→读文件→base64；已是 base64 原样返回。</summary>
    public static string ToBase64(string? img)
    {
        if (string.IsNullOrEmpty(img)) return "";
        if (!IsRef(img)) return img;
        var b = Bytes(img);
        return b != null ? Convert.ToBase64String(b) : "";
    }

    // 递归遍历一步及其子动作/监听动作。
    private static void ForEach(MacroStep s, Action<MacroStep> act)
    {
        act(s);
        if (s.SuccessAction != null) ForEach(s.SuccessAction, act);
        if (s.CompleteAction != null) ForEach(s.CompleteAction, act);
        if (s.FailAction != null) ForEach(s.FailAction, act);
        foreach (var c in s.Children) ForEach(c, act);
    }

    /// <summary>就地把内联 base64 图片外置成 "file:&lt;hash&gt;"（保存前调用）。已是引用的跳过。</summary>
    public static void Externalize(IEnumerable<MacroStep> steps)
    {
        foreach (var top in steps)
            ForEach(top, s =>
            {
                var img = s.RunConditionImage;
                if (!string.IsNullOrEmpty(img) && !IsRef(img))
                {
                    try { s.RunConditionImage = Ref(Convert.FromBase64String(img)); } catch { }
                }
            });
    }

    /// <summary>就地把引用解析回内联 base64（导出前对【克隆】调用，保持导出文件自包含）。</summary>
    public static void Inline(IEnumerable<MacroStep> steps)
    {
        foreach (var top in steps)
            ForEach(top, s =>
            {
                if (!string.IsNullOrEmpty(s.RunConditionImage)) s.RunConditionImage = ToBase64(s.RunConditionImage);
            });
    }
}
