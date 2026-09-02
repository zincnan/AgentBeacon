# AgentBeacon

AgentBeacon 是一个面向 Windows 桌面的 AI Agent 实时状态提示系统。

它把本地或远端主机上正在运行的各个 AI Session 的当前状态，以轻量、无侵入的形式汇报到用户的 Windows 桌面，并显示为一组右上角的状态灯与状态变化通知卡片。

AgentBeacon 不参与 Agent 的推理，不接管 Agent 的工具调用，也不强制 Agent 自己“记得”要通知用户。它只负责把 Agent Runtime 的 Hook 自动捕获到的状态事件，原样、低延迟地搬运到用户的桌面上。

AgentBeacon **不专属**于任何特定 Agent 实现。架构上把"生命周期翻译"与"状态上报"清楚分开：

- `agent-notify` 是一个 **generic status reporting CLI / transport helper**：它不识别 Agent Runtime、不推断状态、不翻译 lifecycle、不绑定 Claude Code 或 OpenCode。它的唯一职责是把已经翻译好的 `running / approval / completed / failed` 状态事件通过 HTTP POST 送到 Receiver。
- 真正的 Adapter 是 **per-runtime 的薄层**：监听 Agent Runtime 的 lifecycle 事件（hook / plugin / callback），把它们**翻译**成四状态之一，再调用 `agent-notify`。每个 Agent Runtime（Claude Code、OpenCode、Codex、自研 runtime）需要各自的 Adapter。

任何能直接调 `POST /api/v1/status` 的程序都可以绕过 `agent-notify` 接入 Receiver，但它仍然要负责把 lifecycle 翻译成 4 状态之一 —— 这就是 Adapter 的工作。v1 在仓库里没有 in-tree Adapter；Claude Code 参考 Adapter 计划在 Round 4（Round 3 被 Indicator 三灯 UI 重构使用）。

---

## 项目边界

项目一开始就保持两侧的清晰隔离：

- **Agent 侧**：`Hook -> agent-notify`。运行在 Agent 所在主机（通常是 Linux/WSL），负责识别状态并把它送到 HTTP endpoint。
- **Windows 侧**：`Receiver` 和 `Indicator` 是两个**独立进程**，各自独立启停。`Receiver` 接收 HTTP 请求、维护最新状态；`Indicator` 是 WPF 桌面 UI，通过本机 Named Pipe 订阅 Receiver 的状态快照。

两侧只通过 `docs/protocol.md` 定义的 v1 HTTP 协议通信。两侧可以用不同语言实现，**不强制共享运行时**。当前：

- `agent-notify` 是 Python 单文件脚本，stdlib only。
- `Receiver` 是 C# / ASP.NET Core（Kestrel），目标框架为 **.NET 10（net10.0）**，跨平台，**v1 不引入 Windows Service / 任何 Windows-only 代码**。
- `Indicator` 是 C# / WPF，目标框架为 **.NET 10 Windows（net10.0-windows）**，**只在 Windows 上运行**，通过 Named Pipe IPC 与 Receiver 通信。该 IPC 是 Windows 本机内部实现细节，不属于 HTTP Protocol v1。

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

UI 行为不在 HTTP 协议层表达，由 Indicator 实现。v1 规则（详见 [docs/ui-policy.md](docs/ui-policy.md)）：

**状态模块（红绿灯）**：一个 Agent Session = 一个独立的竖向三灯模块（上方 `agent` 标签，下方深色 housing）。顶部红灯 = `failed`，中间黄灯 = `approval`，底部灯位由 `running`（蓝）/ `completed`（绿）共用。任意时刻只有一个灯位亮起，其余灯位保持极暗灯罩色 —— 不存在第五种状态。

| 状态        | 亮起灯位 / 颜色 | 卡片行为                                 | 模块行为                     |
| ----------- | --------------- | ---------------------------------------- | ---------------------------- |
| `running`   | 底部 / 蓝       | 不弹卡片                                 | 蓝灯，保留                   |
| `approval`  | 中间 / 黄       | 弹卡片，**8 秒后自动收回**               | 黄灯，保留直到状态变化       |
| `completed` | 底部 / 绿       | 弹卡片，**5 秒后自动收回**               | 绿灯，5 分钟后自动移除       |
| `failed`    | 顶部 / 红       | 弹卡片，**10 秒后自动收回**              | 红灯，长期保留直到状态变化   |

