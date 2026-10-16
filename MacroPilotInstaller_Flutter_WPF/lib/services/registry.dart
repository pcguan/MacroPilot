import 'dart:io';
import 'package:path/path.dart' as p;
import '../app_meta.dart';

/// 控制面板「程序和功能」卸载项。经 reg.exe 读写 HKCU（per-user，无需管理员）。
class RegistryService {
  static Future<void> _add(String name, String type, String data) async {
    final result = await Process.run('reg', [
      'add', AppMeta.uninstallKey, '/v', name, '/t', type, '/d', data, '/f',
    ]);
    if (result.exitCode != 0) throw FileSystemException('登记卸载信息失败：${result.stderr}');
  }

  static const _types = {'DisplayName':'REG_SZ','DisplayVersion':'REG_SZ','Publisher':'REG_SZ',
    'DisplayIcon':'REG_SZ','InstallLocation':'REG_SZ','UninstallString':'REG_SZ',
    'QuietUninstallString':'REG_SZ','NoModify':'REG_DWORD','NoRepair':'REG_DWORD','EstimatedSize':'REG_DWORD'};
  static Future<Map<String,String>> capture() async {
    final result = <String,String>{};
    for (final name in _types.keys) { final value = await readValue(name); if (value != null) result[name] = value; }
    return result;
  }
  static Future<void> restore(Map<String,String> values) async {
    await Process.run('reg', ['delete', AppMeta.uninstallKey, '/f']);
    for (final entry in values.entries) { await _add(entry.key, _types[entry.key]!, entry.value); }
  }

  static Future<String?> readValue(String name) async {
    try {
      final r = await Process.run('reg', ['query', AppMeta.uninstallKey, '/v', name]);
      if (r.exitCode != 0) return null;
      final out = (r.stdout as String).split(RegExp(r'\r?\n'));
      for (final line in out) {
        final trimmed = line.trimLeft();
        if (!trimmed.toLowerCase().startsWith(name.toLowerCase())) continue;
        final parts = trimmed.split(RegExp(r'\s{2,}'));
        if (parts.length >= 3) return parts.sublist(2).join('  ').trim();
      }
    } catch (_) {}
    return null;
  }

  static Future<String?> readInstallLocation() => readValue('InstallLocation');

  /// 写入卸载项。uninstallCommand 为控制面板「卸载」时执行的完整命令。
  static Future<void> writeUninstallEntry({
    required String installDir,
    required String uninstallCommand,
    required String quietUninstallCommand,
    required int estimatedSizeKb,
  }) async {
    final mainExe = p.join(installDir, AppMeta.appExe);
    await _add('DisplayName', 'REG_SZ', AppMeta.appNameCn);
    await _add('DisplayVersion', 'REG_SZ', AppMeta.appVersion);
    await _add('Publisher', 'REG_SZ', AppMeta.publisher);
    await _add('DisplayIcon', 'REG_SZ', mainExe);
    await _add('InstallLocation', 'REG_SZ', installDir);
    // UninstallString：经 wscript 跑隐藏 vbs（无控制台窗口）。vbs 内设好环境变量再启动
    // unins 里的卸载器（原始 SFX）；用环境变量是因为 SFX 解压后拉起内层程序参数不一定透传，
    // 但环境变量一定被子进程继承，在内层 main() 被识别为卸载模式 + 目标目录。
    await _add('UninstallString', 'REG_SZ', uninstallCommand);
    await _add('QuietUninstallString', 'REG_SZ', quietUninstallCommand);
    await _add('NoModify', 'REG_DWORD', '1');
    await _add('NoRepair', 'REG_DWORD', '1');
    await _add('EstimatedSize', 'REG_DWORD', estimatedSizeKb.toString());
  }

  static Future<void> removeUninstallEntry() async {
    if (await readInstallLocation() == null) return;
    final result = await Process.run('reg', ['delete', AppMeta.uninstallKey, '/f']);
    if (result.exitCode != 0) throw FileSystemException('移除卸载信息失败：${result.stderr}');
  }
}
