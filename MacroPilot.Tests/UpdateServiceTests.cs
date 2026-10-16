using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>只使用临时文件和注入的读取/下载动作，验证更新完整性与换源，不访问网络。</summary>
public sealed class UpdateServiceTests : IDisposable
{
    // SHA-256("abc")：固定已知向量，避免测试用被测算法生成期望值。
    private const string Hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MacroPilotUpdateTests_" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static IEnumerable<object?[]> InvalidHashes()
    {
        yield return new object?[] { null };
        yield return new object?[] { "" };
        yield return new object?[] { " " };
        yield return new object?[] { Hash[..63] };
        yield return new object?[] { Hash + "0" };
        yield return new object?[] { new string('g', 64) };
        yield return new object?[] { new string('０', 64) }; // 只能使用 ASCII 十六进制
        yield return new object?[] { " " + Hash };
        yield return new object?[] { Hash + "\n" };
    }

    [Theory]
    [MemberData(nameof(InvalidHashes))]
    public void 缺失或畸形哈希不能通过文件校验(string? hash)
    {
        var file = Path.Combine(_directory, "payload.zip");
        File.WriteAllText(file, "abc");

        Assert.False(UpdateService.VerifyDownload(file, hash));
    }

    [Theory]
    [InlineData(Hash)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("Ba7816bF8f01CfEa414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void 正确哈希允许大小写并校验真实内容(string hash)
    {
        var file = Path.Combine(_directory, "payload.zip");
        File.WriteAllText(file, "abc");

        Assert.True(UpdateService.VerifyDownload(file, hash));
        File.WriteAllText(file, "abd");
        Assert.False(UpdateService.VerifyDownload(file, hash));
    }

    [Fact]
    public void 文件不存在时校验失败()
        => Assert.False(UpdateService.VerifyDownload(Path.Combine(_directory, "missing.zip"), Hash));

    [Theory]
    [MemberData(nameof(InvalidHashes))]
    public void 清单拒绝缺失或畸形资产哈希(string? hash)
    {
        Assert.Null(UpdateService.ParseManifest(Manifest(hash, "zip"), 0));
        Assert.Null(UpdateService.ParseManifest(Manifest(hash, "exe"), 0));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("exe")]
    public void 清单资产没有哈希字段时拒绝(string kind)
        => Assert.Null(UpdateService.ParseManifest(Manifest(null, kind, omitHash: true), 0));

    [Theory]
    [InlineData(123)]
    [InlineData(true)]
    public void 清单拒绝非字符串哈希(object hash)
        => Assert.Null(UpdateService.ParseManifest(Manifest(hash), 0));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"0.6.22\"}")]
    [InlineData("{\"version\":\"0.6.22\",\"zip\":null}")]
    public void 清单无有效资产或格式错误时拒绝(string json)
        => Assert.Null(UpdateService.ParseManifest(json, 0));

    [Theory]
    [InlineData("zip")]
    [InlineData("exe")]
    public void 清单接受带合法哈希的资产(string kind)
    {
        var info = UpdateService.ParseManifest(Manifest(Hash.ToUpperInvariant(), kind), 0);

        Assert.NotNull(info);
        var asset = kind == "zip" ? info.Zip : info.Exe;
        Assert.NotNull(asset);
        Assert.Equal(Hash.ToUpperInvariant(), asset.Sha256);
        Assert.Equal(3, asset.Size);
    }

    [Theory]
    [InlineData("zip", "exe")]
    [InlineData("exe", "zip")]
    public void 有效资产不能掩盖同清单另一资产的无哈希问题(string invalidKind, string validKind)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "0.6.22",
            [invalidKind] = new { name = "unsigned.bin" },
            [validKind] = new { name = "signed.bin", sha256 = Hash },
        });

        Assert.Null(UpdateService.ParseManifest(json, 0));
    }

    [Fact]
    public async Task 坏清单自动换源并优先从有效清单来源下载()
    {
        var requests = new List<string>();
        var info = await UpdateService.CheckSourcesAsync((url, _) =>
        {
            requests.Add(url);
            return Task.FromResult(requests.Count == 1 ? Manifest(null, omitHash: true) : Manifest(Hash));
        });

        Assert.NotNull(info);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[1], info.Bases[0] + "version.json");
        Assert.Equal(requests[0], info.Bases[1] + "version.json");
    }

    [Fact]
    public async Task 所有源均为无哈希清单时没有可用更新()
    {
        var requests = 0;
        var info = await UpdateService.CheckSourcesAsync((_, _) =>
        {
            requests++;
            return Task.FromResult(Manifest(null, omitHash: true));
        });

        Assert.Null(info);
        Assert.Equal(2, requests);
    }

    [Theory]
    [MemberData(nameof(InvalidHashes))]
    public async Task 绕过清单解析也不能下载无有效哈希的资产(string? hash)
    {
        var calls = 0;
        var directory = Path.Combine(_directory, "download");
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(
            Info(hash), null, (_, _, _, _, _) =>
            {
                calls++;
                return Task.CompletedTask;
            }, directory));

        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task 首源内容不匹配时先删档再换源并返回正确文件()
    {
        var requests = new List<string>();
        var info = Info(Hash);
        var file = await UpdateService.DownloadAsync(info, null, (url, path, _, _, _) =>
        {
            Assert.False(File.Exists(path));
            requests.Add(url);
            File.WriteAllText(path, requests.Count == 1 ? "corrupted" : "abc");
            return Task.CompletedTask;
        }, _directory);

        Assert.Equal(new[] { "https://first.invalid/app.zip", "https://second.invalid/app.zip" }, requests);
        Assert.Equal("abc", File.ReadAllText(file));
    }

    [Fact]
    public async Task 所有源内容不匹配时拒绝更新并清理下载文件()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(
            Info(Hash), null, (_, path, _, _, _) =>
            {
                Assert.False(File.Exists(path));
                calls++;
                File.WriteAllText(path, "corrupted");
                return Task.CompletedTask;
            }, _directory));

        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private static string Manifest(object? hash, string kind = "zip", bool omitHash = false)
    {
        var asset = new Dictionary<string, object?> { ["name"] = "app." + kind, ["size"] = 3 };
        if (!omitHash) asset["sha256"] = hash;
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["version"] = "0.6.22", [kind] = asset });
    }

    private static UpdateInfo Info(string? hash) => new()
    {
        Version = new Version(0, 6, 22),
        VersionText = "0.6.22",
        Zip = new UpdateAsset { Name = "app.zip", Size = 3, Sha256 = hash },
        Bases = new[] { "https://first.invalid/", "https://second.invalid/" },
    };
}
