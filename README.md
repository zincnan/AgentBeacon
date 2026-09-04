# AgentBeacon

> Ambient status lights for coding agents on Windows.

[English](README.en.md)

AgentBeacon 在 Windows 桌面右上角显示一列红绿灯，每个 coding agent 会话一盏。会话在运行、在等审批、已完成还是已失败，扫一眼就知道，不用来回切终端。

当同时开着几个 Claude Code、Codex 会话，或者 Agent 跑在远程主机上时，三件事经常发生：任务跑了几分钟不敢确定是否结束、权限确认弹出时没人在场、会话挂了几个小时才被发现。AgentBeacon 把这些状态变成桌面右上角持续存在的信号——卡片弹出几十秒告诉你发生了什么，卡片消失后灯仍然亮着，直到状态真正改变。

## Why AgentBeacon

不开 AgentBeacon 时，你的桌面可能是这样：

```
Claude Code   ─ running    （跑到哪了？不知道）
Codex         ─ approval   （等了 20 分钟，没人看见）
Claude Code   ─ completed  （早完成了，你才发现）
```

开了之后，这些状态就是右上角的三盏灯。你不需要切终端、翻日志，也不用一直盯着屏幕——余光扫一眼即可。

## Status

一个 Agent Session 对应一个独立的三灯模块，模块上方是 Agent 名称，同一 Agent 的多个会话各自一盏灯。任意时刻只有一个灯位亮起：

| 亮起灯位 | 状态 | 颜色 | 含义 | 通知卡片 |
| --- | --- | --- | --- | --- |
| 底部 | `running` | 蓝 `#2F81F7` | 正在执行任务 | 不弹出 |
| 中间 | `approval` | 黄 `#D29922` | 等待人工授权 | 弹出，30 秒后收回，黄灯保持 |
| 底部 | `completed` | 绿 `#3FB950` | 本轮完成 | 弹出，30 秒后收回，绿灯保留 5 分钟 |
| 顶部 | `failed` | 红 `#F85149` | 会话失败 | 弹出，30 秒后收回，红灯长期保持 |

严格只有这四种状态。不引入 idle、offline、unknown 等第五种；状态变化时新亮的灯会闪烁数秒，卡片消失不代表状态消失——灯才是持久信号。

<table>
  <tr>
    <td align="center"><img src="images/running.png" width="150"><br>running</td>
    <td align="center"><img src="images/approval.png" width="310"><br>approval</td>
  </tr>
  <tr>
    <td align="center"><img src="images/completed.png" width="310"><br>completed</td>
    <td align="center"><img src="images/failed.png" width="310"><br>failed</td>
  </tr>
</table>

## How it works

```text
Coding Agent（Claude Code / Codex / 任意程序）
    │
    ▼
Agent Adapter（理解各自 Runtime 的生命周期，翻译成四种状态）
    │  HTTP POST /api/v1/status
    ▼
Windows Receiver（内存状态表，last received wins）
    │
    │ Named Pipe（全量快照推送）
    ▼
Windows Indicator（红绿灯 + 通知卡片）
```

- Receiver 和 Indicator 是两个独立的 Windows 进程，各自启停互不影响。
- 每个 `session_id` 对应一盏独立的灯；Receiver 是状态的唯一权威。
- Adapter 负责理解不同 Agent Runtime 的生命周期并翻译状态；通用传输（一个 HTTP POST）内置于 Adapter，任何语言也可以直接 POST 接入。
- Indicator 置顶但不抢焦点、不进任务栏；没有会话时完全隐藏。
- 协议是单向的：Agent → Receiver 上报状态，不做审批回传，不拦截、不控制 Agent 的推理过程。

## Supported runtimes

| Runtime | 接入方式 | 安装与映射文档 |
| --- | --- | --- |
| Claude Code | 插件（hooks：SessionStart / UserPromptSubmit / PreToolUse / PostToolUse / PermissionRequest / PermissionDenied / Stop / StopFailure / SessionEnd） | [docs/adapter-claude-code.md](docs/adapter-claude-code.md) |
| Codex CLI | 插件（hooks：SessionStart / UserPromptSubmit / PreToolUse / PostToolUse / PermissionRequest / Stop / SessionEnd） | [docs/adapter-codex.md](docs/adapter-codex.md) |

