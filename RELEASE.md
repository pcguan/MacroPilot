# 更新与发布流程

每次改动都走这六步，顺序不能变。**核心原则：构建成功了才提交、才发布**——别在构建前 push，否则远端会留下编不过的 commit 和对不上的版本号。

约定的机器分工：

| 机器 | 角色 |
|---|---|
| 本地（Linux） | **唯一源码真相**；改代码、提交、发布、验证 |
| `corp-win` | **临时编译机**（Flutter SDK + VS C++ 生成工具 + dotnet 8 + 7-Zip）；构建完清空，不长期存源码 |
| NAS | 自建更新源的静态目录（nginx 容器） |

> 有了本体在线更新后，**不再手动同步产物到 corp-win / pc-guan 桌面**，除非另有要求。

---

## 1. 本地修改源码

改完代码后**在当前版本基础上递增版本号**（通常递增补丁号，例如 `0.6.21` → `0.6.22`；不要重置回 `0.0.N`），两处必须同步：

- `MacroPilot/MacroPilot.csproj` 的 `<Version>`
- `MacroPilotInstaller_Flutter_WPF/lib/app_meta.dart` 的 `appVersion`

同时在 `MacroPilot/changelog.json` **最前面**加上本版本的条目（`version` / `date` / `notes`）。这份文件是概况页「本次更新」和 release 说明的**唯一来源**，漏写会导致第 5 步的脚本直接报错退出。

## 2. 推送到 corp-win 跑测试 + 编译构建

统一用 `tools/sync_corpwin.sh` 同步源码。脚本按自身位置确定源码根目录，先本地暂存并检查关键文件，再清理和上传远端；构建产物、Git 元数据及本地会话资料（`.codex-memory` / `.claude` / `.codex` / `AGENTS.md` 等）不会上传。不要临时另写 `tar` / `scp` 流程：

```bash
cd win/ch9329/wpf
bash tools/sync_corpwin.sh

# 先跑单元测试，失败就别往下走
ssh corp-win 'cd /d %USERPROFILE%\MacroPilot_src\MacroPilot.Tests && dotnet test -v q --nologo'

ssh corp-win 'cd /d %USERPROFILE%\MacroPilot_src\MacroPilotInstaller_Flutter_WPF && echo. | build.bat'
```

`build.bat` 六步：publish WPF → 打 `assets\payload\app.zip` → flutter build → 打 `archive.7z` → 拼 SFX 单文件 exe 到桌面。

**测试或构建失败就回第 1 步修，别继续往下走。**

> 测试工程见 `MacroPilot.Tests/`（xUnit）：`FakeBackend` 验证动作序列、条件与表达式、监听、循环跳转、停止暂停、失败暂停和「重复直到条件满足」；另有合成位图搜图、录制编译、标注几何、模型完整性、方案保存/恢复及更新哈希/换源测试。
> 单测不产生真实键鼠输入，也不替代 Win32/串口、桌面抓屏、UI 和更新安装的实际环境验证。**改了模型字段务必跑一遍**——`ModelIntegrityTests` 会用反射查
> `Clone` / `RunCondition.Copy` 有没有漏拷（历史上因此出过两次实际影响运行的 bug）。
> 测试项目不进安装包（`build.bat` 只 publish `MacroPilot.csproj`，已验证 payload 里没有 Tests 文件）。
 构建成功后核对产物版本号确实是新版：

```bash
ssh corp-win 'powershell -NoProfile -Command "(Get-Item C:\Users\pengcheng.guan\MacroPilot_src\MacroPilotInstaller_Flutter_WPF\stage_payload\MacroPilot.dll).VersionInfo.FileVersion"'
```

## 3. 产物同步回本地，清理中间产物

两个产物拉回 `win/ch9329/release/`：

```bash
cd win/ch9329/release
scp corp-win:'C:/.../MacroPilotInstaller_Flutter_WPF/assets/payload/app.zip' ./MacroPilot-app.zip
scp corp-win:'C:/Users/pengcheng.guan/Desktop/MacroPilotInstaller_Flutter.exe' ./
```

