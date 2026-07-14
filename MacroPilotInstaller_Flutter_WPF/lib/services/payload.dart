import 'dart:io';
import 'package:archive/archive.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:path/path.dart' as p;

/// 内嵌主程序 payload（assets/payload/app.zip）解压到安装目录。
class PayloadService {
  static const String assetPath = 'assets/payload/app.zip';

  /// 解压 payload 到 destDir。onFile(已完成数, 总数) 上报进度。
  static Future<void> extractTo(
      String destDir, void Function(int done, int total) onFile) async {
    final data = await rootBundle.load(assetPath);
    final bytes = data.buffer.asUint8List();
    final archive = ZipDecoder().decodeBytes(bytes);

    final files = archive.files.where((f) => f.isFile).toList();
    final total = files.length;
    Directory(destDir).createSync(recursive: true);

    int done = 0;
    for (final f in archive.files) {
      final outPath = p.join(destDir, f.name);
      if (f.isFile) {
        final outFile = File(outPath);
        outFile.parent.createSync(recursive: true);
        outFile.writeAsBytesSync(f.content as List<int>);
        done++;
        onFile(done, total);
      } else {
        Directory(outPath).createSync(recursive: true);
      }
    }
  }

  /// 估算解压后总字节（用于注册表 EstimatedSize）。
  static Future<int> uncompressedSize() async {
    final data = await rootBundle.load(assetPath);
    final archive = ZipDecoder().decodeBytes(data.buffer.asUint8List());
    int sum = 0;
    for (final f in archive.files) {
      if (f.isFile) sum += f.size;
    }
    return sum;
  }
}
