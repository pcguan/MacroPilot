import 'dart:io';
import 'package:path/path.dart' as p;
import '../app_meta.dart';
import 'install_log.dart';
import 'payload.dart';
import 'registry.dart';
import 'shortcuts.dart';

// Process.run 兜底超时：CIM/WMI 首次查询可能慢到数秒甚至偶发卡住，加超时避免整条流程被拖住。
ProcessResult _timedOut() => ProcessResult(0, 0, '', '');

typedef ProgressCb = void Function(double fraction, String message);

class UninstallResult {
  final List<String> leftovers;
  final List<String> warnings;
  const UninstallResult({this.leftovers = const [], this.warnings = const []});

  bool get hasIssues => warnings.isNotEmpty;
}

/// 安装编排：解压 payload → 复制卸载器副本 → 快捷方式 → 注册表。
class InstallerService {
  Future<void> install({
    required String installDir,
    required bool desktopShortcut,
    bool fullReplace = false,
    required ProgressCb onProgress,
  }) async {
    // 结束旧版本进程由调用方（install_page）在安装前交互式处理（含管理员本体的提权强杀）。
    // 全量替换：清空目标目录内容（保留目录本身），移除旧版残留文件。用户数据不在安装目录。
    InstallLog.write('install: 开始 dir=$installDir fullReplace=$fullReplace');
    if (fullReplace) {
      onProgress(0.0, '正在清理旧版本…');
      _wipeContents(Directory(installDir));
      InstallLog.write('install: 旧目录已清理');
    }

    // 1) 解压主程序（0 → 0.7）
    onProgress(0.02, '正在准备安装目录…');
    Directory(installDir).createSync(recursive: true);
    await PayloadService.extractTo(installDir, (done, total) {
      final frac = total == 0 ? 0.7 : 0.02 + (done / total) * 0.68;
      onProgress(frac, '正在释放程序文件…（$done/$total）');
    });
    InstallLog.write('install: 程序文件已释放');

    // 2) 写入卸载器到 installDir\unins（0.7 → 0.88）
    //    只保留"原始安装 SFX 自身"的一份拷贝（约 11MB）——它已是单文件、自带 Flutter
    //    运行时；卸载时再解压到 %TEMP% 跑，沿用同一套 UI。这样 unins 从 ~30MB 降到 ~11MB。
    onProgress(0.72, '正在写入卸载程序…');
    final uninsDir = AppMeta.uninsDir(installDir);
    Directory(uninsDir).createSync(recursive: true);
    final uninsExe = p.join(uninsDir, AppMeta.uninstallerExe);
    final sfx = await _originalInstallerPath();
    if (sfx != null && File(sfx).existsSync()) {
      File(sfx).copySync(uninsExe);
    } else {
      // 退路：拿不到原始 SFX（如开发期直接跑），退回拷贝解压态自身（体积大但可用）。
      final selfDir = File(Platform.resolvedExecutable).parent;
      _copyDir(selfDir, Directory(uninsDir));
      final dup = File(p.join(uninsDir, 'data', 'flutter_assets', 'assets',
          'payload', 'app.zip'));
      if (dup.existsSync()) {
        try {
          dup.deleteSync();
        } catch (_) {}
      }
      final fallbackExe =
          p.join(uninsDir, p.basename(Platform.resolvedExecutable));
      if (File(fallbackExe).existsSync() && fallbackExe != uninsExe) {
        File(fallbackExe).copySync(uninsExe);
      }
    }

    // 2.5) 写一个隐藏的 VBS 启动器：设好环境变量后无窗口拉起卸载器。
    //      注册表 UninstallString 指向 wscript+此 vbs，避免 cmd /c 弹出黑色控制台。
    final vbsPath = p.join(uninsDir, 'uninstall.vbs');
    File(vbsPath).writeAsStringSync(_uninstallVbs(uninsExe, installDir));

    // 3) 快捷方式（0.88 → 0.95）
    onProgress(0.9, '正在创建快捷方式…');
    final mainExe = p.join(installDir, AppMeta.appExe);
    await ShortcutService.create(
      lnkPath: AppMeta.startMenuLnk,
      targetExe: mainExe,
      workingDir: installDir,
      description: AppMeta.appNameCn,
    );
    if (desktopShortcut) {
      await ShortcutService.create(
        lnkPath: AppMeta.desktopLnk,
        targetExe: mainExe,
        workingDir: installDir,
        description: AppMeta.appNameCn,
      );
    }

    // 4) 注册表卸载项（0.95 → 1.0）
    onProgress(0.96, '正在登记卸载信息…');
    final sizeKb = (await PayloadService.uncompressedSize()) ~/ 1024;
    await RegistryService.writeUninstallEntry(
      installDir: installDir,
      // 经 wscript 跑隐藏 vbs（无控制台窗口），由 vbs 设环境变量并启动卸载器。
      uninstallCommand: 'wscript.exe "$vbsPath"',
      quietUninstallCommand: 'wscript.exe "$vbsPath" /quiet',
      estimatedSizeKb: sizeKb,
    );

    onProgress(1.0, '安装完成');
  }

