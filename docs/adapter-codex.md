# Codex CLI Adapter（Round 11）

AgentBeacon 的第二个 Adapter，面向 OpenAI Codex CLI（开源终端 agent）。利用 Codex 的 **hooks 系统**（0.14x+，`SessionStart / UserPromptSubmit / PreToolUse / PostToolUse / PermissionRequest / Stop / SessionEnd / Interrupt` 等 12 个事件，stdin 传 JSON，格式与 Claude Code hooks 近乎同构）。

## 安装

**插件方式（推荐，Codex 0.144+ 实测）**：

```bash
codex plugin marketplace add /path/to/AgentBeacon
codex plugin add agentbeacon-codex@agentbeacon
```

插件捆绑 `hooks/hooks.json`，Codex 通过 `PLUGIN_ROOT` 环境变量注入插件根路径。

**合并脚本方式（旧版 Codex 兜底）**：

```bash
python3 plugins/codex/install.py        # 合并写入 ~/.codex/hooks.json
python3 plugins/codex/install.py --remove
```

脚本方式会把模板里的路径渲染为绝对路径（Codex 以 `env_clear()` 运行 hook 命令，`$HOME` 不可依赖）；只合并/替换 AgentBeacon 自己的事件组，不动已有 hooks。

### 必做一步：信任 hooks

两种安装方式都一样：Codex 默认**跳过未受信的命令 hook**（按来源 + hook 定义哈希记录信任）。安装后打开 `codex`，运行 `/hooks`，对 AgentBeacon 的条目执行 review+trust。自动化场景可用 `--dangerously-bypass-hook-trust`，但正常使用请走 `/hooks`。切换安装方式（如脚本版 → 插件版）后需重新信任。

## 配置

与 Claude 插件共用同一个文件：`~/.agentbeacon.json`

```json
{ "url": "http://127.0.0.1:8765", "token": null }
```

## 事件 → 状态映射

| Codex hook | 状态 | message |
| --- | --- | --- |
| `SessionStart` | `completed`（绿） | session started — idle |
| `UserPromptSubmit` | `running` | prompt 前 100 字符 |
| `PreToolUse` / `PostToolUse` | `running` | tool started / finished |
| `PermissionRequest` | `approval` | permission requested（沙箱升级/命令审批前触发） |
| `Stop` | `completed` | turn completed |
| `SessionEnd` | 见下 | — |
| `Interrupt` / `Subagent*` / `PreCompact` 等 | 忽略 | — |

## failed 的两条路径

Codex **没有任何错误/失败 hook**（`SessionEnd` 的 reason 恒为 "other"，且不覆盖 SIGKILL），因此 failed 语义与 Claude 插件一致、由两条互补路径覆盖：

1. **`SessionEnd` 时仍在 running/approval**（正常退出但工作被截断，如卡在 API error 后手动退出）→ `failed: session ended while <status>`
2. **PID 看门狗**（SessionStart 记录 codex 进程 PID + 崩溃/被杀时无 SessionEnd）→ 宽限 10 秒后 `failed: codex process exited unexpectedly`

已知边界：`codex exec --json` 的 `turn.failed` 事件能区分失败，但仅覆盖 exec 模式；v1 不解析该流。CLI 活着但卡死时灯保持蓝色，直到退出/被杀。

## 实现

```
plugins/codex/
├── hooks.json                    # 模板（__AGENTBEACON_HOOK__ 占位符）
├── install.py                    # 合并式安装 / --remove
└── scripts/
    ├── agentbeacon_codex_hook.py     # stdin JSON → 状态 → POST（agent="codex"）
    └── agentbeacon_watchdog.py       # 每会话看门狗
```

测试：`tests/test_codex_adapter.py`（19 个）——映射（含"SessionStart=绿"“Interrupt 忽略”）、envelope（agent=codex）、配置文件回退、节流、看门狗决策、端到端（含 SessionEnd 两条分支）。
