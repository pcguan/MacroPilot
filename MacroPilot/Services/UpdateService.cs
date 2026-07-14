using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MacroPilot.Services;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }         // 原始 tag，如 v0.0.2
    public required string DownloadUrl { get; init; } // 安装器 exe 的直链
    public string? Notes { get; init; }               // release 说明
    public long Size { get; init; }
}

/// <summary>
/// 在线更新：查 GitHub 公开仓库 pcguan/MacroPilot 的最新 release，比对本机版本，下载安装器并自更新。
/// 仓库是公开的 → release 查询与资产下载都免鉴权、无需内嵌 token。
/// </summary>
public static class UpdateService
{
    private const string LatestApi = "https://api.github.com/repos/pcguan/MacroPilot/releases/latest";
    public const string ReleasesPage = "https://github.com/pcguan/MacroPilot/releases";

    /// <summary>本机版本（取程序集版本 Major.Minor.Build，忽略 Revision）。</summary>
    public static Version CurrentVersion
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0); return Norm(v); }
    }
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacroPilot-Updater");      // GitHub API 要求带 UA
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>查询最新 release。无网络 / 无 release / 无 exe 资产 / 解析失败都返回 null（调用方据 silent 决定是否提示）。</summary>
    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var c = NewClient(TimeSpan.FromSeconds(20));
            var json = await c.GetStringAsync(LatestApi, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ver = ParseVersion(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
            if (ver == null) return null;
            string tag = root.GetProperty("tag_name").GetString() ?? "";

            string? url = null; long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                        size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                        break;
                    }
                }
            if (string.IsNullOrEmpty(url)) return null;
            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            return new UpdateInfo { Version = ver, Tag = tag, DownloadUrl = url!, Notes = notes, Size = size };
        }
        catch { return null; }
    }

    public static bool IsNewer(UpdateInfo info) => info.Version > CurrentVersion;

    // 解析 tag → 版本。容忍前缀 v / 预发布后缀（取前三段数字）。
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Norm(v) : null;
    }
    private static Version Norm(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>下载安装器到 %TEMP%\MacroPilotUpdate；progress 报告 0..1（总大小未知时报 -1）。返回落地文件路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"MacroPilotInstaller_{Safe(info.Tag)}.exe");

        using var c = NewClient(TimeSpan.FromMinutes(10));
        using var resp = await c.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920]; long read = 0; int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            progress?.Report(total > 0 ? (double)read / total : -1);
        }
        return file;
    }

    /// <summary>
    /// 触发【静默在线更新】并退出本程序：给安装器设环境变量进入 update 模式（无 UI、无用户操作，内部做
    /// 备份→覆盖安装→失败回滚→重启本体），随即 Shutdown 让程序文件解锁供安装器覆盖。
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');   // 本体所在目录＝安装目录
        var psi = new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = false };
        psi.EnvironmentVariables["MACROPILOT_UPDATE"] = "1";
        psi.EnvironmentVariables["MACROPILOT_UPDATE_TARGET"] = installDir;
        System.Diagnostics.Process.Start(psi);
        System.Windows.Application.Current?.Shutdown();
    }

    private static string Safe(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }
}
