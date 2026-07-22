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
- 多方案管理：新建 / 复制 / 粘贴 / 重命名 / 删除；导入导出（单方案 / 批量）
- 动作类型：鼠标点击、鼠标移动、滚轮、按键（含修饰键）、等待、激活窗口、组合
- **组合（Group）可嵌套**，支持批量多选合并、拖拽排序、序号显示、备注
- 单个动作可**禁用**（右键切换，运行时整步跳过，可撤销）
- 方案级与动作级**循环次数**（0 = 无限）、循环间隔
- **跳转（goto）**：跳到指定序号并限定次数
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
- 运行时窗口自动下沉、不抢焦点

**其它**：浅色 / 深色 / 跟随系统主题、单实例、按需管理员提权、自定义数据目录。

## 系统要求

- Windows 10 / 11 x64
- .NET 8 桌面运行时（安装器会检测并引导安装）
- CH9329 后端需对应的 USB 串口设备（程序会自动扫描并识别桥接芯片）

## 仓库结构

```
MacroPilot/                     本体（.NET 8 WPF）
├─ Models/                      MacroStep / MacroPlan / MacroDocument / LogEntry
├─ Input/                       IInputBackend、Ch9329Device、NativeInputDevice、Ch9329Scanner、KeyMap、ScreenInfo
├─ Services/                    Storage、MacroRunner（执行引擎）、UpdateService（在线更新）、
│                               ScreenMatch（图片条件）、ImageStore、MouseTraceRecorder、ThemeManager…
└─ MainWindow.xaml(.cs)         主界面（左侧导航：概况 / 配置 / 方案 / 运行）

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

每次改动后：

1. `git commit` + `push`
2. **版本号 +1**：`MacroPilot/MacroPilot.csproj` 的 `<Version>` 与 `MacroPilotInstaller_Flutter_WPF/lib/app_meta.dart` 的 `appVersion` **两处必须同步**
3. 构建安装器（同时产出本体包 `assets/payload/app.zip`）
4. 生成 `version.json`（版本号 + 两个文件的 size 与 SHA-256）
5. 发布到**两个源**：
   - GitHub Release：上传 `MacroPilotInstaller_Flutter.exe`、`MacroPilot-app.zip`、**`version.json`**（三个都要传，否则回退源拿不到清单）
   - 自建源：同步同样三个文件到 NAS 静态目录

> 版本号取自程序集 `<Version>`，更新器只比较 `Major.Minor.Build` 且**严格大于**才算新版（不会降级），也不认 draft / prerelease。

## 许可

个人项目，按现状提供。
