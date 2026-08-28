# Protocol v1

本文档定义 AgentBeacon v1 的 HTTP 状态上报协议。目标是在 Agent Runtime 侧与 Windows Receiver 侧之间，提供一个最小、稳定、可演进的状态事件通道。

## 1. 设计原则

- **最小可用**：只覆盖四种状态的上报，不为未来可能的需求提前增加字段。
- **单向**：v1 仅支持 Agent → Receiver 的事件上报，不支持 Receiver → Agent 的回传。
- **无状态语义**：协议不携带历史、不维护顺序，Receiver 按 `session_id` 维护最新状态（last received wins）。
- **可重发但不需要重发**：协议本身允许重发，但 v1 的 notify 故意不做自动重试 —— 旧事件重试可能覆盖已经到达的新状态。
- **人类可读**：使用 JSON，不使用二进制或自定义编码。
- **共享 Bearer Token 鉴权**：v1 仅使用一个预共享的 Bearer Token；不做账号、用户、权限系统。
- **唯一的正式 endpoint**：`POST /api/v1/status`。其他 endpoint（调试、健康检查等）不属于 Protocol v1。

## 2. 正式 API（v1 唯一 endpoint）

```
POST /api/v1/status
Content-Type: application/json
Authorization: Bearer <shared-token>
```

Receiver 必须监听此 endpoint。

- 必须支持 `Authorization: Bearer <shared-token>` 请求头，缺失或错误时返回 `401 Unauthorized`。
- 允许跨网段访问，Receiver 默认绑定 `0.0.0.0`（不是 `127.0.0.1`），以便 WSL 内部和远端 Linux 主机推送。端口默认 `8765`，均可配置。

这是 v1 **唯一**正式 endpoint。任何调试、健康检查、状态查询 endpoint 都不属于 Protocol v1，不会进入本文档。

## 3. 请求体字段

```json
{
  "session_id": "claude-code-session-2026-08-28T10-00-00Z",
  "agent": "claude-code",
  "host": "workstation-lan",
  "status": "running",
  "message": "Reading file src/main.rs"
}
```

字段详细说明：

| 字段 | 类型 | 必选 | 限制 | 含义 |
| --- | --- | --- | --- | --- |
| `session_id` | string | 必选 | 最大 256 字符；在一个 Receiver 范围内必须唯一 | 唯一标识一个 Agent Session，对应桌面上的一盏状态灯。同一个 `session_id` 的新事件覆盖之前的状态。 |
| `status` | string | 必选 | 仅四个枚举值之一 | 当前状态枚举，取值见第 4 节。 |
| `agent` | string | 必选 | 自由字符串，无枚举限制 | Agent 类型标识，用于 UI 展示与分组，例如 `claude-code`。 |
| `message` | string | 可选 | 最大有效展示长度 512 字符；超出时发送端与 Receiver 都做防御性截断，**不因 message 过长拒绝整个事件** | 人类可读的简短上下文，会作为通知卡片正文。 |
| `host` | string | 可选 | 仅用于展示；不参与路由 | Agent 所在主机的人类可读标识。 |

请求体限制：

- 整体请求体大小不得超过 **8 KiB**；超出时返回 `413 Payload Too Large`。

### 3.1 必选 / 可选的理由

- **`session_id` 必选**：没有 `session_id` 就没有办法把状态对应到桌面上的一盏灯，整个系统无法工作。
- **`status` 必选**：协议层的全部意义就是承载这一字段。
- **`agent` 必选**：Receiver 需要知道这盏灯属于哪一类 Agent，才能在 UI 上正确分组和标注（例如把 `claude-code` 的多个 Session 在视觉上聚在一起）。它是协议级必选字段，但取值是自由字符串，不做枚举限制。
- **`message` 可选**：Hook 在大多数时候没有合适的简短上下文，强制要求会迫使 Hook 编造信息。允许缺失可以保持上报的纯粹性。超出 512 字符由发送端和 Receiver **分别**做防御性截断，整个状态事件不会被拒绝。
- **`host` 可选**：在多台主机同时上报的场景里很有用，但 Receiver 不依赖它来路由。把它列为可选可以避免 Hook 在简单场景下被迫提供。

