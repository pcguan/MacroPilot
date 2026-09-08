import 'dart:io';
import 'package:path/path.dart' as p;

/// 安装/卸载过程日志：写到 %TEMP%\macropilot_install.log，用于诊断"静默退出"这类无提示故障。
/// 每次安装追加，带时间戳；写日志本身绝不抛异常。
class InstallLog {
  static String get path =>
      p.join(Directory.systemTemp.path, 'macropilot_install.log');

  static void write(String msg) {
    try {
      final t = DateTime.now().toIso8601String();
      File(path).writeAsStringSync('$t  $msg\n', mode: FileMode.append, flush: true);
    } catch (_) {}
  }
}
