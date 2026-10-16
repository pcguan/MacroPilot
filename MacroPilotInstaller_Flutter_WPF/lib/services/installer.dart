import 'dart:io';
import 'dart:async';
import 'dart:convert';
import 'package:path/path.dart' as p;
import '../app_meta.dart';
import 'install_log.dart';
import 'payload.dart';
import 'installation_files.dart';
import 'registry.dart';
import 'shortcuts.dart';

// 超时必须结束并回收自己创建的进程；不能只放弃 Future，留下后台 WMI 查询。
Future<ProcessResult> _runBounded(String executable, List<String> arguments, Duration timeout) async {
  final process = await Process.start(executable, arguments);
  final stdout = process.stdout.transform(systemEncoding.decoder).join();
  final stderr = process.stderr.transform(systemEncoding.decoder).join();
  var timedOut = false;
  final timer = Timer(timeout, () { timedOut = true; process.kill(); });
  try {
    final code = await process.exitCode;
    final output = await stdout;
    final error = await stderr;
    if (timedOut) throw TimeoutException('$executable 超时，已终止该进程。');
    return ProcessResult(process.pid, code, output, error);
  } finally { timer.cancel(); }
}

typedef ProgressCb = void Function(double fraction, String message);

class UninstallResult {
  final List<String> leftovers;
  final List<String> warnings;
  const UninstallResult({this.leftovers = const [], this.warnings = const [], this.completed = true});

  final bool completed;
  bool get hasIssues => warnings.isNotEmpty || leftovers.isNotEmpty || !completed;
}