两个 Adapter 均已在真实会话中验收：running / approval / completed 全流程，以及会话中途终止（SessionEnd-while-working）与进程异常退出（PID 看门狗）两条 `failed` 路径。其它 Runtime（OpenCode 等）见 [Roadmap](#roadmap)。

## Quick start

### 1. Windows 端

最简方式是把发布包文件夹发到目标机器（自包含 .NET 运行时，接收方无需安装任何环境）。在本仓库用 WSL 一条命令产出：

```bash
bash scripts/dist.sh --zip
```

得到 `dist/agentbeacon-win-x64/` 文件夹（约 250 MB）和同名 zip。在目标 Windows 机器上：

1. 把文件夹放到一个固定位置（例如 `C:\AgentBeacon`）；
2. 编辑其中的 `agentbeacon.json`（`bind` / `port` / `token` 三个字段，见 [Configuration](#configuration)）；
3. 双击 `install.bat`（或 `install.bat -Token <key> -Port 8765`）。

这一步会安装开机自启、在后台启动 Receiver 和 Indicator，右上角即出现状态灯列。卸载双击 `uninstall.bat`。

在开发机上也可以直接从源码安装：

```powershell
git clone <repo>; cd AgentBeacon
powershell -ExecutionPolicy Bypass -File scripts\windows\install.ps1
```

### 2. Agent 端

**Claude Code**：

```bash
claude plugin marketplace add /path/to/AgentBeacon
claude plugin install agentbeacon@agentbeacon
```

**Codex CLI**：

```bash
codex plugin marketplace add /path/to/AgentBeacon
codex plugin add agentbeacon-codex@agentbeacon
```

装完 Codex 插件后需打开 `codex` 执行 `/hooks`，对 AgentBeacon 条目执行信任——Codex 默认跳过未受信的 hooks。

**其它 Agent / 脚本**：向 `http://<windows-host>:8765/api/v1/status` POST 一个 JSON 即可，见 [Protocol](#protocol)；仓库附带现成脚本 [examples/smoke_curl.sh](examples/smoke_curl.sh) 可直接做四状态冒烟测试。

**共用配置**（两种 Agent 都读这一个文件）：

```json
{
  "url": "http://127.0.0.1:8765",
  "token": null
}
```

### 3. 验证

右上角出现灯即接入成功。没有任何会话时指示器完全隐藏，这也属正常。

## Configuration

Windows 端只有一个配置文件 `agentbeacon.json`（发布包内自带，源码仓库提供 `agentbeacon.example.json` 模板）：

```json
{
  "bind": "0.0.0.0",
  "port": 8765,
  "token": ""
}
```

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `bind` | `0.0.0.0` | 监听地址。仅本机使用可改为 `127.0.0.1` |
| `port` | `8765` | 服务端口 |
| `token` | `""` | 空字符串 = 免鉴权；填入非空值 = 要求 `Authorization: Bearer <token>` |

Agent 端共用 `~/.agentbeacon.json`：

```json
{
  "url": "http://127.0.0.1:8765",
  "token": null
}
```

安全边界：`token` 为空时，任何能连到该端口的主机都可以上报状态。这个模式只建议在本机调试或完全可信的内网使用；跨机器部署请配置相同的 token。`bind 0.0.0.0` + 空 token 的组合意味着局域网内所有人可写。

## Development

主要源码在 WSL 中开发；Windows 端（Receiver / Indicator）需要复制到 Windows 本地目录后 build 和运行——不建议直接在 `\\wsl.localhost\...` 路径上执行 Windows build/run。Git 操作仍在 WSL 原仓库进行。

完整的手工调试步骤见 [docs/wsl中开发时的调试说明.md](docs/wsl中开发时的调试说明.md)。简要流程：

```powershell
# Windows PowerShell：复制源码到本地目录（路径按你的仓库位置调整）
$repo  = (wsl wslpath -w /workstation/mytools/AgentBeacon).Trim()
robocopy $repo C:\Temp\AgentBeacon-Debug /E /XD .git bin obj
```

```powershell
# 构建 + 前台运行 Receiver（配置来自 agentbeacon.json）
dotnet build C:\Temp\AgentBeacon-Debug\receiver\AgentBeacon.Receiver.csproj -c Release
dotnet C:\Temp\AgentBeacon-Debug\receiver\bin\Release\net10.0\agentbeacon-receiver.dll --config C:\Temp\AgentBeacon-Debug\agentbeacon.json --debug
```

自动化测试 170 个（Python 3 套 + C# 2 套；C# 套件在 Linux 与 Windows 原生 .NET 上各执行一遍同一组用例）：`test_receiver.py` 42、`test_hook_adapter.py` 33、`test_codex_adapter.py` 19、`Receiver.IpcTests` 18、`Indicator.CoreTests` 58。

## Protocol

Protocol v1 只有一个 endpoint：

```text
POST /api/v1/status
```

请求体是 JSON：`session_id`（必选，一盏灯的身份）、`agent`（必选，展示名）、`status`（必选，上面四种之一）、可选 `message` 与 `host`。单向上报、last received wins、无重试；鉴权见上文 token 语义。完整规格见 [docs/protocol.md](docs/protocol.md)。

## Design principles

- **Glanceable instead of dashboard-heavy**：目标是在余光里可读，不是再造一个面板。
- **Session-oriented**：一盏灯对应一个会话；重命名、关闭、超时清理都以会话为单位。
- **Runtime-specific lifecycle detection stays in adapters**：Receiver 和协议不认识任何具体 Agent，生命周期翻译全部发生在各 Adapter 内。
- **Windows desktop first**：Receiver 与 Indicator 优先为 Windows 桌面场景设计。
- **Minimal protocol**：一个 endpoint、四个状态、无回传，不预留字段。
- **No agent interference**：只搬运状态，不参与推理、不拦截工具调用、不控制 Agent。

## Roadmap

- 更多 Runtime Adapter（OpenCode 等）
- 优化首次安装与 hook 信任的引导体验
- 诊断工具（连接状态、上报失败排查）

## License

[Apache-2.0](LICENSE)
