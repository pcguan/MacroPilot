# MacroPilot（重写版）

键鼠宏助手。从 0 重写，参考标准为桌面上的旧成品 `MacroPilot_dist`（未改动它）。

- **技术栈**：.NET 8 WPF + WPF-UI（Fluent/Win11 外观）+ System.IO.Ports
- **数据**：`%AppData%\MacroPilot\plans.json`（与参考版 `CH9329MacroClient` 字段兼容；首次运行会只读导入参考版方案，原文件不动）
- **输入后端**：CH9329 硬件（串口，绕反作弊）/ 软件模拟（SendInput）
- **功能**：方案/动作编排、批量多选、组合(Group)/拆分、每步循环、跳转(goto)、监听动作(成功/结束/失败)、明暗主题、按需管理员提权、单实例

## 发布（在 Windows 上运行）
```
build.bat        自包含（零依赖，双击即用）
build.bat fd     框架依赖（小体积，需 .NET 8 桌面运行时）
```
输出到 `%USERPROFILE%\MacroPilot_review`（非桌面）。

## 结构
- `Models/`  数据模型（MacroStep / MacroPlan / MacroDocument / LogEntry）
- `Input/`   IInputBackend / Ch9329Device / NativeInputDevice / Ch9329Scanner / KeyMap
- `Services/` Storage（持久化）/ MacroRunner（执行引擎）/ ThemeManager
- `App.xaml(.cs)` 单实例 + 提权 + 显式建窗口（不依赖 StartupUri）
- `MainWindow.xaml(.cs)` 主界面（左侧栏 + 方案/设置/关于）
