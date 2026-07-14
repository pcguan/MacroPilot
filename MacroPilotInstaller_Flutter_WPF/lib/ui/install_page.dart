import 'dart:io';
import 'package:fluent_ui/fluent_ui.dart';
import 'package:file_picker/file_picker.dart';
import '../app_meta.dart';
import '../services/dotnet.dart';
import '../services/install_log.dart';
import '../services/installer.dart';
import '../services/registry.dart';
import 'title_bar.dart';

enum _Step { config, progress, done, error }

class InstallPage extends StatefulWidget {
  const InstallPage({super.key});
  @override
  State<InstallPage> createState() => _InstallPageState();
}

class _InstallPageState extends State<InstallPage> {
  _Step _step = _Step.config;
  late final TextEditingController _dirCtrl =
      TextEditingController(text: AppMeta.defaultInstallDir);
  bool _desktopShortcut = true;
  bool _launchAfter = true;

  double _progress = 0;
  String _progressMsg = '';
  String _errorMsg = '';

  @override
  void dispose() {
    _dirCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickFolder() async {
    final dir = await FilePicker.platform.getDirectoryPath(
      dialogTitle: '选择安装位置',
    );
    if (dir == null || dir.isEmpty) return;
    final endsWithApp =
        dir.toLowerCase().endsWith(AppMeta.appName.toLowerCase());
    setState(() =>
        _dirCtrl.text = endsWithApp ? dir : '$dir\\${AppMeta.appName}');
  }

  Future<String> _askDotNet() async {
    final r = await showDialog<String>(
      context: context,
      builder: (ctx) => ContentDialog(
        title: const Text('需要 .NET 8 桌面运行时'),
        content: Text(
            '运行 ${AppMeta.appNameCn} 需要「.NET 8 桌面运行时」，当前系统未检测到。\n\n'
            '可由本程序自动下载安装，或前往官网手动安装。'),
        actions: [
          Button(
              onPressed: () => Navigator.pop(ctx, 'exit'),
              child: const Text('退出安装')),
          Button(
              onPressed: () => Navigator.pop(ctx, 'manual'),
              child: const Text('手动安装')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, 'auto'),
              child: const Text('自动下载安装')),
        ],
      ),
    );
    return r ?? 'exit';
  }