### 3.2 截断 vs. 拒绝

身份字段（`session_id`）不允许截断 —— 截断 `session_id` 等同于给事件换上不同的身份，是协议级错误，因此 Receiver 返回 `422 Unprocessable Entity`。

非身份字段（`message`）允许截断 —— 显示用的正文即使丢几个字符也不影响事件语义，因此发送端先截、Receiver 再截兜底，整个事件仍然写入。

## 4. status 枚举

`status` 字段必须是以下四个值之一：

| 值 | 颜色 | 含义 |
| --- | --- | --- |
| `running` | 蓝色 | Agent 正在处理任务 |
| `approval` | 黄色 | Agent 正在等待人工授权 |
| `completed` | 绿色 | 本轮任务正常完成 |
| `failed` | 红色 | 任务发生错误或异常终止 |

v1 严格只支持这四种状态。任何其他取值（包括但不限于 `idle`、`offline`、`unknown`、`paused`）都会被 Receiver 视为非法，返回 `400 Bad Request`。

合法状态流转示例：

```
completed -> running
running   -> approval
approval  -> running
running   -> completed
running   -> failed
approval  -> failed
```

Receiver 不校验流转的合法性。状态怎么变由 Hook 决定，Receiver 只忠实记录 last received wins。

**整条事件作为一个快照整体替换**该 `session_id` 之前的整条记录。如果新事件没有携带 `host` 或 `message`，旧记录中的这两个字段不会被继承——而是按"事件里没传"处理（即 `null`）。Receiver 不做字段级 merge。

## 5. failed 的能力边界

`failed` 仅表示 Hook / Runtime 能够观察到的失败事件。下列硬故障**不保证**能被 `failed` 覆盖：

- Agent Runtime 进程被外部强杀（`kill -9`、OOM、崩溃在 Hook 注册之前）
- 宿主机宕机、断电
- Agent 主机与 Receiver 之间的网络中断
- Hook 自身未启动或已停止

v1 不为此引入 heartbeat、wrapper 进程、或任何形式的存活探测。在文档层面准确描述这一限制即可。

## 6. 鉴权

每个请求必须携带：

```
Authorization: Bearer <shared-token>
```

- 缺失或格式错误 → `401 Unauthorized`
- Token 不匹配 → `401 Unauthorized`

`<shared-token>` 是人工在 Agent 侧和 Receiver 侧之间预共享的字符串。它通过 Receiver 的 CLI 参数或环境变量配置，通过 notify 的 CLI 参数或环境变量配置，**不应硬编码进仓库或 commit 历史**。

v1 不实现：

- 账号、用户、角色
- Token 过期与轮换
- 双向 mTLS / OAuth / OIDC

## 7. 响应

### 7.1 成功

```
HTTP/1.1 200 OK
Content-Type: application/json

{
  "session_id": "claude-code-session-2026-08-28T10-00-00Z",
  "status": "running"
}
```

Receiver 在完成校验、鉴权和内存状态更新后返回 `200 OK`。响应体只回显 `session_id` 和本次写入的 `status`，避免回传任何业务细节。

### 7.2 失败

所有失败响应使用统一的 JSON 错误结构：

```json
{
  "error": "invalid_status",
  "message": "status must be one of: running, approval, completed, failed"
}
```

`error` 为机器可读字符串码，`message` 为人类可读说明。

| HTTP 状态码 | `error` 取值示例 | 触发条件 |
| --- | --- | --- |
| `400 Bad Request` | `invalid_json` / `missing_field` / `invalid_status` | JSON 解析失败、缺少必选字段、`status` 取值非法 |
| `401 Unauthorized` | `unauthorized` | 缺少 `Authorization` 头、Token 不匹配 |
| `413 Payload Too Large` | `payload_too_large` | 请求体超过 8 KiB |
| `415 Unsupported Media Type` | `unsupported_media_type` | `Content-Type` 不是 `application/json` |
| `422 Unprocessable Entity` | `session_id_too_long` | `session_id` 超过 256 字符 |
| `500 Internal Server Error` | `internal_error` | Receiver 自身异常 |

