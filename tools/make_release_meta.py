#!/usr/bin/env python3
"""生成发布清单 version.json。

发布说明取自 MacroPilot/changelog.json 当前版本的条目 —— 与概况页「本次更新」同一份数据，
避免程序里写一套、release 页面写另一套。版本号取自 MacroPilot/MacroPilot.csproj 的 <Version>。

用法：python3 tools/make_release_meta.py <产物目录> [--installer-only]
产物目录需含 MacroPilot-app.zip 与 MacroPilotInstaller_Flutter.exe，输出 version.json 到同目录。
升级安装器/卸载器时使用 --installer-only，清单只声明 exe，ZIP 仍作为手动下载资产发布。
"""
import argparse
import hashlib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ZIP_NAME = "MacroPilot-app.zip"
EXE_NAME = "MacroPilotInstaller_Flutter.exe"


def current_version() -> str:
    csproj = os.path.join(REPO, "MacroPilot", "MacroPilot.csproj")
    with open(csproj, encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        sys.exit("找不到 <Version>")
    return m.group(1).strip()


def notes_for(version: str) -> str:
    path = os.path.join(REPO, "MacroPilot", "changelog.json")
    with open(path, encoding="utf-8") as f:
        entries = json.load(f)
    for e in entries:
        if e["version"] == version:
            return "".join(e["notes"])
    sys.exit(f"changelog.json 里没有 {version} 的条目，先补上再发布")


def describe(directory: str, name: str) -> dict:
    path = os.path.join(directory, name)
    if not os.path.isfile(path):
        sys.exit(f"缺少产物：{path}")
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return {"name": name, "size": os.path.getsize(path), "sha256": h.hexdigest()}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory")
    parser.add_argument("--installer-only", action="store_true")
    args = parser.parse_args()
    outdir = args.directory
    version = current_version()
    meta = {
        "version": version,
        "notes": notes_for(version),
        "zip": describe(outdir, ZIP_NAME),
        "exe": describe(outdir, EXE_NAME),
    }
    if args.installer_only:
        # 旧客户端不理解新字段，只要没有 zip 项便会走既有的 EXE 更新路径。
        del meta["zip"]
    dest = os.path.join(outdir, "version.json")
    with open(dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(dest + ".tmp", dest)
    print(dest)
    print(json.dumps(meta, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
