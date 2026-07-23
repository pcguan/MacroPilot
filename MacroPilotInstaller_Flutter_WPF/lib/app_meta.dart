import 'dart:io';
import 'package:path/path.dart' as p;

/// 安装器全局常量与路径计算。与旧 Inno 脚本（installer.iss）保持一致。
class AppMeta {
  static const String appName = 'MacroPilot'; // 内部标识 / 安装文件夹名
  static const String appNameCn = '键鼠宏助手'; // 显示名
  static const String appVersion = '0.1.8';
  static const String publisher = 'MacroPilot';
  static const String appExe = 'MacroPilot.exe'; // 主程序
  static const String uninstallerExe =
      'MacroPilotUninstaller.exe'; // unins\ 下的卸载器（= 原始安装 SFX 的一份拷贝，约 11MB）

  // 卸载触发用的环境变量（由注册表 UninstallString 经 cmd 设置，经 SFX 传给内层程序）
  static const String envUninstall = 'MACROPILOT_UNINSTALL';
  static const String envUninstallTarget = 'MACROPILOT_UNINSTALL_TARGET';
  static const String envQuietUninstall = 'MACROPILOT_UNINSTALL_QUIET';
  static const String envDeleteUserData = 'MACROPILOT_DELETE_USER_DATA';

  // 静默在线更新触发用（本体设好后启动 SFX，经 SFX 传给内层安装器）。
  static const String envUpdate = 'MACROPILOT_UPDATE';
  static const String envUpdateTarget = 'MACROPILOT_UPDATE_TARGET';

  // 注册表卸载项（per-user，无需管理员）
  static const String uninstallKey =
      r'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\MacroPilot';

  // .NET 8 桌面运行时（沿用 installer.iss）
  static const String dotNetUrl =
      'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  static const String dotNetManualUrl =
      'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0/runtime?cid=getdotnetcore';

  /// 默认安装目录：%localappdata%\Programs\MacroPilot（per-user，无 UAC）。
  static String get defaultInstallDir {
    final local = Platform.environment['LOCALAPPDATA'] ??
        p.join(Platform.environment['USERPROFILE'] ?? r'C:\', 'AppData', 'Local');
    return p.join(local, 'Programs', appName);
  }

  /// 卸载器副本存放子目录。
  static String uninsDir(String installDir) => p.join(installDir, 'unins');

  /// 默认用户数据目录。若用户在主程序内改过数据目录，这里仍保存 datapath.txt 指针。
  static String get defaultDataDir {
    final appData = Platform.environment['APPDATA'] ??
        p.join(Platform.environment['USERPROFILE'] ?? r'C:\', 'AppData', 'Roaming');
    return p.join(appData, appName);
  }

  static String get dataPointerFile => p.join(defaultDataDir, 'datapath.txt');

  /// 开始菜单快捷方式路径。
  static String get startMenuLnk {
    final appData = Platform.environment['APPDATA'] ??
        p.join(Platform.environment['USERPROFILE'] ?? r'C:\', 'AppData', 'Roaming');
    return p.join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs',
        '$appNameCn.lnk');
  }

  /// 桌面快捷方式路径。
  static String get desktopLnk {
    final home = Platform.environment['USERPROFILE'] ?? r'C:\';
    return p.join(home, 'Desktop', '$appNameCn.lnk');
  }
}
