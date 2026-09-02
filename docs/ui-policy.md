# UI Policy

本文档定义 AgentBeacon v1 桌面 UI 的行为规则。UI 行为**不进入 HTTP 协议层**——协议只负责把状态事件送到 Receiver，UI 规则由 Indicator 自行实现并可演进。

v1 规则如下。后续如果发现需要更复杂的策略（例如不同 agent 的不同卡片样式、用户手动 pin 某个 Session），应在本文档里扩展，不应反射回协议层。

---

## 1. 状态模块（红绿灯）

桌面右上角按 `session_id` 一对一渲染「红绿灯模块」：模块上方是 `agent` 名称标签，下方是竖向三个物理灯位的深色 housing。**一个 Agent Session = 一个独立模块**，不按 agent 字段聚合。

三个物理灯位与四种状态的映射（唯一不变式：任意时刻**只有一个灯位亮起**）：

| 状态        | 亮起灯位 | 颜色 | hex       | 含义                       |
| ----------- | -------- | ---- | --------- | -------------------------- |
| `failed`    | 顶部     | 🔴 红色 | `#F85149` | 任务发生错误或异常终止      |
| `approval`  | 中间     | 🟡 黄色 | `#D29922` | Agent 正在等待人工授权      |
| `running`   | 底部     | 🔵 蓝色 | `#2F81F7` | Agent 正在处理任务          |
| `completed` | 底部     | 🟢 绿色 | `#3FB950` | 本轮任务正常完成            |

注意 `running` 与 `completed` **共用底部物理灯位**、只是颜色不同 —— 共用的是"位置"，不是状态本身。未亮起的灯位保持极暗的灯罩色（`#34383D`），不消失、不变灰、不复用为其它含义。

规则：

- 一个模块对应一个 `session_id`，不是对应一个 Agent 类型；同一 agent 的多个并发 session 会出现多个同名模块。
- v1 严格只有上述四种亮起颜色，**不引入灰色（offline / idle / unknown / paused / connecting）第五状态**。
- `completed` 的模块保留 5 分钟后自动移除（见 §3）。
- 状态映射的 canonical 实现在 `Indicator.Core` 的 `LampStateMapper.ForStatus`，未知 status 直接抛异常（fail-fast）。
- 模块 hover 显示 Tooltip：`agent · session_id · host`（host 可选）。Tooltip 是 `agent` 与 `host` 的函数；任一字段在后续快照中被替换，Tooltip 都随之刷新（whole-event replacement）。

## 2. 通知卡片

| 状态        | 卡片行为                                                              |
| ----------- | --------------------------------------------------------------------- |
| `running`   | **不弹卡片**。只更新对应模块的灯位/颜色。                               |
| `approval`  | **弹出卡片**，停留 **8 秒** 后自动收回；**黄色灯保持**直到状态变化。     |
| `completed` | **弹出卡片**，展示最近一次的 `message`（如有）。卡片停留 **5 秒** 后自动收回。模块继续保留（5 分钟）。 |
| `failed`    | **弹出卡片**，展示 `message`（如有）。卡片停留 **10 秒** 后自动收回。红色灯长期保留。 |

时长（8 秒 / 5 秒 / 10 秒）是 **UI 常量**，不属于 HTTP Protocol v1，可独立调整。

规则：

- 卡片内容只读自 Receiver 已经更新的最新状态；Indicator 不重新解析历史。
- 同一 Session 短时间内连续发生多次同向变化（例如 `running` → `running` 但 `message` 不同），默认行为是只更新最近一次的内容，不堆叠多张卡片。
- `completed → running` 这种重新进入运行的合法流转，会按 `running` 的新规则处理（只更新灯，不弹卡片）。
- **卡片锚定在对应 Agent 模块的左侧**：从模块右侧的收回座位向左弹出（约 220 ms，EaseOut），垂直中心对齐模块中心，停留时长按状态；到期后**向右收回**到模块旁（约 200 ms，EaseIn）再隐藏。卡片消失 ≠ 状态消失，灯保持。
- 停留期间状态变化（如 `approval → running`）：立即开始收回，不等剩余停留时间；若新状态本身要弹卡（如 `approval → failed`），复用同 session 的卡片更新内容并重新计时，**同一 session 永远只有一张卡片**。
- 多个模块同时弹卡时按 `AnchoredCardLayout`（纯计算 helper）做碰撞调整：卡片尽量贴近各自模块，相互间保持间隙；空间不足时优先隐藏较旧的卡片。
- 卡片与模块均使用 `ShowActivated=False` + `WS_EX_NOACTIVATE`，**不抢占前台焦点**。

## 3. completed 状态灯的自动清理

- `completed` 状态的 Session 在状态灯列表中保留 **5 分钟**（按 `updated_at` 计算），之后从列表中移除。
- 5 分钟内如果该 Session 又上报了新的状态（例如再次进入 `running`），`updated_at` 前进，计时器按新时间计算。
- 5 分钟后 Session 从状态灯列表中消失。Receiver 内部状态表中的条目可以保留更长（v1 仅要求内存中存在即可，是否清理、何时清理交由 Indicator 与 Receiver 协同）。
- “5 分钟”是 v1 默认值，后续可做成 Indicator 侧的可配置项。

## 4. failed 状态灯与卡片的分离

`failed` 在 v1 中**严格分离卡片与状态灯的保留策略**：

- **`failed` 状态灯**：红色，长期保留，直到下一次状态变化或用户手动清理。
- **`failed` 通知卡片**：弹卡片，停留 **10 秒**（UI 常量）后自动隐藏。后续同一 Session 的 `updated_at` 推进会触发新的卡片。

