# Architecture

本文档描述 AgentBeacon v1 的整体链路、组件职责和数据流。

## 1. 组件视图

```
┌─────────────────────────┐
│      Agent 侧           │
│                         │
│  Agent Runtime          │
│       ↓                 │
│  Hook (立即翻译为 4 状态) │
│       ↓                 │
│  agent-notify (搬运)    │
│   - Python stdlib       │
│   - env var 配置        │
└────────────┬────────────┘
             │ HTTP POST /api/v1/status
             │ Authorization: Bearer <token>
             ▼
┌─────────────────────────┐
│      Windows 侧         │
│                         │
│  Receiver (HTTP 服务)   │
│   - C# / ASP.NET Core   │
│   - Kestrel, 跨平台     │
│       │                 │
│       │ Named Pipe IPC  │   <-- 本机内部细节,不属于 Protocol v1
│       │ (System.IO.Pipes)│
│       ▼                 │
│  Indicator (WPF)        │
│   - C# / .NET 10 Windows│
│   - 独立进程,独立启停   │
└─────────────────────────┘
```

**Receiver 与 Indicator 是两个独立进程**，各自独立启停。Agent 侧只与 Receiver 的 HTTP endpoint 通信；Indicator 通过 Windows Named Pipe 订阅 Receiver 的状态快照。

两侧只通过 `docs/protocol.md` 定义的 v1 HTTP 协议通信：

- `agent-notify` 不依赖任何 Windows UI 实现。
- Receiver 不依赖任何特定 Agent Runtime。
- Named Pipe IPC **不属于 HTTP Protocol v1**，是 Windows 本机内部的实现细节，未来若有 macOS / Linux Indicator 再重新设计。

两侧可以用不同语言实现，**不强制共享运行时**。当前 `agent-notify` 是 Python，`Receiver` 是 C# / ASP.NET Core，`Indicator` 是 C# / WPF。隔离让 Agent 侧可以在 Linux/WSL 上单独演进，Windows 侧可以独立实现 UI。

---

## 2. Agent 侧组件

### 2.1 Agent Runtime

负责实际执行 AI Agent 的工作流。它本身不直接与 AgentBeacon 通信，也不应该由模型主动调用通知命令。

Agent Runtime 在以下时刻会产生可观测的状态变化：

- 进入处理循环 → `running`
- 进入授权等待 → `approval`
- 本轮正常结束 → `completed`
- 发生未捕获错误或异常终止 → `failed`

### 2.2 Hook

Hook 是 Agent Runtime 的状态事件源，挂在 Agent Runtime 提供的生命周期事件上。第一个参考 Adapter 是 Claude Code，已以插件形态实现（`plugins/claude-code/`，见 [adapter-claude-code.md](adapter-claude-code.md)）。

Hook 的职责：

- 监听 Agent Runtime 的生命周期事件
- **事件出现后立即**将其翻译为 `running` / `approval` / `completed` / `failed` 之一，并调用 `agent-notify`
- 负责生成并保持同一个 Session 生命周期内 `session_id` 不变

Hook 必须做到：

- **立即上报**：不做 debounce、不做合并、不做节流。`approval` 尤其要在进入等待的那一刻立刻发出（让用户在超时之前能看到）。
- **不修改、不补全业务字段**：除了把状态事件翻译成协议字段，不做任何业务推断。

Claude Code 的具体生命周期事件到四个状态之间的映射，等真正实现 Claude Code 参考 Adapter 时依据 Claude Code 自身的 Hook / Plugin API 确定。

Hook 不应该：

- 自行重试失败的通知（v1 不实现重试）
- 自行选择目标主机（由配置层处理）
- 缓存历史状态（接收端负责）

### 2.3 agent-notify

`agent-notify` 是一个 Python 单文件脚本（stdlib only），与具体 Agent 解耦。它的唯一职责是把状态事件通过 HTTP 转发给 Windows 端的 Receiver。

约束：

