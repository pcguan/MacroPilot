#!/usr/bin/env bash
# 把 wpf/ 源码树同步到 corp-win 的 MacroPilot_src（构建机上只放源码，构建完就删）。
#
# 【为什么要有这个脚本】直接在命令行里 `cd .. && tar ... .` 同步过两次事故：
# cwd 差一级，整棵树就传错位置，而 dotnet build 照样"成功"——编的是上一次的旧代码，
# 于是"构建通过 + 测试通过"验证的根本不是本次改动。这里全部用绝对路径，并在末尾校验。
set -euo pipefail

SRC=/root/workspace/claude_code/win/ch9329/wpf
DST='C:/Users/pengcheng.guan/MacroPilot_src'
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

ssh corp-win 'rmdir /s /q %USERPROFILE%\MacroPilot_src' 2>/dev/null || true

tar -cf - -C "$SRC" \
    --exclude=.git --exclude=bin --exclude=obj --exclude=build \
    --exclude=.dart_tool --exclude=windows --exclude=stage_payload \
    --exclude=archive.7z --exclude=payload . | tar -xf - -C "$STAGE"

# 传之前先自查：这三个目录必须在，否则就是传错层级了
for d in MacroPilot MacroPilot.Tests MacroPilotInstaller_Flutter_WPF; do
    [ -d "$STAGE/$d" ] || { echo "同步中止：暂存目录里没有 $d，说明打包的根路径不对"; exit 1; }
done

scp -rq "$STAGE/." "corp-win:$DST/"
ssh corp-win 'if not exist %USERPROFILE%\MacroPilot_src\MacroPilot\MacroPilot.csproj (echo SYNC_BROKEN & exit 1)'
echo "SYNCED -> $DST"
