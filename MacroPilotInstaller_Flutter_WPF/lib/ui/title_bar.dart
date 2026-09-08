import 'package:fluent_ui/fluent_ui.dart';
import 'package:window_manager/window_manager.dart';
import '../theme.dart';

/// 沉浸式自定义标题栏：与窗口背景一致（透出 Mica），左侧品牌+标题可拖动，右侧最小化/关闭。
class InstallerTitleBar extends StatelessWidget {
  final String title;
  const InstallerTitleBar({super.key, required this.title});

  @override
  Widget build(BuildContext context) {
    final t = FluentTheme.of(context);
    return SizedBox(
      height: 40,
      child: Row(
        children: [
          const SizedBox(width: 12),
          Container(
            width: 18,
            height: 18,
            decoration: BoxDecoration(
              color: InstallerTheme.brand,
              borderRadius: BorderRadius.circular(4),
            ),
            child: const Icon(FluentIcons.touch_pointer,
                size: 11, color: Colors.white),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: DragToMoveArea(
              child: Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  title,
                  style: TextStyle(
                      fontSize: 12.5,
                      color: t.resources.textFillColorSecondary),
                ),
              ),
            ),
          ),
          _CaptionButton(
            icon: FluentIcons.chrome_minimize,
            onPressed: () => windowManager.minimize(),
          ),
          _CaptionButton(
            icon: FluentIcons.chrome_close,
            danger: true,
            onPressed: () => windowManager.close(),
          ),
        ],
      ),
    );
  }
}

class _CaptionButton extends StatefulWidget {
  final IconData icon;
  final VoidCallback onPressed;
  final bool danger;
  const _CaptionButton(
      {required this.icon, required this.onPressed, this.danger = false});
  @override
  State<_CaptionButton> createState() => _CaptionButtonState();
}

class _CaptionButtonState extends State<_CaptionButton> {
  bool _hover = false;
  @override
  Widget build(BuildContext context) {
    final t = FluentTheme.of(context);
    final Color bg = !_hover
        ? Colors.transparent
        : (widget.danger
            ? const Color(0xFFC42B1C)
            : t.resources.subtleFillColorSecondary);
    final Color fg = (_hover && widget.danger)
        ? Colors.white
        : t.resources.textFillColorPrimary;
    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onPressed,
        child: Container(
          width: 44,
          height: 40,
          color: bg,
          child: Icon(widget.icon, size: 12, color: fg),
        ),
      ),
    );
  }
}
