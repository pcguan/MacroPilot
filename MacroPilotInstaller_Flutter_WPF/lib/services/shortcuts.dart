import 'dart:io';

/// 快捷方式（.lnk）创建/删除。经 PowerShell WScript.Shell，比 FFI COM 简洁可靠。
class ShortcutService {
  static Future<void> create({
    required String lnkPath,
    required String targetExe,
    required String workingDir,
    String? iconLocation,
    String description = '',
  }) async {
    final icon = iconLocation ?? targetExe;
    File(lnkPath).parent.createSync(recursive: true);
    // 用单引号包裹路径，并转义路径中的单引号。
    String q(String s) => "'${s.replaceAll("'", "''")}'";
    final script = StringBuffer()
      ..write(r'$ws = New-Object -ComObject WScript.Shell; ')
      ..write('\$s = \$ws.CreateShortcut(${q(lnkPath)}); ')
      ..write('\$s.TargetPath = ${q(targetExe)}; ')
      ..write('\$s.WorkingDirectory = ${q(workingDir)}; ')
      ..write('\$s.IconLocation = ${q(icon)}; ')
      ..write('\$s.Description = ${q(description)}; ')
      ..write(r'$s.Save();');
    final result = await Process.run('powershell', [
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      script.toString(),
    ]);
    if (result.exitCode != 0 || !File(lnkPath).existsSync()) throw FileSystemException('创建快捷方式失败：${result.stderr}', lnkPath);
  }

  static bool delete(String lnkPath) {
    try {
      final f = File(lnkPath);
      if (!f.existsSync()) return true;
      f.deleteSync();
      return true;
    } catch (_) {
      return false;
    }
  }
}
