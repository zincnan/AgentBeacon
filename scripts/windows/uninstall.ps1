# AgentBeacon Windows 卸载：停进程 + 删开机自启快捷方式（可选 -RemoveFiles 删除安装目录）
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AgentBeacon",
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Continue'

Write-Host "== AgentBeacon uninstall =="

foreach ($name in 'agentbeacon-receiver', 'agentbeacon-indicator') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "stopped    : $name"
}

$startup = [Environment]::GetFolderPath('Startup')
foreach ($n in 'AgentBeacon Receiver.lnk', 'AgentBeacon Indicator.lnk') {
    $p = Join-Path $startup $n
    if (Test-Path $p) {
        Remove-Item $p -Force
        Write-Host "removed    : $p"
    }
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "removed    : $InstallDir"
}

Write-Host "== done =="
