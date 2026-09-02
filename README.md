# AgentBeacon

> 你的 AI Agent 们，在你 Windows 桌面右上角的一排"红绿灯"。

同时开着好几个 Claude Code / Codex 会话干活时，你大概也有这些时刻：

- 任务一跑十几分钟，你隔一会儿就得切回终端看看"跑完没"——打断手头的事，又怕它早就在等你；
- Agent 停在权限确认上没人理，等你发现时半天已经过去了；
- 一个会话悄悄挂了，你两个小时后才知道。

AgentBeacon 把这些全部变成**余光扫一眼**的事：每个 Agent 会话在桌面右上角有一盏独立的红绿灯，蓝灯在转、黄灯喊你、绿灯收工、红灯出事。然后你就可以安心去干别的了（是的，安稳摸鱼）。

## 它能帮你做什么

- **不用反复查询**：agent 还在跑就是蓝灯亮着，扫一眼就知道，不必切终端、翻 tmux、看日志。
- **approval 秒级响应**：Agent 一等你授权，黄灯亮起 + 一张卡片从灯旁边弹出告诉你它在等什么；点完 yes 灯立刻变蓝，回去继续干自己的事。
- **多 Agent 一屏管理**：每个会话一盏独立的灯（同一种 Agent 开几个会话就是几盏），谁在干活、谁在等你、谁完成了、谁挂了，一眼全知道。
- **挂了立刻看见**：红灯长亮 + 错误卡片，不用等你自己撞上去。
- **零打扰**：永远置顶但不抢焦点、不进任务栏、无边框；你打字它不碰你的光标；没有任何会话时整个指示器**完全隐身**。

## 它长什么样

每个 Agent 会话 = 一个竖向三灯模块，上方是 Agent 名字：

```
 Claude Code          OpenCode
 ┌─────────┐          ┌─────────┐
 │    ·    │          │    ●    │  ← 红 = failed（挂了）
 │    ●    │          │    ·    │  ← 黄 = approval（等你授权）
 │    ·    │          │    ·    │  ← 底灯 = running 蓝 / completed 绿
 └─────────┘          └─────────┘
```

| 灯位 / 颜色 | 状态 | 含义 | 通知卡片 |
| --- | --- | --- | --- |
| 底部 🔵 蓝 | `running` | 正在干活，别打断 | 不弹 |
| 中间 🟡 黄 | `approval` | 在等你授权，快去 | 弹出 8 秒后收回，**黄灯保持** |
| 底部 🟢 绿 | `completed` | 本轮完成 | 弹出 5 秒后收回，绿灯保留 5 分钟 |
| 顶部 🔴 红 | `failed` | 挂了，去看错误 | 弹出 10 秒后收回，**红灯长亮** |

卡片从对应灯的左侧弹出、展示 agent / 状态 / 消息，停留片刻自动收回——**卡片消失 ≠ 状态消失**，灯才是持久信号。多张卡片同时弹出时自动避让不重叠。

日常小操作：**左键拖动**任意灯可挪动整个灯列；**右键 → 关闭此灯**可清掉不再关心的会话（该会话一旦有新状态，灯会自动重建）。

## 环境要求

| 组件 | 要求 |
| --- | --- |
| Windows 桌面端（Receiver + Indicator） | Windows 10/11，.NET 10 SDK（开发验证版本 10.0.400），WPF 桌面运行时随 SDK 提供 |
| Agent 端 | 任何能发 HTTP POST 的环境（WSL / Linux / macOS / Windows） |
| `agent-notify`（通用上报脚本） | Python 3.8+，仅标准库，无第三方依赖（测试环境 3.12） |
| Claude Code 插件 Adapter | Claude Code 2.1+（hooks / 插件机制，验证版本 2.1.250） |
| 网络 | Agent 能访问 Receiver 的 `IP:端口`；**WSL 场景** Receiver 需绑定 `0.0.0.0` 并在 Windows 防火墙放行端口 |

无数据库、无后台服务、无第三方运行时依赖：Receiver 和 Indicator 就是两个小进程，状态全在内存里。

## 快速开始

### 1. 启动 Windows 端（一次性）

```powershell
git clone <repo> ; cd AgentBeacon

# Receiver（鉴权二选一：带 key 或免 key）
dotnet build receiver -c Release
dotnet run --project receiver -c Release --no-build -- `
  --bind 0.0.0.0 --port 8765 --token "<你的token>" --debug
# 免 key（仅本机调试 / 可信内网）：
#   ... --bind 0.0.0.0 --port 8765 --no-auth --debug

