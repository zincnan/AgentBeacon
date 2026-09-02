### WSL 开发时手工调试 Windows 端

项目源码位于 WSL，但 Windows 端程序应复制到 **Windows 本地目录**后再构建和运行。

> 不要直接在 `\\wsl.localhost\...` 上 build/run。Windows .NET SDK 在 WSL UNC 路径上可能出现 `CS0006`、reference assembly 找不到等问题。

#### 1. 复制源码到 Windows

在 Windows PowerShell：

```powershell
$repo  = (wsl wslpath -w /workstation/mytools/AgentBeacon).Trim()
$local = "C:\Temp\AgentBeacon-Debug"

Remove-Item $local -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $local | Out-Null

robocopy $repo $local /E /XD .git bin obj .vs /R:1 /W:1
```

#### 2. 构建 Windows 端

```powershell
dotnet build "$local\receiver\AgentBeacon.Receiver.csproj" -c Release

dotnet build `
  "$local\windows\AgentBeacon.Indicator\AgentBeacon.Indicator.csproj" `
  -c Release
```

#### 3. 启动 Indicator

```powershell
Start-Process `
  "$local\windows\AgentBeacon.Indicator\bin\Release\net10.0-windows\agentbeacon-indicator.exe" `
  -WorkingDirectory $local
```

#### 4. 前台启动 Receiver

鉴权二选一（都不给会拒绝启动）：

带 key（共享 Bearer）：

```powershell
dotnet "$local\receiver\bin\Release\net10.0\agentbeacon-receiver.dll" `
  --bind 0.0.0.0 `
  --port 8765 `
  --token "<TEST_TOKEN>" `
  --debug
```

免 key（仅限本机调试 / 可信内网，启动日志会有警告）：

```powershell
dotnet "$local\receiver\bin\Release\net10.0\agentbeacon-receiver.dll" `
  --bind 0.0.0.0 `
  --port 8765 `
  --no-auth `
  --debug
```

前台运行方便直接查看错误和日志。

#### 5. WSL 中联调

```bash
curl http://127.0.0.1:8765/healthz

export AGENTBEACON_URL=http://127.0.0.1:8765
```

带 key 模式还需要（免 key 模式跳过，不设置即可，不会发 Authorization 头）：

```bash
export AGENTBEACON_TOKEN="<TEST_TOKEN>"
```

然后在 WSL 原仓库中启动需要调试的 Agent，例如：

```bash
cd /workstation/mytools/AgentBeacon

claude --plugin-dir plugins/claude-code
```

真实链路为：

```text
WSL Agent
  → Adapter / Hook
  → agent-notify
  → HTTP
  → Windows Receiver
  → Named Pipe
  → Windows Indicator
```

每次修改 Windows 端代码后，应重新复制源码、重新 build，并重启 Receiver / Indicator，避免误测旧 binary。

Git 操作始终在 WSL 原仓库中进行；`C:\Temp\AgentBeacon-Debug` 仅用于 Windows 构建、运行和手工调试。