也就是说：状态灯是"长期可见"，卡片是"瞬时通知"。两者互不耦合。

不做超时清理是因为失败往往需要人工介入确认；但卡片本身不应该永久停留阻塞 UI。

## 5. 去重 / 重发

- 同一 `(session_id, updated_at)` 的卡片**最多弹出一次**：如果 Receiver 重启或重发同一快照，不会重复弹卡片。
- 同一 Session 收到**更新**的 `updated_at` 时，按新事件重新触发 `Show`：approval 重置 8 秒停留、failed 重置 10 秒停留、completed 重新弹出 5 秒卡片。
- 这是 UI 层的去重，不是协议层的语义。协议层不保证 `updated_at` 单调，只保证 last-received-wins。

### 5.1 completed 灯的“墓碑”抑制（Round 2 收尾）

completed 灯在 §3 中按 5 分钟老化，老化后在 `_hiddenCompleted` 表里留下一条 `(session_id, updated_at)` 墓碑。在此之后：

- **同一 `(session_id, updated_at)` 的 completed 重发**：灯不复活，卡片不重弹。测试 `Store_Completed_Hidden_SameSnapshotDoesNotReappear` 覆盖。
- **同一 session 的 newer completed（updated_at 严格更大）**：清除墓碑，按新的 completed 处理（重新显示灯、重新弹 5s 卡片）。测试 `Store_Completed_Hidden_NewerCompletedReappears` 覆盖。
- **同一 session 的非 completed 事件**（running / approval / failed）：清除墓碑，按新状态正常显示。测试 `Store_Completed_Hidden_RunningReappears` 覆盖。

不允许用灰色 / idle / unknown 等第五种状态表达“隐藏”——隐藏只是“当前没有灯要画”的状态，不是 UI 状态。

## 6. 不在 UI 层表达的内容

以下信息**不应该**通过 UI 隐式推导或展示，除非协议层显式给出：

- Agent 是否在线 / 是否还在运行
- 网络是否中断 / Receiver 是否离线
- Agent 是否空闲

Indicator 只展示协议上报过的最新状态。如果一个 Session 不再上报，Indicator 显示的就是它最后一次上报的状态，不会自动转灰、不会显示“最后心跳时间”，也不会自己推断它是“离线了”。这与协议层“last received wins”的语义保持一致。

## 7. 未知状态的防御

Protocol v1 已经约束 `status` 只能是 `running / approval / completed / failed` 四种取值，Receiver 在 HTTP 层拒绝其他取值。

Indicator 的 Core 层作为防御性约束再次校验：构造 `SessionViewModel` 时传入未知 `status` 会**直接抛出 `ArgumentException`**（fail-fast），不会创建第五种灯、不会映射为灰色。这是 Core 层与 WPF 视觉层共用的约束，不允许在 UI 视觉层静默吞掉未知状态。

## 8. 卡片停留 timer 的管理

卡片停留（到期收回）的 timer 必须按 `session_id` 持有，最多每 session 一个：

- 收到同一 session 的新 `Show`（含 approval / completed / failed 三种，以及同状态 `updated_at` 推进后的重新 Show）：先取消旧 timer，再注册新 timer —— 这是 `completed` 5s 之后紧接 `failed` 10s 能正确生效的前提。
- `running` 的 `Show` 不携带停留时长（无卡片）：确保旧 timer 已被取消，不创建新 timer；若该 session 有卡片正在显示则立即收回。
- 收到同 session 的 `Hide`（状态离开卡片状态、或 authoritative snapshot 删除 session）：取消 timer 并收回/隐藏卡片。
- timer 回调里要自检“我是不是该 session 仍然登记的 timer”，防止被替换的旧 callback 误杀新卡片。
- 卡片收回动画（~200 ms）完成时通过 `RetractCompleted` 事件通知 MainWindow 清理登记；清理时做 instance-safe 检查，防止被替换的旧卡片迟到事件误删新卡片。

WPF DispatcherTimer / 动画本身不做单元测试；上述语义在 Core 层有等价的 dedup / stay-policy / tombstone 行为覆盖测试。

## 9. 灯的右键关闭与拖拽（Round 5）

**右键关闭（dismiss）**：右键任意灯模块 →「关闭此灯」。该模块被**完全拆除**（不保留隐藏窗口，省资源），同时收回其卡片、取消 timer。

- 关闭时记录一条水位：`(session_id, 被隐藏事件的 updated_at)`（`LampDismissal.ShouldShow` 为 canonical 规则）。
- 之后收到**同一 `(session_id, updated_at)`** 的重发（如别的会话触发 full snapshot 重播）：灯**不复活** —— 与 completed 墓碑同一防复活原则。
- 收到**严格更新**的 `updated_at`（会话又活了、有新信号）：灯**重建**，按新状态正常显示；若新状态带卡片，卡片正常弹出（快照先于卡片事件送达，锚点模块已就位）。
- 红灯被关闭后，该 session 用 `-c` 继续产生新事件 → 灯重建为新状态（红→蓝/绿…）。

**拖拽**：按住左键拖动任意灯模块可移动整个灯列。一旦拖过，Indicator 不再自动贴回屏幕右缘（窗口增减模块时保持右缘对齐）；位置保持到进程退出，不落盘持久化。拖动后卡片随模块新位置重新锚定。

两者都不改协议：dismiss 是 Indicator 本地 UI 状态，Receiver 状态表不变。
