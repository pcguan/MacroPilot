# MacroPilot 本体（.NET 8 WPF）

键鼠宏助手的主程序。面向使用者的说明见[仓库根 README](../README.md)，这里只记开发相关的结构与关键设计。

- **技术栈**：.NET 8 WPF + WPF-UI（Fluent / Win11）+ System.IO.Ports + System.Drawing
- **数据目录**：默认 `%AppData%\MacroPilot`，可在「配置」页改到自定义目录（指针文件 `datapath.txt` 始终留在默认目录下）
  - `plans.json` 方案（仅在显式「保存方案」时写）
  - `settings.json` 设置（改后端 / 主题 / 开关即写，**不重写方案**）
  - `images\<sha256>.png` 图片条件模板；方案里只存引用 `file:<hash>`
  - `logs\run-YYYYMMDD.log` 运行日志

## 结构

| 目录 / 文件 | 内容 |
| --- | --- |
| `Models/` | `MacroStep`（动作，含子动作与监听动作，递归；点击/移动/拖动/滚轮/键盘/等待/激活窗口/跳转/组合）、`MacroPlan`、`MacroDocument`、`IRunCondition`（方案级与动作级运行条件的公共契约）、`LogEntry` |
| `Input/` | `IInputBackend` 抽象（含 `MouseDown/MouseUp` 供拖动）；`Ch9329Device`（串口硬件）、`NativeInputDevice`（SendInput）、`Ch9329Scanner`（按 USB VID:PID 过滤后探测串口）、`KeyMap`、`ScreenInfo`（多屏拓扑） |
| `Services/` | `Storage` 持久化、`MacroRunner` 执行引擎、`UpdateService` 在线更新、`Changelog`（内置更新日志）、`ScreenMatch` 图片条件匹配、`ImageStore` 图片外置+孤儿清理、`WindowMemory`（窗口几何记忆）、`WindowActivator`、`MouseTraceRecorder` 轨迹录制、`ThemeManager`、`PreciseTimer` |
| `App.xaml(.cs)` | 单实例（唤起已有窗口后静默退出，**不弹模态**）+ 按需提权 + 显式建窗口 + 全局统一 ToolTip 延迟 |
| `MainWindow.xaml(.cs)` | 主界面；partial 拆分：`.Dialogs`（编辑对话框，内含 `CoordBlock`/`RepeatBlock`/`TimeInputRow` 可复用块）、`.RunCondition`（运行条件编辑器 + 方案设置对话框）、`.Hud`（运行悬浮窗）、`.Tray`（系统托盘）、`.Schedule`（定时启动）、`.Update`（更新进度窗） |

## 几个容易踩的设计点

**执行引擎（`MacroRunner`）**
- `RunTop` → `RunGroup`（递归嵌套组合）→ `RunLeaf`；监听动作经 `RunHook` 复用 `RunLeaf/RunGroup`，因此天然支持递归。
- 跳转是独立的 Jump 动作：执行时上报 `_pendingJump`，由 `RunTop` 在当前顶层步骤结束后统一消费（在组合内/监听里执行同样生效）。旧格式挂在其它动作上的 `JumpTarget` **一律忽略、不迁移**——保持引擎只有一条跳转路径。
- 运行的是当前方案的**克隆快照**，避免运行中编辑与引擎争同一个集合。`MacroStep.Clone()` 会带上 `DisplayIndex`，否则运行页序号全是 0。
- 运行页是**扁平列表**（只有顶层行）：自动滚动时要把当前步映射到它的**顶层祖先行**，否则执行组合内部时列表不滚。

**运行条件（`ShouldRun`）**
- 返回值即"是否执行"，取反逻辑是 `Invert ? !found : found`。
- `conditionText` **只在跳过时打日志**，因此必须报**实际观测状态**（如"目标图片未出现（匹配度 0.62/阈值 0.90）"），不能报"条件目标标签"，否则日志读起来正好相反。

**图片匹配（`ScreenMatch`）**
- **固定位置**逐像素比对（不搜索）：抓取模板同尺寸区域，单通道差 ≤ 28 视为相同，相同占比 ≥ 阈值即命中。
- 对位置偏移、缩放、DPI、动画都敏感；调不准时先看日志里的实际匹配度再定阈值。

**拟人化移动**
- `MoveDuration` 次线性于距离 + 随机；`EmitStroke` 用贝塞尔轻弧 + 前快后慢速度剖面（`v(u)=u^a(1-u)^b`）+ 相关抖动。
- native 后端 ~120Hz 连续发点；CH9329 每个路点是一次相对闭环收敛，故按**时间均匀**取路点（空间上前疏后密），保住手感又不增加收敛次数。

