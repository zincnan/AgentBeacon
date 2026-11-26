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

function Stop-AgentBeaconProcess {
    param(
        [string]$Name,
        [string]$ExpectedPath,
        [string]$PidFile = ""
    )

    if ($PidFile -and (Test-Path $PidFile)) {
        try {
            $pidValue = [int](Get-Content $PidFile -Raw)
            $p = Get-Process -Id $pidValue -ErrorAction Stop
            if ($p.ProcessName -eq $Name -and $p.Path -eq $ExpectedPath) {
                $p | Stop-Process -Force
                Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
                return
            }
        } catch {
        }
        Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
    }

    Get-Process -Name $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $ExpectedPath } |
        Stop-Process -Force
}

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

# 2. 清理旧版安装遗留的 PowerShell 启动包装脚本
Remove-Item (Join-Path $Dir 'run-receiver.ps1') -Force -ErrorAction SilentlyContinue

# 3. 停掉当前发布目录对应的旧实例（避免误杀其他端口/开发实例）
$receiverExe = Join-Path $Dir 'receiver\agentbeacon-receiver.exe'
$launcherExe = Join-Path $Dir 'receiver\agentbeacon-receiver-launcher.exe'
$indicatorExe = Join-Path $Dir 'indicator\agentbeacon-indicator.exe'
Stop-AgentBeaconProcess 'agentbeacon-receiver' $receiverExe (Join-Path $Dir 'receiver.pid')
Stop-AgentBeaconProcess 'agentbeacon-indicator' $indicatorExe

function Enable-StartupShortcut {
    param([string]$ShortcutName)
    $regPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'
    if (-not (Test-Path $regPath)) { return }
    $props = Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue
    if ($null -ne $props -and $null -ne $props.PSObject.Properties[$ShortcutName]) {
        $bytes = [byte[]]$props.$ShortcutName
        if ($bytes.Length -gt 0 -and $bytes[0] -ne 2) {
            Remove-ItemProperty -Path $regPath -Name $ShortcutName -ErrorAction SilentlyContinue
            Write-Host "自启修复  : 已清除 Windows 对 $ShortcutName 的禁用记录"
        }
    }
}

# 4. 开机自启快捷方式
$startup = [Environment]::GetFolderPath('Startup')
$ws = New-Object -ComObject WScript.Shell

$lnk1Path = Join-Path $startup 'AgentBeacon Receiver.lnk'
if (Test-Path $lnk1Path) { Remove-Item $lnk1Path -Force }
$lnk1 = $ws.CreateShortcut($lnk1Path)
$lnk1.TargetPath = $launcherExe
$lnk1.Arguments = ''
$lnk1.WorkingDirectory = $Dir
$lnk1.Save()

$lnk2Path = Join-Path $startup 'AgentBeacon Indicator.lnk'
if (Test-Path $lnk2Path) { Remove-Item $lnk2Path -Force }
$lnk2 = $ws.CreateShortcut($lnk2Path)
$lnk2.TargetPath = $indicatorExe
$lnk2.WorkingDirectory = $Dir
$lnk2.Save()

Enable-StartupShortcut 'AgentBeacon Receiver.lnk'
Enable-StartupShortcut 'AgentBeacon Indicator.lnk'
Write-Host "自启      : $startup"

# 5. 立即启动
if (-not $NoStart) {
    Start-Process $launcherExe -WorkingDirectory $Dir
    Start-Sleep -Seconds 1
    Start-Process $indicatorExe -WorkingDirectory $Dir
    Write-Host "已启动    : receiver + indicator（后台）"
}

Write-Host ""
Write-Host "== 完成 =="
Write-Host "改配置    : 编辑 $config 后重新运行本脚本"
Write-Host "卸载      : powershell -ExecutionPolicy Bypass -File uninstall.ps1"
