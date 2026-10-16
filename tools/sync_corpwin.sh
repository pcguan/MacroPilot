#!/usr/bin/env bash
# 把 wpf/ 源码树同步到 corp-win 的 MacroPilot_src（构建机上只放源码，构建完就删）。
#
# 【为什么要有这个脚本】直接在命令行里 `cd .. && tar ... .` 同步过两次事故：
# cwd 差一级，整棵树就传错位置，而 dotnet build 照样"成功"——编的是上一次的旧代码，
# 于是"构建通过 + 测试通过"验证的根本不是本次改动。这里全部用绝对路径，并在末尾校验。
set -euo pipefail

SRC="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
DST='C:/Users/pengcheng.guan/MacroPilot_src'
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

tar -cf - -C "$SRC" \
    --exclude=.git --exclude=bin --exclude=obj --exclude=build \
    --exclude=.codex-memory --exclude=.claude --exclude=.codex \
    --exclude=AGENTS.md --exclude=AGENTS.local.md \
    --exclude=CLAUDE.md --exclude=CLAUDE.local.md --exclude=MEMORY.md \
    --exclude=.dart_tool --exclude=windows --exclude=stage_payload \
    --exclude=archive.7z --exclude=payload . | tar -xf - -C "$STAGE"

# 本地打包及关键文件检查全部成功后，才允许清理远端或传输。
for f in MacroPilot/MacroPilot.csproj MacroPilot.Tests/MacroPilot.Tests.csproj MacroPilotInstaller_Flutter_WPF/build.bat; do
    [ -f "$STAGE/$f" ] || { echo "同步中止：暂存目录里没有 $f，说明打包不完整或根路径不对"; exit 1; }
done

ssh corp-win 'if exist "%USERPROFILE%\MacroPilot_src" rmdir /s /q "%USERPROFILE%\MacroPilot_src"'
scp -rq "$STAGE/." "corp-win:$DST/"
ssh corp-win 'if not exist %USERPROFILE%\MacroPilot_src\MacroPilot\MacroPilot.csproj (echo SYNC_BROKEN & exit 1)'
echo "SYNCED -> $DST"
