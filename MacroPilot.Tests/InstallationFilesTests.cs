using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public sealed class InstallationFilesTests
{
    [Fact]
    public void PendingBackupCleanupRequiresExactPathAndMatchingTransactionMarker() => WithDirectory(root =>
    {
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        var token = Guid.NewGuid().ToString("N");
        var backup = install + ".bak_" + token; Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "personal.txt"), "user file");
        File.WriteAllText(Path.Combine(install, ".macropilot-pending.json"), JsonSerializer.Serialize(new { token, backup }));
        Assert.Throws<InvalidDataException>(() => InstallationFiles.FinalizePendingBackup(install));
        Assert.True(Directory.Exists(backup));
        File.WriteAllText(Path.Combine(backup, ".macropilot-backup-token"), token);
        InstallationFiles.FinalizePendingBackup(install);
        Assert.False(Directory.Exists(backup));
    });

    [Fact]
    public void UpdateStageKeepsUserFilesAndDataButRemovesOwnedObsoleteFiles() => WithDirectory(root =>
    {
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(install, "data"));
        File.WriteAllText(Path.Combine(install, "data", "plans.json"), "valuable plans");
        File.WriteAllText(Path.Combine(install, "personal.txt"), "personal");
        File.WriteAllText(Path.Combine(install, "old.dll"), "obsolete");
        File.WriteAllText(Path.Combine(install, InstallationFiles.ManifestName), JsonSerializer.Serialize(new {schema=1,files=new[]{"old.dll"}}));
        var zip = CreatePackage(root);
        var stage = Path.Combine(root, "stage");
        InstallationFiles.Prepare(zip, install, stage);
        Assert.Equal("valuable plans", File.ReadAllText(Path.Combine(stage, "data", "plans.json")));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(stage, "personal.txt")));
        Assert.False(File.Exists(Path.Combine(stage, "old.dll")));
        Assert.True(File.Exists(Path.Combine(install, "old.dll"))); // 提交前原安装完全不变
        Assert.DoesNotContain("personal.txt", InstallationFiles.ReadOwned(stage));
    });

    [Fact]
    public void BrokenPackageDoesNotTouchOriginalInstall() => WithDirectory(root =>
    {
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        var original = Path.Combine(install, "MacroPilot.exe"); File.WriteAllText(original, "original");
        var zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("only.txt");
        Assert.Throws<InvalidDataException>(() => InstallationFiles.Prepare(zip, install, Path.Combine(root,"stage")));
        Assert.Equal("original", File.ReadAllText(original));
        Assert.False(Directory.Exists(Path.Combine(root,"stage")));
    });

    [Fact]
    public void NewPackageCannotOverwriteUnknownSameNameUserFile() => WithDirectory(root =>
    {
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "notes.txt"), "user notes");
        var zip = CreatePackage(root, "notes.txt");
        Assert.Throws<IOException>(() => InstallationFiles.Prepare(zip, install, Path.Combine(root, "stage")));
        Assert.Equal("user notes", File.ReadAllText(Path.Combine(install, "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "stage")));
    });

    [Fact]
    public void ArchiveTraversalIsRejectedBeforeWriting() => WithDirectory(root =>
    {
        var zip = CreatePackage(root, "../escape.txt");
        Assert.Throws<InvalidDataException>(() => InstallationFiles.Prepare(zip, Path.Combine(root,"app"),Path.Combine(root,"stage")));
        Assert.False(File.Exists(Path.Combine(root,"escape.txt")));
    });

    [Fact]
    public void InvalidOwnershipManifestCannotDeleteOutsideInstall() => WithDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, InstallationFiles.ManifestName), "{\"schema\":1,\"files\":[\"../personal.txt\"]}");
        Assert.Throws<InvalidDataException>(() => InstallationFiles.ReadOwned(root));
    });

    private static string CreatePackage(string root,string? extra=null)
    {
        var zip=Path.Combine(root,"app.zip");
        using var archive=ZipFile.Open(zip,ZipArchiveMode.Create);
        foreach(var name in new[]{"MacroPilot.exe","MacroPilot.dll","MacroPilot.deps.json","MacroPilot.runtimeconfig.json"})
        { using var writer=new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("package"); }
        if(extra!=null) archive.CreateEntry(extra);
        return zip;
    }
    private static void WithDirectory(Action<string> action)
    {
        var dir=Path.Combine(Path.GetTempPath(),"MacroPilotInstallTest_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {action(dir);} finally {Directory.Delete(dir,true);}
    }
}