  /// 结束安装目录下正在运行的主程序（按精确 exe 路径匹配，不误杀其它位置的同名进程）。
  /// 普通权限：结束不了管理员权限的本体（Access Denied，静默）。结束后稍等释放句柄。
  static Future<void> killAppProcesses(String installDir) async {
    final exePath = p.join(installDir, AppMeta.appExe);
    final escPath = exePath.replaceAll("'", "''");
    // 先按精确路径杀（不误杀别处同名进程）；CIM 首查可能慢，加 8s 超时防卡。
    try {
      await Process.run('powershell', [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        "Get-CimInstance Win32_Process -Filter \"Name='${AppMeta.appExe}'\""
            " | Where-Object { \$_.ExecutablePath -ieq '$escPath' }"
            " | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }",
      ]).timeout(const Duration(seconds: 8), onTimeout: () {
        InstallLog.write('killAppProcesses: CIM 超时(8s)，改用 taskkill 兜底');
        return _timedOut();
      });
    } catch (e) {
      InstallLog.write('killAppProcesses 异常: $e');
    }
    // 再用 taskkill 按映像名快速兜底（同用户非管理员进程可直接结束，无需 CIM）。
    try {
      await Process.run('taskkill', ['/f', '/im', AppMeta.appExe])
          .timeout(const Duration(seconds: 5), onTimeout: () => _timedOut());
    } catch (_) {}
    await Future.delayed(const Duration(milliseconds: 400));
  }

  /// 提权强杀主程序（用于本体以管理员身份运行、普通权限杀不掉时）。
  /// 按映像名 taskkill（能杀掉管理员进程）；经 Start-Process -Verb RunAs 弹一次 UAC。
  /// 不用 -WindowStyle（与 -Verb 参数集冲突）；加超时避免 -Wait 罕见挂起。
  static Future<void> killAppProcessesElevated() async {
    try {
      await Process.run('powershell', [
        '-NoProfile',
        '-Command',
        "Start-Process taskkill -ArgumentList '/f','/im','${AppMeta.appExe}' -Verb RunAs -Wait",
      ]).timeout(const Duration(seconds: 30), onTimeout: () => ProcessResult(0, 0, '', ''));
    } catch (_) {}
    await Future.delayed(const Duration(milliseconds: 600));
  }

  /// 安装目录下的主程序是否仍在运行——用"目标 exe 能否以写方式打开"判断。
  /// 运行中的 exe 会被系统锁定（禁止写/删），管理员权限的本体同样锁着，故能可靠命中。
  /// 比按 WMI ExecutablePath 匹配可靠：普通权限查不到管理员进程的 ExecutablePath。
  static Future<bool> isAppRunning(String installDir) async {
    final f = File(p.join(installDir, AppMeta.appExe));
    if (!f.existsSync()) return false;
    try {
      final h = f.openSync(mode: FileMode.append); // 需写权限；不截断内容
      h.closeSync();
      return false; // 能打开写 → 未运行
    } catch (_) {
      return true; // 打不开 → 被占用/运行中（含管理员本体）
    }
  }

  /// 目标目录是否已存在一份安装（据主程序或 unins\ 判断）。
  static bool isInstalled(String installDir) {
    final exe = File(p.join(installDir, AppMeta.appExe));
    final unins = Directory(AppMeta.uninsDir(installDir));
    return exe.existsSync() || unins.existsSync();
  }

  static List<String> cleanInstallDir(String installDir) =>
      UninstallerService.deleteProgramFiles(installDir);

  /// 清空目录内容但保留目录本身（被占用的项跳过，由后续覆盖处理）。
  static void _wipeContents(Directory dir) {
    if (!dir.existsSync()) return;
    for (final e in dir.listSync(recursive: false)) {
      try {
        e.deleteSync(recursive: true);
      } catch (_) {}
    }
  }

  /// 安装完成后运行主程序。
  static Future<void> launchApp(String installDir) async {
    final exe = p.join(installDir, AppMeta.appExe);
    await Process.start(exe, [],
        workingDirectory: installDir, mode: ProcessStartMode.detached);
  }

  static void _copyDir(Directory src, Directory dst) {
    dst.createSync(recursive: true);
    for (final e in src.listSync(recursive: false)) {
      final target = p.join(dst.path, p.basename(e.path));
      if (e is Directory) {
        _copyDir(e, Directory(target));
      } else if (e is File) {
        e.copySync(target);
      }
    }
  }

