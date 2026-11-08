"""Runs claude -p against a legacy and a modern (2026-07-28) probe server and has it call both tools."""
import json
import os
import subprocess
import sys

here = os.path.dirname(os.path.abspath(__file__))
work = os.path.join(here, "claude-work")
os.makedirs(work, exist_ok=True)
log = os.path.join(here, "claude-modern.jsonl")
if os.path.exists(log):
    os.remove(log)
py = sys.executable
server = os.path.join(here, "probe_server.py")
mcp = os.path.join(here, "claude-modern-mcp.json")
with open(mcp, "w", encoding="utf-8") as f:
    json.dump({"mcpServers": {
        "probe-legacy": {"type": "stdio", "command": py, "args": [server, "--name", "probe-legacy", "--log", log]},
        "probe-modern": {"type": "stdio", "command": py, "args": [server, "--name", "probe-modern", "--log", log, "--modern"]},
    }}, f)
env = {k: v for k, v in os.environ.items()
       if not (k.upper().startswith(("CLAUDE", "ANTHROPIC", "MCP_")) or k.upper() == "CLAUDECODE")}
cmd = ["claude", "-p", "Call the tool mcp__probe-legacy__probe_echo once and the tool mcp__probe-modern__probe_echo once, then reply with just ok.",
       "--output-format", "stream-json", "--verbose", "--strict-mcp-config", "--mcp-config", mcp, "--model", "haiku",
       "--permission-mode", "default",
       "--allowedTools", "mcp__probe-legacy__probe_echo,mcp__probe-modern__probe_echo"]
proc = subprocess.run(cmd, cwd=work, env=env, capture_output=True, timeout=240)
lines = [json.loads(l) for l in proc.stdout.decode("utf-8", "replace").splitlines() if l.strip().startswith("{")]
init = next((l for l in lines if l.get("type") == "system" and l.get("subtype") == "init"), {})
print("exit", proc.returncode, "servers", json.dumps(init.get("mcp_servers")))
print("tools", [t for t in init.get("tools", []) if "probe" in t])
print("result", next((l.get("result") for l in lines if l.get("type") == "result"), None))
for l in open(log, encoding="utf-8"):
    d = json.loads(l)
    if d["kind"] in ("request", "note", "tools/call"):
        print(d["server"], d["kind"], d.get("method", ""), json.dumps(d.get("params"))[:400])
