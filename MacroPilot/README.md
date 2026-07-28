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
| `Models/` | `MacroStep`（动作，含子动作与 **7 个监听挂点**，递归；点击/点击坐标/点击图片/移动/移动图片/拖动/滚轮/按键/文本/等待/激活窗口/跳转/组合；带稳定 `Id` 供跳转绑定）、`MacroPlan`、`MacroDocument`、`IRunCondition` + `ConditionItem`（多条件 + 与/或，三级共用的公共契约）、`LogEntry` |
| `Input/` | `IInputBackend` 抽象（含 `MouseDown/MouseUp` 供拖动）；`Ch9329Device`（串口硬件）、`NativeInputDevice`（SendInput）、`Ch9329Scanner`（按 USB VID:PID 过滤后探测串口）、`KeyMap`、`ScreenInfo`（多屏拓扑） |
| `Services/` | `Storage` 持久化、`MacroRunner` 执行引擎、`UpdateService` 在线更新、`Changelog`（内置更新日志）、`ScreenMatch` 图片搜索匹配、`ImageStore` 图片外置+孤儿清理、`RandomText`（逆向正则：按模式生成随机串）、`WindowMemory`（窗口几何记忆）、`WindowActivator`、`MouseTraceRecorder` 轨迹录制、`ThemeManager`、`PreciseTimer` |
| `App.xaml(.cs)` | 单实例（唤起已有窗口后静默退出，**不弹模态**）+ 按需提权 + 显式建窗口 + 全局统一 ToolTip 延迟 |
| `MainWindow.xaml(.cs)` | 主界面；partial 拆分：`.Dialogs`（编辑对话框，内含 `CoordBlock`/`RepeatBlock`/`TimeInputRow`/`EdgeCell`/`ClickImagePanel`/`RectPicker` 可复用块）、`.RunCondition`（运行条件编辑器 + 方案设置对话框）、`.Snip`（截图覆盖层：自动框窗 + 标注工具条）、`.Hud`（运行悬浮窗）、`.Tray`（系统托盘）、`.Schedule`（定时启动）、`.Update`（更新进度窗） |
| `../MacroPilot.Tests/` | xUnit 单元测试。`FakeBackend` 顶掉真实键鼠输出、只记录调用序列，于是「方案跑出来的动作序列」可直接断言；业务逻辑（条件 / 挂点 / 循环 / 跳转 / 停止暂停）全覆盖，实际注入层不测。**改模型字段必跑**——`ModelIntegrityTests` 用反射查 `Clone`/`RunCondition.Copy` 有没有漏拷 |

## 几个容易踩的设计点

**执行引擎（`MacroRunner`）**
- `RunTop` → `RunGroup`（递归嵌套组合）→ `RunLeaf`；监听动作经 `RunHook` 复用 `RunLeaf/RunGroup`，因此天然支持递归。
- 跳转是独立的 Jump 动作：执行时上报 `_pendingJump`，由 `RunTop` 在当前顶层步骤结束后统一消费（在组合内/监听里执行同样生效）。旧格式挂在其它动作上的 `JumpTarget` **一律忽略、不迁移**——保持引擎只有一条跳转路径。
- 跳转目标绑定 `MacroStep.Id`（稳定身份）而非序号，插入/删除/排序都不会指错；只有序号的旧存档回退按序号定位。`Clone` 必须带上 `Id`（运行副本、撤销快照靠它继续指向目标），**复制粘贴则要 `RenewId()`** 换新身份，否则副本与原件抢同一个跳转目标。
- 监听有 **7 个挂点**，统一经 `MacroStep.HookList()` 枚举——图片收集、运行页映射、脏对比等所有「遍历监听」的地方都走它，别再手写多连 if（漏一处就孤儿图误删 / 映射缺失）。
- 条件判定（含重复检查的整个等待）跑在动作高亮**之前**，故 `RunTop` 取到 step 后要先发一次 `StepStateChanged(step,true)`，否则等待期间界面上看不出卡在哪一步。
- 运行页高亮取「本轮最后一个开始执行的动作」并**保持到下一个动作开始**：瞬时动作的 开始→结束 会落在同一个刷新周期里互相抵消，逐条 `IsExecuting = on` 会导致根本看不到高亮。
- 运行的是当前方案的**克隆快照**，避免运行中编辑与引擎争同一个集合。`MacroStep.Clone()` 会带上 `DisplayIndex`，否则运行页序号全是 0。
- 运行页是**扁平列表**（只有顶层行）：自动滚动时要把当前步映射到它的**顶层祖先行**，否则执行组合内部时列表不滚。