  /// 生成隐藏卸载启动器 VBS：设两个环境变量（卸载标记 + 目标目录），无窗口启动卸载器。
  /// VBS 字符串里的 " 用 "" 转义；路径中的反斜杠在 VBS 里是普通字符无需转义。
  static String _uninstallVbs(String uninsExe, String installDir) {
    String esc(String s) => s.replaceAll('"', '""');
    return 'Set sh = CreateObject("WScript.Shell")\r\n'
        'quiet = False\r\n'
        'deleteData = False\r\n'
        'For Each a In WScript.Arguments\r\n'
        '  If LCase(a) = "/quiet" Or LCase(a) = "/s" Then quiet = True\r\n'
        '  If LCase(a) = "/deletedata" Then deleteData = True\r\n'
        'Next\r\n'
        'sh.Environment("PROCESS")("${AppMeta.envUninstall}") = "1"\r\n'
        'sh.Environment("PROCESS")("${AppMeta.envUninstallTarget}") = "${esc(installDir)}"\r\n'
        'If quiet Then sh.Environment("PROCESS")("${AppMeta.envQuietUninstall}") = "1"\r\n'
        'If deleteData Then sh.Environment("PROCESS")("${AppMeta.envDeleteUserData}") = "1"\r\n'
        'sh.Run """${esc(uninsExe)}""", 0, False\r\n';
  }

  /// 取"原始安装 SFX"的完整路径。运行中的本进程是 SFX 解压到 %TEMP% 后拉起的，
  /// 其父进程正是那个 SFX（installer.exe），查父进程的可执行文件路径即可。
  static Future<String?> _originalInstallerPath() async {
    try {
      final myPid = pid; // dart:io 顶层 getter
      final r = await Process.run('powershell', [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        '\$pp=(Get-CimInstance Win32_Process -Filter "ProcessId=$myPid").ParentProcessId;'
            '(Get-CimInstance Win32_Process -Filter "ProcessId=\$pp").ExecutablePath',
      ]).timeout(const Duration(seconds: 8), onTimeout: () => _timedOut());
      final out = (r.stdout as String).trim();
      if (out.toLowerCase().endsWith('.exe') && File(out).existsSync()) {
        return out;
      }
    } catch (_) {}
    return null;
  }
}

/// 卸载编排。
///
/// 正常路径：卸载器是 unins\ 里那份原始 SFX。注册表 UninstallString 经 cmd 设好
/// 环境变量后运行它 → SFX 把自己解压到 %TEMP% 再跑 → 本进程位于 %TEMP%（不在安装目录），
/// 可直接删安装目录。唯一删不掉的是 unins 里那份正被父进程 SFX 占用的卸载器文件，
/// 故收尾时安排一个延时 cmd，在本进程与父 SFX 都退出后再整目录删除。
///
/// 退路：拿不到原始 SFX 时，unins 里是解压态主程序本体；它从安装目录内运行，
/// 需先 [stageAndRelaunch] 中转到 %TEMP% 再删。
class UninstallerService {
  /// 判断当前运行的卸载器是否位于安装目录内（退路情形）。
  static bool runningInsideTarget(String target) {
    final selfDir = File(Platform.resolvedExecutable).parent.path;
    return p.isWithin(target, selfDir) || p.equals(target, selfDir);
  }

  /// 把自身拷到 %TEMP% 并以 --staged 重启，返回 true（调用方应退出，不显示 UI）。
  static Future<bool> stageAndRelaunch(String target,
      {bool quiet = false, bool deleteUserData = false}) async {
    final selfExe = Platform.resolvedExecutable;
    final selfDir = File(selfExe).parent;
    final tempDir = Directory(p.join(Directory.systemTemp.path,
        'mp_unins_${DateTime.now().millisecondsSinceEpoch}'));
    InstallerService._copyDir(selfDir, tempDir);
    final stagedExe = p.join(tempDir.path, p.basename(selfExe));
    final args = ['--uninstall', '--staged', '--target', target];
    if (quiet) args.add('--quiet');
    if (deleteUserData) args.add('--delete-data');
    await Process.start(
      stagedExe,
      args,
      mode: ProcessStartMode.detached,
    );
    return true;
  }

