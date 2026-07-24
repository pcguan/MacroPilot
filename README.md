# MacroPilot · 键鼠宏助手

Windows 桌面端的键盘 / 鼠标宏工具：把**点击、移动、滚轮、按键、等待、激活窗口**等动作编排成「方案」，按设定的循环次数与间隔自动执行。

两种输出后端可切换：

| 后端 | 说明 |
| --- | --- |
| **CH9329 硬件** | 经 USB 串口驱动 CH9329 芯片，在系统层面等同**真实 USB 键鼠**，兼容性最好 |
| **本机模拟** | Win32 `SendInput`，无需额外硬件，可选扫描码 / 虚拟键 |

技术栈：.NET 8 WPF + [WPF-UI](https://github.com/lepoco/wpfui)（Fluent / Win11 外观）+ System.IO.Ports；安装器为 Flutter + 7-Zip SFX 单文件。

---

## 下载与安装

到 **[Releases](https://github.com/pcguan/MacroPilot/releases/latest)** 下载：

| 文件 | 用途 |
| --- | --- |
| `MacroPilotInstaller_Flutter.exe` | **安装器**（约 12 MB）。双击即装，per-user 安装、**无需管理员**；会自动检测 .NET 8 运行时并引导安装 |
| `MacroPilot-app.zip` | **本体文件包**（约 2.8 MB）。主要供在线更新使用；手动使用需自备 .NET 8 桌面运行时 |
| `version.json` | 版本清单（版本号 + 各文件大小与 SHA-256），供更新器读取 |

- 默认安装目录：`%LocalAppData%\Programs\MacroPilot`
- 用户数据目录：`%AppData%\MacroPilot`（`plans.json` 方案、`settings.json` 设置、`logs\` 运行日志、`images\` 图片条件模板），可在「配置」页更改并迁移
- 卸载：设置→应用，或安装目录下 `unins\`

## 在线更新

本体自带在线更新：**启动后查一次，之后每 30 秒后台检查**。发现新版会在**窗口底部状态栏**出现提示条（可「立即更新」或「关闭提醒」；关闭后该版本不再打扰，出更新版本时会重新提醒）。

点更新后**全程自动、无需任何操作**：

```
下载(优先 2.8MB 本体包) → 校验 SHA-256 → 备份旧目录 → 覆盖 → 保留卸载器
                                              ↓ 失败
                                        自动回滚到旧版本
                                              ↓
                                         重启本体
```

更新源为**有序多源回退**，前者不通自动降级，**不是单点**：

1. `https://nas.pcguan.cn/macropilot/` —— 自建源，**国内直连快**（无需代理）
2. `https://github.com/pcguan/MacroPilot/releases/latest/download/` —— 源头 / 海外回退

版本检测读各源同格式的 `version.json`（普通静态文件，不走 GitHub API，因此不受未鉴权限流影响）。下载完成后**强制校验 SHA-256**，不匹配或下载失败会自动换下一个源重下。

## 主要功能

**方案与动作**
- 多方案管理：新建 / 复制 / 粘贴 / 重命名 / 删除；导入导出（单方案 / 批量，导出文件自包含图片）
- 动作类型分层：**输入 → 鼠标**（点击 / 移动 / 拖动 / 滚轮）、**输入 → 键盘**（含修饰键组合捕获）；**运行 → 等待 / 激活窗口 / 跳转**；**组合**
  - 点击可选是否带坐标（带坐标＝先移动再点击）；拖动＝起点按下→移到终点→松开（起终点可跨屏）
  - 每类动作的**次数 + 重复间隔**统一放在各自基础设置里（点击次数 / 滚动次数 / 按键次数 / 执行次数）
- **组合（Group）可嵌套**，支持批量多选合并、拖拽排序、序号显示、备注
- 单个动作可**禁用**（右键切换，运行时整步跳过，可撤销）
- **跳转**是独立动作：纯 goto，可设「最大重复次数」作防死循环上限；目标下拉显示备注 / 动作简述
- **监听动作**：成功后 / 结束后 / 失败后追加动作，与普通动作同款配置，可递归嵌套

**运行条件**（满足才执行，可取反）
- 时间段：在 / 不在某时段
- **图片出现**：框选屏幕区域截图作模板，运行时同位置逐像素比对（可调相似度阈值）；跳过时日志会打印**实际匹配度**，便于调阈值

**拟人化鼠标移动**（基于真人轨迹录制数据反推）
- 时长**次线性**于距离且带随机（10px≈440ms / 100px≈525ms / 1000px≈800ms，同距离每次不同）
- **前快后慢**速度剖面（峰值约在 1/4 处）、贝塞尔轻弧 + 相关抖动，同起终点每次轨迹都不同
- 大距离偶发过冲后回正；配套内置**鼠标轨迹录制器**用于采集真人数据调参

**运行控制**
- 暂停 / 继续 / 停止，全局热键 **F9 / F10 / F11**（仅方案执行期间注册，平时把按键还给系统）
- 运行页：当前动作高亮并**自动滚动跟随**、实时日志、进度条、方案级与动作级循环计数
- **运行悬浮窗（HUD）**：置顶显示当前动作 / 进度 / 循环 / 热键，带暂停·停止按钮；可拖动、可调不透明度（悬停临时变清晰）；开启后运行时本体自动最小化
- 运行时窗口下沉 / 最小化、不抢焦点；可配「结束后回到前台」

**系统托盘**：图标常驻，双击显示主窗口，右键直接选方案运行 / 停止 / 退出；可「最小化到托盘」。

**定时启动**（方案列表工具栏，全局单一）：让一个方案到点自动运行——**每日定时**（时:分:秒 + 可选星期）或**指定时间**（年月日时分秒，跑一次）；精确到秒；到点撞上正在运行可选「忽略」或「停止当前改跑定时」。被定时的方案在列表里带时钟标记。

**其它**：浅色 / 深色 / 跟随系统主题、单实例、按需管理员提权、自定义数据目录、所有窗口记住位置与大小、自动更新可开关。

## 系统要求

- Windows 10 / 11 x64
- .NET 8 桌面运行时（安装器会检测并引导安装）
- CH9329 后端需对应的 USB 串口设备（程序会自动扫描并识别桥接芯片）

## 仓库结构

```
MacroPilot/                     本体（.NET 8 WPF）
├─ Models/                      MacroStep / MacroPlan / MacroDocument / IRunCondition / LogEntry
├─ Input/                       IInputBackend、Ch9329Device、NativeInputDevice、Ch9329Scanner、KeyMap、ScreenInfo
├─ Services/                    Storage、MacroRunner（执行引擎）、UpdateService（在线更新）、Changelog、
│                               ScreenMatch（图片条件）、ImageStore、WindowMemory、WindowActivator、
│                               MouseTraceRecorder、ThemeManager、PreciseTimer…
└─ MainWindow.xaml(.cs)         主界面；partial 拆分：.Dialogs / .RunCondition / .Hud / .Tray / .Schedule / .Update

MacroPilotInstaller_Flutter_WPF/ 安装器（Flutter + 7z SFX 单文件）
├─ lib/services/                installer（安装/静默更新）、payload、registry、shortcuts、dotnet
└─ build.bat                    一键构建安装器 exe
```

## 构建

**本体**（输出到 `%USERPROFILE%\MacroPilot_review`，不动桌面）：
```bat
cd MacroPilot
build.bat        :: 自包含，零依赖
build.bat fd     :: 框架依赖，体积小，需 .NET 8 桌面运行时
```

**安装器**（需 Flutter SDK + VS C++ 生成工具 + 7-Zip；会先发布本体再打包成单文件 SFX）：
```bat
cd MacroPilotInstaller_Flutter_WPF
build.bat
```

## 发布流程

详见 **[RELEASE.md](RELEASE.md)**。六步，顺序不能变：

1. 本地改源码（版本号两处 +1、`changelog.json` 补本版条目）
2. 推送到 corp-win 编译构建 —— **构建失败就回第 1 步**
3. 产物同步回本地，清理 corp-win 上的中间产物
4. 提交 commit 并走代理 push 到 GitHub
5. 维护两个源的发布文件（`version.json` + 本体包 + 安装器，三个都要传）
6. 走公网读两个源的 `version.json` 核对版本号（不下载文件）

> 核心原则：**构建成功了才提交、才发布**。版本号取自程序集 `<Version>`，更新器只比较 `Major.Minor.Build` 且**严格大于**才算新版（不会降级），也不认 draft / prerelease。

## 许可

个人项目，按现状提供。
