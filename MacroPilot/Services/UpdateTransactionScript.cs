namespace MacroPilot.Services;

internal static class UpdateTransactionScript
{
    // 暂存已在退出前准备并校验；同卷目录改名提交。必须收到新版窗口初始化后的 PID+随机令牌确认才清备份。
    internal const string Text = """
param([string]$Stage,[string]$Dir,[int]$AppPid,[string]$Ready,[string]$Token)
$ErrorActionPreference='Stop'
$logDir=Join-Path $env:TEMP 'MacroPilotUpdate'
Set-Location -LiteralPath $logDir
$log=Join-Path $logDir 'update.log'
function Log($m){Add-Content -LiteralPath $log -Value ((Get-Date).ToString('HH:mm:ss.fff')+' '+$m)}
$panel=$null; $label=$null
try {
  Add-Type -AssemblyName System.Windows.Forms
  $panel=New-Object Windows.Forms.Form
  $panel.Text='键鼠宏助手 · 更新'; $panel.Width=460; $panel.Height=150
  $panel.StartPosition='CenterScreen'; $panel.FormBorderStyle='FixedDialog'; $panel.ControlBox=$false
  $label=New-Object Windows.Forms.Label
  $label.Dock='Fill'; $label.TextAlign='MiddleCenter'; $label.Text='正在准备更新…'
  $panel.Controls.Add($label); $panel.Show()
} catch { $panel=$null }
function Show-Progress($message) {
  try { if($panel){$label.Text=$message;$panel.Refresh();[Windows.Forms.Application]::DoEvents()} } catch {}
}
function Pump-Progress { try { if($panel){[Windows.Forms.Application]::DoEvents()} } catch {} }
$backup=$Dir+'.bak_'+$Token
$failedDir=$Dir+'.failed_'+$Token
$exe=Join-Path $Dir 'MacroPilot.exe'
$renamed=$false; $committed=$false; $started=$null; $rollback=$false
Log "=== start dir=$Dir pid=$AppPid ==="
try {
  Show-Progress '正在等待原程序退出…'
  # 只等待请求更新的进程，不按名称杀其它目录/会话的实例。
  if ($AppPid -gt 0) {
    for($wait=0;$wait -lt 300 -and (Get-Process -Id $AppPid -ErrorAction SilentlyContinue);$wait++) {
      Pump-Progress; Start-Sleep -Milliseconds 100
    }
    if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { throw '原程序未退出，已停止更新。' }
  }
  Show-Progress '正在备份原版本并切换程序文件…'
  for($i=0;$i -lt 30 -and -not $renamed;$i++) {
    try { Move-Item -LiteralPath $Dir -Destination $backup; $renamed=$true }
    catch { if($i -eq 29){throw}; Pump-Progress; Start-Sleep -Milliseconds 300 }
  }
  Move-Item -LiteralPath $Stage -Destination $Dir
  $committed=$true
  $env:MACROPILOT_UPDATE_READY=$Ready
  $env:MACROPILOT_UPDATE_TOKEN=$Token
  $started=Start-Process -FilePath $exe -WorkingDirectory $Dir -PassThru
  Remove-Item Env:MACROPILOT_UPDATE_READY; Remove-Item Env:MACROPILOT_UPDATE_TOKEN
  $confirmed=$false
  Show-Progress '正在启动新版，等待初始化确认…'
  $expectedPid=$started.Id; $tracked=$started; $exitedTicks=0
  for($i=0;$i -lt 120;$i++) {
    Pump-Progress
    if(Test-Path -LiteralPath ($Ready+'.handoff')) {
      $signal=(Get-Content -LiteralPath ($Ready+'.handoff') -Raw).Trim()
      $handoff='^'+[Regex]::Escape($Token)+':handoff:'+$expectedPid+':([1-9][0-9]*)$'
      if($signal -match $handoff -and [int]$Matches[1] -ne $expectedPid) {
        $expectedPid=[int]$Matches[1]; $tracked=Get-Process -Id $expectedPid -ErrorAction Stop; $exitedTicks=0
        Log "startup handoff -> pid=$expectedPid"
      }
    }
    if((Test-Path -LiteralPath $Ready) -and ((Get-Content -LiteralPath $Ready -Raw).Trim() -eq ($Token+':'+$expectedPid))) {
      $tracked.Refresh()
      if($tracked.HasExited){throw '新版确认后已退出。'}
      $confirmed=$true; break
    }
    $tracked.Refresh()
    if($tracked.HasExited){$exitedTicks++;if($exitedTicks -ge 6){throw '新版在完成初始化前退出。'}}else{$exitedTicks=0}
    Start-Sleep -Milliseconds 500
  }
  if(-not $confirmed){throw '新版未能在 60 秒内确认启动。'}
  Log 'success: startup confirmed'
  Show-Progress '新版已启动，正在完成更新…'
  try { Remove-Item -LiteralPath $backup -Recurse -Force } catch { Log "backup retained: $_" }
} catch {
  Log "FAILED: $_"
  $reason=$_.ToString()
  Show-Progress '更新未完成，正在检查并恢复原版本…'
  $canRollback=$true
  if($tracked) {
    try {
      if(-not $tracked.HasExited){$tracked.Kill();$tracked.WaitForExit(10000)|Out-Null}
      $tracked.Refresh()
      if(-not $tracked.HasExited){$canRollback=$false}
    } catch { $canRollback=$false; Log "cannot stop startup process: $_" }
  }
  try {
    if(-not $canRollback){throw '新版进程无法结束，保留当前目录和原版本备份。'}
    if($committed -and (Test-Path -LiteralPath $Dir)){Move-Item -LiteralPath $Dir -Destination $failedDir}
    if($renamed){Move-Item -LiteralPath $backup -Destination $Dir; $rollback=$true; Log 'rolled back'}
    if(Test-Path -LiteralPath $exe){Start-Process -FilePath $exe -WorkingDirectory $Dir}
  } catch {Log "rollback FAILED: $_"}
  try {
    Add-Type -AssemblyName System.Windows.Forms
    $state=if($rollback){'已恢复原版本。'}elseif(-not $renamed){'原安装未改变。'}else{'自动回滚未完成；当前目录：'+$Dir+'；原版本备份：'+$backup}
    [Windows.Forms.MessageBox]::Show("更新未完成。$state`n$reason`n日志：$log",'键鼠宏助手 · 更新失败')|Out-Null
  } catch {}
} finally {
  try { if($panel){$panel.Close();$panel.Dispose()} } catch {}
  Remove-Item Env:MACROPILOT_UPDATE_READY -ErrorAction SilentlyContinue
  Remove-Item Env:MACROPILOT_UPDATE_TOKEN -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $Ready -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath ($Ready+'.handoff') -Force -ErrorAction SilentlyContinue
  if(Test-Path -LiteralPath $Stage){Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue}
  Log '=== done ==='
}
""";
}
