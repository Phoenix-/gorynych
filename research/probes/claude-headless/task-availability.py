"""Compare native tool availability in two owned, nonpersistent Claude launches."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

exe = Path(os.environ["LOCALAPPDATA"]) / "Microsoft/WinGet/Links/claude.exe"
root = Path(tempfile.mkdtemp(prefix="claude-plan-review-"))
for enabled in (False, True):
    name = "opt-in" if enabled else "default"
    cwd = root / name
    cwd.mkdir()
    env = os.environ.copy()
    for key in ("CLAUDECODE", "CLAUDE_CODE_ENABLE_TODO_TOOLS"):
        env.pop(key, None)
    if enabled:
        env["CLAUDE_CODE_ENABLE_TODO_TOOLS"] = "1"
    args = [str(exe), "-p", "Reply only OK. Do not call any tools.",
            "--model", "opus", "--output-format", "stream-json", "--verbose",
            "--no-session-persistence", "--max-turns", "1",
            "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}',
            "--settings", '{"disableAllHooks":true}']
    result = subprocess.run(args, cwd=cwd, env=env, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=120)
    (cwd / "stdout.jsonl").write_text(result.stdout, encoding="utf-8")
    (cwd / "stderr.txt").write_text(result.stderr, encoding="utf-8")
    messages = []
    for line in result.stdout.splitlines():
        try:
            messages.append(json.loads(line))
        except ValueError:
            pass
    init = next((m for m in messages if m.get("type") == "system" and m.get("subtype") == "init"), {})
    print(json.dumps({"launch": name, "exit_code": result.returncode,
                      "version": init.get("claude_code_version"), "model": init.get("model"),
                      "task_tools": [t for t in init.get("tools", []) if t.startswith("Task") or t == "TodoWrite"],
                      "evidence": str(cwd)}, ensure_ascii=False), flush=True)
    if not init or result.returncode:
        raise SystemExit("Probe failed; inspect saved output before continuing")
