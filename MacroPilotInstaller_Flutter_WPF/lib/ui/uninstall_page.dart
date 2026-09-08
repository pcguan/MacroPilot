import 'dart:io';
import 'package:fluent_ui/fluent_ui.dart';
import '../app_meta.dart';
import '../theme.dart';
import '../services/installer.dart';
import 'title_bar.dart';

enum _UStep { confirm, progress, done }

class UninstallPage extends StatefulWidget {
  final String target;
  final bool deleteUserData;
  const UninstallPage(
      {super.key, required this.target, this.deleteUserData = false});
  @override
  State<UninstallPage> createState() => _UninstallPageState();
}

class _UninstallPageState extends State<UninstallPage> {
  _UStep _step = _UStep.confirm;
  double _progress = 0;
  String _msg = '';
  late bool _deleteUserData = widget.deleteUserData;
  UninstallResult? _result;
  bool _cleanupScheduled = false;

  Future<void> _doUninstall() async {
    setState(() => _step = _UStep.progress);
    final result = await UninstallerService.performUninstall(
      widget.target,
      (frac, msg) {
      setState(() {
        _progress = frac;
        _msg = msg;
      });
      },
      deleteUserData: _deleteUserData,
    );
    _result = result;
    UninstallerService.scheduleFinalClean(widget.target);
    _cleanupScheduled = true;
    setState(() => _step = _UStep.done);
  }

  Future<void> _finish() async {
    if (!_cleanupScheduled) {
      UninstallerService.scheduleFinalClean(widget.target);
      _cleanupScheduled = true;
    }
    await Future.delayed(const Duration(milliseconds: 150));
    exit(0);
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      color: Colors.transparent,
      child: Column(
        children: [
          const InstallerTitleBar(title: '键鼠宏助手 卸载程序'),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(32, 8, 32, 0),
              child: switch (_step) {
                _UStep.confirm => _buildConfirm(),
                _UStep.progress => _buildProgress(),
                _UStep.done => _buildDone(),
              },
            ),
          ),
          _footer(),
        ],
      ),
    );
  }

  Widget _centerHeader(String title, String subtitle) {
    final t = FluentTheme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        Image.asset('assets/app_icon.png', width: 56, height: 56),
        const SizedBox(height: 14),
        Text(title, style: t.typography.title),
        const SizedBox(height: 4),
        Text(subtitle, style: t.typography.caption),
      ],
    );
  }

  Widget _buildConfirm() {
    final t = FluentTheme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('卸载 ${AppMeta.appNameCn}', '将从您的电脑移除本程序'),
        const SizedBox(height: 30),
        Card(
          padding: const EdgeInsets.all(18),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                '确定要卸载 ${AppMeta.appNameCn} 吗？\n'
                '程序文件与快捷方式将被删除。',
                style: t.typography.body,
              ),
              const SizedBox(height: 14),
              Checkbox(
                checked: _deleteUserData,
                onChanged: (v) =>
                    setState(() => _deleteUserData = v ?? false),
                content: const Text('同时删除方案和日志数据'),
              ),
              const SizedBox(height: 6),
              Text(
                _deleteUserData
                    ? '将删除默认数据目录中的 plans.json、logs 和数据目录指针；若使用自定义数据目录，只删除其中的 plans.json 与 logs。'
                    : '您保存的方案数据不在安装目录中，不会被删除。',
                style: t.typography.caption,
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildProgress() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('正在卸载…', '请稍候'),
        const Spacer(),
        Text(_msg, style: FluentTheme.of(context).typography.body),
        const SizedBox(height: 12),
        ProgressBar(value: _progress <= 0 ? null : _progress * 100),
        const Spacer(),
      ],
    );
  }

  Widget _buildDone() {
    final result = _result;
    final hasIssues = result?.warnings.isNotEmpty == true;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('卸载完成', '${AppMeta.appNameCn} 已从您的电脑移除'),
        const SizedBox(height: 28),
        InfoBar(
          title: Text(hasIssues ? '已卸载，部分数据未能删除' : '已成功卸载'),
          content: hasIssues
              ? Text(_issueSummary(result!))
              : null,
          severity:
              hasIssues ? InfoBarSeverity.warning : InfoBarSeverity.success,
          isLong: hasIssues,
        ),
      ],
    );
  }

  String _issueSummary(UninstallResult result) {
    final lines = <String>[];
    if (result.warnings.isNotEmpty) {
      lines.add('有 ${result.warnings.length} 个快捷方式或数据项未能立即删除。');
      lines.add(result.warnings.take(3).join('\n'));
    }
    return lines.join('\n');
  }

  Widget _footer() {
    final List<Widget> buttons;
    switch (_step) {
      case _UStep.confirm:
        buttons = [
          Button(onPressed: () => exit(0), child: const Text('取消')),
          const SizedBox(width: 10),
          FilledButton(
            style: ButtonStyle(
              backgroundColor: WidgetStateProperty.all(InstallerTheme.brand),
            ),
            onPressed: _doUninstall,
            child: const Text('卸载'),
          ),
        ];
        break;
      case _UStep.progress:
        buttons = const [
          SizedBox(width: 18, height: 18, child: ProgressRing(strokeWidth: 2.5)),
        ];
        break;
      case _UStep.done:
        buttons = [
          FilledButton(onPressed: _finish, child: const Text('完成')),
        ];
        break;
    }
    return Container(
      decoration: const BoxDecoration(
        border: Border(top: BorderSide(color: Color(0x1A808080), width: 1)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 32, vertical: 18),
      child: Row(mainAxisAlignment: MainAxisAlignment.end, children: buttons),
    );
  }
}
