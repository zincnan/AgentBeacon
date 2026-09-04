#!/usr/bin/env python3
"""Install the AgentBeacon adapter into Codex CLI.

Merges plugins/codex/hooks.json into ~/.codex/hooks.json:
  - renders the __AGENTBEACON_HOOK__ placeholder to the absolute script
    path (Codex runs hook commands with env_clear(), so no $HOME
    expansion)
  - preserves every event group you already have; AgentBeacon's groups
    replace only AgentBeacon's previous entries (matched by the
    agentbeacon script path inside the command)

After installing, Codex will NOT run these hooks until they are trusted:
open Codex and run /hooks to review+trust them (or pass
--dangerously-bypass-hook-trust for automation you control).

Usage:
  python3 plugins/codex/install.py            # install / re-install
  python3 plugins/codex/install.py --remove   # remove AgentBeacon entries
"""

import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))            # plugins/codex
SCRIPTS = os.path.join(HERE, "scripts")
TEMPLATE = os.path.join(HERE, "hooks.json")
MARKER = "__AGENTBEACON_HOOK__"


def codex_home():
    return os.environ.get("CODEX_HOME") or os.path.join(
        os.path.expanduser("~"), ".codex")


def is_agentbeacon_group(group):
    for h in group.get("hooks", []):
        cmd = str(h.get("command", ""))
        if "agentbeacon_codex_hook.py" in cmd or MARKER in cmd:
            return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--remove", action="store_true")
    ap.add_argument("--codex-home", default=codex_home())
    args = ap.parse_args()

    hooks_path = os.path.join(args.codex_home, "hooks.json")
    script_path = os.path.join(SCRIPTS, "agentbeacon_codex_hook.py")
    if not os.path.exists(script_path):
        print(f"agentbeacon: script missing: {script_path}", file=sys.stderr)
        return 1

    # Load existing config. Absent OR empty (Codex creates a 0-byte
    # placeholder) → start from an empty object; malformed non-empty JSON
    # is refused, never overwritten.
    doc = {}
    if os.path.exists(hooks_path) and os.path.getsize(hooks_path) > 0:
        try:
            with open(hooks_path, encoding="utf-8") as f:
                doc = json.load(f)
            if not isinstance(doc, dict):
                print(f"agentbeacon: {hooks_path} is not a JSON object; "
                      "refusing to touch it", file=sys.stderr)
                return 1
        except ValueError as e:
            print(f"agentbeacon: {hooks_path} is not valid JSON ({e}); "
                  "refusing to touch it", file=sys.stderr)
            return 1

    hooks = doc.setdefault("hooks", {})

    if args.remove:
        removed = 0
        for event in list(hooks.keys()):
            groups = hooks[event]
            if not isinstance(groups, list):
                continue
            kept = [g for g in groups if not is_agentbeacon_group(g)]
            removed += len(groups) - len(kept)
            if kept:
                hooks[event] = kept
            else:
                del hooks[event]
        os.makedirs(args.codex_home, exist_ok=True)
        with open(hooks_path, "w", encoding="utf-8") as f:
            json.dump(doc, f, ensure_ascii=False, indent=2)
        print(f"agentbeacon: removed {removed} hook groups from {hooks_path}")
        return 0

    # Install: render the template and merge event-by-event.
    with open(TEMPLATE, encoding="utf-8") as f:
        template = json.load(f)
    rendered = json.loads(
        json.dumps(template).replace(MARKER, script_path.replace("\\", "\\\\")))

    added = updated = 0
    for event, groups in rendered["hooks"].items():
        ours = groups[0]
        existing = hooks.setdefault(event, [])
        # Replace a previous AgentBeacon group if present.
        replaced = False
        for i, g in enumerate(existing):
            if is_agentbeacon_group(g):
                existing[i] = ours
                replaced = True
                break
        if not replaced:
            existing.append(ours)
            added += 1
        else:
            updated += 1

    os.makedirs(args.codex_home, exist_ok=True)
    with open(hooks_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)

    print(f"agentbeacon: installed → {hooks_path}")
    print(f"  events: {added} added, {updated} updated")
    print("  NEXT (required): open `codex`, run /hooks and TRUST the")
    print("  AgentBeacon entries — Codex skips untrusted command hooks.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