卡片停留时长（8s / 5s / 10s）与弹出/收回动画时长是 **UI 常量**，不属于 HTTP Protocol v1，可独立调整。

**卡片锚定在所属 Agent 模块的左侧**：向左弹出（~220 ms）→ 停留（按时长）→ 向右收回（~200 ms）→ 隐藏；灯保持。多卡同时弹出时由 `AnchoredCardLayout` 做碰撞调整，不重叠。模块和卡片均 `ShowActivated=False` + `WS_EX_NOACTIVATE`，**不会抢占前台焦点**；没有任何 session 时 Indicator 完全不可见。

`completed -> running` 是合法的（例如用户在同一 Session 发起下一轮任务）。仍严格只有四色，不引入 idle / offline / unknown / paused。任何未知 status 都会被 Receiver 拒绝（HTTP 400），即便绕开 Receiver，Indicator 的 Core 层也会 fail-fast 抛出。

---

## 当前状态

Round 3（Indicator 三灯 UI 重构）已完成：

- `agent-notify`：Python 单文件 HTTP 转发脚本（stdlib only）
- Receiver：C# / .NET 10 / ASP.NET Core / Kestrel，跨平台，承载 v1 HTTP 协议
- Indicator：C# / .NET 10 Windows / WPF，独立进程，通过本地 Named Pipe 订阅 Receiver
- Receiver → Indicator 的 IPC：长度前缀 JSON SnapshotEnvelope，每客户端独立 bounded Channel，DropOldest；POST 不等待 Named Pipe I/O，慢 Indicator 不会 back-pressure POST
- 默认 Pipe 名 `AgentBeacon.Status`，可通过 `--pipe <name>` / `AGENTBEACON_PIPE` 覆盖，`--no-pipe` 显式关闭（主要用于测试）
- Indicator UI（Round 3）：
  - 三灯红绿灯模块（`LampModuleView`），每 session 一个模块，映射逻辑在纯函数 `LampStateMapper`
  - 卡片锚定所属模块左侧，弹出/停留/收回动画；停留时长 approval 8s / completed 5s / failed 10s / running 无卡片
  - `AnchoredCardLayout` 纯计算碰撞布局，多卡不重叠、空间不足优先隐藏旧卡
  - 零 session 完全不可见；authoritative snapshot 删除 session 时模块与卡片同步清理
  - 诊断日志：回调异常写入 `%TEMP%\AgentBeacon-indicator.log`
- 自动化测试：**115 个 unique tests**（45 个 Round 1 + 46 个 Round 2 + 24 个 Round 3）全部通过
  - `tests/test_notify.py` — 7
  - `tests/test_receiver.py` — 38
  - `tests/Receiver.IpcTests` — 15（race-aware connect-before-concurrent-POSTs + multi-client fanout 1-snapshot-per-Upsert）
  - `tests/Indicator.CoreTests` — 55（completed-suppression 墓碑 + partial-read/payload-EOF 语义 + 三灯映射/停留策略 + AnchoredCardLayout 碰撞布局 + 快照/卡片事件顺序）
  - `Indicator.CoreTests` 与 `Receiver.IpcTests` 在 Linux 与 Windows 原生 `dotnet.exe` 上各执行一次，跑同一组 unique tests（不重复计数）

尚未实现：

- Claude Code 参考 Adapter（计划 Round 4）
- Windows Service / 安装器 / 自动启动
- SQLite、用户账号、复杂认证（v1 只用共享 Bearer Token）
- WebSocket / SSE / 长连接
- 自动重试（v1 不实现，避免迟到旧事件覆盖新状态）
- heartbeat（v1 不实现）
- 权限审批回传
- 卡片点击交互、设置页、多语言

参见：

- [docs/architecture.md](docs/architecture.md) — 整体链路与组件职责
- [docs/protocol.md](docs/protocol.md) — v1 HTTP 状态上报协议
- [docs/ui-policy.md](docs/ui-policy.md) — UI 行为规则（canonical）
- [docs/round2.md](docs/round2.md) — Round 2（Windows Indicator）的设计与运行说明（历史文档）