  /// 真正卸载（运行于 %TEMP% 副本）：删快捷方式 → 删注册表 → 尽量删安装目录文件。
  /// 被占用而删不掉的（unins 里的卸载器自身）留给 [scheduleFinalClean] 收尾。
  static Future<UninstallResult> performUninstall(
      String target, ProgressCb onProgress,
      {bool deleteUserData = false}) async {
    final warnings = <String>[];
    onProgress(0.05, '正在结束正在运行的程序…');
    await InstallerService.killAppProcesses(target);

    onProgress(0.1, '正在删除快捷方式…');
    if (!ShortcutService.delete(AppMeta.startMenuLnk)) warnings.add(AppMeta.startMenuLnk);
    if (!ShortcutService.delete(AppMeta.desktopLnk)) warnings.add(AppMeta.desktopLnk);

    onProgress(0.3, '正在清理注册表…');
    await RegistryService.removeUninstallEntry();

    if (deleteUserData) {
      onProgress(0.45, '正在删除用户数据…');
      warnings.addAll(_deleteUserData());
    }

    onProgress(0.6, '正在删除程序文件…');
    final leftovers = deleteProgramFiles(target);
    onProgress(1.0, '卸载完成');
    return UninstallResult(leftovers: leftovers, warnings: warnings);
  }

  static List<String> deleteProgramFiles(String target) =>
      _bestEffortDelete(Directory(target));

  /// 逐项尽力删除（跳过被占用的文件），最后尝试删空目录。
  static List<String> _bestEffortDelete(Directory dir) {
    if (!dir.existsSync()) return const [];
    final entries = dir.listSync(recursive: true, followLinks: false);
    // 先文件后目录：反向（深层在前）逐个删。
    for (final e in entries.reversed) {
      try {
        e.deleteSync(recursive: false);
      } catch (_) {}
    }
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
    if (!dir.existsSync()) return const [];
    return dir
        .listSync(recursive: true, followLinks: false)
        .map((e) => e.path)
        .toList();
  }

  static List<String> _deleteUserData() {
    final warnings = <String>[];
    final dirs = <String>{AppMeta.defaultDataDir};
    try {
      final pointer = File(AppMeta.dataPointerFile);
      if (pointer.existsSync()) {
        final custom = pointer.readAsStringSync().trim();
        if (custom.isNotEmpty) dirs.add(custom);
      }
    } catch (e) {
      warnings.add('${AppMeta.dataPointerFile}：$e');
    }

    for (final dirPath in dirs) {
      final dir = Directory(dirPath);
      try {
        final plans = File(p.join(dirPath, 'plans.json'));
        if (plans.existsSync()) plans.deleteSync();
        final logs = Directory(p.join(dirPath, 'logs'));
        if (logs.existsSync()) logs.deleteSync(recursive: true);
        final pointer = File(p.join(dirPath, 'datapath.txt'));
        if (pointer.existsSync()) pointer.deleteSync();

        if (dir.existsSync() && dir.listSync(followLinks: false).isEmpty) {
          dir.deleteSync();
        }
      } catch (e) {
        warnings.add('$dirPath：$e');
      }
    }
    return warnings;
  }

  /// 收尾：用隐藏 VBS 轮询删除整个安装目录 + 清理 SFX 的 %TEMP% 解压目录，最后自删。
  /// 轮询是因为 unins\ 里的卸载器（SFX）正被父进程占用，需等其退出释放锁；写死延时不可靠。
  /// 经 wscript 跑（无控制台窗口），避免再闪黑框。
  static void scheduleFinalClean(String target) {
    final selfDir = File(Platform.resolvedExecutable).parent.path; // SFX 解压的 temp 目录
    String esc(String s) => s.replaceAll('"', '""');
    final vbs = StringBuffer()
      ..writeln('Set fso = CreateObject("Scripting.FileSystemObject")')
      ..writeln('WScript.Sleep 1200')
      ..writeln('n = 0')
      // 轮询重试 ~3 分钟：等父 SFX 退出 + Defender 扫描这个 SFX 时的间歇占用释放。
      ..writeln('Do While fso.FolderExists("${esc(target)}") And n < 360')
      ..writeln('  On Error Resume Next')
      ..writeln('  fso.DeleteFolder "${esc(target)}", True')
      ..writeln('  On Error GoTo 0')
      ..writeln('  If fso.FolderExists("${esc(target)}") Then WScript.Sleep 500')
      ..writeln('  n = n + 1')
      ..writeln('Loop')
      ..writeln('On Error Resume Next')
      ..writeln('fso.DeleteFolder "${esc(selfDir)}", True')
      ..writeln('fso.DeleteFile WScript.ScriptFullName');
    final vbsFile = File(p.join(Directory.systemTemp.path,
        'mp_cleanup_${DateTime.now().millisecondsSinceEpoch}.vbs'));
    vbsFile.writeAsStringSync(vbs.toString());
    Process.start(
      'wscript.exe',
      [vbsFile.path],
      mode: ProcessStartMode.detached,
      workingDirectory: Directory.systemTemp.path,
    );
  }
}
