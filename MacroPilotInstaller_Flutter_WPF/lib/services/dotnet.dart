import 'dart:io';
import 'package:path/path.dart' as p;
import '../app_meta.dart';

/// .NET 8 桌面运行时检测与安装（移植自 installer.iss 的 DotNet8DesktopInstalled / InstallDotNet）。
class DotNetService {
  /// 看共享框架目录下有无 8.* 子目录。
  static bool isDesktopRuntime8Installed() {
    final pf = Platform.environment['ProgramFiles'] ?? r'C:\Program Files';
    final candidates = <String>[
      p.join(pf, 'dotnet', 'shared', 'Microsoft.WindowsDesktop.App'),
      r'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App',
    ];
    for (final base in candidates) {
      final dir = Directory(base);
      if (!dir.existsSync()) continue;
      for (final e in dir.listSync()) {
        if (e is Directory) {
          final name = p.basename(e.path);
          if (name.startsWith('8.')) return true;
        }
      }
    }
    return false;
  }

  /// 下载运行时到临时文件，progress 回调 0..1（总长未知时回调 -1）。
  static Future<File> download(void Function(double) progress) async {
    final tmp = p.join(Directory.systemTemp.path,
        'windowsdesktop-runtime-8-x64.exe');
    final out = File(tmp);
    final client = HttpClient();
    try {
      var req = await client.getUrl(Uri.parse(AppMeta.dotNetUrl));
      var resp = await req.close();
      // aka.ms 会 302 跳转，HttpClient 默认跟随重定向。
      final total = resp.contentLength;
      final sink = out.openWrite();
      int received = 0;
      await for (final chunk in resp) {
        sink.add(chunk);
        received += chunk.length;
        progress(total > 0 ? received / total : -1);
      }
      await sink.close();
      return out;
    } finally {
      client.close(force: true);
    }
  }

  /// 静默安装（运行时安装包自带 requireAdministrator，会触发 UAC）。
  /// 返回退出码：0 成功，3010 需重启，其它失败。
  static Future<int> installSilently(File runtimeExe) async {
    final r = await Process.run(
        runtimeExe.path, ['/install', '/quiet', '/norestart']);
    return r.exitCode;
  }

  /// 打开官方手动下载页。
  static Future<void> openManualPage() async {
    await Process.start('cmd', ['/c', 'start', '', AppMeta.dotNetManualUrl],
        runInShell: true);
  }
}