/// 安装编排：解压 payload → 复制卸载器副本 → 快捷方式 → 注册表。
class InstallerService {
  Future<void> install({
    required String installDir,
    required bool desktopShortcut,
    required ProgressCb onProgress,
  }) async {
    final transaction = InstallTransaction(installDir);
    final previousRegistry = await RegistryService.capture();
    final shortcutBackup = <String, List<int>?>{};
    for (final path in [AppMeta.startMenuLnk, AppMeta.desktopLnk]) {
      shortcutBackup[path] = File(path).existsSync() ? File(path).readAsBytesSync() : null;
    }
    Future<void> restoreIntegration() async {
      await RegistryService.restore(previousRegistry);
      for (final entry in shortcutBackup.entries) {
        try {
          if (entry.value == null) { if (File(entry.key).existsSync()) File(entry.key).deleteSync(); }
          else { File(entry.key).writeAsBytesSync(entry.value!); }
        } catch (_) {}
      }
    }
    try {
      final owned = await PayloadService.fileNames();
      InstallationFiles.rejectCollisions(installDir, {...owned, 'unins/${AppMeta.uninstallerExe}', 'unins/uninstall.vbs'});
      transaction.prepare();
      final previousOwned = InstallationFiles.owned(installDir);
      for (final name in previousOwned) {
        if (!owned.contains(name)) {
          final file = File(InstallationFiles.child(transaction.stage, name));
          if (file.existsSync()) file.deleteSync();
        }
      }
      final stageDir = transaction.stage;
    // 1) 解压主程序（0 → 0.7）
    onProgress(0.02, '正在准备安装目录…');
    Directory(stageDir).createSync(recursive: true);
    await PayloadService.extractTo(stageDir, (done, total) {
      final frac = total == 0 ? 0.7 : 0.02 + (done / total) * 0.68;
      onProgress(frac, '正在释放程序文件…（$done/$total）');
    });
    InstallLog.write('install: 程序文件已释放');

    // 2) 写入卸载器到 installDir\unins（0.7 → 0.88）
    //    只保留"原始安装 SFX 自身"的一份拷贝（约 11MB）——它已是单文件、自带 Flutter
    //    运行时；卸载时再解压到 %TEMP% 跑，沿用同一套 UI。这样 unins 从 ~30MB 降到 ~11MB。
    onProgress(0.72, '正在写入卸载程序…');
    final uninsDir = AppMeta.uninsDir(stageDir);
    Directory(uninsDir).createSync(recursive: true);
    final uninsExe = p.join(uninsDir, AppMeta.uninstallerExe);
    final sfx = await _originalInstallerPath();
    if (sfx != null && File(sfx).existsSync()) {
      File(sfx).copySync(uninsExe);
      owned.add('unins/${AppMeta.uninstallerExe}');
    } else {
      // 退路：拿不到原始 SFX（如开发期直接跑），退回拷贝解压态自身（体积大但可用）。
      final selfDir = File(Platform.resolvedExecutable).parent;
      _copyDir(selfDir, Directory(uninsDir));
      for (final entry in selfDir.listSync(recursive: true, followLinks: false).whereType<File>()) {
        owned.add('unins/${p.relative(entry.path, from: selfDir.path).replaceAll(r"\", "/")}');
      }
      owned.add('unins/${AppMeta.uninstallerExe}');
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
    final vbsPath = p.join(AppMeta.uninsDir(installDir), 'uninstall.vbs');
    _writeVbs(p.join(uninsDir, 'uninstall.vbs'), _uninstallVbs(p.join(AppMeta.uninsDir(installDir), AppMeta.uninstallerExe), installDir));
    owned.add('unins/uninstall.vbs');
    owned.add(InstallationFiles.startedName);
    InstallationFiles.writeOwned(stageDir, owned);
    transaction.commit();

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

    _pending[installDir] = transaction;
    _restoreIntegration[installDir] = restoreIntegration;
    onProgress(1.0, '安装完成');
    } catch (_) {
      transaction.rollback();
      await restoreIntegration();
      rethrow;
    }
  }

  static final Map<String, InstallTransaction> _pending = {};
  static final Map<String, Future<void> Function()> _restoreIntegration = {};

  static void _writeVbs(String path, String script) {
    // WSH 识别 UTF-16 LE BOM；默认 UTF-8 会破坏非 ASCII 安装路径。
    final bytes = <int>[0xff, 0xfe];
    for (final unit in script.codeUnits) { bytes.add(unit & 255); bytes.add(unit >> 8); }
    File(path).writeAsBytesSync(bytes, flush: true);
  }

  /// 结束安装目录下正在运行的主程序（按精确 exe 路径匹配，不误杀其它位置的同名进程）。
  /// 普通权限：结束不了管理员权限的本体（Access Denied，静默）。结束后稍等释放句柄。
  static Future<void> killAppProcesses(String installDir) async {
    final exePath = p.join(installDir, AppMeta.appExe);
    final escPath = exePath.replaceAll("'", "''");
    // 先按精确路径杀（不误杀别处同名进程）；CIM 首查可能慢，加 8s 超时防卡。
    try {
      await _runBounded('powershell', [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        "Get-CimInstance Win32_Process -Filter \"Name='${AppMeta.appExe}'\""
            " | Where-Object { \$_.ExecutablePath -ieq '$escPath' }"
            " | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }",
      ], const Duration(seconds: 8));
    } catch (e) {
      InstallLog.write('killAppProcesses 异常: $e');
    }
    await Future.delayed(const Duration(milliseconds: 400));
  }

  /// 提权强杀主程序（用于本体以管理员身份运行、普通权限杀不掉时）。
  /// 提权后仍按精确路径匹配，不能误杀其它安装目录的同名进程。
  /// 不用 -WindowStyle（与 -Verb 参数集冲突）；加超时避免 -Wait 罕见挂起。
  static Future<void> killAppProcessesElevated(String installDir) async {
    try {
      await _runBounded('powershell', [
        '-NoProfile',
        '-Command',
        "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -EncodedCommand ${base64Encode(utf16le("Get-CimInstance Win32_Process -Filter \"Name='${AppMeta.appExe}'\" | Where-Object { \$_.ExecutablePath -ieq '${p.join(installDir, AppMeta.appExe).replaceAll("'", "''")}' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }"))}'",
      ], const Duration(seconds: 30));
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

  static Future<List<String>> cleanInstallDir(String installDir) async =>
      UninstallerService.deleteProgramFiles(installDir);

  static List<int> utf16le(String text) => [for (final c in text.codeUnits) ...[c & 255, c >> 8]];

  /// 安装完成后运行主程序。
  static Future<void> launchApp(String installDir) async {
    final transaction = _pending[installDir];
    if (transaction == null) {
      await Process.start(p.join(installDir, AppMeta.appExe), [],
          workingDirectory: installDir, mode: ProcessStartMode.detached);
      return;
    }
    final startedFile = File(p.join(installDir, InstallationFiles.startedName));
    if (startedFile.existsSync()) {
      try {
        final marker = jsonDecode(startedFile.readAsStringSync());
        if (marker is Map && marker['token'] == transaction.token) {
          try { transaction.confirm(); } catch (e) { InstallLog.write('备份保留: $e'); }
          _pending.remove(installDir); _restoreIntegration.remove(installDir);
          if (!await isAppRunning(installDir)) await launchApp(installDir);
          return;
        }
      } on FormatException { /* 无效标记不算启动确认 */ }
    }
    if (transaction.backedUp && !File(p.join(installDir, InstallationFiles.pendingName)).existsSync() &&
        !Directory(transaction.backup).existsSync()) {
      // 用户已通过快捷方式启动，新窗口已独立确认并完成备份清理。
      _pending.remove(installDir); _restoreIntegration.remove(installDir);
      return;
    }
    if (await isAppRunning(installDir)) throw FileSystemException(
        '程序已从其它入口启动，尚未收到初始化确认；当前安装及备份均已保留，请关闭程序后重试。备份：${transaction.backup}', installDir);
    final readyDir = Directory(p.join(Directory.systemTemp.path, 'MacroPilotUpdate'))..createSync(recursive: true);
    final ready = File(p.join(readyDir.path, '${transaction.token}.ready'));
    final handoff = File('${ready.path}.handoff');
    Process? process;
    var exited = false;
    try {
      process = await Process.start(p.join(installDir, AppMeta.appExe), [],
          workingDirectory: installDir,
          environment: {'MACROPILOT_UPDATE_READY': ready.path, 'MACROPILOT_UPDATE_TOKEN': transaction.token});
      process.stdout.drain<void>(); process.stderr.drain<void>();
      process.exitCode.then((_) { exited = true; });
      var confirmed = false;
      var expectedPid = process.pid;
      var exitedTicks = 0;
      for (var i = 0; i < 120; i++) {
        // 交接文件独立于最终 ready，子窗口再快也不会覆盖父→子身份链。
        if (handoff.existsSync()) {
          final signal = InstallationFiles.parseStartupSignal(handoff.readAsStringSync(), transaction.token, expectedPid);
          if (signal?.handoff == true) { expectedPid = signal!.pid; exitedTicks = 0; }
        }
        if (ready.existsSync() && InstallationFiles.parseStartupSignal(ready.readAsStringSync(), transaction.token, expectedPid)?.handoff == false) {
          if ((expectedPid == process.pid && exited) || (expectedPid != process.pid && !await isAppRunning(installDir))) {
            throw const FileSystemException('新版在确认后已退出。');
          }
          confirmed = true; break;
        }
        if (exited && expectedPid == process.pid) {
          if (++exitedTicks >= 6) throw const FileSystemException('新版在完成初始化前退出。');
        }
        await Future.delayed(const Duration(milliseconds: 500));
      }
      if (!confirmed) throw const FileSystemException('新版没有确认启动，已停止更新并尝试恢复原版本。');
      // 启动确认后清理失败仅留下备份，不能回滚一个已经正常运行的新版。
      try { transaction.confirm(); } catch (e) { InstallLog.write('备份保留: $e'); }
      _pending.remove(installDir);
      _restoreIntegration.remove(installDir);
    } catch (failure) {
      if (process != null && !exited) {
        process.kill();
        for (var i = 0; i < 30 && await isAppRunning(installDir); i++) {
          await Future.delayed(const Duration(milliseconds: 200));
        }
      }
      if (handoff.existsSync()) await killAppProcesses(installDir);
      try {
        transaction.rollback(targetInUse: await isAppRunning(installDir));
      } catch (rollbackError) {
        // 不能结束管理员子进程时保留两侧目录和当前注册信息，禁止声称已回滚。
        throw FileSystemException('更新未完成：$failure\n自动回滚未完成：$rollbackError\n旧版备份：${transaction.backup}\n当前版本：$installDir');
      }
      _pending.remove(installDir);
      final restore = _restoreIntegration.remove(installDir);
      if (restore != null) await restore();
      if (File(p.join(installDir, AppMeta.appExe)).existsSync()) {
        await Process.start(p.join(installDir, AppMeta.appExe), [], workingDirectory: installDir, mode: ProcessStartMode.detached);
      }
      rethrow;
    } finally {
      if (ready.existsSync()) ready.deleteSync();
      if (handoff.existsSync()) handoff.deleteSync();
    }
  }

  static void _copyDir(Directory src, Directory dst) => InstallationFiles.copyTree(src.path, dst.path);

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
      final r = await _runBounded('powershell', [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        '\$pp=(Get-CimInstance Win32_Process -Filter "ProcessId=$myPid").ParentProcessId;'
            '(Get-CimInstance Win32_Process -Filter "ProcessId=\$pp").ExecutablePath',
      ], const Duration(seconds: 8));
      final out = (r.stdout as String).trim();
      if (out.toLowerCase().endsWith('.exe') && File(out).existsSync() && File(out).lengthSync() < 64 * 1024 * 1024) {
        final bytes = File(out).readAsBytesSync();
        if (latin1.decode(bytes).contains(';!@Install@!UTF-8!')) return out;
      }
    } catch (_) {}
    return null;
  }
}

