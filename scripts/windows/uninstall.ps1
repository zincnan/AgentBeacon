# AgentBeacon Windows 卸载：停进程 + 删开机自启快捷方式（可选 -RemoveFiles 删除安装目录）
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AgentBeacon",
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Continue'

Write-Host "== AgentBeacon uninstall =="

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
    $status = if ($stopped) { 'matched install dir' } else { 'not running' }
    Write-Host "stopped    : $Name ($status)"
}

Stop-AgentBeaconProcess 'agentbeacon-receiver' `
    (Join-Path $InstallDir 'receiver\bin\Release\net10.0\agentbeacon-receiver.exe') `
    (Join-Path $InstallDir 'receiver.pid')
Stop-AgentBeaconProcess 'agentbeacon-indicator' `
    (Join-Path $InstallDir 'windows\AgentBeacon.Indicator\bin\Release\net10.0-windows\agentbeacon-indicator.exe')

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
