# AgentBeacon 卸载（针对发布好的 dist 文件夹）：停进程 + 删开机自启（可选删文件）
param(
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Continue'
$Dir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "== AgentBeacon 卸载 =="

foreach ($name in 'agentbeacon-receiver', 'agentbeacon-indicator') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "已停止    : $name"
}

$startup = [Environment]::GetFolderPath('Startup')
foreach ($n in 'AgentBeacon Receiver.lnk', 'AgentBeacon Indicator.lnk') {
    $p = Join-Path $startup $n
    if (Test-Path $p) {
        Remove-Item $p -Force
        Write-Host "已删除    : $p"
    }
}

if ($RemoveFiles) {
    Remove-Item $Dir -Recurse -Force
    Write-Host "已删除    : $Dir"
}

Write-Host "== 完成 =="
