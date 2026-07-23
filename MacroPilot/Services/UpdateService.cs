using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MacroPilot.Services;

/// <summary>更新清单里的一个文件（本体 zip / 安装器 exe）。</summary>
public sealed class UpdateAsset
{
    public required string Name { get; init; }
    public long Size { get; init; }
    public string? Sha256 { get; init; }   // 可为空（老清单）；有则下载后强校验
}

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string VersionText { get; init; }   // 如 0.0.14
    public string? Notes { get; init; }
    public UpdateAsset? Zip { get; init; }              // 本体包（优先，~2.8MB）
    public UpdateAsset? Exe { get; init; }              // 安装器（兜底/全新安装）
    /// <summary>可用源的基址，按尝试顺序；[0] 是本次成功返回清单的那个源。</summary>
    public required string[] Bases { get; init; }
    public bool HasAsset => Zip != null || Exe != null;
}

/// <summary>
/// 在线更新：从【有序多源】读同一格式的 version.json 比对版本，下载后按 SHA-256 校验，再交给就地更新。
///
/// 源顺序（谁先应答用谁，前一个不通自动降级，不是单点）：
///   ① 自建 NAS 静态源——国内直连快，给不翻墙的用户当主力；
///   ② GitHub Release——源头/海外兜底（用 releases/latest/download/ 普通文件直链，
///      不再走 api.github.com，因此彻底摆脱未鉴权 60次/小时限流，也便于被代理/CDN 缓存）。
/// </summary>
public static class UpdateService
{
    // 基址必须以 / 结尾；每个源下都放同名的 version.json / 本体 zip / 安装器 exe。
    private static readonly string[] Sources =
    {
        "https://nas.pcguan.cn/macropilot/",
        "https://github.com/pcguan/MacroPilot/releases/latest/download/",
    };
    public const string ReleasesPage = "https://github.com/pcguan/MacroPilot/releases";
    private const string ManifestName = "version.json";

    private static UpdateInfo? _cached;   // 全部源都失败时沿用上次结果，不打扰用户