  /// 目标目录已存在安装时，询问处理方式。返回 'replace' / 'update' / 'cancel'。
  Future<String> _askExisting(String dir) async {
    final t = FluentTheme.of(context);
    final r = await showDialog<String>(
      context: context,
      builder: (ctx) => ContentDialog(
        title: const Text('该目录已存在安装'),
        constraints: const BoxConstraints(maxWidth: 460),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('检测到目标目录已安装过 ${AppMeta.appNameCn}：', style: t.typography.body),
            const SizedBox(height: 4),
            Text(dir, style: t.typography.caption),
            const SizedBox(height: 14),
            Text('· 全量替换：先清空该目录再全新安装，移除旧版残留文件',
                style: t.typography.body),
            const SizedBox(height: 6),
            Text('· 增量更新：用新版本覆盖现有文件，保留目录中的其他文件',
                style: t.typography.body),
          ],
        ),
        actions: [
          Button(
              onPressed: () => Navigator.pop(ctx, 'cancel'),
              child: const Text('取消')),
          Button(
              onPressed: () => Navigator.pop(ctx, 'update'),
              child: const Text('增量更新')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, 'replace'),
              child: const Text('全量替换')),
        ],
      ),
    );
    return r ?? 'cancel';
  }

  /// 确保这些目录下的旧本体都已退出。普通杀不掉（管理员本体）时弹框问是否提权强杀。
  /// 返回 true=已全部退出可继续；false=用户取消或仍无法结束，应中止安装。
  Future<bool> _ensureNotRunning(List<String> dirs) async {
    InstallLog.write('_ensureNotRunning: dirs=$dirs');
    Future<bool> anyRunning() async {
      for (final d in dirs) {
        if (await InstallerService.isAppRunning(d)) return true;
      }
      return false;
    }

    // 普通杀，然后轮询等文件释放（非管理员本体通常很快退出）。
    for (final d in dirs) {
      await InstallerService.killAppProcesses(d);
    }
    for (int i = 0; i < 6; i++) {
      if (!await anyRunning()) { InstallLog.write('_ensureNotRunning: 已全部退出'); return true; }
      await Future.delayed(const Duration(milliseconds: 300));
    }
    InstallLog.write('_ensureNotRunning: 普通杀后仍在运行，将询问提权强杀');

    // 仍被占用 → 多半是本体以管理员身份运行（普通权限杀不掉），问用户是否提权强杀。
    if (!mounted) return false;
    if (!await _askForceKill()) return false;

    await InstallerService.killAppProcessesElevated();
    for (int i = 0; i < 12; i++) {
      if (!await anyRunning()) return true;
      await Future.delayed(const Duration(milliseconds: 300));
    }

    if (mounted) {
      await _showInfo('无法结束正在运行的本体',
          '可能取消了提权授权，或进程仍被占用。请手动退出 ${AppMeta.appNameCn} 后重试。');
    }
    return false;
  }

  Future<bool> _askForceKill() async {
    final t = FluentTheme.of(context);
    final r = await showDialog<bool>(
      context: context,
      builder: (ctx) => ContentDialog(
        title: const Text('本体正在运行'),
        constraints: const BoxConstraints(maxWidth: 460),
        content: Text(
          '检测到 ${AppMeta.appNameCn} 正在运行且无法正常结束'
          '（通常是以管理员身份运行）。\n\n是否强制结束它并继续安装？将弹出一次 UAC 授权。',
          style: t.typography.body,
        ),
        actions: [
          Button(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('取消')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('强制结束并继续')),
        ],
      ),
    );
    return r ?? false;
  }

  Future<void> _showInfo(String title, String msg) async {
    await showDialog(
      context: context,
      builder: (ctx) => ContentDialog(
        title: Text(title),
        content: Text(msg),
        actions: [
          FilledButton(
              onPressed: () => Navigator.pop(ctx), child: const Text('知道了')),
        ],
      ),
    );
  }

  Future<String> _askOldInstall(String oldDir, String newDir) async {
    final t = FluentTheme.of(context);
    final r = await showDialog<String>(
      context: context,
      builder: (ctx) => ContentDialog(
        title: const Text('检测到旧安装目录'),
        constraints: const BoxConstraints(maxWidth: 500),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('当前系统记录的旧安装位置与本次选择不同：', style: t.typography.body),
            const SizedBox(height: 8),
            Text('旧位置：$oldDir', style: t.typography.caption),
            const SizedBox(height: 4),
            Text('新位置：$newDir', style: t.typography.caption),
            const SizedBox(height: 14),
            Text('可在安装前清理旧目录，避免留下旧版残留。用户方案数据不在安装目录中，不受影响。',
                style: t.typography.body),
          ],
        ),
        actions: [
          Button(
              onPressed: () => Navigator.pop(ctx, 'cancel'),
              child: const Text('取消')),
          Button(
              onPressed: () => Navigator.pop(ctx, 'keep'),
              child: const Text('保留旧目录')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, 'clean'),
              child: const Text('清理旧目录')),
        ],
      ),
    );
    return r ?? 'cancel';
  }

  Future<void> _startInstall() async {
    final dir = _dirCtrl.text.trim();
    InstallLog.write('_startInstall: dir=$dir');

    final oldDir = await RegistryService.readInstallLocation();
    String? oldDirToClean;
    if (oldDir != null &&
        oldDir.isNotEmpty &&
        !_samePath(oldDir, dir) &&
        InstallerService.isInstalled(oldDir)) {
      final choice = await _askOldInstall(oldDir, dir);
      if (choice == 'cancel') return;
      if (choice == 'clean') oldDirToClean = oldDir;
    }

    // 目录已存在安装：先问全量替换 / 增量更新 / 取消。
    bool fullReplace = false;
    if (InstallerService.isInstalled(dir)) {
      final choice = await _askExisting(dir);
      if (choice == 'cancel') return; // 留在配置页
      fullReplace = choice == 'replace';
    }

    // 确保旧本体已退出（否则文件被占用无法覆盖/清理）。管理员权限的本体会弹 UAC 强杀确认。
    final targets = <String>[if (InstallerService.isInstalled(dir)) dir];
    if (oldDirToClean != null) targets.add(oldDirToClean);
    if (targets.isNotEmpty) {
      try {
        if (!await _ensureNotRunning(targets)) return;
      } catch (e, st) {
        InstallLog.write('_ensureNotRunning 抛异常: $e\n$st');
        return _fail('结束正在运行的旧程序时出错：$e');
      }
    }

    setState(() {
      _step = _Step.progress;
      _progress = 0;
      _progressMsg = '正在检查运行环境…';
    });

    if (!DotNetService.isDesktopRuntime8Installed()) {
      final choice = await _askDotNet();
      if (choice == 'exit') {
        exit(0);
      } else if (choice == 'manual') {
        await DotNetService.openManualPage();
        exit(0);
      }
      try {
        setState(() => _progressMsg = '正在下载 .NET 8 运行时…');
        final file = await DotNetService.download((f) {
          setState(() {
            _progressMsg = f < 0
                ? '正在下载 .NET 8 运行时…'
                : '正在下载 .NET 8 运行时…（${(f * 100).toStringAsFixed(0)}%）';
          });
        });
        setState(() => _progressMsg = '正在安装 .NET 8 运行时…（可能弹出 UAC）');
        final code = await DotNetService.installSilently(file);
        if (code != 0 && code != 3010) {
          return _fail('.NET 运行时安装未成功（代码 $code）。可改用手动安装。');
        }
        if (!DotNetService.isDesktopRuntime8Installed()) {
          return _fail('运行时安装后仍未检测到 .NET 8 桌面运行时。');
        }
      } catch (e) {
        return _fail('下载或安装 .NET 运行时失败：$e');
      }
    }

    try {
      if (oldDirToClean != null) {
        setState(() => _progressMsg = '正在清理旧安装目录…');
        await InstallerService.killAppProcesses(oldDirToClean);
        final leftovers = InstallerService.cleanInstallDir(oldDirToClean);
        if (leftovers.isNotEmpty) {
          setState(() => _progressMsg = '旧目录部分文件被占用，继续安装新版本…');
          await Future.delayed(const Duration(milliseconds: 600));
        }
      }
      await InstallerService().install(
        installDir: dir,
        desktopShortcut: _desktopShortcut,
        fullReplace: fullReplace,
        onProgress: (frac, msg) => setState(() {
          _progress = frac;
          _progressMsg = msg;
        }),
      );
      setState(() => _step = _Step.done);
    } catch (e) {
      _fail('安装失败：$e');
    }
  }

  void _fail(String msg) => setState(() {
        _step = _Step.error;
        _errorMsg = msg;
      });

  bool _samePath(String a, String b) {
    String norm(String s) =>
        s.trim().replaceAll('/', r'\').replaceAll(RegExp(r'\\+$'), '').toLowerCase();
    return norm(a) == norm(b);
  }

  Future<void> _finish() async {
    if (_launchAfter) {
      await InstallerService.launchApp(_dirCtrl.text.trim());
    }
    exit(0);
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      color: Colors.transparent,
      child: Column(
        children: [
          const InstallerTitleBar(title: '键鼠宏助手 安装程序'),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(32, 8, 32, 0),
              child: switch (_step) {
                _Step.config => _buildConfig(),
                _Step.progress => _buildProgress(),
                _Step.done => _buildDone(),
                _Step.error => _buildError(),
              },
            ),
          ),
          _footer(),
        ],
      ),
    );
  }

  // 居中头部：应用图标 + 标题 + 版本号
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

  Widget _buildConfig() {
    final t = FluentTheme.of(context);
    final dotnetOk = DotNetService.isDesktopRuntime8Installed();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('安装 ${AppMeta.appNameCn}', '版本 ${AppMeta.appVersion}'),
        const SizedBox(height: 30),
        // 设置项卡片：安装位置 + 更改 + 桌面快捷方式
        Card(
          padding: const EdgeInsets.all(18),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('安装位置', style: t.typography.bodyStrong),
              const SizedBox(height: 8),
              Row(
                children: [
                  Expanded(child: TextBox(controller: _dirCtrl)),
                  const SizedBox(width: 10),
                  Button(onPressed: _pickFolder, child: const Text('更改…')),
                ],
              ),
              const SizedBox(height: 20),
              Checkbox(
                checked: _desktopShortcut,
                onChanged: (v) => setState(() => _desktopShortcut = v ?? true),
                content: const Text('创建桌面快捷方式'),
              ),
            ],
          ),
        ),
        const SizedBox(height: 14),
        InfoBar(
          title: Text(dotnetOk
              ? '已检测到 .NET 8 桌面运行时'
              : '未检测到 .NET 8 桌面运行时，安装时将提示下载'),
          severity:
              dotnetOk ? InfoBarSeverity.success : InfoBarSeverity.warning,
          isLong: false,
        ),
      ],
    );
  }

  Widget _buildProgress() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('正在安装…', '请稍候'),
        const Spacer(),
        Text(_progressMsg, style: FluentTheme.of(context).typography.body),
        const SizedBox(height: 12),
        ProgressBar(value: _progress <= 0 ? null : _progress * 100),
        const Spacer(),
      ],
    );
  }

  Widget _buildDone() {
    final t = FluentTheme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('安装完成', '${AppMeta.appNameCn} 已安装到您的电脑'),
        const SizedBox(height: 28),
        InfoBar(
          title: const Text('安装成功'),
          severity: InfoBarSeverity.success,
          isLong: false,
        ),
        const SizedBox(height: 16),
        Checkbox(
          checked: _launchAfter,
          onChanged: (v) => setState(() => _launchAfter = v ?? true),
          content: Text('立即运行 ${AppMeta.appNameCn}'),
        ),
        const Spacer(),
        Text('感谢使用', style: t.typography.caption),
      ],
    );
  }

  Widget _buildError() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 10),
        _centerHeader('安装未完成', '出现了一个问题'),
        const SizedBox(height: 28),
        InfoBar(
          title: const Text('安装失败'),
          content: Text(_errorMsg),
          severity: InfoBarSeverity.error,
          isLong: true,
        ),
      ],
    );
  }

  Widget _footer() {
    final List<Widget> buttons;
    switch (_step) {
      case _Step.config:
        buttons = [
          Button(onPressed: () => exit(0), child: const Text('取消')),
          const SizedBox(width: 10),
          FilledButton(onPressed: _startInstall, child: const Text('开始安装')),
        ];
        break;
      case _Step.progress:
        buttons = const [
          SizedBox(width: 18, height: 18, child: ProgressRing(strokeWidth: 2.5)),
        ];
        break;
      case _Step.done:
        buttons = [
          FilledButton(onPressed: _finish, child: const Text('完成')),
        ];
        break;
      case _Step.error:
        buttons = [
          FilledButton(onPressed: () => exit(1), child: const Text('退出')),
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