注意：**`message` 超长不会导致任何错误状态码**。`message` 超过 512 字符时 Receiver 会做防御性截断，事件照常写入。截断策略是**保留前 512 字符**，**不追加省略号**（`...`）也不追加 Unicode ellipsis（`…`）。视觉省略由 UI 自行处理。

---

## 8. 行为约束

### 8.1 Receiver 侧

- 校验 Bearer Token，未通过直接 `401`。
- 校验 `Content-Type: application/json`，否则 `415`。
- 校验请求体大小 ≤ 8 KiB，否则 `413`。
- 校验 JSON 与必选字段，否则 `400`。
- 校验 `status` 枚举，否则 `400`。
- 校验 `session_id` 长度 ≤ 256，否则 `422`。
- `message` 长度若超过 512，**截断**为前 512 字符后写入；不返回错误。
- 维护从 `session_id` 到最新 `status` 的内存映射，last received wins。
- 不持久化历史。
- 不主动向 Agent 回传任何信息。

### 8.2 notify 侧

- 收到 `200 OK` 视为送达成功。
- 收到 `4xx` 响应视为请求本身非法，**不重试**。
- 收到 `5xx` 响应或网络错误时**也不重试**（v1 故意不做重试）；失败由调用方（Hook / 手工脚本）自行决定后续处理。
- 本地校验必选字段非空、`status` 合法；`message` 超长本地截断；`session_id` 超长本地直接失败（不发请求）。
- 重试与可靠投递留待后续版本；届时需要先解决 sequence / 去重问题。

### 8.3 Hook 侧

- 生命周期事件出现后**立即**翻译为 4 状态之一并调用 notify。**不做 debounce、不做合并、不做节流或人为等待**。`approval` 尤其要在进入等待的那一刻立刻发出。
- 负责生成并保持同一个 Session 生命周期内 `session_id` 不变。
- 不应缓存、合并或延迟事件。

---

## 9. 示例

### 9.1 进入运行

```http
POST /api/v1/status HTTP/1.1
Host: receiver.lan:8765
Content-Type: application/json
Authorization: Bearer dev-token-placeholder

{
  "session_id": "claude-code-session-2026-08-28T10-00-00Z",
  "agent": "claude-code",
  "host": "workstation-lan",
  "status": "running"
}
```

### 9.2 进入授权等待

```http
POST /api/v1/status HTTP/1.1
Host: receiver.lan:8765
Content-Type: application/json
Authorization: Bearer dev-token-placeholder

{
  "session_id": "claude-code-session-2026-08-28T10-00-00Z",
  "agent": "claude-code",
  "status": "approval",
  "message": "需要授权执行 rm -rf ./build"
}
```

### 9.3 正常完成

```http
POST /api/v1/status HTTP/1.1
Host: receiver.lan:8765
Content-Type: application/json
Authorization: Bearer dev-token-placeholder

{
  "session_id": "claude-code-session-2026-08-28T10-00-00Z",
  "agent": "claude-code",
  "status": "completed"
}
```

### 9.4 失败

```http
POST /api/v1/status HTTP/1.1
Host: receiver.lan:8765
Content-Type: application/json
Authorization: Bearer dev-token-placeholder

{
  "session_id": "codex-session-2026-08-28T11-15-42Z",
  "agent": "codex",
  "status": "failed",
  "message": "tool call timed out after 30s"
}
```

### 9.5 鉴权失败

```http
HTTP/1.1 401 Unauthorized
Content-Type: application/json

{
  "error": "unauthorized",
  "message": "missing or invalid bearer token"
}
```

### 9.6 message 超长

发送方携带一段超过 512 字符的 `message`，例如 800 字符。`agent-notify` 会在本地先截到 512，HTTP body 内仍是 512 字符；即使发送方未截，Receiver 也会再次截到 512。**状态事件正常写入，返回 200 OK**。

---

## 10. 演进说明

v1 协议刻意保持最小。后续如需扩展（例如增加 `timestamp`、增加可选 `metadata`、增加 `event_id` 用于去重、支持可靠投递与重试），应通过新的协议版本（`/api/v2/...`）引入，而不是在 v1 上叠加字段。

如果未来需要双向通道（如 Receiver 回传用户授权决策），应作为一条独立的协议线，不在 v1 上扩展。