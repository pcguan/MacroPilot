import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'package:path/path.dart' as p;

/// 与 WPF InstallationFiles 使用同一份所有权协议；未知文件永不属于卸载范围。
class InstallationFiles {
  static const manifestName = '.macropilot-files.json';
  static const pendingName = '.macropilot-pending.json';
  static const startedName = '.macropilot-started.json';
  static const backupMarker = '.macropilot-backup-token';

  static ({bool handoff, int pid})? parseStartupSignal(String value, String token, int expectedPid) {
    final parts = value.trim().split(':');
    if (parts.length < 2 || parts[0] != token) return null;
    if (parts.length == 2 && int.tryParse(parts[1]) == expectedPid && expectedPid > 0) return (handoff: false, pid: expectedPid);
    if (parts.length == 4 && parts[1] == 'handoff' && int.tryParse(parts[2]) == expectedPid) {
      final child = int.tryParse(parts[3]);
      if (child != null && child > 0 && child != expectedPid) return (handoff: true, pid: child);
    }
    return null;
  }
  static const legacyFiles = {
    'MacroPilot.exe', 'MacroPilot.dll', 'MacroPilot.deps.json', 'MacroPilot.runtimeconfig.json',
    'SharpGen.Runtime.COM.dll', 'SharpGen.Runtime.dll', 'System.IO.Ports.dll',
    'Vortice.Direct3D11.dll', 'Vortice.DirectX.dll', 'Vortice.DXGI.dll', 'Vortice.Mathematics.dll',
    'Wpf.Ui.Abstractions.dll', 'Wpf.Ui.dll',
    'runtimes/linux-arm/native/libSystem.IO.Ports.Native.so', 'runtimes/linux-arm64/native/libSystem.IO.Ports.Native.so',
    'runtimes/linux-x64/native/libSystem.IO.Ports.Native.so', 'runtimes/osx-arm64/native/libSystem.IO.Ports.Native.dylib',
    'runtimes/osx-x64/native/libSystem.IO.Ports.Native.dylib', 'runtimes/unix/lib/net8.0/System.IO.Ports.dll',
    'runtimes/win/lib/net8.0/System.IO.Ports.dll', 'unins/MacroPilotUninstaller.exe', 'unins/uninstall.vbs',
  };

  static void rejectCollisions(String target, Iterable<String> incoming) {
    final previous = owned(target, legacy: legacyFiles).map((e) => e.toLowerCase()).toSet();
    for (final name in incoming) {
      final path = child(target, name);
      if (Directory(path).existsSync() || (File(path).existsSync() && !previous.contains(name.toLowerCase()))) {
        throw FileSystemException('新版本文件与未登记的用户文件同名，请先移动该文件；原安装已保留。', path);
      }
    }
  }

  static String token() => List.generate(16, (_) => Random.secure().nextInt(256).toRadixString(16).padLeft(2, '0')).join();

  static String validateTarget(String target) {
    if (target.trim().isEmpty || !p.isAbsolute(target)) {
      throw const FileSystemException('安装位置必须是绝对路径的独立文件夹。');
    }
    final full = p.normalize(target);
    if (p.equals(full, p.dirname(full))) throw const FileSystemException('不能安装到磁盘根目录。');
    for (final segment in p.split(full).skip(1)) {
      if (segment.endsWith('.') || segment.trim() != segment) {
        throw const FileSystemException('安装目录不能含尾随点或空格。');
      }
    }
    final env = Platform.environment;
    for (final key in ['SystemRoot', 'WINDIR', 'ProgramFiles', 'ProgramFiles(x86)', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'TEMP']) {
      final reserved = env[key];
      if (reserved != null && (p.equals(full, p.normalize(reserved)) ||
          ((key == 'SystemRoot' || key == 'WINDIR') && p.isWithin(reserved, full)))) {
        throw const FileSystemException('不能把系统或用户根目录作为安装位置。');
      }
    }
    var current = full;
    while (true) {
      if (FileSystemEntity.typeSync(current, followLinks: false) == FileSystemEntityType.link) {
        throw const FileSystemException('安装路径不能经过符号链接或目录联接。');
      }
      final parent = p.dirname(current);
      if (parent == current) break;
      current = parent;
    }
    return full;
  }

  static String child(String root, String name) {
    if (name.isEmpty || p.isAbsolute(name) || name.contains(':') || name.split(RegExp(r'[/\\]')).any((s) => s == '..' || s == '.')) {
      throw const FormatException('安装文件路径无效或越界。');
    }
    final full = p.normalize(p.join(root, name.replaceAll('/', p.separator)));
    if (!p.isWithin(root, full)) throw const FormatException('安装文件路径越界。');
    return full;
  }

