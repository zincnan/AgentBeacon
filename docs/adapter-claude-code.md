# Claude Code Adapter（Round 4，插件形态）

AgentBeacon 的第一个 in-tree Adapter。它是一个 **Claude Code 插件**（`plugins/claude-code/`）：通过 Claude Code 的 hooks 机制监听会话生命周期事件，翻译成四状态之一，POST 给 Receiver —— 传输语义：单次 POST、不重试、3s 超时，安装一次后全自动。

## 安装

本仓库自带 marketplace（仓库根 `.claude-plugin/marketplace.json`）：

```
/plugin marketplace add zinc/AgentBeacon     # 或本地路径 add /path/to/AgentBeacon
/plugin install agentbeacon
```

开发期免安装侧载测试：

```
claude --plugin-dir plugins/claude-code
```

安装后的常用命令：

| 场景 | 命令 |
| --- | --- |
| 改了插件代码后更新已安装副本 | `claude plugin marketplace update agentbeacon && claude plugin update agentbeacon` |
| 查看安装状态 | `claude plugin list` |
| 暂时停用 / 恢复 | `claude plugin disable agentbeacon` / `claude plugin enable agentbeacon` |
| 卸载 | `claude plugin uninstall agentbeacon@agentbeacon`（可选再 `claude plugin marketplace remove agentbeacon`） |

## 配置

推荐方式（Round 7）：编辑一次 `~/.agentbeacon.json`，之后所有会话生效：

```json
{ "url": "http://127.0.0.1:8765", "token": null }
```

- `token`：Windows Receiver 配了 key 就填，`--no-auth` 模式保持 `null`（不发 Authorization 头）
- 环境变量（`AGENTBEACON_URL` / `AGENTBEACON_TOKEN`）与插件安装配置（`--config agentbeacon_url=...`）仍然优先于该文件 —— 临时覆盖、CI、测试不受影响
- 文件缺失或损坏不报错，hook 静默跳过（还有其它来源兜底）

注意：claude 跑在 WSL/Linux 时，Windows Receiver 必须绑定到 WSL 可达的地址（`--bind 0.0.0.0` 或 LAN IP），不能用 `127.0.0.1`。

## 事件 → 状态映射

| Claude Code hook | 状态 | message |
| --- | --- | --- |
| `SessionStart` | `completed`（绿） | session started — idle |
| `UserPromptSubmit` | `running` | prompt 前 100 字符 |
| `PreToolUse` | `running` | tool started |
| `PostToolUse` | `running` | tool finished |
| `PermissionDenied` | `running` | permission denied — continuing |
| `PermissionRequest` | `approval` | permission requested |
| `Stop` | `completed` | turn completed |
| `StopFailure` | `failed` | turn failed |
| `SessionEnd` | 见下 | — |
| 其它（`SubagentStop` / `PreCompact` …） | 忽略 | 防止 subagent 结束导致灯闪烁 |

关键行为：

- **启动/resume 即绿灯**：`SessionStart`（含 `--continue`/`--resume`，实测 session_id 不变）映射为 completed —— "活着但没任务"是绿灯空闲态。附带好处：开了 claude 没干活就退出，不会误报红灯（最后状态是 completed）。
- **授权 yes/no 后立即变蓝**：用户批准的瞬间 `PreToolUse` 触发（工具开始执行）→ 蓝；拒绝走 `PermissionDenied` → 蓝。不等工具跑完。
- **红灯不是终态**：同一会话内再次交互（`UserPromptSubmit`）→ 蓝；成功走完 `Stop` → 绿；再次失败 → 红。
- 同一状态 2 秒内重复触发会被节流（如 `PreToolUse` 后紧跟 `PostToolUse`）。

**红灯与会话身份（已确认的设计）**：红灯长期保留。进程死掉后：

- 全新启动 `claude` → 新 session_id = 一盏**新灯**；旧红灯继续挂着（可右键「关闭此灯」清掉，见 ui-policy §9；若旧会话后续被 `-c` 复活，新事件会重建该灯）。
- `claude -c` / `--resume` → 复用同一 session_id = **同一盏灯**，红灯 → 绿（SessionStart idle）→ 蓝（新 prompt）→ 绿/红。

## failed 的两条上报路径

`failed` 的语义是**会话级致命终止**（agent 的工作不会有下文），不是单次工具/请求错误。经验证（Claude Code 2.1.250），两条路径覆盖：

1. **`SessionEnd` 时仍在工作中**：会话结束时最后状态是 `running` / `approval`（没有正常走到 `Stop`），说明任务被中途终止 —— 卡在 API error 后关掉终端、中途退出、崩溃后优雅收尾都走这条。上报 `failed`，message 为 `session ended while <status> (reason: <reason>)`。
2. **进程看门狗**：`SessionStart` 时 hook 记录 claude 进程 PID（sidecar 文件 `$TMPDIR/agentbeacon-claude-code/<session>.json`），并拉起一个 detached watchdog。watchdog 每 5 秒检查：进程死亡（含 zombie，读 `/proc/<pid>/stat` 判定）且最后状态为 `running`/`approval`，经 10 秒宽限后上报 `failed: claude process exited unexpectedly`。正常退出会先走 `SessionEnd` 清掉 sidecar，watchdog 静默退出，不误报。

已知边界：CLI **还活着但卡在重试**（API 持续故障、界面停在 API error）时，没有任何 hook 事件产生 —— 实测确认。此时灯保持蓝色，直到用户关闭终端（→ 路径 1 报红）或进程被杀（→ 路径 2 报红）。要做到"卡住即报红"需要 transcript 轮询启发式，v1 不做。

## 实现

```
plugins/claude-code/
├── .claude-plugin/plugin.json     # 插件 manifest
├── hooks/hooks.json               # 8 个事件 → 同一个脚本
└── scripts/
    ├── agentbeacon_hook.py        # 入口：stdin JSON → 状态 → POST（内联传输，
    │                              #   单次 POST、不重试、
    │                              #   3s 超时、永远 exit 0 不阻塞 Claude）
    └── agentbeacon_watchdog.py    # 每会话看门狗（failed 路径 2）
```

测试：`tests/test_hook_adapter.py`（27 个）—— 映射表、envelope、节流、看门狗决策、以及用本地 HTTP 假 Receiver 跑真实脚本的端到端（含 SessionEnd 两条分支）。

## 真机验收过的场景

- 成功一轮 `claude -p`：Receiver 收到 `running → … → completed`，SessionEnd 后 sidecar 清理
- API 全程 401 + SIGKILL：watchdog 上报 `failed`（含 zombie 进程判定修复）
- API 全程 401 + 关终端：SessionEnd 路径上报 `failed (reason: other)`