**运行条件（`ShouldRun`）**
- 条件是**一组** `ConditionItem` + `And`/`Or`，取反是**每条独立**的（`Invert ? !found : found`）。求值短路：And 遇假、Or 遇真即停——图片条件要抓屏搜索，能省一次是一次。
- 历史存档的单条 `RunConditionXxx` 字段由 `RunCondition.Normalize` 并入列表后清空。**幂等**，且判定 / 图片收集 / 编辑器三个入口都会先调一次，任一路径漏调也不会丢条件。
- `conditionText` **只在跳过时打日志**，因此必须报**实际观测状态**（如「目标图片未出现（区域内最高相似度 0.62 / 阈值 0.90）」），不能报「条件目标标签」，否则日志读起来正好相反。
- **重复检查**：不满足时按间隔轮询重判（走 `Wait`，暂停/停止照常响应），每次判定都记一条带相似度的日志。方案级历来就是「空转等到满足」，故忽略该勾选始终等待，但间隔与次数上限对它生效。

**图片匹配（`ScreenMatch.FindMatches`）**
- **区域内滑窗搜索**（不是固定位置比对）：返回所有 ≥ 阈值的命中中心点，NMS 去重后按阅读顺序排序，供「匹配第几个」索引。运行条件、「点击图片」、「移动图片」共用这一套（后两者共用 `LocateImage`，区别只在到位后点不点）。
- 相似度是**模板梯度加权**的一致像素占比：结构像素（文字/图标边缘）权重高、平坦背景权重 1。**等权的「90% 一致」对平坦底+小特征的模板必然失效**——特征只占 ~15% 面积时任何近色平坦区都能凑够分（实测一屏 33 个假命中）。
- 必须**逐像素扫描**：加权指标下偏 1px 分数就跌破阈值，隔点扫会把真目标整个漏掉。性能靠**结构像素优先 + 预算早退**（按权重降序比，假位置在前百来个像素上就爆预算），872×762 区域实测 71ms。
- 早退预算按 `DiagSlack` 放宽一档，用来算出未命中时的**区域内最高相似度**——否则只知道「没到阈值」，无从判断是画面变了还是差一点。
- 改这个算法前先用 Python + 真实截图离线验证，再在 corp-win 上用独立 harness 对齐 C# 移植。

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

**全屏覆盖层（点选 / 截图 / 区域编辑 / 预览）**
- **要实时跟手的**（点选十字线）必须**每屏一个独立窗**：铺满虚拟桌面的单个大窗按整窗面积合成，几千像素宽时十字线明显跟不上鼠标。多窗模态用 `Dispatcher.PushFrame` 替代 `ShowDialog`。**冻屏快照类**（截图框选 / 编辑区域）可以用单个大窗。
- 跟手三件套：`Cursor=None` 藏系统光标（软件绘制永远落后硬件光标 1-2 帧，同屏可比就永远像在「追」）、`CompositionTarget.Rendering` 每帧直读 `GetCursorPos`（鼠标事件携带的是过去的位置）、元素用 `TranslateTransform` 移动（不触发布局）。
- 初始几何**别依赖某个时点的 `ActualWidth`**：窗口从默认尺寸被 `SetWindowPos` 撑到全屏要经历多轮布局，`Loaded` 时它常是 0。正解是用户第一次上手前，每轮 `SizeChanged` 都从原始虚拟像素重新推导；结果也要在**关窗前**算好。
- 子覆盖层的 `KeyDown` **必须 `e.Handled = true`**——否则 Enter/Esc 会透传到父对话框触发 `IsDefault`/`IsCancel`，把整个编辑窗一起关掉。
- 矩形交互统一走 `RectPicker`（四角/四边命中 + 内部拖动 + 外部重画 + 方向键微调），截图框选与编辑限制区域共用，手感一致。自动框窗与手动拖拽的取舍按**位移阈值**判定（原地点击才用窗口矩形），别在 `MouseDown` 就下结论。编辑限制区域**只在没有既有区域时**进入自动框窗阶段（有区域就直接画出来给你调）。
- 浮动工具条走 `OverlayToolbar`（截图 / 编辑区域共用）。摆位范围必须传**选区所在那块屏的工作区**而不是整个画布：跨屏画布上「选区下方」可能落到另一块屏，而最大化窗口的下边缘正贴着任务栏——塞进那条带子会被任务栏（同为置顶窗口）盖住，看起来就是「工具条不见了」。另外它从 `Collapsed` 变可见的那一帧 `ActualHeight` 还是 0，要退回 `DesiredSize`，否则按 0 高度算会被推出屏幕。
- 标注（`Annot`）画完保持选中态，可再拖动 / 改大小：矩形类复用 `RectPicker` 的八向手柄，箭头拖两端。撤销是**回滚闭包栈**（新增/改动/删除各压一条），不是「删掉最后一个」。马赛克预览＝裁剪→按块尺寸缩小→`NearestNeighbor` 放大，块尺寸与 GDI 烧录共用 `MosaicBlock()`，否则所见非所得。

**主窗口快捷键与运行期窗口状态**
- 运行页 Esc 用 **`PreviewKeyDown`（隧道）**：冒泡的 `KeyDown` 会被列表控件先行消费，表现为「点日志区能按、点动作列表按不动」。文本框内的 Esc 仍留给它自己。
- 「把程序自己最小化的窗口还回来」与「结束后是否抢前台」是**两件事**：前者必须无条件执行，否则关掉 `ActivateOnFinish` 时窗口一直扣在最小化状态，焦点不在本窗口，所有快捷键失灵。

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