**多屏移动（CH9329）**
- 0x04 绝对定位**只映射主屏**；副屏靠读实时屏幕相邻图 BFS、逐段相对收敛跨屏。
- 跨屏拟人化按**逐屏分段**：同屏子段各自 `EmitStroke`，段间一次相对收敛越过共享边。
- 相对增量下限是 **-127**（`-128` 是死值，该轴不动）；移动期须强制系统指针 1:1（关加速 + 速度滑块置 10），否则闭环发散。

**在线更新（`UpdateService`）**
- 有序多源回退（自建源 → GitHub），读各源同格式 `version.json`，下载后校验 SHA-256，失败换源。
- zip 就地更新由一段隐藏 PowerShell 助手完成（备份 → 覆盖 → 保留 `unins\` → 失败回滚 → 重启）。
  **启动该助手必须指定 `WorkingDirectory` 为临时目录**——否则 PowerShell 继承本体的当前目录（= 安装目录），自己占住该目录导致改名失败，更新失败且进程消失。

**热键**
- F9 / F10 / F11 **仅方案执行期间**注册（`RegisterHotKey` + 低级键盘钩子兜底全屏游戏），结束即注销，平时把按键还给系统。

**暂停 / 等待（`MacroRunner.Wait`）**
- 暂停期间必须 `sw.Stop()`、恢复时 `sw.Start()`——否则秒表照走，恢复后剩余时间算成负、等待被"跳过"。

**次数 / 间隔（`RepeatBlock`）**
- 点击/滚动/按键/执行次数 + 重复间隔是同一套 `RepeatBlock`；存 `LoopCount`/`LoopDelayMs`/`LoopDelayUnit`。间隔仅在次数 != 1 时显示与生效；重复时必填校验。

**坐标（`CoordBlock`）**
- 显示器 + 屏内百分比（`MoveMonitor`/`MoveNormX/Y`，换分辨率仍可用）+ 点选/预览。点击/移动一个，拖动两个（起点复用 Move* 字段，终点用 `DragEnd*`）。默认选主屏；单屏时不自动弹屏幕编号。

**运行悬浮窗（`RunHud`）**
- **不设 `Window.Owner`**（否则主窗口最小化会连带隐藏 HUD），只存引用取主题画刷；`WS_EX_NOACTIVATE|TOOLWINDOW` 不抢焦点；700ms 定时重申 `HWND_TOPMOST` 防被压下。开启 HUD 时运行会把本体最小化。

**系统托盘（`.Tray`）** — `Shell_NotifyIcon` P/Invoke + 常驻 HwndSource 钩子；菜单用 WPF `ContextMenu`（弹前 `SetForegroundWindow` 才能正常关闭）。

**定时启动（`.Schedule`）** — **全局单一**（`MacroDocument.ScheduledPlan` + `ScheduleMode` Daily/Once）；0.5s 轮询精确到秒，Daily 命中秒当天一次、Once 触发/错过后自清；到点撞运行按 `ScheduleConflict` 忽略或停当前改跑（`_pendingScheduled` + `OnRunFinished` 拉起）。重命名跟随、删除清除。

**图片外置 / 孤儿清理（`ImageStore`）**
- 存储（plans.json）用 `file:<sha256>` 引用 + `images\<hash>.png` 外置；导出用 `Inline` 内联成 base64 自包含，导入用 `Externalize` 落地转引用（**方案级与动作级条件都要处理**）。保存成功后 `Sweep` 删掉不再被任何方案/剪贴板引用的孤儿图（运行期跳过）。

**单实例 / 窗口拾取（易踩）**
- 单实例撞锁**不能弹模态框**——更新助手会重启本体，模态框会常驻占住安装目录、任务管理器也杀不掉；改为唤起已有窗口后静默退出。
- 窗口拾取器选中项 `Pick` 里**别同步调用会 `SetForegroundWindow` 的操作**（会泵消息、重入导致选错窗口）；FlashPick 延后到 `Dispatcher.BeginInvoke`，选中项取 `e.AddedItems`。

**生成给 Windows 跑的 PowerShell**
- 无 BOM 写出的 `.ps1` 会被 Windows PowerShell 按 ANSI/GBK 读，中文（含注释）会破坏解析。要么脚本体**纯 ASCII**，要么**带 UTF-8 BOM** 写出（见 `UpdateService.ApplyZipUpdateAndExit` 与 `shot-pcguan.sh`）。

## 构建

```bat
build.bat        :: 自包含（零依赖，双击即用）
build.bat fd     :: 框架依赖（体积小，需 .NET 8 桌面运行时）
```
输出到 `%USERPROFILE%\MacroPilot_review`（不动桌面）。正式分发走安装器，见根 README 的「发布流程」。
