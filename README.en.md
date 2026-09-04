# AgentBeacon

[中文](README.md)

> A row of traffic-light indicators for your AI agents, parked at the top-right of your Windows desktop.

When you run several Claude Code or Codex sessions at the same time, a few things get annoying fast:

- a task runs for ten minutes, so you keep switching back to the terminal to check whether it is done;
- an agent is waiting for approval, but you do not notice until much later;
- a session silently fails, and you only find out hours afterwards.

AgentBeacon turns those into glanceable signals. Each agent session gets its own small traffic light on the desktop: blue means working, yellow means waiting for you, green means done, and red means failed.

## What It Does

- **No more polling terminals**: if the agent is still running, the blue light is on.
- **Fast approval awareness**: when an agent needs permission, the yellow light turns on and a card slides out with the request context.
- **Multiple agents on one screen**: every session gets its own light, even when several sessions use the same agent.
- **Visible failures**: failed sessions keep a red light on and show an error card.
- **Low interruption**: always on top, borderless, not in the taskbar, and does not steal focus. When there are no sessions, the indicator is completely hidden.

## What It Looks Like

Each agent session is a vertical three-light module with the agent name above it:

```text
 Claude Code          OpenCode
 ┌─────────┐          ┌─────────┐
 │    ·    │          │    ●    │  <- red = failed
 │    ●    │          │    ·    │  <- yellow = approval
 │    ·    │          │    ·    │  <- bottom = running blue / completed green
 └─────────┘          └─────────┘
```

| Light / color | Status | Meaning | Notification card |
| --- | --- | --- | --- |
| Bottom blue | `running` | The agent is working | No card |
| Middle yellow | `approval` | Waiting for your permission | Slides out for 30 seconds; yellow light stays on |
| Bottom green | `completed` | Current turn is done | Slides out for 30 seconds; green light stays for 5 minutes |
| Top red | `failed` | Something failed | Slides out for 30 seconds; red light stays on |

<table>
  <tr>
    <td><img src="images/running.png" width="170"></td>
    <td><img src="images/approval.png" width="330"></td>
  </tr>
  <tr>
    <td><img src="images/completed.png" width="330"></td>
    <td><img src="images/failed.png" width="330"></td>
  </tr>
</table>

When a yellow, green, or red light changes, it blinks for a few seconds. Cards slide out from the left side of the corresponding light module, show the agent, status, and message, then retract after 30 seconds. A card disappearing does not mean the status disappeared; the light is the persistent signal. Multiple cards avoid overlapping automatically.

Daily controls: left-drag any light to move the whole column; right-click and choose "Rename this light" to give a session a readable local name; right-click and choose "Close this light" to dismiss a session you no longer care about. If that session reports a new status later, the light is rebuilt.

## Requirements

| Component | Requirement |
| --- | --- |
| Windows desktop side, Receiver + Indicator | Windows 10/11. Release packages are self-contained and do not require a .NET runtime on the target machine; running from source requires the .NET 10 SDK. |
| Agent side | Any environment that can send HTTP POST requests: WSL, Linux, macOS, or Windows. |
| Claude Code adapter | Claude Code 2.1+ with hooks/plugin support. Verified with 2.1.250. |
| Network | The agent must be able to reach the Receiver at `IP:port`. For WSL and Windows on the same machine, `127.0.0.1` is usually enough. |

There is no database and no background service. Receiver and Indicator are two small processes, and state is kept in memory.

## Quick Start

### 0. Share a Ready-To-Run Windows Package

From WSL, build a self-contained package with one command:

```bash
bash scripts/dist.sh --zip
```

This produces `dist/agentbeacon-win-x64/` and a matching zip file. The package includes the .NET runtime. Send that folder to another Windows user, then they can:

1. Put it somewhere stable, such as `C:\AgentBeacon`.
2. Double-click `install.bat`. They may edit `agentbeacon.json` first, or run `install.bat -Token key -Port 8765`.
3. Done: AgentBeacon starts in the background on login, and the traffic lights appear at the top-right when sessions exist.

No .NET install, source checkout, build step, or command line is required on the target machine. To change configuration, edit `agentbeacon.json` in the package folder and run `install.bat` again. To uninstall, double-click `uninstall.bat`.

The equivalent Windows-side publishing script is `scripts\windows\publish.ps1`.

### 1. Windows Side: Install From Source For Development

```powershell
git clone <repo> ; cd AgentBeacon
powershell -ExecutionPolicy Bypass -File scripts\windows\install.ps1
```

This builds locally, enables startup on login, and runs the processes in the background. Configuration lives in one file:

```jsonc
// %LOCALAPPDATA%\AgentBeacon\agentbeacon.json
{
  "bind": "0.0.0.0",
  "port": 8765,
  "token": ""            // set a token here; empty string = no auth
}
```

After changing the file, run `install.ps1` again. It stops the old processes, rebuilds, and starts with the new configuration. To uninstall, run `uninstall.ps1`.

You can also run the two processes manually in the foreground:

```powershell
dotnet run --project receiver -c Release -- --bind 0.0.0.0 --port 8765 --token "<your-token>"
dotnet run --project windows\AgentBeacon.Indicator -c Release --no-build
```

### 2. Agent Side: Connect Once

