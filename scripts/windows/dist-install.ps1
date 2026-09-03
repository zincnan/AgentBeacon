# AgentBeacon 接收方安装脚本（针对发布好的 dist 文件夹，无需构建、无需 .NET SDK）
#
# 用法（在这个文件夹里，右键“使用 PowerShell 运行”或）：
#   powershell -ExecutionPolicy Bypass -File install.ps1
# 可选参数：
#   -Token <key>      写入 agentbeacon.json 的 token（与服务端约定一致）
#   -Port <端口>      写入 agentbeacon.json 的端口（默认 8765）
#   -NoStart          只安装，不立即启动
#
# 安装内容：创建开机自启快捷方式 + 立即后台启动 Receiver 与 Indicator。
# 本文件夹就是程序本体（自包含发布，不需要安装 .NET）；不要把它挪走或删除。
# 卸载：powershell -ExecutionPolicy Bypass -File uninstall.ps1

param(
    [string]$Token = "",
    [int]$Port = 0,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$Dir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "== AgentBeacon 安装 =="
Write-Host "程序目录: $Dir"

# 1. 可选：把 Token / Port 写进配置文件
$config = Join-Path $Dir 'agentbeacon.json'
if ((-not (Test-Path $config)) -and (Test-Path (Join-Path $Dir 'agentbeacon.example.json'))) {
    Copy-Item (Join-Path $Dir 'agentbeacon.example.json') $config
}
if ((Test-Path $config) -and ($Token -ne "" -or $Port -gt 0)) {
    # -Encoding UTF8 is required on Windows PowerShell 5.1: without a BOM
    # it decodes the file as ANSI and multi-byte comments corrupt the JSON.
    $json = Get-Content $config -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($Token -ne "") {
        # token 非空 = 开鉴权（Round 9 简化语义）
        $json | Add-Member -NotePropertyName token -NotePropertyValue $Token -Force
    }
    if ($Port -gt 0) {
        $json | Add-Member -NotePropertyName port -NotePropertyValue $Port -Force
    }
    $json | ConvertTo-Json -Depth 5 | Set-Content $config -Encoding UTF8
    Write-Host "config    : 已写入 token/port"
}
if (-not (Test-Path $config)) {
    Write-Warning "未找到 agentbeacon.json，Receiver 将以默认值（0.0.0.0:8765，需 token）启动"
}

# 2. Receiver 启动脚本（隐藏窗口；apphost exe 保证进程名可被 Stop-Process 找到）
$runReceiver = @'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $dir 'receiver\agentbeacon-receiver.exe') --config (Join-Path $dir 'agentbeacon.json')
'@
Set-Content -Path (Join-Path $Dir 'run-receiver.ps1') -Value $runReceiver -Encoding UTF8

# 3. 停掉旧实例
foreach ($name in 'agentbeacon-receiver', 'agentbeacon-indicator') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
}

# 4. 开机自启快捷方式
$startup = [Environment]::GetFolderPath('Startup')
$ws = New-Object -ComObject WScript.Shell

$lnk1 = $ws.CreateShortcut((Join-Path $startup 'AgentBeacon Receiver.lnk'))
$lnk1.TargetPath = 'powershell.exe'
$lnk1.Arguments  = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Dir\run-receiver.ps1`""
$lnk1.WorkingDirectory = $Dir
$lnk1.WindowStyle = 7
$lnk1.Save()

$indicatorExe = Join-Path $Dir 'indicator\agentbeacon-indicator.exe'
$lnk2 = $ws.CreateShortcut((Join-Path $startup 'AgentBeacon Indicator.lnk'))
$lnk2.TargetPath = $indicatorExe
$lnk2.WorkingDirectory = $Dir
$lnk2.Save()
Write-Host "自启      : $startup"

# 5. 立即启动
if (-not $NoStart) {
    Start-Process 'powershell.exe' `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Dir\run-receiver.ps1`"" `
        -WorkingDirectory $Dir -WindowStyle Hidden
    Start-Sleep -Seconds 1
    Start-Process $indicatorExe -WorkingDirectory $Dir
    Write-Host "已启动    : receiver + indicator（后台）"
}

Write-Host ""
Write-Host "== 完成 =="
Write-Host "改配置    : 编辑 $config 后重新运行本脚本"
Write-Host "卸载      : powershell -ExecutionPolicy Bypass -File uninstall.ps1"
