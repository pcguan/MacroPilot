using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace MacroPilot.Services;

/// <summary>安装包预检及同卷暂存。只有清单登记的文件属于程序；其它文件完整复制保留。</summary>
internal static class InstallationFiles
{
    internal const string ManifestName = ".macropilot-files.json";
    // 0.6.22 以前没有清单；只认可已发布的明确程序路径，不把新包任意同名文件当作旧程序。
    internal static readonly HashSet<string> LegacyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "MacroPilot.exe", "MacroPilot.dll", "MacroPilot.deps.json", "MacroPilot.runtimeconfig.json",
        "SharpGen.Runtime.COM.dll", "SharpGen.Runtime.dll", "System.IO.Ports.dll",
        "Vortice.Direct3D11.dll", "Vortice.DirectX.dll", "Vortice.DXGI.dll", "Vortice.Mathematics.dll",
        "Wpf.Ui.Abstractions.dll", "Wpf.Ui.dll",
        "runtimes/linux-arm/native/libSystem.IO.Ports.Native.so", "runtimes/linux-arm64/native/libSystem.IO.Ports.Native.so",
        "runtimes/linux-x64/native/libSystem.IO.Ports.Native.so", "runtimes/osx-arm64/native/libSystem.IO.Ports.Native.dylib",
        "runtimes/osx-x64/native/libSystem.IO.Ports.Native.dylib", "runtimes/unix/lib/net8.0/System.IO.Ports.dll",
        "runtimes/win/lib/net8.0/System.IO.Ports.dll", "unins/MacroPilotUninstaller.exe", "unins/uninstall.vbs"
    };

    // 手工安装允许暂不启动；首次主窗口初始化成功后再处理安装器留下的备份。
    internal static void FinalizePendingBackup(string install)
    {
        var pending = Path.Combine(install, ".macropilot-pending.json");
        if (!File.Exists(pending)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(pending));
        var token = json.RootElement.GetProperty("token").GetString();
        var backup = json.RootElement.GetProperty("backup").GetString();
        if (!Guid.TryParseExact(token, "N", out _) ||
            !string.Equals(backup, Path.TrimEndingDirectorySeparator(install) + ".bak_" + token, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装备份路径无效，已保留文件。");
        if (Directory.Exists(backup))
        {
            var marker = Path.Combine(backup, ".macropilot-backup-token");
            if (!File.Exists(marker) || File.ReadAllText(marker) != token)
                throw new InvalidDataException("安装备份标识不匹配，已保留文件。");
        }
        // 单独启动（含首次安装无备份）也留下可核验的确认，安装器不能把第二次单实例退出当作失败。
        File.WriteAllText(Path.Combine(install, ".macropilot-started.json"), JsonSerializer.Serialize(new { token, pid = Environment.ProcessId }));
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        File.Delete(pending);
    }

    internal static string ResolveChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(s => s == "." || s == ".."))
            throw new InvalidDataException("安装包包含非法路径。");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包路径越界。");
        return full;
    }

    internal static void ValidateTarget(string target)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Path.IsPathFullyQualified(target) || string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装位置必须是独立的应用文件夹，不能是磁盘根目录。");
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(path) && full.Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能把系统或用户根目录作为安装位置。");
            if (special == Environment.SpecialFolder.Windows && !string.IsNullOrEmpty(path) &&
                full.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能把 Windows 系统目录作为安装位置。");
        }
        for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装路径不能经过符号链接或目录联接。");
    }

    internal static HashSet<string> ReadOwned(string directory)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) return result; // 旧安装无清单：保守保留未知旧文件。
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("安装文件清单版本无效。");
        foreach (var entry in json.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetString() ?? throw new InvalidDataException("安装文件清单无效。");
            ResolveChild(directory, relative);
            result.Add(relative.Replace('\\', '/'));
        }
        return result;
    }

    internal static void Prepare(string zip, string install, string stage)
    {
        ValidateTarget(install);
        if (Directory.Exists(stage)) throw new IOException("更新暂存目录已存在。");
        using var archive = ZipFile.OpenRead(zip);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ResolveChild(stage, entry.FullName);
            var name = entry.FullName.Replace('\\', '/');
            if (!incoming.Add(name) || name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包含重复文件或保留清单路径。");
        }
        foreach (var required in new[] { "MacroPilot.exe", "MacroPilot.dll", "MacroPilot.deps.json", "MacroPilot.runtimeconfig.json" })
            if (!incoming.Contains(required)) throw new InvalidDataException("更新包缺少 " + required);
        var oldOwned = ReadOwned(install);
        var previousFiles = File.Exists(Path.Combine(install, ManifestName)) ? oldOwned : LegacyFiles;
        foreach (var relative in incoming)
        {
            var existing = ResolveChild(install, relative);
            if (Directory.Exists(existing) || (File.Exists(existing) && !previousFiles.Contains(relative)))
                throw new IOException("新版本文件与未登记的用户文件同名，已保留原安装。请先移动该文件：" + relative);
        }
        try
        {
            Directory.CreateDirectory(stage);
            if (Directory.Exists(install)) CopyTree(install, stage);
            // 仅删除清单确认的旧程序文件；保留卸载器及所有未知用户文件/旧数据。
            foreach (var relative in oldOwned)
                if (!incoming.Contains(relative) && !relative.StartsWith("unins/", StringComparison.OrdinalIgnoreCase))
                    File.Delete(ResolveChild(stage, relative));
            foreach (var entry in entries)
            {
                var target = ResolveChild(stage, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            foreach (var old in oldOwned.Where(s => s.StartsWith("unins/", StringComparison.OrdinalIgnoreCase))) incoming.Add(old);
            foreach (var legacy in new[] { "unins/MacroPilotUninstaller.exe", "unins/uninstall.vbs" })
                if (File.Exists(ResolveChild(stage, legacy))) incoming.Add(legacy);
            File.WriteAllText(Path.Combine(stage, ManifestName), JsonSerializer.Serialize(new { schema = 1, files = incoming.OrderBy(s => s).ToArray() }));
        }
        catch { try { Directory.Delete(stage, recursive: true); } catch { } throw; }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装目录含符号链接或目录联接，已保留原安装并停止更新：" + entry.Name);
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo) CopyTree(entry.FullName, destination);
            else File.Copy(entry.FullName, destination, overwrite: false);
        }
    }
}
