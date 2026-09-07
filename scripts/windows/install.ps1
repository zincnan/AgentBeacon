# AgentBeacon Windows 一键安装：
#   复制到本地目录 → 构建 → 生成 agentbeacon.json（首次）→ 创建开机自启快捷方式 → 后台启动
#
# 用法（Windows PowerShell）：
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   可选: -Repo <仓库路径>（默认脚本位置向上两级）
#         -InstallDir <安装目录>（默认 %LOCALAPPDATA%\AgentBeacon）
#         -NoStart（只安装，不立即启动）
#
# 之后改配置只需编辑 <安装目录>\agentbeacon.json 然后重启（或重新运行本脚本）。
# 卸载：uninstall.ps1

param(
    [string]$Repo = "",
    [string]$InstallDir = "$env:LOCALAPPDATA\AgentBeacon",
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

if (-not $Repo) {
    # ProviderPath strips the "Microsoft.PowerShell.Core\FileSystem::"
    # prefix that Resolve-Path adds for UNC paths (robocopy can't parse it)
    $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
}

Write-Host "== AgentBeacon install =="
Write-Host "repo       : $Repo"
Write-Host "install dir: $InstallDir"

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

# 1. 复制源码到本地目录（Windows .NET SDK 不应在 \\wsl.localhost UNC 路径上构建）
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
robocopy $Repo $InstallDir /E /XD .git bin obj .vs /XF agentbeacon.json /R:1 /W:1 /NFL /NDL /NJH | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }

# 2. 构建 Receiver + Indicator
dotnet build (Join-Path $InstallDir 'receiver\AgentBeacon.Receiver.csproj') -c Release `
    | Out-Null
if ($LASTEXITCODE -ne 0) { throw "receiver build failed" }
dotnet build (Join-Path $InstallDir 'windows\AgentBeacon.Indicator\AgentBeacon.Indicator.csproj') -c Release `
    | Out-Null
if ($LASTEXITCODE -ne 0) { throw "indicator build failed" }
Write-Host "build      : ok"

# 3. 首次安装生成配置文件（不覆盖已有配置）
$config = Join-Path $InstallDir 'agentbeacon.json'
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $InstallDir 'agentbeacon.example.json') $config
    Write-Host "config     : created $config  <-- 按需修改端口 / token / no_auth"
}

# 4. Receiver 启动脚本（隐藏窗口运行，读取配置文件）。
#    用 apphost .exe 而不是 `dotnet dll`：进程名是 agentbeacon-receiver，
#    重装/卸载时 Stop-Process -Name 才能找到并停掉它。
$runReceiver = @'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir 'receiver\bin\Release\net10.0\agentbeacon-receiver.exe'
$cfg = Join-Path $dir 'agentbeacon.json'
$pidFile = Join-Path $dir 'receiver.pid'
$p = Start-Process $exe -ArgumentList @('--config', $cfg) -WorkingDirectory $dir -PassThru -WindowStyle Hidden
Set-Content -Path $pidFile -Value $p.Id -Encoding ASCII
Wait-Process -Id $p.Id
Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
'@
Set-Content -Path (Join-Path $InstallDir 'run-receiver.ps1') -Value $runReceiver -Encoding UTF8

# 5. 停掉当前安装目录对应的旧实例（避免误杀其他端口/开发实例）
$receiverExe = Join-Path $InstallDir 'receiver\bin\Release\net10.0\agentbeacon-receiver.exe'
$indicatorExe = Join-Path $InstallDir 'windows\AgentBeacon.Indicator\bin\Release\net10.0-windows\agentbeacon-indicator.exe'
Stop-AgentBeaconProcess 'agentbeacon-receiver' $receiverExe (Join-Path $InstallDir 'receiver.pid')
Stop-AgentBeaconProcess 'agentbeacon-indicator' $indicatorExe

# 6. 开机自启快捷方式（shell:startup）
$startup = [Environment]::GetFolderPath('Startup')
$ws = New-Object -ComObject WScript.Shell

$lnk1 = $ws.CreateShortcut((Join-Path $startup 'AgentBeacon Receiver.lnk'))
$lnk1.TargetPath = 'powershell.exe'
$lnk1.Arguments  = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$InstallDir\run-receiver.ps1`""
$lnk1.WorkingDirectory = $InstallDir
$lnk1.WindowStyle = 7
$lnk1.Save()

$lnk2 = $ws.CreateShortcut((Join-Path $startup 'AgentBeacon Indicator.lnk'))
$lnk2.TargetPath = $indicatorExe
$lnk2.WorkingDirectory = $InstallDir
$lnk2.Save()
Write-Host "autostart  : 2 shortcuts in $startup"

# 7. 立即后台启动
if (-not $NoStart) {
    Start-Process 'powershell.exe' `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$InstallDir\run-receiver.ps1`"" `
        -WorkingDirectory $InstallDir -WindowStyle Hidden
    Start-Process $indicatorExe -WorkingDirectory $InstallDir
    Write-Host "started    : receiver + indicator (background)"
}

Write-Host ""
Write-Host "== done =="
Write-Host "编辑配置: $config"
Write-Host "开机自启: $startup (AgentBeacon Receiver / Indicator 两个快捷方式，删掉即取消自启)"
Write-Host "卸载    : powershell -ExecutionPolicy Bypass -File uninstall.ps1"
