# AgentBeacon Windows 发布脚本（开发者侧）：
#   自包含发布 Receiver + Indicator → dist\agentbeacon-win-x64\
#   产出即可直接发给别人的"一个文件夹"：内含 .NET 运行时、配置模板、
#   接收方 install.ps1 / uninstall.ps1、使用说明。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File publish.ps1            # 自包含（对方免装 .NET）
#   ... -FrameworkDependent                                          # 体积小，但对方需装 .NET Desktop Runtime 10
#   ... -Zip                                                         # 额外产出 agentbeacon-win-x64.zip
#
# 接收方拿到文件夹后：编辑 agentbeacon.json（或 install.ps1 -Token ...）→ 运行 install.ps1。

param(
    [string]$Repo = "",
    [string]$Out = "",
    [switch]$FrameworkDependent,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

if (-not $Repo) {
    $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
}
if (-not $Out) {
    $Out = Join-Path $Repo 'dist\agentbeacon-win-x64'
}

$rid = 'win-x64'
$sc = if ($FrameworkDependent) { '--self-contained', 'false' } else { '--self-contained', 'true' }

Write-Host "== AgentBeacon publish =="
Write-Host "repo: $Repo"
Write-Host "out : $Out  ($(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))"

# Windows .NET SDK 在 \\wsl.localhost UNC 路径上构建会出现 CS0006 /
# ref assembly 丢失等问题 —— 先把源码复制到本地临时目录再发布。
$work = Join-Path ([IO.Path]::GetTempPath()) ("AgentBeacon-publish-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
robocopy $Repo $work /E /XD .git bin obj dist .vs /XF agentbeacon.json /R:1 /W:1 /NFL /NDL /NJH | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

# 1. 发布 Receiver（进程名 = agentbeacon-receiver，便于停旧实例）
dotnet publish (Join-Path $work 'receiver\AgentBeacon.Receiver.csproj') `
    -c Release -r $rid @sc `
    -p:PublishSingleFile=false `
    -o (Join-Path $Out 'receiver')
if ($LASTEXITCODE -ne 0) { throw "receiver publish failed" }

# 2. 发布 Indicator（WPF）
dotnet publish (Join-Path $work 'windows\AgentBeacon.Indicator\AgentBeacon.Indicator.csproj') `
    -c Release -r $rid @sc `
    -p:PublishSingleFile=false `
    -o (Join-Path $Out 'indicator')
if ($LASTEXITCODE -ne 0) { throw "indicator publish failed" }
Write-Host "publish   : ok"

# 3. 配置模板 → 直接可改的 agentbeacon.json
Copy-Item (Join-Path $work 'agentbeacon.example.json') (Join-Path $Out 'agentbeacon.json')

# 4. 接收方脚本 + 说明
Copy-Item (Join-Path $PSScriptRoot 'dist-install.ps1')   (Join-Path $Out 'install.ps1')
Copy-Item (Join-Path $PSScriptRoot 'dist-uninstall.ps1') (Join-Path $Out 'uninstall.ps1')
Copy-Item (Join-Path $PSScriptRoot '使用说明.txt')        (Join-Path $Out '使用说明.txt')

Write-Host "assembled : config + install.ps1 + uninstall.ps1 + 使用说明.txt"

# 5. 清理临时工作目录 + 可选 zip
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if ($Zip) {
    $zipPath = "$Out.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $Out -DestinationPath $zipPath
    Write-Host "zip       : $zipPath"
}

$size = (Get-ChildItem $Out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("== done ==  {0}  ({1:N0} MB)" -f $Out, $size)
Write-Host "把这个文件夹（或 -Zip 出来的压缩包）发给对方即可。"
