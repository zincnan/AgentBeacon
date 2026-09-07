# AgentBeacon 卸载（针对发布好的 dist 文件夹）：停进程 + 删开机自启（可选删文件）
param(
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Continue'
$Dir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "== AgentBeacon 卸载 =="

function Stop-AgentBeaconProcess {
    param(
        [string]$Name,
        [string]$ExpectedPath,
        [string]$PidFile = ""
    )

    $stopped = $false
    if ($PidFile -and (Test-Path $PidFile)) {
        try {
            $pidValue = [int](Get-Content $PidFile -Raw)
            $p = Get-Process -Id $pidValue -ErrorAction Stop
            if ($p.ProcessName -eq $Name -and $p.Path -eq $ExpectedPath) {
                $p | Stop-Process -Force
                $stopped = $true
            }
        } catch {
        }
        Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
    }

    Get-Process -Name $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $ExpectedPath } |
        ForEach-Object {
            $_ | Stop-Process -Force
            $stopped = $true
        }
    $status = if ($stopped) { '匹配当前目录' } else { '未运行' }
    Write-Host "已停止    : $Name ($status)"
}

Stop-AgentBeaconProcess 'agentbeacon-receiver' `
    (Join-Path $Dir 'receiver\agentbeacon-receiver.exe') `
    (Join-Path $Dir 'receiver.pid')
Stop-AgentBeaconProcess 'agentbeacon-indicator' `
    (Join-Path $Dir 'indicator\agentbeacon-indicator.exe')

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