  static Set<String> owned(String directory, {Iterable<String> legacy = const []}) {
    final file = File(p.join(directory, manifestName));
    Iterable<dynamic> names = legacy;
    if (file.existsSync()) {
      final data = jsonDecode(file.readAsStringSync());
      if (data is! Map || data['schema'] != 1 || data['files'] is! List) throw const FormatException('安装文件清单损坏。');
      names = data['files'] as List;
    }
    final result = <String>{};
    for (final value in names) {
      if (value is! String) throw const FormatException('安装文件清单损坏。');
      child(directory, value);
      result.add(value.replaceAll(r'\', '/'));
    }
    return result;
  }

  static void writeOwned(String directory, Iterable<String> files) {
    final names = files.toSet().toList()..sort();
    for (final name in names) { child(directory, name); }
    File(p.join(directory, manifestName)).writeAsStringSync(jsonEncode({'schema': 1, 'files': names}), flush: true);
  }

  static void copyTree(String source, String target) {
    Directory(target).createSync(recursive: true);
    for (final entry in Directory(source).listSync(followLinks: false)) {
      if (entry is Link) throw FileSystemException('安装目录包含符号链接，已停止操作并保留原安装。', entry.path);
      final destination = p.join(target, p.basename(entry.path));
      if (entry is Directory) { copyTree(entry.path, destination); }
      else if (entry is File) { entry.copySync(destination); }
    }
  }

  /// 返回真正未删掉的已拥有文件；未知文件保留，不算卸载失败。
  static List<String> deleteOwned(String target, Iterable<String> names) {
    validateTarget(target);
    final dirs = <String>{target};
    final leftovers = <String>[];
    for (final name in names) {
      final path = child(target, name);
      // 不经由目录联接删除其它位置的文件。
      var parent = p.dirname(path);
      while (p.isWithin(target, parent)) {
        if (FileSystemEntity.typeSync(parent, followLinks: false) == FileSystemEntityType.link) {
          throw FileSystemException('文件路径经过目录联接，已停止删除。', parent);
        }
        dirs.add(parent); parent = p.dirname(parent);
      }
      try {
        final type = FileSystemEntity.typeSync(path, followLinks: false);
        if (type == FileSystemEntityType.file) File(path).deleteSync();
        else if (type != FileSystemEntityType.notFound) leftovers.add(path);
      } catch (_) { leftovers.add(path); }
    }
    if (leftovers.isEmpty) {
      try { File(p.join(target, manifestName)).deleteSync(); } catch (_) {}
    }
    final ordered = dirs.toList()..sort((a,b) => b.length.compareTo(a.length));
    for (final dir in ordered) { try { Directory(dir).deleteSync(); } catch (_) {} }
    return leftovers;
  }
}

/// 同卷暂存 → 目录改名提交。确认启动前始终保留完整旧版本。
class InstallTransaction {
  final String target;
  final String token = InstallationFiles.token();
  late final String stage = '$target.stage_$token';
  late final String backup = '$target.bak_$token';
  bool backedUp = false;
  bool committed = false;
  InstallTransaction(String path) : target = InstallationFiles.validateTarget(path);

  void prepare() {
    if (Directory(target).existsSync()) { InstallationFiles.copyTree(target, stage); }
    else { Directory(stage).createSync(recursive: true); }
  }

  void commit() {
    if (Directory(target).existsSync()) {
      Directory(target).renameSync(backup); backedUp = true;
      File(p.join(backup, InstallationFiles.backupMarker)).writeAsStringSync(token, flush: true);
    }
    try {
      Directory(stage).renameSync(target); committed = true;
      File(p.join(target, InstallationFiles.pendingName)).writeAsStringSync(
          jsonEncode({'token': token, 'backup': backup}), flush: true);
    } catch (_) { rollback(); rethrow; }
  }

  void confirm() {
    if (backedUp && Directory(backup).existsSync()) Directory(backup).deleteSync(recursive: true);
    final pending = File(p.join(target, InstallationFiles.pendingName));
    if (pending.existsSync()) pending.deleteSync();
  }

  void rollback({bool targetInUse = false}) {
    if (committed && targetInUse) throw FileSystemException(
        '新版仍被进程占用，自动回滚未完成。新版保留在 $target；原版本备份保留在 $backup。请退出该程序后重试或恢复备份。', target);
    if (committed && Directory(target).existsSync()) {
      // 保留失败新版启动期间可能产生的数据，绝不递归删除它。
      Directory(target).renameSync('$target.failed_$token'); committed = false;
    }
    if (backedUp && Directory(backup).existsSync()) {
      Directory(backup).renameSync(target); backedUp = false;
      try { File(p.join(target, InstallationFiles.backupMarker)).deleteSync(); } catch (_) {}
    }
    if (Directory(stage).existsSync()) Directory(stage).deleteSync(recursive: true);
  }
}
