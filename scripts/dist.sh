#!/usr/bin/env bash
# AgentBeacon Windows 发布包（在 WSL/Linux 里执行，无需 PowerShell）：
#
#   bash scripts/dist.sh          # 产出 dist/agentbeacon-win-x64/
#   bash scripts/dist.sh --zip    # 额外产出 agentbeacon-win-x64.zip
#
# 产出是一个完整绿色文件夹（自包含 .NET 运行时），拷到 Windows 任意固定位置，
# 双击 install.bat 即完成安装（开机自启 + 后台运行）。发给别人就发这个文件夹/zip。
#
# 原理：dotnet publish -r win-x64 --self-contained -p:EnableWindowsTargeting=true
#       （从 Linux 交叉编译 Windows 目标；先复制源码到临时目录，避免与
#        Windows 侧构建互相污染 bin/obj。）

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${REPO}/dist/agentbeacon-win-x64"
ZIP=0
[[ "${1:-}" == "--zip" ]] && ZIP=1

echo "== AgentBeacon dist (cross-compile from Linux) =="
echo "repo: ${REPO}"
echo "out : ${OUT}"

# 1. 复制源码到临时目录（bin/obj/.git 不带，防止与 Windows 构建产物互染）
WORK="$(mktemp -d /tmp/agentbeacon-dist-XXXXXX)"
trap 'rm -rf "${WORK}"' EXIT
cp -a "${REPO}/." "${WORK}/"
rm -rf "${WORK}/.git" "${WORK}/dist"
find "${WORK}" -type d \( -name bin -o -name obj \) -exec rm -rf {} + 2>/dev/null || true

# 2. 自包含交叉发布（EnableWindowsTargeting 允许从 Linux 打 Windows 包）
publish() {
    local csproj="$1" out="$2"
    dotnet publish "${csproj}" \
        -c Release -r win-x64 --self-contained true \
        -p:EnableWindowsTargeting=true \
        -p:PublishSingleFile=false \
        -o "${out}"
}
publish "${WORK}/receiver/AgentBeacon.Receiver.csproj"              "${OUT}/receiver"
publish "${WORK}/windows/AgentBeacon.Indicator/AgentBeacon.Indicator.csproj" "${OUT}/indicator"
echo "publish   : ok"

# 3. 配置模板 → 接收方直接改的 agentbeacon.json
cp "${WORK}/agentbeacon.example.json" "${OUT}/agentbeacon.json"

# 4. 接收方脚本 + 说明 + 双击入口
cp "${WORK}/scripts/windows/dist-install.ps1"   "${OUT}/install.ps1"
cp "${WORK}/scripts/windows/dist-uninstall.ps1" "${OUT}/uninstall.ps1"
cp "${WORK}/scripts/windows/install.bat"        "${OUT}/install.bat"
cp "${WORK}/scripts/windows/uninstall.bat"      "${OUT}/uninstall.bat"
cp "${WORK}/scripts/windows/使用说明.txt"        "${OUT}/使用说明.txt"

# 5. 可选 zip
if [[ "${ZIP}" == "1" ]]; then
    ( cd "$(dirname "${OUT}")" && zip -qr "$(basename "${OUT}").zip" "$(basename "${OUT}")" )
    echo "zip       : ${OUT}.zip"
fi

SIZE=$(du -sm "${OUT}" | cut -f1)
echo "== done ==  ${OUT}  (${SIZE} MB)"
echo "把这个文件夹（或 zip）拷到 Windows 固定位置，双击 install.bat 即可。"