# Indicator（另开一个终端）
dotnet build windows\AgentBeacon.Indicator -c Release
dotnet run --project windows\AgentBeacon.Indicator -c Release --no-build
```

启动成功后 Indicator 安静地待在屏幕右上角（此时没有会话，所以什么都看不到——这是设计）。

### 2. 接入你的 Agent

**方式 A：Claude Code 用户（推荐，装完全自动）**

安装一次，之后**任何目录直接 `claude`**，红绿灯自动跟随所有会话：

```bash
# 一次性安装（本仓库自带 marketplace）
claude plugin marketplace add /path/to/AgentBeacon    # 或 github: zinc/AgentBeacon
claude plugin install agentbeacon@agentbeacon

# 配置 Receiver 地址（二选一）：
#  ① 写进 ~/.bashrc：export AGENTBEACON_URL=... [AGENTBEACON_TOKEN=...]
#  ② 跟插件走：安装时加 --config agentbeacon_url=... [--config agentbeacon_token=...]
```

开发期免安装临时侧载用 `claude --plugin-dir plugins/claude-code`。详见 [docs/adapter-claude-code.md](docs/adapter-claude-code.md)。

**方式 B：任何其他 Agent / 脚本（一个 HTTP POST 的事）**

```bash
curl -X POST http://<windows主机IP>:8765/api/v1/status \
  -H "Authorization: Bearer <你的token>" \
  -H "Content-Type: application/json" \
  -d '{"session_id":"job-1","agent":"my-script","status":"running","message":"开始处理"}'
```

或用通用上报脚本 `notify/agent_notify.py`（token 可选，与 `--no-auth` 模式配合可完全免 key）。协议只有这一个 endpoint，字段说明见 [docs/protocol.md](docs/protocol.md)。

### 3. 验证

上面那条 curl 发完，屏幕右上角立刻出现 `my-script` 的蓝灯；把 status 换成 `approval` / `completed` / `failed` 再发，看灯变色、卡片弹出收回。开 `--debug` 时可随时 `GET /debug/sessions` 查看 Receiver 收到的全部状态。

## 它是怎么工作的（30 秒版）

```
Agent（Claude Code 插件 / 任意脚本）
    │  HTTP POST /api/v1/status（单向、无重试、last-received-wins）
    ▼
Receiver（Windows，C# / ASP.NET Core）     ←─ 状态的唯一权威，内存中维护
    │  本机 Named Pipe（全量快照推送）
    ▼
Indicator（Windows，WPF 红绿灯面板）
```

三个进程各自独立启停：Agent 侧不依赖任何 Windows UI 实现，Receiver 不认识任何具体 Agent，Indicator 只订阅状态。AgentBeacon 不参与推理、不接管工具调用、不修改 Agent 本体——它只是把 Hook 捕获的生命周期事件搬运到你眼前。详细介绍见 [docs/architecture.md](docs/architecture.md)。

## 项目状态

- **Round 1**：Protocol v1 + `agent-notify` + Receiver（HTTP、校验、last-received-wins）
- **Round 2**：Windows Indicator MVP（WPF + Named Pipe IPC、无焦点、completed 墓碑）
- **Round 3**：三灯红绿灯 UI 重构（模块化、卡片锚定、碰撞布局）
- **Round 4**：Claude Code 插件 Adapter（hooks 映射、进程看门狗、两条 failed 上报路径）
- **Round 5**：灯右键关闭 + 拖拽定位
- **Round 6**：鉴权双模式（token / --no-auth）

自动化测试 **151 个 unique tests** 全部通过（Python 3 套 + C# 2 套；C# 套件在 Linux 与 Windows 原生 .NET 上各跑一遍同一组用例）：

| 套件 | 数量 |
| --- | --- |
| `tests/test_notify.py` | 8 |
| `tests/test_receiver.py` | 40 |
| `tests/test_hook_adapter.py` | 29 |
| `tests/Receiver.IpcTests` | 16 |
| `tests/Indicator.CoreTests` | 58 |

尚未实现（不在本期范围）：其它 Agent Runtime 的官方 Adapter、Windows Service / 安装器 / 开机自启、SQLite 持久化、WebSocket/SSE、审批回传、设置界面。

## 文档

- [docs/architecture.md](docs/architecture.md) — 整体链路与组件职责
- [docs/protocol.md](docs/protocol.md) — v1 HTTP 状态上报协议（含鉴权双模式）
- [docs/ui-policy.md](docs/ui-policy.md) — UI 行为规则（canonical）
- [docs/adapter-claude-code.md](docs/adapter-claude-code.md) — Claude Code Adapter 安装与映射
- [docs/round2.md](docs/round2.md) — Round 2 设计与运行说明（历史文档）
- [docs/wsl中开发时的调试说明.md](docs/wsl中开发时的调试说明.md) — WSL 开发时的手工调试指南
