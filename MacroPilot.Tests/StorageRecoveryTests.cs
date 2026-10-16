using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

[CollectionDefinition("StorageIsolation", DisableParallelization = true)]
public sealed class StorageIsolationCollection { }

[Collection("StorageIsolation")]
public sealed class StorageRecoveryTests
{
    [Fact]
    public void ExistingTruncatedImageIsRepairedWithoutLosingOriginalBytes() => WithStorage(dir =>
    {
        byte[] original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1sAAAAASUVORK5CYII=");
        var hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        Directory.CreateDirectory(ImageStore.ImagesDir);
        File.WriteAllBytes(Path.Combine(ImageStore.ImagesDir, hash + ".png"), original[..8]);
        Assert.Equal(original, ImageStore.Bytes(ImageStore.Ref(original)));
        Assert.Empty(Directory.GetFiles(ImageStore.ImagesDir, "*.tmp_*"));
    });

    [Fact]
    public void CorruptPrimaryRecoversBackupAndKeepsItValidAfterSave() => WithStorage(dir =>
    {
        var backup = new MacroDocument();
        backup.Plans.Add(new MacroPlan { Name = "备份中的方案", LoopCount = 7 });
        File.WriteAllText(Storage.PlansPath + ".bak", JsonSerializer.Serialize(backup));
        File.WriteAllText(Storage.PlansPath, "{ corrupt primary");
        var loaded = Storage.LoadDirect();
        Assert.Equal("备份中的方案", Assert.Single(loaded.Plans).Name);
        Assert.Equal(7, loaded.Plans[0].LoopCount);
        Assert.True(Storage.CanSavePlans);
        Assert.NotNull(Storage.LastLoadWarning);
        Assert.True(Storage.Save(loaded));
        Assert.Single(JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(Storage.PlansPath + ".bak"))!.Plans);
        Assert.Single(Directory.GetFiles(dir, "plans.json.corrupt_*"));
    });

    [Fact]
    public void UnrecoverablePrimaryCannotBeOverwrittenEvenWithEmptyDocument() => WithStorage(dir =>
    {
        File.WriteAllText(Storage.PlansPath, "bad primary");
        File.WriteAllText(Storage.PlansPath + ".bak", "bad backup");
        Storage.LoadDirect();
        Assert.False(Storage.CanSavePlans);
        Assert.False(Storage.Save(new MacroDocument()));
        Assert.Equal("bad primary", File.ReadAllText(Storage.PlansPath));
        Assert.Equal("bad backup", File.ReadAllText(Storage.PlansPath + ".bak"));
        Assert.True(Storage.CanSaveSettings);
    });

    [Fact]
    public void UnrecoverableSettingsCannotBeSilentlyReplaced() => WithStorage(dir =>
    {
        File.WriteAllText(Storage.SettingsPath, "bad settings");
        Storage.LoadDirect();
        Assert.False(Storage.CanSaveSettings);
        Assert.False(Storage.SaveSettings(new MacroDocument()));
        Assert.Equal("bad settings", File.ReadAllText(Storage.SettingsPath));
    });

    [Fact]
    public void SweepRetainsBackupImagesAndRemovesActualOrphans() => WithStorage(dir =>
    {
        var retained = ImageStore.Ref(new byte[] { 1, 2, 3 });
        var orphan = ImageStore.Ref(new byte[] { 4, 5, 6 });
        var document = new MacroDocument();
        var plan = new MacroPlan();
        plan.Steps.Add(new MacroStep { ClickImage = retained });
        document.Plans.Add(plan);
        Assert.True(Storage.Save(document));
        Assert.True(Storage.Save(new MacroDocument()));
        Assert.Equal(1, ImageStore.Sweep(new HashSet<string>()));
        Assert.NotNull(ImageStore.Bytes(retained));
        Assert.Null(ImageStore.Bytes(orphan));
    });

    [Fact]
    public void SweepDoesNothingWhenBackupCannotBeRead() => WithStorage(dir =>
    {
        var image = ImageStore.Ref(new byte[] { 7, 8, 9 });
        File.WriteAllText(Storage.PlansPath + ".bak", "broken");
        Assert.Equal(0, ImageStore.Sweep(new HashSet<string>()));
        Assert.NotNull(ImageStore.Bytes(image));
    });

    [Fact]
    public void DataDirectoryInsideInstallIsRejectedWithoutWritingPointer()
    {
        Assert.False(Storage.ValidateDataDirectory(Path.Combine(AppContext.BaseDirectory, "data"), out _));
        Assert.False(Storage.ValidateDataDirectory(AppContext.BaseDirectory, out _));
    }

    private static void WithStorage(Action<string> action)
    {
        var field = typeof(Storage).GetField("_cachedDir", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        var dir = Path.Combine(Path.GetTempPath(), "MacroPilotStorageTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { field.SetValue(null, dir); action(dir); }
        finally { field.SetValue(null, previous); Directory.Delete(dir, recursive: true); }
    }
}
