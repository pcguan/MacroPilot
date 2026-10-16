using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>
/// 文档持久化。数据默认放 %AppData%\MacroPilot；用户可改到自定义目录。
/// 自定义目录路径记录在【固定的默认目录】下的指针文件 datapath.txt（不能存进 plans.json 自身——它就在数据目录里）。
/// 首次运行若本版无数据、但参考版 CH9329MacroClient 有，则【只读】导入一份对照（原文件不动）。
/// </summary>
public static class Storage
{
    // 固定默认目录（也是指针文件所在），永远可定位。
    public static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPilot");
    private static readonly string PointerFile = Path.Combine(DefaultDir, "datapath.txt");

    private static readonly string RefPlansPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CH9329MacroClient", "plans.json");

    private static string? _cachedDir;
    private static readonly Dictionary<string, string> _blockedWrites = new(StringComparer.OrdinalIgnoreCase);
    public static string? LastLoadWarning { get; private set; }
    public static bool CanSavePlans => !_blockedWrites.ContainsKey(Path.GetFullPath(PlansPath));
    public static bool CanSaveSettings => !_blockedWrites.ContainsKey(Path.GetFullPath(SettingsPath));

    /// <summary>当前生效的数据目录（默认目录或用户自定义目录）。</summary>
    public static string DataDir
    {
        get
        {
            if (_cachedDir != null) return _cachedDir;
            try
            {
                if (File.Exists(PointerFile))
                {
                    var p = File.ReadAllText(PointerFile).Trim();
                    if (!string.IsNullOrWhiteSpace(p)) { _cachedDir = p; return p; }
                }
            }
            catch { }
            _cachedDir = DefaultDir;
            return DefaultDir;
        }
    }

    public static string PlansPath => Path.Combine(DataDir, "plans.json");
    // 设置单独存一份小文件：改下拉框/开关只写它，不再把含 base64 截图的整份方案重写一遍。
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
    // 鼠标轨迹录制（拟人化调优用）的 CSV 落此目录。
    public static string TraceDir => Path.Combine(DataDir, "traces");
    public static bool IsCustomDir =>
        !string.Equals(Path.GetFullPath(DataDir), Path.GetFullPath(DefaultDir), StringComparison.OrdinalIgnoreCase);