**Claude Code users: plugin mode**

```bash
# Install
claude plugin marketplace add /path/to/AgentBeacon
claude plugin install agentbeacon@agentbeacon

# Update after changing plugin code
claude plugin marketplace update agentbeacon
claude plugin update agentbeacon

# Check status
claude plugin list

# Disable / enable
claude plugin disable agentbeacon
claude plugin enable agentbeacon

# Uninstall
# Add `claude plugin marketplace remove agentbeacon` if you also want to remove the marketplace registration.
claude plugin uninstall agentbeacon@agentbeacon
```

**Codex CLI users: plugin mode**

```bash
# Install
codex plugin marketplace add /path/to/AgentBeacon
codex plugin add agentbeacon-codex@agentbeacon

# Update after changing plugin code; local marketplace paths are read live, so reinstall.
codex plugin remove agentbeacon-codex@agentbeacon
codex plugin add agentbeacon-codex@agentbeacon

# Check status
codex plugin list

# Uninstall
# Add `codex plugin marketplace remove agentbeacon` if you also want to remove the marketplace registration.
codex plugin remove agentbeacon-codex@agentbeacon
```

Required after installing: open `codex`, run `/hooks`, and trust the AgentBeacon entries. Codex skips untrusted command hooks by default. After updating or reinstalling the plugin, trust them again.

For older Codex versions without the `plugin` subcommand, use the merge script:

```bash
python3 plugins/codex/install.py
```

Uninstall with:

```bash
python3 plugins/codex/install.py --remove
```

**Shared agent configuration**

Both adapters read the same file:

```jsonc
// ~/.agentbeacon.json
{
  "url": "http://127.0.0.1:8765",
  "token": null
}
```

After that, run `claude` or `codex` from any directory and AgentBeacon follows every session automatically. Other agents or scripts can integrate with a single HTTP POST; see [docs/protocol.md](docs/protocol.md).

### 3. Verify

If a light appears at the top-right of the screen, it is working. When there are no sessions, the whole indicator is completely hidden, which is also expected.

For development, set `"debug": true` in the Receiver configuration and request `GET /debug/sessions` to inspect every received status.

## How It Works

```text
Agent: Claude Code plugin / Codex hooks / any script
    |  HTTP POST /api/v1/status
    |  one-way, no retry, last-received-wins
    v
Receiver: Windows, C# / ASP.NET Core
    |  authoritative in-memory state
    |  local Named Pipe full-snapshot push
    v
Indicator: Windows, WPF traffic-light panel
```

The three pieces start and stop independently. The agent side does not depend on the Windows UI implementation. Receiver does not know about specific agents. Indicator only subscribes to state. AgentBeacon does not participate in reasoning, take over tool calls, or modify the agent itself; it only moves lifecycle events captured by hooks into a visible desktop signal.

For more detail, see [docs/architecture.md](docs/architecture.md).

## Project Status

- **Round 1**: Protocol v1 + Receiver: HTTP, validation, last-received-wins.
- **Round 2**: Windows Indicator MVP: WPF + Named Pipe IPC, no focus stealing, completed tombstones.
- **Round 3**: Three-light UI redesign: modular lights, anchored cards, collision layout.
- **Round 4**: Claude Code plugin adapter: hook mapping, process watchdog, two failed-report paths.
- **Round 5**: Right-click dismissal and drag positioning.
- **Round 6**: Dual auth mode: token or no auth.
- **Round 7**: Configuration files: Windows `agentbeacon.json`, agent-side `~/.agentbeacon.json`, one-command Windows install and startup.
- **Round 8**: Release package: WSL cross-build via `bash scripts/dist.sh`, Windows publish via `publish.ps1`, self-contained portable folder with `install.bat`.
- **Round 9**: Tray icon with right-click exit, single-instance guard, simpler config where empty `token` means no auth.
- **Round 10**: Light cards, status-colored accent bars, blinking status changes, unified 30-second card duration, brighter lights.
- **Round 11**: Codex CLI adapter: `~/.codex/hooks.json` integration, trust guidance, watchdog.
- **Round 12**: Right-click rename: inline editing, per-session persistence, tooltips retain the real identity.

Automated tests: **170 unique tests** passing.

| Suite | Count |
| --- | --- |
| `tests/test_receiver.py` | 42 |
| `tests/test_hook_adapter.py` | 33 |
| `tests/test_codex_adapter.py` | 19 |
| `tests/Receiver.IpcTests` | 18 |
| `tests/Indicator.CoreTests` | 58 |

## Documentation

- [docs/architecture.md](docs/architecture.md): end-to-end flow and component responsibilities.
- [docs/protocol.md](docs/protocol.md): v1 HTTP status-reporting protocol, including auth modes.
- [docs/ui-policy.md](docs/ui-policy.md): canonical UI behavior.
- [docs/adapter-claude-code.md](docs/adapter-claude-code.md): Claude Code adapter installation and mapping.
- [docs/adapter-codex.md](docs/adapter-codex.md): Codex CLI adapter installation and mapping.
- [docs/round2.md](docs/round2.md): Round 2 design and historical notes.
- [docs/wsl中开发时的调试说明.md](docs/wsl中开发时的调试说明.md): manual debugging guide for WSL development.
