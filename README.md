# AgentBeacon

AgentBeacon 是一个面向 Windows 桌面的 AI Agent 实时状态提示系统。

它把本地或远端主机上正在运行的各个 AI Session 的当前状态，以轻量、无侵入的形式汇报到用户的 Windows 桌面，并显示为一组右上角的状态灯与状态变化通知卡片。

AgentBeacon 不参与 Agent 的推理，不接管 Agent 的工具调用，也不强制 Agent 自己“记得”要通知用户。它只负责把 Agent Runtime 的 Hook 自动捕获到的状态事件，原样、低延迟地搬运到用户的桌面上。

---

## 项目边界

项目一开始就保持两侧的清晰隔离：

- **Agent 侧**：`Hook -> agent-notify`。运行在 Agent 所在主机（通常是 Linux/WSL），负责识别状态并把它送到 HTTP endpoint。
- **Windows 侧**：`Receiver -> Indicator / Notification`。运行在用户的 Windows 主机，负责接收 HTTP 请求、维护最新状态、驱动桌面 UI。

两侧只通过 `docs/protocol.md` 定义的 v1 HTTP 协议通信。两侧可以用不同语言实现，**不强制共享运行时**。当前：

- `agent-notify` 是 Python 单文件脚本，stdlib only。
- `Receiver` 是 C# / ASP.NET Core（Kestrel），目标框架为 **.NET 10（net10.0）**，跨平台，**v1 不引入 Windows Service / WPF / 任何 Windows-only 代码**。当前开发环境 SDK 为 .NET 10.0.111。如未来要支持 .NET 8，需要显式 retarget / multi-target，不属于当前任务。进入 Windows 阶段时会在当前 Receiver 之上加一层 Windows Service host，不会重写 Receiver。

---

## 它解决什么问题

当你同时跑着多个 Agent Session（例如 Claude Code Session A、Claude Code Session B、Codex Session C），你经常需要快速知道：

- 哪个 Agent 现在还在干活（不要再去打断它）
- 哪个 Agent 正在等你点确认（必须马上处理）
- 哪个 Agent 这一轮已经完成（可以接着推下一步）
- 哪个 Agent 跑挂了（需要立刻看错误）

把这些信息从终端日志、tmux 标题、各家 CLI 的内置状态里翻出来太分散，也不直观。AgentBeacon 把它们集中到 Windows 桌面右上角的一排状态灯上。

---

## 四种状态

v1 严格只支持以下四种状态。其他任何取值（包括 `idle` / `offline` / `unknown` / `paused` 等）都不会被 Receiver 接受。

| 颜色 | 状态 | 含义 |
| --- | --- | --- |
| 🔵 蓝色 | `running` | Agent 正在处理任务 |
| 🟡 黄色 | `approval` | Agent 正在等待人工授权 |
| 🟢 绿色 | `completed` | 本轮任务正常完成 |
| 🔴 红色 | `failed` | 任务发生错误或异常终止 |

合法状态流转示例：

```
completed -> running
running   -> approval
approval  -> running
running   -> completed
running   -> failed
approval  -> failed
```

注意：v1 **不提供 Session 离线/在线状态**。一个 Session 如果不再上报任何事件，Receiver 会保留它最后一次的状态；UI 侧的“清理已完成的 Session”是显式策略，不在协议中表达。

一盏状态灯对应一个 Agent Session，而不是一个 Agent 类型。同一个 Agent 类型下的多个 Session 会以多盏独立的状态灯同时存在。

`failed` 的能力边界：v1 的 `failed` 仅表示 Hook / Runtime 能观察到的失败事件。进程被强杀、宿主机宕机、网络中断导致 Hook 自身无法执行的硬故障，v1 **不保证**能上报 `failed`。当前不为此引入 heartbeat 或 wrapper。

---

## 基本工作原理

```
Agent Runtime
    ↓ (生命周期事件)
Hook (立即翻译为 4 状态之一)
    ↓ (本地 CLI 调用，无 debounce)
agent-notify 脚本
    ↓ (HTTP POST /api/v1/status，单次请求，无重试)
Windows AgentBeacon Receiver
    ↓ (内部事件)
桌面右上角状态灯 + 状态变化通知卡片
```

职责划分：

- **Agent Runtime / Hook**：识别状态事件，**生命周期事件出现后立即**翻译为 4 状态之一并调用 notify。`approval` 必须立刻触发（不要等用户即将超时）。不做 debounce、不做合并。
- **agent-notify 脚本**：只负责把状态事件通过 HTTP 转发出去，不做推断、不做重试、不做缓存。
- **Windows Receiver**：接收 HTTP 请求，校验 Bearer Token，按 `session_id` 维护每个 Session 的最新状态（last received wins），向 Indicator 推送变化。
- **桌面 Indicator / Notification**：把当前状态渲染为颜色灯 + 通知卡片。

Agent 自身不需要也不应该主动发送通知。所有上报都由 Hook 自动触发。

---

## UI 行为规则

UI 行为不在 HTTP 协议层表达，由 Indicator 实现。v1 暂定规则：

- `running`：蓝色，只更新状态灯，默认不弹卡片。
- `approval`：黄色，弹出卡片，卡片持续显示直到状态离开 `approval`。
- `completed`：绿色，弹出卡片；卡片展示完毕后，Session 在状态灯列表中保留 5 分钟后自动移除（后续做成可配置项）。
- `failed`：红色，弹出卡片；Session 在状态灯列表中长期保留，直到下一次状态变化或人工清理。

`completed` 状态不会转成灰色。`completed -> running` 是合法的（例如用户在同一 Session 发起下一轮任务）。仍然严格只有四色，不引入 idle / offline / unknown。

---

## 当前状态

早期开发阶段。

本仓库当前只确定：

- 项目目标与产品边界
- 四种状态枚举及其语义
- Agent 侧 / Windows 侧分层与通信协议
- v1 HTTP 状态上报协议
- UI 行为规则（暂定）

第一阶段已经确定但尚未实现：

- `agent-notify`：HTTP 转发脚本（Python 单文件，stdlib only）
- Windows Receiver：C# / ASP.NET Core / Kestrel，跨平台
- Claude Code 参考 Adapter（仅在 receiver + notify 跑通后再做）

尚未实现：

- Windows 桌面 GUI
- Claude Code 参考 Adapter
- Windows Service / 安装器 / 自动启动
- SQLite、用户账号、复杂认证（v1 只用共享 Bearer Token）
- WebSocket / SSE / 长连接
- 自动重试（v1 不实现，避免迟到旧事件覆盖新状态）
- heartbeat（v1 不实现）
- 权限审批回传

参见：

- [docs/architecture.md](docs/architecture.md) — 整体链路与组件职责
- [docs/protocol.md](docs/protocol.md) — v1 HTTP 状态上报协议
- [docs/ui-policy.md](docs/ui-policy.md) — UI 行为规则