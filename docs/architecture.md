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
│  Adapter (内置传输)     │
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

- Adapter / hook 不依赖任何 Windows UI 实现。
- Receiver 不依赖任何特定 Agent Runtime。
- Named Pipe IPC **不属于 HTTP Protocol v1**，是 Windows 本机内部的实现细节，未来若有 macOS / Linux Indicator 再重新设计。

两侧可以用不同语言实现，**不强制共享运行时**。当前 Adapter / hook 是 Python，`Receiver` 是 C# / ASP.NET Core，`Indicator` 是 C# / WPF。隔离让 Agent 侧可以在 Linux/WSL 上单独演进，Windows 侧可以独立实现 UI。

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

Hook 是 Agent Runtime 的状态事件源，挂在 Agent Runtime 提供的生命周期事件上。已有两个 in-tree Adapter：Claude Code 插件（`plugins/claude-code/`，见 [adapter-claude-code.md](adapter-claude-code.md)）与 Codex CLI hooks（`plugins/codex/`，见 [adapter-codex.md](adapter-codex.md)）。

Hook 的职责：

- 监听 Agent Runtime 的生命周期事件
- **事件出现后立即**将其翻译为 `running` / `approval` / `completed` / `failed` 之一，并直接 POST 给 Receiver（内置传输；单次、不重试）
- 负责生成并保持同一个 Session 生命周期内 `session_id` 不变

Hook 必须做到：

- **立即上报**：不做 debounce、不做合并、不做节流。`approval` 尤其要在进入等待的那一刻立刻发出（让用户在超时之前能看到）。
- **不修改、不补全业务字段**：除了把状态事件翻译成协议字段，不做任何业务推断。

两个 Adapter 的事件到四状态的具体映射分别见 [adapter-claude-code.md](adapter-claude-code.md) 与 [adapter-codex.md](adapter-codex.md)。

Hook 不应该：

- 自行重试失败的通知（v1 不实现重试）
- 自行选择目标主机（由配置层处理）
- 缓存历史状态（接收端负责）

### 2.3 Adapter 内置传输

两个 in-tree Adapter（Claude Code 插件、Codex hooks）都把传输逻辑内联在 hook 脚本里：读 stdin 的 hook JSON，翻译状态后直接 `POST /api/v1/status`。约定语义一致：

- **不识别除自身外的 Agent 类型**，不推断状态。
- **单次 POST、不重试**——迟到的旧事件覆盖新状态会破坏 last-received-wins。
- 2xx 视为送达；4xx 视为请求非法；5xx/网络错误交给调用方。
- `message` 超 512 字符本地截断；未知 status 直接抛错。

仓库历史上曾提供独立的 `agent-notify` 通用脚本（R1）；随着两个 Adapter 内置了完全相同的传输语义，该脚本已移除——任何语言用任意 HTTP 客户端 POST 一次即可接入，见 `docs/protocol.md`。

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

- 默认绑定 `0.0.0.0`，端口默认 `8765`。配置解析为 `CLI flag > 环境变量 > agentbeacon.json（--config 指定，或 exe 旁 / 当前目录自动发现）> 默认`；`scripts/windows/install.ps1` 提供一键安装 + 开机自启 + 后台驻留。
- 鉴权为显式双模式：`--token <t>` / `AGENTBEACON_TOKEN` / 配置文件 `"token"`（共享 Bearer，拒绝无头请求）或 `--no-auth` / `AGENTBEACON_NO_AUTH=1` / `"no_auth": true`（完全关闭鉴权，仅限开发/可信内网）。同一来源内两者互斥；都不给时拒绝启动。详见 [protocol.md](protocol.md) §6。
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
- 卡片从所属灯模块左侧弹出/停留/收回，多卡碰撞自动避让
- 状态变化时非 running 的灯闪烁数秒（真实红绿灯语义）；亮灯带同色辉光
- 托盘图标（右键退出）；灯右键支持重命名（持久化到 `lamp-labels.json`）与关闭
- 状态灯与卡片均使用 `ShowActivated=False` + `WS_EX_NOACTIVATE`，**不会抢占前台焦点**

Indicator 是 C# / WPF，目标框架为 `net10.0-windows`，**只能在 Windows 上运行**。其纯逻辑层（`SessionViewModelStore` 等）单独打成 `net10.0` 类库以便在 Linux 上做单元测试；WPF 视觉层仅做渲染。

---

## 4. 数据流

一次状态上报的完整数据流：

1. Agent Runtime 出现生命周期事件。
2. Hook **立即**将其翻译为 `running` / `approval` / `completed` / `failed` 之一。
3. Adapter / Hook 通过 HTTP `POST /api/v1/status` 把翻译好的事件（`session_id`、`agent`、`status`、可选 `message` 与 `host`）发送给 Windows Receiver，附上 Bearer Token。**仅一次请求，不重试**。
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
- 卡片点击交互、设置页、多语言
- 跨平台 Indicator（v1 Indicator 仅 Windows；macOS / Linux 留待后续）
