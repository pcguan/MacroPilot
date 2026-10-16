using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public sealed class UpdateTransactionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("handoff-success")]
    [InlineData("invalid-handoff")]
    [InlineData("launch-fails")]
    [InlineData("exits-before-ready")]
    [InlineData("rollback-fails")]
    [InlineData("cannot-stop")]
    public async Task DirectoryTransactionPreservesOriginalUntilStartupConfirmation(string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "MacroPilotUpdateTest_" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "installed"), stage = Path.Combine(root, "staged");
        string ready = Path.Combine(root, "ready.txt");
        const string token = "test-token";
        Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Combine(root, "MacroPilotUpdate"));
        File.WriteAllText(Path.Combine(target, "MacroPilot.exe"), "old-version");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "user-data");
        File.WriteAllText(Path.Combine(stage, "MacroPilot.exe"), "new-version");
        File.WriteAllText(Path.Combine(stage, "personal.txt"), "user-data");
        // 实际执行目录改名/清理逻辑，只替代进程启动和消息框；绝不启动虚假的 exe 或本体。
        string mocks = """
$script:starts=0
function Start-Process {
  param($FilePath,$WorkingDirectory,[switch]$PassThru)
  $script:starts++
  if($script:starts -eq 1 -and $env:MP_TEST_MODE -in @('launch-fails','rollback-fails')){throw 'injected startup failure'}
  $p=[pscustomobject]@{Id=4242;HasExited=($env:MP_TEST_MODE -in @('exits-before-ready','handoff-success','invalid-handoff') -and $script:starts -eq 1)}
  $p|Add-Member ScriptMethod Refresh {}
  $p|Add-Member ScriptMethod Kill {if($env:MP_TEST_MODE -eq 'cannot-stop'){throw 'access denied'};$this.HasExited=$true}
  $p|Add-Member ScriptMethod WaitForExit {param($ms) return $true}
  if($script:starts -eq 1 -and $env:MP_TEST_MODE -eq 'success') {
    [IO.File]::WriteAllText($env:MACROPILOT_UPDATE_READY,$env:MACROPILOT_UPDATE_TOKEN+':4242')
  }
  if($script:starts -eq 1 -and $env:MP_TEST_MODE -eq 'handoff-success') {
    [IO.File]::WriteAllText($env:MACROPILOT_UPDATE_READY+'.handoff',$env:MACROPILOT_UPDATE_TOKEN+':handoff:4242:4243')
    [IO.File]::WriteAllText($env:MACROPILOT_UPDATE_READY,$env:MACROPILOT_UPDATE_TOKEN+':4243')
  }
  if($script:starts -eq 1 -and $env:MP_TEST_MODE -eq 'invalid-handoff') {
    [IO.File]::WriteAllText($env:MACROPILOT_UPDATE_READY+'.handoff',$env:MACROPILOT_UPDATE_TOKEN+':handoff:999:4243')
    [IO.File]::WriteAllText($env:MACROPILOT_UPDATE_READY,$env:MACROPILOT_UPDATE_TOKEN+':4243')
  }
  if($PassThru){return $p}
}
function Get-Process {
  param($Id,$ErrorAction)
  if($Id -ne 4243){throw 'unexpected process lookup'}
  $p=[pscustomobject]@{Id=4243;HasExited=$false}
  $p|Add-Member ScriptMethod Refresh {}
  $p|Add-Member ScriptMethod Kill {$this.HasExited=$true}
  $p|Add-Member ScriptMethod WaitForExit {param($ms) return $true}
  return $p
}
function Move-Item {
  param($LiteralPath,$Destination)
  if($env:MP_TEST_MODE -eq 'rollback-fails' -and $LiteralPath -like '*.bak_test-token'){throw 'injected rollback failure'}
  Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}
function Add-Type {param($AssemblyName) throw 'test suppresses interactive error dialog'}
function Start-Sleep {param($Milliseconds) if($env:MP_TEST_MODE -ne 'cannot-stop'){Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds $Milliseconds}}
""";
        string script = UpdateTransactionScript.Text;
        int firstLine = script.IndexOf('\n');
        script = script.Insert(firstLine + 1, mocks + "\n");
        string path = Path.Combine(root, "transaction.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path,
                     "-Stage", stage, "-Dir", target, "-AppPid", "0", "-Ready", ready, "-Token", token })
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["TEMP"] = root;
        process.StartInfo.Environment["MP_TEST_MODE"] = mode;
        try
        {
            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            string output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            string backup = target + ".bak_" + token;
            if (mode is "success" or "handoff-success")
            {
                Assert.Equal("new-version", File.ReadAllText(Path.Combine(target, "MacroPilot.exe")));
                Assert.False(Directory.Exists(backup));
                Assert.Equal("user-data", File.ReadAllText(Path.Combine(target, "personal.txt")));
            }
            else if (mode == "rollback-fails" || mode == "cannot-stop")
            {
                Assert.Equal("old-version", File.ReadAllText(Path.Combine(backup, "MacroPilot.exe")));
                Assert.Equal("user-data", File.ReadAllText(Path.Combine(backup, "personal.txt")));
                if (mode == "cannot-stop")
                {
                    Assert.Equal("new-version", File.ReadAllText(Path.Combine(target, "MacroPilot.exe")));
                    string log = File.ReadAllText(Path.Combine(root, "MacroPilotUpdate", "update.log"));
                    Assert.Contains("rollback FAILED", log);
                    Assert.DoesNotContain("success", log);
                }
            }
            else
            {
                Assert.Equal("old-version", File.ReadAllText(Path.Combine(target, "MacroPilot.exe")));
                Assert.Equal("user-data", File.ReadAllText(Path.Combine(target, "personal.txt")));
                Assert.False(Directory.Exists(backup));
            }
            Assert.False(File.Exists(ready));
            Assert.False(File.Exists(ready + ".handoff"));
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } } catch { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