然后清干净 corp-win（源码树 + 桌面 exe）和本地暂存目录：

```bash
ssh corp-win 'rmdir /s /q %USERPROFILE%\MacroPilot_src & del /q %USERPROFILE%\Desktop\MacroPilotInstaller_Flutter.exe'
```

> scp 抽风（`Connection closed` / reset）时加 `-o ControlMaster=no -o ControlPath=none` 走独立连接，或重试。**传完核对字节数**，别拿半截文件去发布。
> 只删自己造的东西：corp-win 桌面上的 `tool\`（工具链）、快捷方式、`temparary.txt`、`wsl-portproxy.*` 等一律保留。

## 4. 提交并 push 到 GitHub

```bash
git status --short
git -c core.whitespace=cr-at-eol diff --check
# 仅列本次修改的文件；下面是文档修订的示例，按实际改动调整路径
git add -- RELEASE.md README.md
git diff --cached
git commit
git push origin HEAD
```

按文件选择提交，检查暂存 diff 后再提交，不使用 `git add -A` 把无关改动或本地资料一并带入。push 走已配置的 GitHub SSH 身份并遵循公网代理约定。commit message 结尾保留 `Co-Authored-By:`，使用本次实际协作者的姓名和邮箱；由 Codex 完成的改动不要沿用历史 Claude 署名。格式检查允许 Windows 批处理所需的 CRLF 行尾。

## 5. 维护两个源的发布文件

先由脚本生成清单（版本号取自 csproj，notes 取自 `changelog.json`，两个产物的 size / SHA-256 现算）：

```bash
python3 tools/make_release_meta.py ../release
```

**升级安装器/卸载器的版本（包括 0.6.23）必须加 `--installer-only`。** ZIP 更新会保留旧卸载器，因此本次清单只声明 EXE，让旧客户端也能升级到按文件归属清单卸载的新安装器。三个资产仍全部发布，ZIP 可供手动下载。后续纯本体版本可恢复普通清单。

`release/` 目录里应有**三个文件**：`version.json`、`MacroPilot-app.zip`、`MacroPilotInstaller_Flutter.exe`。

**GitHub 源**——先建 draft release，上传并确认三个资产完整，再将 draft 发布为正式 release；不要先公开空 release。**三个都要传**（少了 `version.json` 回退源就拿不到清单）：

```bash
TK=$(cat /root/workspace/codex/.claude/macropilot_gh_token)
# POST /repos/pcguan/MacroPilot/releases  {tag_name:vX, target_commitish:main, body:<changelog 条目>}
# POST https://uploads.github.com/repos/pcguan/MacroPilot/releases/<id>/assets?name=<文件名>
```

访问 GitHub **必须显式走代理** `-x http://192.168.33.9:7890`（环境里只有大写 `HTTP_PROXY`，而 curl 故意忽略它，不加 `-x` 就是裸连，大文件必超时）。

**自建源（NAS）**——先上传到本次独立暂存目录，核对远端大小与本地 SHA-256，再切换。程序文件先就位，`version.json` 最后原子替换；禁止直接上传覆盖线上文件，禁止先放新清单。**必须 chmod 644**，否则 nginx 用户读不到会 403。以下以 0.6.23 为例，实际发布必须使用本次版本的新暂存目录：

```bash
ssh nas 'mkdir /vol3/1000/HDD2/tool/docker/nginx/html/macropilot/.stage-0.6.23'
scp version.json MacroPilot-app.zip MacroPilotInstaller_Flutter.exe \
    nas:/vol3/1000/HDD2/tool/docker/nginx/html/macropilot/.stage-0.6.23/
# 核对暂存三文件大小/SHA 后，在同一文件系统逐项 rename：
# 1. chmod 644 暂存的三个文件。
# 2. 将 ZIP 和 EXE 从暂存目录移到线上同名路径。
# 3. 最后将 version.json 移到线上同名路径。
# 4. 删除空暂存目录。若上传或核对失败，不切换线上清单。
```