    /// <summary>本机版本（程序集版本 Major.Minor.Build，忽略 Revision）。</summary>
    public static Version CurrentVersion
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0); return Norm(v); }
    }
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacroPilot-Updater");
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    /// <summary>依次向各源要 version.json，第一个成功的即采用。全失败则返回上次缓存（首次为 null）。</summary>
    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        for (int i = 0; i < Sources.Length; i++)
        {
            try
            {
                using var c = NewClient(TimeSpan.FromSeconds(10));   // 单源短超时，坏源快速跳过
                var json = await c.GetStringAsync(Sources[i] + ManifestName, ct);
                var info = Parse(json, i);
                if (info != null) { _cached = info; return info; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 该源不通 → 试下一个 */ }
        }
        return _cached;
    }

    // 解析清单；bases 以命中的源打头，其余按原顺序跟上（下载时可继续回退）。
    private static UpdateInfo? Parse(string json, int hitIndex)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var vs = r.TryGetProperty("version", out var v) ? v.GetString() : null;
            var ver = ParseVersion(vs);
            if (ver == null) return null;

            UpdateAsset? Asset(string key)
            {
                if (!r.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) return null;
                var n = e.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                if (string.IsNullOrWhiteSpace(n)) return null;
                return new UpdateAsset
                {
                    Name = n!,
                    Size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
                    Sha256 = e.TryGetProperty("sha256", out var h) ? h.GetString() : null,
                };
            }

            var bases = new string[Sources.Length];
            bases[0] = Sources[hitIndex];
            for (int i = 0, k = 1; i < Sources.Length; i++) if (i != hitIndex) bases[k++] = Sources[i];

            var info = new UpdateInfo
            {
                Version = ver,
                VersionText = vs!.Trim().TrimStart('v', 'V'),
                Notes = r.TryGetProperty("notes", out var nt) ? nt.GetString() : null,
                Zip = Asset("zip"),
                Exe = Asset("exe"),
                Bases = bases,
            };
            return info.HasAsset ? info : null;
        }
        catch { return null; }
    }

    public static bool IsNewer(UpdateInfo info) => info.Version > CurrentVersion;

    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) t = t[..cut];
        return Version.TryParse(t, out var v) ? Norm(v) : null;
    }
    private static Version Norm(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// 下载更新包（优先本体 zip，无则安装器 exe）到 %TEMP%\MacroPilotUpdate。
    /// 逐源尝试：下完即校验 SHA-256，不匹配/下载失败就删档换下一个源；全失败抛异常。
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        bool useZip = info.Zip != null;
        var asset = useZip ? info.Zip! : info.Exe!;
        var dir = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"MacroPilot_{Safe(info.VersionText)}.{(useZip ? "zip" : "exe")}");

        Exception? last = null;
        foreach (var b in info.Bases)
        {
            try
            {
                await DownloadOne(b + asset.Name, file, asset.Size, progress, ct);
                if (Verify(file, asset.Sha256)) return file;
                last = new InvalidOperationException("下载内容校验失败（SHA-256 不匹配），已换源重试");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
            try { File.Delete(file); } catch { }
        }
        throw last ?? new InvalidOperationException("所有更新源都不可用");
    }

    private static async Task DownloadOne(string url, string file, long knownSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var c = NewClient(TimeSpan.FromMinutes(10));
        using var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? knownSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920]; long read = 0; int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            progress?.Report(total > 0 ? (double)read / total : -1);
        }
    }

    /// <summary>校验 SHA-256；清单没给哈希则跳过（视为通过）。</summary>
    private static bool Verify(string file, string? expect)
    {
        if (string.IsNullOrWhiteSpace(expect)) return true;
        try
        {
            using var fs = File.OpenRead(file);
            var hex = Convert.ToHexString(SHA256.HashData(fs));
            return string.Equals(hex, expect.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>应用更新并退出：zip → PowerShell 助手就地覆盖；exe → 静默跑安装器（兜底）。</summary>
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
        File.WriteAllText(ps1, ZipUpdateScript, new UTF8Encoding(true));   // 必须带 BOM：否则 Windows PowerShell 按 ANSI 读，中文全乱
        int pid = Environment.ProcessId;
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            // 关键：别让 PowerShell 继承本体的当前目录(=安装目录)，否则 PS 自己占着该目录 → 改名时"目录正在使用中"。
            WorkingDirectory = dir,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\" " +
                        $"-Zip \"{zipPath}\" -Dir \"{installDir}\" -AppPid {pid}",
        };
        System.Diagnostics.Process.Start(psi);
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>兜底：触发安装器静默就地更新（拿到的是 exe 时用），随即退出本程序。</summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var psi = new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = false };
        psi.EnvironmentVariables["MACROPILOT_UPDATE"] = "1";
        psi.EnvironmentVariables["MACROPILOT_UPDATE_TARGET"] = installDir;
        System.Diagnostics.Process.Start(psi);
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>
    /// 读 update.log 里最后一次运行的结果；若是失败则返回原因，否则返回 null。
    /// 供本体启动后把"上次更新没成功"摆出来，而不是默默停在旧版本。
    /// </summary>
    public static string? LastUpdateFailure()
    {
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate", "update.log");
            if (!File.Exists(log)) return null;
            var lines = File.ReadAllLines(log);
            // 从末尾往前找最近一次 "=== start"，只看这一段
            int start = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains("=== start")) { start = i; break; }
            if (start < 0) return null;
            for (int i = start; i < lines.Length; i++)
            {
                if (lines[i].Contains(" success")) return null;          // 最近一次是成功的
                int k = lines[i].IndexOf("FAILED:", StringComparison.Ordinal);
                if (k >= 0) return lines[i][(k + 7)..].Replace("-> rollback", "").Trim();
            }
            return null;
        }
        catch { return null; }
    }

    private static string Safe(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    // PowerShell 就地更新助手（脚本以 UTF-8 带 BOM 写出，可含中文）。日志写 %TEMP%\MacroPilotUpdate\update.log。
    // 自带一个置顶小窗显示进度，避免本体退出后到重启前的这段"什么都看不到"。
    // 要点：① Set-Location 到临时目录，别占住安装目录（否则改名报"目录正在使用中"）；② 改名带重试等句柄释放；
    // ③ finally 里【无论成败都把本体拉起来】——绝不让进程凭空消失。
    private const string ZipUpdateScript = @"
param([string]$Zip, [string]$Dir, [int]$AppPid)
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:TEMP 'MacroPilotUpdate'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Set-Location -LiteralPath $logDir
$log = Join-Path $logDir 'update.log'
function Log($m){ Add-Content -LiteralPath $log -Value ((Get-Date).ToString('HH:mm:ss.fff') + ' ' + $m) }
$exe = Join-Path $Dir 'MacroPilot.exe'
$leaf = Split-Path $Dir -Leaf

# ---- 进度小窗：本体已退出，这段时间必须让用户看见在干什么 ----
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object Windows.Forms.Form
$form.Text = '键鼠宏助手 · 正在更新'
$form.Size = New-Object Drawing.Size(460, 170)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false; $form.MinimizeBox = $false; $form.TopMost = $true
$lbl = New-Object Windows.Forms.Label
$lbl.SetBounds(20, 22, 410, 44); $lbl.Text = '准备中…'
$bar = New-Object Windows.Forms.ProgressBar
$bar.SetBounds(20, 74, 410, 18); $bar.Style = 'Marquee'; $bar.MarqueeAnimationSpeed = 30
$sub = New-Object Windows.Forms.Label
$sub.SetBounds(20, 100, 410, 30); $sub.ForeColor = [Drawing.Color]::Gray
$form.Controls.AddRange(@($lbl, $bar, $sub))
$form.Show(); [Windows.Forms.Application]::DoEvents()
function Step($t, $d) {
  $lbl.Text = $t; $sub.Text = $d
  [Windows.Forms.Application]::DoEvents()
  Log ""step: $t | $d""
}
$backup = ""$Dir.bak_"" + ([DateTimeOffset]::Now.ToUnixTimeSeconds())
$renamed = $false
Log ""=== start dir=$Dir pid=$AppPid ===""
try {
  Step '正在等待程序退出…' ""进程 $AppPid，最多等 20 秒""
  if ($AppPid -gt 0) {
    try { Wait-Process -Id $AppPid -Timeout 20 -ErrorAction SilentlyContinue } catch {}
    # 没退干净就强杀：本体若被模态框(如未保存询问)挂住，等多久都不会退，
    # 之前的表现就是「进程还在、目录被占、改名失败」。
    $p = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
    if ($p) {
      Step '程序未自行退出，正在强制结束…' ""进程 $AppPid""
      Log ""app pid=$AppPid still alive after wait -> force kill""
      try { Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue } catch {}
      try { Wait-Process -Id $AppPid -Timeout 10 -ErrorAction SilentlyContinue } catch {}
    }
  }
  # 同名残留进程（上一次更新失败留下的）也一并清掉，否则照样占住目录
  foreach ($q in @(Get-Process -Name 'MacroPilot' -ErrorAction SilentlyContinue)) {
    Log ""stray MacroPilot pid=$($q.Id) -> force kill""
    try { Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue } catch {}
  }
  for ($i=0; $i -lt 60; $i++) {
    try { $fs=[IO.File]::Open($exe,'Open','ReadWrite','None'); $fs.Close(); break } catch { Start-Sleep -Milliseconds 300 }
  }
  for ($i=0; $i -lt 20 -and (-not $renamed); $i++) {
    try { Rename-Item -LiteralPath $Dir -NewName (Split-Path $backup -Leaf) -Force; $renamed = $true }
    catch { Start-Sleep -Milliseconds 300 }
  }
  if (-not $renamed) {
    $still = @(Get-Process -Name 'MacroPilot' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ','
    throw ""backup rename failed: dir still in use (MacroPilot pids: [$still])""
  }
  Log ""backup -> $backup""
  Step '正在安装新版本…' '已备份旧版本，正在解压覆盖'
  New-Item -ItemType Directory -Force -Path $Dir | Out-Null
  Expand-Archive -LiteralPath $Zip -DestinationPath $Dir -Force
  $uninsSrc = Join-Path $backup 'unins'
  if (Test-Path -LiteralPath $uninsSrc) { Copy-Item -LiteralPath $uninsSrc -Destination (Join-Path $Dir 'unins') -Recurse -Force }
  Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
  Step '更新完成，正在重启…' ''
  Log 'success'
} catch {
  Log ""FAILED: $_ -> rollback""
  Step '更新失败，正在回滚到旧版本…' ""$_""
  $failed = $_.ToString()
  try { if ($renamed -and (Test-Path -LiteralPath $Dir)) { Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue } } catch {}
  if ($renamed -and (Test-Path -LiteralPath $backup)) { try { Rename-Item -LiteralPath $backup -NewName $leaf -Force; Log 'rolled back' } catch { Log ""rollback FAILED: $_"" } }
} finally {
  # 只有在没有存活实例时才拉起，避免拉起来的新实例撞单实例锁、变成占住目录的僵尸
  $alive = @(Get-Process -Name 'MacroPilot' -ErrorAction SilentlyContinue)
  if ($alive.Count -gt 0) { Log ""skip relaunch: MacroPilot already running ($($alive.Count))"" }
  elseif (Test-Path -LiteralPath $exe) { try { Start-Process -FilePath $exe -WorkingDirectory $Dir; Log 'relaunched' } catch { Log ""relaunch FAILED: $_"" } }
  try { Remove-Item -LiteralPath $Zip -Force -ErrorAction SilentlyContinue } catch {}
  Log '=== done ==='
  try { $form.Close() } catch {}
  if ($failed) {
    [Windows.Forms.MessageBox]::Show(""更新未成功，已回滚到原版本。`n`n原因：$failed`n`n日志：$log"", '键鼠宏助手 · 更新失败', 'OK', 'Warning') | Out-Null
  }
}
";
}