/// 静默在线更新编排。
///
/// 本体在"检查更新 → 下载安装器 exe"后，设环境变量 [AppMeta.envUpdate]=1 +
/// [AppMeta.envUpdateTarget]=安装目录，启动本 SFX 并自行退出。SFX 解压后内层安装器在
/// 【显示任何窗口之前】(见 main.dart) 走这里——无 UI、无用户操作地完成：
///   等本体退出 → 备份旧目录(同卷改名) → 全新安装(复用 [InstallerService.install]) →
///   失败则回滚备份 → 重启本体。SFX 会在内层 exe 退出后自动清掉自己的 %TEMP% 解压目录。
class UpdaterService {
  static Future<void> performSilentUpdate(String installDir,
      {bool relaunch = true}) async {
    InstallLog.write('update: 开始 dir=$installDir');

    // 1) 等本体退出（触发更新后本体会自行 Shutdown）。最多 ~15s；仍占用则尝试结束。
    for (var i = 0;
        i < 30 && await InstallerService.isAppRunning(installDir);
        i++) {
      await Future.delayed(const Duration(milliseconds: 500));
    }
    if (await InstallerService.isAppRunning(installDir)) {
      await InstallerService.killAppProcesses(installDir);
    }
    if (await InstallerService.isAppRunning(installDir)) {
      InstallLog.write('update: 本体仍占用，放弃更新（保留旧版）');
      if (relaunch) await InstallerService.launchApp(installDir);
      return;
    }

    try {
      await InstallerService().install(
        installDir: installDir,
        desktopShortcut: File(AppMeta.desktopLnk).existsSync(),
        onProgress: (_, __) {},
      );
      if (relaunch) await InstallerService.launchApp(installDir);
      InstallLog.write('update: 结束（未启动时保留旧版备份，首次成功启动再清理）');
    } catch (e, st) {
      InstallLog.write('update FAILED: $e\n$st');
      if (!InstallerService._pending.containsKey(installDir) &&
          File(p.join(installDir, AppMeta.appExe)).existsSync() && !await InstallerService.isAppRunning(installDir)) {
        try { await InstallerService.launchApp(installDir); } catch (_) {}
      }
      final message = '更新未完成。\n$e\n详情见安装日志。'.replaceAll("'", "''");
      final script = "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show('$message','键鼠宏助手 · 更新失败') | Out-Null";
      await Process.start('powershell', ['-NoProfile', '-EncodedCommand', base64Encode(InstallerService.utf16le(script))],
          mode: ProcessStartMode.detached);
    }
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
        'mp_unins_${InstallationFiles.token()}'));
    InstallerService._copyDir(selfDir, tempDir);
    File(p.join(tempDir.path, '.macropilot-stage-token')).writeAsStringSync(p.basename(tempDir.path).substring('mp_unins_'.length), flush: true);
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

  static final Map<String, Set<String>> _cleanupFiles = {};

  static Future<Set<String>> _owned(String target) async {
    if (File(p.join(target, InstallationFiles.manifestName)).existsSync()) return InstallationFiles.owned(target);
    return InstallationFiles.owned(target, legacy: InstallationFiles.legacyFiles);
  }

  static Future<UninstallResult> performUninstall(
      String target, ProgressCb onProgress,
      {bool deleteUserData = false}) async {
    final warnings = <String>[];
    try {
      InstallationFiles.validateTarget(target);
      if (!InstallerService.isInstalled(target) && !File(p.join(target, InstallationFiles.manifestName)).existsSync()) {
        return const UninstallResult(completed: false, warnings: ['未找到可确认的 MacroPilot 安装，未删除任何文件。']);
      }
      final owned = await _owned(target); // 全部路径在删除任何文件前校验。
      onProgress(0.05, '正在结束正在运行的程序…');
      await InstallerService.killAppProcesses(target);
      if (await InstallerService.isAppRunning(target)) await InstallerService.killAppProcessesElevated(target);
      if (await InstallerService.isAppRunning(target)) {
        return const UninstallResult(completed: false, warnings: ['程序仍在运行或安装目录不可写，卸载已停止。请退出程序或使用有权限的账户重试。']);
      }
      onProgress(0.3, '正在删除已登记的程序文件…');
      final leftovers = InstallationFiles.deleteOwned(target, owned);
      final blockedProgram = leftovers.where((f) => !p.isWithin(AppMeta.uninsDir(target), f)).toList();
      if (blockedProgram.isNotEmpty) {
        return UninstallResult(completed: false, leftovers: blockedProgram, warnings: ['部分程序文件被占用，已保留卸载入口，请稍后重试。']);
      }
      onProgress(0.6, '正在清理快捷方式和卸载信息…');
      if (!ShortcutService.delete(AppMeta.startMenuLnk)) warnings.add(AppMeta.startMenuLnk);
      if (!ShortcutService.delete(AppMeta.desktopLnk)) warnings.add(AppMeta.desktopLnk);
      await RegistryService.removeUninstallEntry();
      if (deleteUserData) warnings.addAll(_deleteUserData());
      _cleanupFiles[target] = {...owned, InstallationFiles.manifestName};
      onProgress(1, '卸载完成');
      // unins 自身的锁由退出后清理处理；其它残留绝不能伪装成成功。
      return UninstallResult(warnings: warnings);
    } catch (e) {
      return UninstallResult(completed: false, warnings: [...warnings, e.toString()]);
    }
  }

  static Future<List<String>> deleteProgramFiles(String target) async =>
      InstallationFiles.deleteOwned(target, await _owned(target));

  static List<String> _deleteUserData() {
    final warnings = <String>[];
    final dirs = <String>{AppMeta.defaultDataDir};
    try {
      final pointer = File(AppMeta.dataPointerFile);
      if (pointer.existsSync()) {
        final custom = pointer.readAsStringSync().trim();
        if (custom.isNotEmpty && p.isAbsolute(custom)) dirs.add(custom);
      }
    } catch (e) { warnings.add('${AppMeta.dataPointerFile}：$e'); }
    for (final root in dirs) {
      try {
        final dir = Directory(root);
        if (!dir.existsSync()) continue;
        final files = <String>[];
        for (final entry in dir.listSync(followLinks: false).whereType<File>()) {
          final name = p.basename(entry.path);
          if (name == 'datapath.txt' || RegExp(r'^(plans|settings)\.json($|\.bak$|\.tmp|\.corrupt_|\.recover_)').hasMatch(name)) files.add(name);
        }
        final patterns = {
          'images': RegExp(r'^[a-fA-F0-9]{64}\.png($|\.tmp_)'),
          'logs': RegExp(r'^run-\d{8}\.log$'),
          'traces': RegExp(r'^mousetrace-\d{8}-\d{6}\.csv$'),
        };
        for (final entry in patterns.entries) {
          final sub = Directory(p.join(root, entry.key));
          if (FileSystemEntity.typeSync(sub.path, followLinks: false) == FileSystemEntityType.link) {
            warnings.add('已跳过目录联接：${sub.path}'); continue;
          }
          if (sub.existsSync()) {
            for (final file in sub.listSync(followLinks: false).whereType<File>()) {
              if (entry.value.hasMatch(p.basename(file.path))) files.add('${entry.key}/${p.basename(file.path)}');
            }
          }
        }
        // 只删除已知数据格式；自定义文件夹中的其它文件保持原样。
        warnings.addAll(InstallationFiles.deleteOwned(root, files));
      } catch (e) { warnings.add('$root：$e'); }
    }
    return warnings;
  }

  /// 只清理登记的文件；等待本卸载 UI 退出后才开始有限重试，完成页停多久都不会耗尽重试。
  static void scheduleFinalClean(String target) {
    final owned = _cleanupFiles.remove(target);
    if (owned == null) return; // 卸载失败时不能安排后续删除。
    InstallationFiles.validateTarget(target);
    String esc(String value) => value.replaceAll('"', '""');
    final dirs = <String>{target};
    final files = <String>[];
    for (final name in owned) {
      final file = InstallationFiles.child(target, name);
      files.add(file);
      var parent = p.dirname(file);
      while (p.isWithin(target, parent)) { dirs.add(parent); parent = p.dirname(parent); }
    }
    final selfDir = File(Platform.resolvedExecutable).parent.path;
    final stageMarker = File(p.join(selfDir, '.macropilot-stage-token'));
    if (p.isWithin(Directory.systemTemp.path, selfDir) && p.basename(selfDir).startsWith('mp_unins_') &&
        stageMarker.existsSync() && stageMarker.readAsStringSync() == p.basename(selfDir).substring('mp_unins_'.length)) {
      // 仅回收本工具创建并签出的中转副本，仍逐文件删除，不递归跟随后来出现的联接。
      dirs.add(selfDir);
      for (final entry in Directory(selfDir).listSync(recursive: true, followLinks: false)) {
        if (entry is File) files.add(entry.path);
        if (entry is Directory) dirs.add(entry.path);
      }
    }
    final ordered = dirs.toList()..sort((a,b) => b.length.compareTo(a.length));
    final vbs = StringBuffer()
      ..writeln('Set fso = CreateObject("Scripting.FileSystemObject")')
      ..writeln('Function SafePath(value)')
      ..writeln('  On Error Resume Next')
      ..writeln('  SafePath = False')
      ..writeln('  probe = value')
      ..writeln('  If fso.FileExists(probe) Then')
      ..writeln('    Err.Clear')
      ..writeln('    attrs = fso.GetFile(probe).Attributes')
      ..writeln('    If Err.Number <> 0 Or (attrs And 1024) <> 0 Then Exit Function')
      ..writeln('    probe = fso.GetParentFolderName(probe)')
      ..writeln('  End If')
      ..writeln('  Do While Len(probe) > 0')
      ..writeln('    If fso.FolderExists(probe) Then')
      ..writeln('      Err.Clear')
      ..writeln('      attrs = fso.GetFolder(probe).Attributes')
      ..writeln('      If Err.Number <> 0 Or (attrs And 1024) <> 0 Then Exit Function')
      ..writeln('    End If')
      ..writeln('    probe = fso.GetParentFolderName(probe)')
      ..writeln('  Loop')
      ..writeln('  SafePath = True')
      ..writeln('End Function')
      ..writeln('Set svc = GetObject("winmgmts:\\\\.\\root\\cimv2")')
      ..writeln('Do While svc.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE ProcessId=$pid").Count > 0')
      ..writeln('  WScript.Sleep 500')
      ..writeln('Loop')
      ..writeln('For n = 1 To 360')
      ..writeln('  On Error Resume Next');
    for (final file in files) {
      vbs.writeln('  If SafePath("${esc(file)}") And fso.FileExists("${esc(file)}") Then fso.DeleteFile "${esc(file)}", True');
    }
    // DeleteFolder 无空目录模式：必须明确检查文件和子目录都为零才能删除。
    for (final dir in ordered) {
      vbs.writeln('  If SafePath("${esc(dir)}") And fso.FolderExists("${esc(dir)}") Then');
      vbs.writeln('    Set folder = fso.GetFolder("${esc(dir)}")');
      vbs.writeln('    If folder.Files.Count = 0 And folder.SubFolders.Count = 0 Then fso.DeleteFolder "${esc(dir)}", False');
      vbs.writeln('  End If');
    }
    vbs.writeln('  remaining = False');
    for (final file in files) { vbs.writeln('  If fso.FileExists("${esc(file)}") Then remaining = True'); }
    vbs
      ..writeln('  On Error GoTo 0')
      ..writeln('  If Not remaining Then Exit For')
      ..writeln('  WScript.Sleep 500')
      ..writeln('Next')
      ..writeln('On Error Resume Next');
    vbs.writeln('fso.DeleteFile WScript.ScriptFullName');
    final script = File(p.join(Directory.systemTemp.path, 'mp_cleanup_${InstallationFiles.token()}.vbs'));
    InstallerService._writeVbs(script.path, vbs.toString());
    Process.start('wscript.exe', [script.path], mode: ProcessStartMode.detached,
        workingDirectory: Directory.systemTemp.path);
  }
}
