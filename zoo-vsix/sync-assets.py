"""ZooVS 资产同步脚本 —— webview/daemon 构建产物 + 手工资产(material icons / images / ripgrep)。

背景:assets/webview 曾用 `rm -rf` 后仅拷 vite build 输出,把手工复制的
vscode-material-icons(910 个 svg,@ 提及图标)清掉导致 @ 弹窗破图(已发生过一次)。
以后重建 webview 一律用本脚本,不要手敲 rm/cp。

用法:python zoo-vsix/sync-assets.py [--skip-dist] [--skip-webview]
"""
import argparse
import os
import shutil

ROOT = os.path.dirname(os.path.abspath(__file__))
ZOO = os.path.join(os.path.dirname(ROOT), "Zoo-Code")


def sync_dir(src: str, dst: str, label: str) -> None:
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    print(f"[sync] {label}: {src} -> {dst}")


def sync_webview() -> None:
    build = os.path.join(ZOO, "src", "webview-ui", "build")
    if not os.path.isdir(build):
        raise SystemExit("webview build 缺失:先 PLATFORM=standalone npx vite build")
    dst = os.path.join(ROOT, "assets", "webview")
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    shutil.copytree(build, dst)
    # 手工资产:@ 提及图标(webview 虚拟域 /assets/vscode-material-icons)
    icons = os.path.join(ZOO, "src", "assets", "vscode-material-icons")
    sync_dir(icons, os.path.join(dst, "assets", "vscode-material-icons"), "material-icons")
    # 欢迎页 logo(/assets/images)
    images = os.path.join(ZOO, "src", "assets", "images")
    if os.path.isdir(images):
        sync_dir(images, os.path.join(dst, "assets", "images"), "images")
    print("[sync] webview 完成")


def sync_dist() -> None:
    dist = os.path.join(ZOO, "src", "dist")
    if not os.path.isfile(os.path.join(dist, "extension.js")):
        raise SystemExit("daemon dist 缺失:先 bun esbuild.mjs --standalone")
    dst = os.path.join(ROOT, "assets", "dist")
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    shutil.copytree(dist, dst)
    # 手工资产:ripgrep(候选路径布局 node_modules/@vscode/ripgrep-win32-x64/bin/rg.exe,
    # 与上游 ripgrepCandidatePaths 的平台包布局一致;appRoot=extensionPath)
    rg_meta = os.path.join(ZOO, "node_modules", ".pnpm")
    base = os.path.join(dst, "node_modules", "@vscode")
    os.makedirs(base, exist_ok=True)
    found = None
    for d in os.listdir(rg_meta):
        if d.startswith("@vscode+ripgrep@"):
            found = os.path.join(rg_meta, d, "node_modules", "@vscode", "ripgrep")
        if d.startswith("@vscode+ripgrep-win32-x64@"):
            sync_dir(os.path.join(rg_meta, d, "node_modules", "@vscode", "ripgrep-win32-x64"),
                     os.path.join(base, "ripgrep-win32-x64"), "ripgrep-win32-x64")
    if found:
        sync_dir(found, os.path.join(base, "ripgrep"), "ripgrep(ESM 包装)")
    print("[sync] dist 完成")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-dist", action="store_true")
    ap.add_argument("--skip-webview", action="store_true")
    a = ap.parse_args()
    if not a.skip_webview:
        sync_webview()
    if not a.skip_dist:
        sync_dist()
    print("[sync] 全部完成")