    // MaxDepth 放大到 256：嵌套组合 + 递归监听动作会让 JSON 层级超过默认 64 而序列化/反序列化抛异常。
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        MaxDepth = 256,
    };

    private static MacroDocument? ReadDoc(string path, out bool missing)
    {
        missing = false;
        try
        {
            var doc = JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(path), Opts);
            if (doc == null || doc.Plans == null || doc.Windows == null)
                throw new JsonException("存档结构不完整。");
            return doc;
        }
        catch (FileNotFoundException) { missing = true; return null; }
    }

    private static void Warn(string text) => LastLoadWarning =
        string.IsNullOrEmpty(LastLoadWarning) ? text : LastLoadWarning + "\n" + text;

    private static MacroDocument? ReadRecoverable(string path, out bool missing)
    {
        var key = Path.GetFullPath(path);
        _blockedWrites.Remove(key);
        missing = false;
        Exception? failure = null;
        try
        {
            var doc = ReadDoc(path, out missing);
            if (doc != null) return doc;
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            var backup = ReadDoc(path + ".bak", out var backupMissing);
            if (backup != null)
            {
                // 恢复时不能用坏主文件覆盖唯一的有效备份；坏文件另行留存供排查。
                var temp = path + ".recover_" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(path + ".bak", temp);
                    if (File.Exists(path))
                        File.Replace(temp, path, path + ".corrupt_" + Guid.NewGuid().ToString("N"));
                    else File.Move(temp, path);
                    Warn($"已从备份恢复 {Path.GetFileName(path)}。原始损坏文件已保留。");
                }
                catch (Exception ex)
                {
                    _blockedWrites[key] = ex.Message;
                    Warn($"已读取 {Path.GetFileName(path)} 的备份，但无法恢复文件；已禁止覆盖保存：{ex.Message}");
                }
                finally { try { File.Delete(temp); } catch { } }
                missing = false;
                return backup;
            }
            if (failure == null && missing && backupMissing) return null;
        }
        catch (Exception ex) { failure ??= ex; }
        missing = false; // 读取失败绝不能触发“首次导入参考版”或当作新存档。
        _blockedWrites[key] = failure?.Message ?? "无法读取存档及备份";
        Warn($"无法读取 {path} 及有效备份，已禁止覆盖保存。请恢复文件或切换数据目录。{failure?.Message}");
        return null;
    }

    private static MacroDocument LoadCore(bool importReference)
    {
        LastLoadWarning = null;
        // 默认目录首次运行可创建；自定义目录暂时离线时不能当作空存档。
        if (!Directory.Exists(DataDir) && !IsCustomDir)
            try { Directory.CreateDirectory(DataDir); } catch { /* 下方读取报告错误并阻止写入 */ }
        var plans = ReadRecoverable(PlansPath, out var missing);
        if (importReference && missing)
        {
            try { plans = ReadDoc(RefPlansPath, out _); } catch { }
        }
        return Merge(ReadRecoverable(SettingsPath, out _), plans);
    }

    // 合并：设置以 settings.json 为准；没有它则退回 plansDoc 里的设置（旧单文件格式自动兼容）。方案永远取自 plansDoc。
    private static MacroDocument Merge(MacroDocument? settings, MacroDocument? plansDoc)
    {
        var doc = settings ?? plansDoc ?? new MacroDocument();
        doc.Plans = plansDoc?.Plans ?? new List<MacroPlan>();
        return Migrate(doc);
    }

    public static MacroDocument Load()
    {
        // 方案（含 base64 截图）来自 plans.json；首次运行本版无数据时【只读】导入参考版一份对照。
        return LoadCore(importReference: true);
    }

    // 加载后一次性迁移：旧默认按住时长 50ms 对部分应用易"吞键"，抬到 75ms 安全阈值（仅动这个老默认值，用户自定义的其它值不动）。
    private static MacroDocument Migrate(MacroDocument doc)
    {
        if (doc.DefaultHoldMs == 50) doc.DefaultHoldMs = 75;
        // 0.0.18 只记了主窗口，字段是平铺的；0.0.20 起所有窗口统一放进 Windows 字典，把老记录搬过去。
        if (doc.WindowWidth > 0 && doc.WindowHeight > 0 && !doc.Windows.ContainsKey("Main"))
        {
            doc.Windows["Main"] = new WindowGeometry
            {
                Left = doc.WindowLeft, Top = doc.WindowTop,
                Width = doc.WindowWidth, Height = doc.WindowHeight, Maximized = doc.WindowMaximized,
            };
        }
        doc.WindowLeft = doc.WindowTop = doc.WindowWidth = doc.WindowHeight = 0; doc.WindowMaximized = false;
        return doc;
    }

    /// <summary>只读取当前数据目录（不做参考版首次导入）；无文件返回空文档。用于切换数据目录后重新加载。</summary>
    public static MacroDocument LoadDirect() => LoadCore(importReference: false);

    // Sweep 必须同时保护当前存档和上一份可恢复备份；任一文件读不明白就暂停回收。
    internal static bool CollectPersistedImageHashes(HashSet<string> hashes)
    {
        foreach (var path in new[] { PlansPath, PlansPath + ".bak" })
        {
            try
            {
                var doc = ReadDoc(path, out _);
                if (doc != null) foreach (var plan in doc.Plans) ImageStore.CollectHashes(plan, hashes);
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>原子写：先写 .tmp，再 File.Replace 换正式文件并留 .bak 备份（写一半崩溃/断电不损坏原文件）。</summary>
    private static bool WriteAtomic(string path, string json)
    {
        if (_blockedWrites.ContainsKey(Path.GetFullPath(path))) return false;
        var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
            return true;
        }
        catch { return false; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>保存方案（含截图）到 plans.json。仅在显式"保存方案"时调用，不随设置变更频繁触发。</summary>
    public static bool Save(MacroDocument doc) => WriteAtomic(PlansPath, JsonSerializer.Serialize(doc, Opts));

    private static readonly List<MacroPlan> _noPlans = new();
    /// <summary>只保存设置到 settings.json（不含方案）——改主题/串口/桥片/开关等走这里，避免重写整份方案。</summary>
    public static bool SaveSettings(MacroDocument doc)
    {
        var plans = doc.Plans;
        try { doc.Plans = _noPlans; return WriteAtomic(SettingsPath, JsonSerializer.Serialize(doc, Opts)); }
        finally { doc.Plans = plans; }
    }

    /// <summary>设置数据目录。传 null/空 或默认目录本身 → 恢复默认（删除指针）。返回是否成功。</summary>
    public static bool SetDataDir(string? dir, out string error)
    {
        error = "";
        try
        {
            if (!ValidateDataDirectory(string.IsNullOrWhiteSpace(dir) ? DefaultDir : dir, out error)) return false;
            Directory.CreateDirectory(DefaultDir);
            if (string.IsNullOrWhiteSpace(dir) ||
                string.Equals(Path.GetFullPath(dir), Path.GetFullPath(DefaultDir), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(PointerFile)) File.Delete(PointerFile);
            }
            else
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(PointerFile, dir.Trim());
            }
            _cachedDir = null; // 失效缓存，下次重新计算
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static bool ValidateDataDirectory(string dir, out string error)
    {
        error = "";
        try
        {
            if (!Path.IsPathFullyQualified(dir)) throw new IOException("请选择绝对路径的数据目录。");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (full.Equals(install, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("数据目录不能放在程序安装目录内，请选择独立的数据文件夹。");
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>把当前数据目录下的 plans.json 与 logs 复制到新目录（不删除原数据）。</summary>
    public static bool MigrateTo(string newDir, out string error)
    {
        error = "";
        try
        {
            if (!ValidateDataDirectory(newDir, out error)) return false;
            if (!CanSavePlans || !CanSaveSettings) throw new IOException("当前存档未能恢复，不能迁移损坏数据。");
            var oldDir = DataDir;
            if (string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
                return true; // 同目录，无需迁移
            Directory.CreateDirectory(newDir);
            var oldPlans = Path.Combine(oldDir, "plans.json");
            if (File.Exists(oldPlans)) File.Copy(oldPlans, Path.Combine(newDir, "plans.json"), true);
            var oldSettings = Path.Combine(oldDir, "settings.json");
            if (File.Exists(oldSettings)) File.Copy(oldSettings, Path.Combine(newDir, "settings.json"), true);
            foreach (var name in new[] { "plans.json.bak", "settings.json.bak" })
                if (File.Exists(Path.Combine(oldDir, name))) File.Copy(Path.Combine(oldDir, name), Path.Combine(newDir, name), true);
            var oldImages = Path.Combine(oldDir, "images");
            if (Directory.Exists(oldImages))
            {
                var newImages = Path.Combine(newDir, "images");
                Directory.CreateDirectory(newImages);
                foreach (var f in Directory.GetFiles(oldImages))
                    File.Copy(f, Path.Combine(newImages, Path.GetFileName(f)), true);
            }
            var oldLogs = Path.Combine(oldDir, "logs");
            if (Directory.Exists(oldLogs))
            {
                var newLogs = Path.Combine(newDir, "logs");
                Directory.CreateDirectory(newLogs);
                foreach (var f in Directory.GetFiles(oldLogs))
                    File.Copy(f, Path.Combine(newLogs, Path.GetFileName(f)), true);
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // 写入一行原始日志（调用方已含时间戳与状态，磁盘格式与界面一致）。
    public static void AppendRunLog(string line)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var f = Path.Combine(LogDir, $"run-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(f, line + Environment.NewLine);
        }
        catch { }
    }
}