同名资产切换期间，持有旧清单的客户端仍可能遇到一次哈希不符并重试；不可变版本目录可进一步消除此窗口。上述顺序保证客户端下载不到半上传文件，也不会提前看到尚未就位的新清单。

> nginx 是 NAS 上的 docker 容器，配置在 `/vol3/1000/HDD2/tool/docker/nginx/cfg/nginx.conf`，`nas.pcguan.cn` 块里的 `location /macropilot/` 已配好。改配置要先备份 + `nginx -t` 再 `-s reload`，**别重建容器**（会中断面板 / frps / mihomo / sub2api）。

## 6. 走公网验证两个源

**只校验版本号，不下载任何文件。**两个源各读一次几百字节的 `version.json`，`version` 是新版本号即通过：

```bash
# 自建源
curl -fsS -x http://192.168.33.9:7890 https://nas.pcguan.cn/macropilot/version.json | head -3

# GitHub 源（记得带代理）
curl -fsSL -x http://192.168.33.9:7890 https://github.com/pcguan/MacroPilot/releases/latest/download/version.json | head -3
```

外加一处**零成本**检查：读一下 release 的资产列表，确认三个资产都在（就是上传时已经在调的那个 API，不产生下载）。

```bash
curl -s -x http://192.168.33.9:7890 -H "Authorization: token $TK" \
  https://api.github.com/repos/pcguan/MacroPilot/releases/latest | grep '"name"'
```

> 为什么保留这一条：2026-07-22 发 v0.0.20 时，exe 上传遇代理抖动返回空响应，**release 当时只有 2 个资产**——版本号正常、安装器 404。这类"某一步整个没发生"的故障靠资产列表就能抓住，不需要下载。
>
> 不再全量核 SHA-256 的原因：这条代理拉 12MB 实测 20~170 秒（吞吐波动近 10 倍），占掉单次发布的一大截；而传输本身有 TCP 校验，实际会发生的是"没传上去"而不是"传坏了"。zip 的 SHA-256 仍写在 `version.json` 里，**客户端每次更新下载后都会强制校验**，字节级可信由那一层保证。


---

## 客户端拿到更新的路径

`AutoUpdate` 控制**启动时自动安装更新**：开启时，启动检查发现新版就进入下载更新；关闭时跳过这次启动检查。两种模式都保留每 30 秒后台检查，发现新版在底部状态栏提醒，由用户选择更新或关闭提醒。手动「检查更新」也可随时使用。

检查按**有序多源回退**读同格式的 `version.json`（普通静态文件，不走 GitHub API，因此不受未鉴权限流影响）：

1. `https://nas.pcguan.cn/macropilot/` —— 自建源，国内直连快，主力
2. `https://github.com/pcguan/MacroPilot/releases/latest/download/` —— 源头 / 海外回退

按上述顺序逐源尝试，单源超时 10 秒，采用第一个有效清单；全失败沿用上次缓存。清单中每个已声明资产必须提供 64 位十六进制 SHA-256，缺失或格式错误会拒绝该清单并尝试下一源。版本只比 `Major.Minor.Build` 且**严格大于**才算新版。清单声明 ZIP 时优先用本体包：下载并校验 SHA-256（不匹配就换源重下），在同卷暂存目录保留用户文件并应用程序文件清单，改名备份旧目录，再切换新版。新版完成启动确认后才清理备份；启动失败尝试回滚，回滚失败明确报告备份位置。只声明 EXE 时通过安装器完成更新。

下载与校验由本体进度窗显示；本体退出后，PowerShell 助手通过可见进度小窗显示覆盖、回滚和重启过程（隐藏的是控制台）。运行中的方案或未保存修改会先走处理流程，不应把更新描述为始终无交互。
