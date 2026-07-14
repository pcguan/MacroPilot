import 'dart:async';
import 'dart:io';
import 'dart:ui' show PlatformDispatcher;
import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_acrylic/flutter_acrylic.dart' as acrylic;
import 'package:window_manager/window_manager.dart';
import 'app_meta.dart';
import 'theme.dart';
import 'services/install_log.dart';
import 'services/installer.dart';
import 'ui/install_page.dart';
import 'ui/uninstall_page.dart';

Future<void> main(List<String> args) async {
  // 全局错误兜底：安装流程里 _ensureNotRunning / 设备/CIM 等步骤在 install() 的 try 之外，
  // 一旦抛异常又没人接，进程会"静默消失连错误页都不显示"。这里统一捕获 → 写日志 + 不让它直接崩掉。
  runZonedGuarded(() async {
    WidgetsFlutterBinding.ensureInitialized();
    FlutterError.onError = (d) {
      InstallLog.write('FlutterError: ${d.exceptionAsString()}\n${d.stack}');
      FlutterError.presentError(d);
    };
    PlatformDispatcher.instance.onError = (e, s) {
      InstallLog.write('PlatformDispatcher error: $e\n$s');
      return true; // 已处理，别让它终止 app
    };
    InstallLog.write('=== 安装器启动 args=${args.join(" ")} ===');
    await _run(args);
  }, (e, s) {
    InstallLog.write('Zone 未捕获异常: $e\n$s');
  });
}

Future<void> _run(List<String> args) async {
  // 卸载模式：命令行 --uninstall 或注册表 UninstallString 设的环境变量。
  final env = Platform.environment;
  final isUninstall =
      args.contains('--uninstall') || env[AppMeta.envUninstall] == '1';
  final staged = args.contains('--staged');
  final quiet = args.contains('--quiet') || env[AppMeta.envQuietUninstall] == '1';
  final deleteUserData =
      args.contains('--delete-data') || env[AppMeta.envDeleteUserData] == '1';
  final target = _argValue(args, '--target') ??
      env[AppMeta.envUninstallTarget] ??
      AppMeta.defaultInstallDir;

  // 仅当卸载器从安装目录内运行（退路情形）且尚未中转时，才拷到 %TEMP% 重启，
  // 以便能删自己所在目录。正常路径下本进程已在 %TEMP%（SFX 解压处），无需中转。
  if (isUninstall && !staged && UninstallerService.runningInsideTarget(target)) {
    await UninstallerService.stageAndRelaunch(target,
        quiet: quiet, deleteUserData: deleteUserData);
    exit(0);
  }

  if (isUninstall && quiet) {
    await UninstallerService.performUninstall(
      target,
      (_, __) {},
      deleteUserData: deleteUserData,
    );
    UninstallerService.scheduleFinalClean(target);
    exit(0);
  }

  await acrylic.Window.initialize();
  await windowManager.ensureInitialized();

  const winSize = Size(720, 580);
  await windowManager.waitUntilReadyToShow(
    const WindowOptions(
      size: winSize,
      center: true,
      backgroundColor: Colors.transparent,
      titleBarStyle: TitleBarStyle.hidden,
      title: '键鼠宏助手 安装程序',
      minimumSize: winSize,
      maximumSize: winSize,
    ),
    () async {
      await windowManager.setResizable(false);
      await windowManager.show();
      await windowManager.focus();
    },
  );

  // 跟随系统明暗：Win11 显示 Mica；不支持时降级为对应明暗的纯色（避免黑/白窗）。
  final isDark =
      PlatformDispatcher.instance.platformBrightness == Brightness.dark;
  await acrylic.Window.setEffect(
    effect: acrylic.WindowEffect.mica,
    color: isDark ? InstallerTheme.micaDark : InstallerTheme.micaLight,
    dark: isDark,
  );

  runApp(InstallerApp(
      uninstall: isUninstall, target: target, deleteUserData: deleteUserData));
}

String? _argValue(List<String> args, String key) {
  final i = args.indexOf(key);
  if (i >= 0 && i + 1 < args.length) return args[i + 1];
  return null;
}

class InstallerApp extends StatelessWidget {
  final bool uninstall;
  final String target;
  final bool deleteUserData;
  const InstallerApp(
      {super.key,
      required this.uninstall,
      required this.target,
      required this.deleteUserData});

  @override
  Widget build(BuildContext context) {
    return FluentApp(
      debugShowCheckedModeBanner: false,
      title: '键鼠宏助手 安装程序',
      themeMode: ThemeMode.system,
      theme: InstallerTheme.light(),
      darkTheme: InstallerTheme.dark(),
      home: uninstall
          ? UninstallPage(target: target, deleteUserData: deleteUserData)
          : const InstallPage(),
    );
  }
}
