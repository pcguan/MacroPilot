import 'package:fluent_ui/fluent_ui.dart';

/// Fluent/Win11 风格主题，跟随系统明暗。背景透明以便透出 Mica（不支持时降级为 micaBase）。
class InstallerTheme {
  static const Color brand = Color(0xFF2D6CDF);

  // Mica 不可用时的纯色降级底
  static const Color micaLight = Color(0xFFF3F3F3);
  static const Color micaDark = Color(0xFF202020);

  static FluentThemeData light() => FluentThemeData(
        brightness: Brightness.light,
        accentColor: Colors.blue,
        scaffoldBackgroundColor: Colors.transparent,
        micaBackgroundColor: Colors.transparent,
        fontFamily: 'Microsoft YaHei UI',
      );

  static FluentThemeData dark() => FluentThemeData(
        brightness: Brightness.dark,
        accentColor: Colors.blue,
        scaffoldBackgroundColor: Colors.transparent,
        micaBackgroundColor: Colors.transparent,
        fontFamily: 'Microsoft YaHei UI',
      );
}
