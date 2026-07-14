using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MacroPilot.Services;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }        // 原始 tag，如 v0.0.7
    public string? ZipUrl { get; init; }             // 本体压缩包（MacroPilot-app.zip，~2.8MB，优先）
    public long ZipSize { get; init; }
    public string? ExeUrl { get; init; }             // 安装器 exe（兜底：release 无 zip 时用）
    public long ExeSize { get; init; }
    public string? Notes { get; init; }
    public bool HasAsset => ZipUrl != null || ExeUrl != null;
}

/// <summary>
/// 在线更新：查 GitHub 公开仓库 pcguan/MacroPilot 的最新 release，比对本机版本。
/// 更新优先下载【本体压缩包 zip】（轻量），经 PowerShell 助手就地解压覆盖（备份→覆盖→回滚→重启，
/// 无需用户操作、保留 unins\ 卸载器）；release 若无 zip 则回退到静默跑安装器 exe。
/// 仓库公开 → 查询与下载都免鉴权、无需内嵌 token。
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
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacroPilot-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>查询最新 release。无网络 / 无 release / 无可用资产 / 解析失败都返回 null。</summary>
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

            string? zipUrl = null, exeUrl = null; long zipSize = 0, exeSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    if (url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zipUrl = url; zipSize = size; }
                    else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { exeUrl = url; exeSize = size; }
                }
            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var info = new UpdateInfo { Version = ver, Tag = tag, ZipUrl = zipUrl, ZipSize = zipSize, ExeUrl = exeUrl, ExeSize = exeSize, Notes = notes };
            return info.HasAsset ? info : null;
        }
        catch { return null; }
    }

    public static bool IsNewer(UpdateInfo info) => info.Version > CurrentVersion;

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Norm(v) : null;
    }
    private static Version Norm(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>下载更新包到 %TEMP%\MacroPilotUpdate（优先 zip，否则 exe）。progress 报告 0..1（未知总大小报 -1）。返回落地路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        bool useZip = info.ZipUrl != null;
        string url = useZip ? info.ZipUrl! : info.ExeUrl!;
        long knownSize = useZip ? info.ZipSize : info.ExeSize;
        string ext = useZip ? "zip" : "exe";

        var dir = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"MacroPilot_{Safe(info.Tag)}.{ext}");

        using var c = NewClient(TimeSpan.FromMinutes(10));
        using var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? knownSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920]; long read = 0; int nn;
        while ((nn = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, nn), ct);
            read += nn;
            progress?.Report(total > 0 ? (double)read / total : -1);
        }
        return file;
    }

    /// <summary>应用下载好的更新并退出：zip → PowerShell 助手就地解压覆盖；exe → 静默跑安装器（兜底）。</summary>
    public static void ApplyAndExit(string file)
    {
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ApplyZipUpdateAndExit(file);
        else LaunchInstallerAndExit(file);
    }

    /// <summary>
    /// zip 就地更新：写一段 PowerShell 助手并隐藏启动它，随即退出本程序让文件解锁。助手负责
    /// 等本体退出 → 同卷改名备份旧目录 → 解压 zip 覆盖 → 保留 unins\ 卸载器 → 失败回滚 → 重启本体。
    /// </summary>
    public static void ApplyZipUpdateAndExit(string zipPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var dir = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate");
        Directory.CreateDirectory(dir);
        var ps1 = Path.Combine(dir, "apply_update.ps1");
        File.WriteAllText(ps1, ZipUpdateScript, new UTF8Encoding(false));
        int pid = Environment.ProcessId;
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\" " +
                        $"-Zip \"{zipPath}\" -Dir \"{installDir}\" -AppPid {pid}",
        };
        System.Diagnostics.Process.Start(psi);
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>兜底：触发安装器静默就地更新（release 无 zip 时用），随即退出本程序。</summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
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

    // PowerShell 就地更新助手（纯 ASCII，避免编码坑）。日志写 %TEMP%\MacroPilotUpdate\update.log。
    private const string ZipUpdateScript = @"
param([string]$Zip, [string]$Dir, [int]$AppPid)
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:TEMP 'MacroPilotUpdate'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'update.log'
function Log($m){ Add-Content -LiteralPath $log -Value ((Get-Date).ToString('HH:mm:ss.fff') + ' ' + $m) }
Log ""=== start dir=$Dir zip=$Zip pid=$AppPid ===""
try {
  if ($AppPid -gt 0) { try { Wait-Process -Id $AppPid -Timeout 30 -ErrorAction SilentlyContinue } catch {} }
  $exe = Join-Path $Dir 'MacroPilot.exe'
  for ($i=0; $i -lt 40; $i++) {
    try { $fs=[IO.File]::Open($exe,'Open','ReadWrite','None'); $fs.Close(); break } catch { Start-Sleep -Milliseconds 500 }
  }
  $leaf = Split-Path $Dir -Leaf
  $backup = ""$Dir.bak_"" + ([DateTimeOffset]::Now.ToUnixTimeSeconds())
  $backupLeaf = Split-Path $backup -Leaf
  if (Test-Path -LiteralPath $Dir) { Rename-Item -LiteralPath $Dir -NewName $backupLeaf -Force; Log ""backup -> $backup"" }
  try {
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    Expand-Archive -LiteralPath $Zip -DestinationPath $Dir -Force
    Log 'extracted'
    $uninsSrc = Join-Path $backup 'unins'
    if (Test-Path -LiteralPath $uninsSrc) { Copy-Item -LiteralPath $uninsSrc -Destination (Join-Path $Dir 'unins') -Recurse -Force; Log 'unins preserved' }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    Log 'success'
  } catch {
    Log ""extract FAILED: $_ -> rollback""
    try { if (Test-Path -LiteralPath $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue } } catch {}
    if (Test-Path -LiteralPath $backup) { Rename-Item -LiteralPath $backup -NewName $leaf -Force; Log 'rolled back to old version' }
  }
  if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -WorkingDirectory $Dir; Log 'relaunched' }
  try { Remove-Item -LiteralPath $Zip -Force -ErrorAction SilentlyContinue } catch {}
  Log '=== done ==='
} catch { Log ""fatal: $_"" }
";
}
