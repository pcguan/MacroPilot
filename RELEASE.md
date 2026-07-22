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

改完代码后**版本号 +1**（开发阶段用 `0.0.N`），两处必须同步：

- `MacroPilot/MacroPilot.csproj` 的 `<Version>`
- `MacroPilotInstaller_Flutter_WPF/lib/app_meta.dart` 的 `appVersion`

同时在 `MacroPilot/changelog.json` **最前面**加上本版本的条目（`version` / `date` / `notes`）。这份文件是概况页「本次更新」和 release 说明的**唯一来源**，漏写会导致第 5 步的脚本直接报错退出。

## 2. 推送到 corp-win 编译构建

直接 scp 源码上去（**不要**绕 GitHub clone——源码本体不到 1 MB，直推更快）：

```bash
cd win/ch9329/wpf
D=/tmp/.../src && rm -rf $D && mkdir -p $D
tar -cf - --exclude=.git --exclude=bin --exclude=obj --exclude=build \
          --exclude=.dart_tool --exclude=windows --exclude=stage_payload \
          --exclude=archive.7z --exclude=payload . | tar -xf - -C $D
scp -rq $D/. corp-win:'C:/Users/pengcheng.guan/MacroPilot_src/'

ssh corp-win 'cd /d %USERPROFILE%\MacroPilot_src\MacroPilotInstaller_Flutter_WPF && echo. | build.bat'
```

`build.bat` 六步：publish WPF → 打 `assets\payload\app.zip` → flutter build → 打 `archive.7z` → 拼 SFX 单文件 exe 到桌面。

**构建失败就回第 1 步修，别继续往下走。** 构建成功后核对产物版本号确实是新版：

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
git add -A && git commit -m "..." && git push origin HEAD
```

push 走 SSH（`~/.ssh/id_ed25519` 已认证为 GitHub 用户 `pcguan`），不需要 token。commit message 结尾带 `Co-Authored-By:` 行。

## 5. 维护两个源的发布文件

先由脚本生成清单（版本号取自 csproj，notes 取自 `changelog.json`，两个产物的 size / SHA-256 现算）：

```bash
python3 tools/make_release_meta.py ../release
```

`release/` 目录里应有**三个文件**：`version.json`、`MacroPilot-app.zip`、`MacroPilotInstaller_Flutter.exe`。

**GitHub 源**——建 release 再传资产，**三个都要传**（少了 `version.json` 回退源就拿不到清单）：

```bash
TK=$(cat /root/workspace/claude_code/.claude/macropilot_gh_token)
# POST /repos/pcguan/MacroPilot/releases  {tag_name:vX, target_commitish:main, body:<changelog 条目>}
# POST https://uploads.github.com/repos/pcguan/MacroPilot/releases/<id>/assets?name=<文件名>
```

访问 GitHub **必须显式走代理** `-x http://192.168.33.9:7890`（环境里只有大写 `HTTP_PROXY`，而 curl 故意忽略它，不加 `-x` 就是裸连，大文件必超时）。

**自建源（NAS）**——同样三个文件同步到 nginx 静态目录，**必须 chmod 644**，否则 nginx 用户读不到会 403：

```bash
scp version.json MacroPilot-app.zip MacroPilotInstaller_Flutter.exe \
    nas:/vol3/1000/HDD2/tool/docker/nginx/html/macropilot/
ssh nas 'chmod 644 /vol3/1000/HDD2/tool/docker/nginx/html/macropilot/*'
```

> nginx 是 NAS 上的 docker 容器，配置在 `/vol3/1000/HDD2/tool/docker/nginx/cfg/nginx.conf`，`nas.pcguan.cn` 块里的 `location /macropilot/` 已配好。改配置要先备份 + `nginx -t` 再 `-s reload`，**别重建容器**（会中断面板 / frps / mihomo / sub2api）。

## 6. 走公网验证两个源

**只校验版本号，不下载任何文件。**两个源各读一次几百字节的 `version.json`，`version` 是新版本号即通过：

```bash
# 自建源
curl -s https://nas.pcguan.cn/macropilot/version.json | head -3

# GitHub 源（记得带代理）
curl -sL -x http://192.168.33.9:7890 https://github.com/pcguan/MacroPilot/releases/latest/download/version.json | head -3
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

本体启动 1.5 秒后查一次、之后每 30 秒轮询，按**有序多源回退**读同格式的 `version.json`（普通静态文件，不走 GitHub API，因此不受未鉴权限流影响）：

1. `https://nas.pcguan.cn/macropilot/` —— 自建源，国内直连快，主力
2. `https://github.com/pcguan/MacroPilot/releases/latest/download/` —— 源头 / 海外回退

单源 10 秒短超时，谁先应答用谁；全失败沿用上次缓存、不打扰。版本只比 `Major.Minor.Build` 且**严格大于**才算新版。发现新版在底部状态栏提醒（可立即更新 / 关闭提醒）。更新走 2.8 MB 的本体 zip：下载 → 校验 SHA-256（不匹配就换源重下）→ 改名备份旧目录 → 解压覆盖 → 从备份拷回 `unins\` → 失败回滚 → 重启本体。
