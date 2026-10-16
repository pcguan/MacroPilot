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
    public string? Sha256 { get; init; }   // 清单必须提供 64 位十六进制 SHA-256；解析和下载入口都会校验
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
        var info = await CheckSourcesAsync(async (url, token) =>
        {
            using var c = NewClient(TimeSpan.FromSeconds(10));
            return await c.GetStringAsync(url, token);
        }, ct);
        if (info != null) _cached = info;
        return _cached;
    }

    // 注入读取动作便于离线验证坏清单/网络失败的换源；缓存只在公开入口维护。
    internal static async Task<UpdateInfo?> CheckSourcesAsync(
        Func<string, CancellationToken, Task<string>> readManifest, CancellationToken ct = default)
    {
        for (int i = 0; i < Sources.Length; i++)
        {
            try
            {
                var json = await readManifest(Sources[i] + ManifestName, ct);
                var info = ParseManifest(json, i);
                if (info != null) return info;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 该源不通 → 试下一个 */ }
        }
        return null;
    }

    // 解析清单；bases 以命中的源打头，其余按原顺序跟上（下载时可继续回退）。
    internal static UpdateInfo? ParseManifest(string json, int hitIndex)
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
                if (!r.TryGetProperty(key, out var e)) return null;
                if (e.ValueKind != JsonValueKind.Object) throw new JsonException("更新文件信息格式错误");
                var n = e.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                var hash = e.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString() : null;
                if (string.IsNullOrWhiteSpace(n) || !HasValidSha256(hash))
                    throw new JsonException("更新文件缺少名称或有效的 SHA-256");
                return new UpdateAsset
                {
                    Name = n!,
                    Size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
                    Sha256 = hash,
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
    public static Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
        => DownloadAsync(info, progress, DownloadOne, Path.Combine(Path.GetTempPath(), "MacroPilotUpdate"), ct);

    internal static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
        Func<string, string, long, IProgress<double>?, CancellationToken, Task> download,
        string directory, CancellationToken ct = default)
    {
        bool useZip = info.Zip != null;
        var asset = useZip ? info.Zip : info.Exe;
        if (asset == null || !HasValidSha256(asset.Sha256))
            throw new InvalidOperationException("更新清单缺少有效的 SHA-256，已拒绝下载");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"MacroPilot_{Safe(info.VersionText)}.{(useZip ? "zip" : "exe")}");

        Exception? last = null;
        foreach (var b in info.Bases)
        {
            try
            {
                await download(b + asset.Name, file, asset.Size, progress, ct);
                if (VerifyDownload(file, asset.Sha256)) return file;
                last = new InvalidOperationException("下载内容校验失败（SHA-256 不匹配），已换源重试");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { File.Delete(file); } catch { }
                throw;
            }
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

    private static bool HasValidSha256(string? hash)
    {
        if (hash == null || hash.Length != 64) return false;
        foreach (var c in hash)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    /// <summary>SHA-256 是必填校验项；缺失、格式错误或内容不匹配一律拒绝。</summary>
    internal static bool VerifyDownload(string file, string? expect)
    {
        if (!HasValidSha256(expect)) return false;
        try
        {
            using var fs = File.OpenRead(file);
            var hex = Convert.ToHexString(SHA256.HashData(fs));
            return string.Equals(hex, expect, StringComparison.OrdinalIgnoreCase);
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
        var installDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var transaction = Guid.NewGuid().ToString("N");
        var stage = installDir + ".stage_" + transaction;
        // 任何解压/校验/复制错误都发生在退出前，原安装始终完整。
        InstallationFiles.Prepare(zipPath, installDir, stage);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate");
            Directory.CreateDirectory(dir);
            var ps1 = Path.Combine(dir, "apply_" + transaction + ".ps1");
            var ready = Path.Combine(dir, transaction + ".ready");
            File.WriteAllText(ps1, UpdateTransactionScript.Text, new UTF8Encoding(true));
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = dir,
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1,
                         "-Stage", stage, "-Dir", installDir, "-AppPid", Environment.ProcessId.ToString(),
                         "-Ready", ready, "-Token", transaction }) psi.ArgumentList.Add(argument);
            _ = System.Diagnostics.Process.Start(psi) ?? throw new IOException("无法启动更新助手。");
            System.Windows.Application.Current?.Shutdown();
        }
        catch { try { Directory.Delete(stage, recursive: true); } catch { } throw; }
    }

    /// <summary>主窗口初始化成功后确认启动；更新助手收到 PID 与随机令牌才删除旧版备份。</summary>
    public static void ConfirmSuccessfulStartup()
    {
        bool requested = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MACROPILOT_UPDATE_READY")) ||
            Array.Exists(Environment.GetCommandLineArgs(), value => value == "--update-ready" || value == "--update-token");
        var context = StartupContext();
        Environment.SetEnvironmentVariable("MACROPILOT_UPDATE_READY", null);
        Environment.SetEnvironmentVariable("MACROPILOT_UPDATE_TOKEN", null);
        if (context == null)
        {
            if (requested) return; // 握手参数无效必须失败保留备份，不能误当普通首次启动完成安装。
            _ = Task.Run(() => { try { InstallationFiles.FinalizePendingBackup(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)); } catch { } });
            return;
        }
        try { WriteStartupSignal(context.Value.Path, context.Value.Token + ":" + Environment.ProcessId); }
        catch { /* 助手无法收到确认时保留旧版并回滚；不能令已初始化的窗口崩溃。 */ }
    }

    private static (string Path, string Token)? StartupContext()
    {
        var ready = Environment.GetEnvironmentVariable("MACROPILOT_UPDATE_READY");
        var token = Environment.GetEnvironmentVariable("MACROPILOT_UPDATE_TOKEN");
        var args = Environment.GetCommandLineArgs();
        int parentPid = 0;
        for (var i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] == "--update-ready") ready = args[++i];
            else if (args[i] == "--update-token") token = args[++i];
            else if (args[i] == "--update-parent") int.TryParse(args[++i], out parentPid);
        }
        try
        {
            var expected = Path.Combine(Path.GetTempPath(), "MacroPilotUpdate", token + ".ready");
            if (!Guid.TryParseExact(token, "N", out _) || string.IsNullOrEmpty(ready) || !Path.IsPathFullyQualified(ready)) return null;
            if (Path.GetFullPath(ready).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) return (ready, token!);
            // UAC 可使用另一管理员账户，TEMP 因而变化；只接受父进程明确签出的交接路径。
            if (parentPid > 0 && Path.GetFileName(ready) == token + ".ready" &&
                Path.GetFileName(Path.GetDirectoryName(ready)) == "MacroPilotUpdate")
            {
                var proof = ParseStartupSignal(File.ReadAllText(ready + ".handoff"), token!, parentPid);
                if (proof.HasValue && proof.Value.Handoff && proof.Value.Pid == Environment.ProcessId) return (ready, token!);
            }
        }
        catch { }
        return null;
    }

    public static void ConfigureStartupHandoff(System.Diagnostics.ProcessStartInfo start)
    {
        var context = StartupContext();
        if (context == null) return;
        start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(context.Value.Path);
        start.ArgumentList.Add("--update-token"); start.ArgumentList.Add(context.Value.Token);
        start.ArgumentList.Add("--update-parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
    }

    public static bool RecordStartupHandoff(int childPid)
    {
        var context = StartupContext();
        if (context == null) return true;
        if (childPid <= 0 || childPid == Environment.ProcessId) return false;
        try
        {
            WriteStartupSignal(context.Value.Path + ".handoff", $"{context.Value.Token}:handoff:{Environment.ProcessId}:{childPid}");
            return true;
        }
        catch { return false; } // 子进程已创建，父必须继续退出释放 mutex。
    }

    private static void WriteStartupSignal(string path, string value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, value, new UTF8Encoding(false)); File.Move(temp, path, overwrite: true); }
        finally { try { File.Delete(temp); } catch { } }
    }

    internal static (bool Handoff, int Pid)? ParseStartupSignal(string text, string token, int expectedPid)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length < 2 || parts[0] != token) return null;
        if (parts.Length == 2 && int.TryParse(parts[1], out int readyPid) && readyPid == expectedPid && readyPid > 0)
            return (false, readyPid);
        if (parts.Length == 4 && parts[1] == "handoff" && int.TryParse(parts[2], out int parentPid) &&
            parentPid == expectedPid && int.TryParse(parts[3], out int childPid) && childPid > 0 && childPid != parentPid)
            return (true, childPid);
        return null;
    }

    /// <summary>兜底：触发安装器静默就地更新（拿到的是 exe 时用），随即退出本程序。</summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var psi = new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = false };
        psi.EnvironmentVariables["MACROPILOT_UPDATE"] = "1";
        psi.EnvironmentVariables["MACROPILOT_UPDATE_TARGET"] = installDir;
        _ = System.Diagnostics.Process.Start(psi) ?? throw new IOException("无法启动安装器。");
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

}