- **不识别 Agent 类型**。不关心当前上报来自 Claude Code 还是 Codex。
- **不推断状态**。收到的 `status` 字段是什么就发什么。
- **不持有会话状态**。每次调用都是一次独立的 HTTP 请求。
- **v1 不做重试**。如果网络抖动导致请求失败，notify 直接把失败交给调用方（Hook / 手工测试脚本）。这是有意的设计：异步重试可能让迟到的旧事件覆盖已经到达的新状态，违反 last-received-wins 语义。可靠投递与 sequence 留待后续版本。
- **响应为 2xx 视为送达成功**；响应为 4xx 视为请求本身非法；响应为 5xx 或网络错误视为暂时性失败，由调用方决定后续处理（v1 即直接失败）。

#### 2.3.1 配置

| 配置项 | 优先级 | 说明 |
| --- | --- | --- |
| `AGENTBEACON_URL` | env var | Receiver 地址，例如 `http://127.0.0.1:8765` |
| `AGENTBEACON_TOKEN` | env var | 共享 Bearer Token |
| `--url` | CLI flag | 覆盖 `AGENTBEACON_URL` |
| `--token` | CLI flag | 覆盖 `AGENTBEACON_TOKEN`。**仅用于开发调试**：命令行 secret 会进入 shell history 与 `ps` / `/proc/<pid>/cmdline`，正常部署不应使用。 |

#### 2.3.2 字段防御性处理

- 必选字段本地校验：缺失或为空 → 直接退出非零，不发请求。
- `status` 在本地校验是否在四个合法值内；非法 → 直接退出非零。
- `message` 超 512 字符 → 本地截断到 512 字符，再发。
- `session_id` 超 256 字符 → 本地直接退出非零（身份字段不允许截断）。

---

## 3. Windows 侧组件

### 3.1 Windows Receiver

Receiver 是 AgentBeacon 暴露 HTTP 接口的接收端，最终运行在用户的 Windows 主机上。v1 实现采用 **C# / ASP.NET Core（Kestrel）**，**保持跨平台**：当前阶段不加 Windows Service / WPF / 任何 Windows-only 代码。

职责：

- 监听 `POST /api/v1/status`（详见 protocol.md；这是 v1 **唯一**的正式 API endpoint）
- 校验 Bearer Token
- 校验请求体与字段长度
- 维护内存中 `session_id → 最新状态` 的映射（last received wins）
- 通过本机 Named Pipe 把状态变化推送给 Indicator（订阅 + 初始快照 + 后续推送均在 store 锁内串行；POST 不等待 Named Pipe I/O，慢或断开的 Indicator 不会对 POST 产生 back-pressure）

绑定与可达性：

- 默认绑定 `0.0.0.0`，端口默认 `8765`，均必须可通过 CLI flag / 环境变量覆盖。
- 鉴权为显式双模式：`--token <t>` / `AGENTBEACON_TOKEN`（共享 Bearer，拒绝无头请求）或 `--no-auth` / `AGENTBEACON_NO_AUTH=1`（完全关闭鉴权，仅限开发/可信内网）。两者互斥；都不给时拒绝启动。详见 [protocol.md](protocol.md) §6。
- Named Pipe 默认启用：pipe 名 `AgentBeacon.Status`。`--pipe <name>` 或 `AGENTBEACON_PIPE=<name>` 可覆盖，`--no-pipe` 显式关闭（主要用于测试与无 UI 场景）。

Receiver 不应该：

- 持久化历史状态（v1 不引入 SQLite 等存储）
- 主动调用 Agent Runtime
- 处理权限审批的回传（v1 不实现）
- 实现账号、用户、权限系统等复杂认证
- 在 v1 范围内做自动重试或消息重排
- 不等待 Pipe I/O 完成后再返回 POST 响应。每个客户端独立的 bounded Channel（容量 8，DropOldest）保证慢消费者只丢中间帧；POST 本身仍可能（短暂地）等待 store lock 与 JSON serialization 这类正常同步工作，但**不**会因 Named Pipe I/O 被阻塞。

#### 3.1.1 调试 endpoint（不属于 Protocol v1）

仅在开发模式下启用：

```
GET /debug/sessions
Authorization: Bearer <token>
```

返回当前内存中的状态表。仍要求 Bearer Token。**该 endpoint 不属于 Protocol v1**，不进入正式协议文档，Indicator 也不应依赖它。

### 3.2 Indicator

Indicator 是 Windows 桌面右上角的状态灯与通知卡片 UI，作为**独立进程**通过 Windows Named Pipe 订阅 Receiver 的状态快照。它不直接接收 HTTP 请求，与 Receiver 之间**没有 v1 协议级**契约——它是 Receiver 的本地订阅者。

职责：

- 连接 Receiver 的 Named Pipe（默认 `AgentBeacon.Status`，可通过 `--pipe <name>` 覆盖）
- 按 `session_id` 渲染一盏独立的状态灯
- 按 UI 策略弹出通知卡片（详见 [docs/ui-policy.md](ui-policy.md)）
- 维护 `completed` 状态 5 分钟后的状态灯自动清理
- 状态灯 hover 显示 `agent / session_id / host` tooltip（whole-event replacement：`agent` 或 `host` 变化都会刷新 tooltip）
- completed 灯老化后写入 `_hiddenCompleted` 墓碑，避免无关 POST 触发的 full snapshot 复活同一 completed 灯
- 卡片严格从屏幕右侧滑入；状态灯与卡片均使用 `ShowActivated=False` + `WS_EX_NOACTIVATE`，**不会抢占前台焦点**

Indicator 是 C# / WPF，目标框架为 `net10.0-windows`，**只能在 Windows 上运行**。其纯逻辑层（`SessionViewModelStore` 等）单独打成 `net10.0` 类库以便在 Linux 上做单元测试；WPF 视觉层仅做渲染。

---

## 4. 数据流

一次状态上报的完整数据流：

1. Agent Runtime 出现生命周期事件。
2. Hook **立即**将其翻译为 `running` / `approval` / `completed` / `failed` 之一。
3. Hook 调用 `agent-notify`，传入 `session_id`、`agent`、`status`、可选 `message` 与 `host`。
4. `agent-notify` 通过 HTTP `POST /api/v1/status` 把这条事件发送给 Windows Receiver，附上 Bearer Token。**仅一次请求，不重试**。
5. Receiver 校验 Token → 校验请求体 → 校验字段 → 更新内存中该 `session_id` 的最新状态（last received wins）。
6. Receiver 立即把当前完整 snapshot 推送给所有已连接的 Indicator 客户端（per-client bounded Channel + DropOldest）。
7. Indicator 接收 snapshot，应用 UI 规则（颜色、卡片、滑入动画）。

整条链路对 Agent 完全透明，Agent 既不需要修改 prompt，也不需要执行任何“通知”命令。

---

## 5. Session 身份语义

`session_id` 是 AgentBeacon 中唯一标识“一盏状态灯”的字段。

规则：

- **在一个 Receiver 范围内** `session_id` 必须唯一。Receiver 不做跨 Receiver 的协调。
- 同一个 `session_id` 的新事件覆盖它之前的状态（**last received wins**）。
- 不同 `session_id` 完全独立，互不覆盖、互不影响。
- 一盏灯对应一个 Session，不是对应一个 Agent 类型。

生成 `session_id` 的责任在 Hook 一侧（Agent Runtime 侧），协议层不做要求。常见做法包括：

- 使用 Agent Runtime 自身的会话标识
- 使用 `agent + host + pid + start_time` 组合
- 使用稳定的 UUID

只要满足“同一个 Session 生命周期内 session_id 不变”，协议层无需关心具体生成方式。

---

## 6. 显式不做的事（v1 范围之外）

为保持 v1 边界清晰，以下能力在当前阶段不实现，也不应在协议中预留字段：

- WebSocket / SSE / 长连接推送（HTTP 单向已足够）
- heartbeat（v1 不实现；硬故障不保证上报是已知限制）
- 自动重试与消息去重（v1 故意不做；旧事件重试会破坏 last-received-wins）
- Agent 自动发现（由人工配置告知 Receiver）
- 权限审批的回传链路（仅做单向状态上报）
- 复杂认证 / 用户账号 / 多租户（仅共享 Bearer Token）
- SQLite 或任何持久化存储
- Windows Service / 安装器 / 自动启动（v1 故意不加 Windows-only 代码）
- Claude Code 参考 Adapter（仅在 Receiver + notify 跑通后再做）
- 卡片点击交互、设置页、多语言
- 跨平台 Indicator（v1 Indicator 仅 Windows；macOS / Linux 留待后续）
