"""Runs claude -p with two probe MCP servers (one denied in --settings) and an env marker."""
import json
import os
import subprocess
import sys

here = os.path.dirname(os.path.abspath(__file__))
work = os.path.join(here, "claude-work")
os.makedirs(work, exist_ok=True)
log = os.path.join(here, "claude-servers.jsonl")
if os.path.exists(log):
    os.remove(log)
py = sys.executable
server = os.path.join(here, "probe_server.py")


def entry(n):
    return {"type": "stdio", "command": py, "args": [server, "--name", n, "--log", log]}


mcp = os.path.join(here, "claude-mcp.json")
with open(mcp, "w", encoding="utf-8") as f:
    json.dump({"mcpServers": {"gorynych-outside": entry("gorynych-outside"), "probe-allowed": entry("probe-allowed")}}, f)
settings = os.path.join(here, "claude-settings.json")
with open(settings, "w", encoding="utf-8") as f:
    json.dump({"permissions": {"deny": ["mcp__gorynych-outside"]}}, f)

env = {k: v for k, v in os.environ.items()
       if not (k.upper().startswith(("CLAUDE", "ANTHROPIC", "MCP_")) or k.upper() == "CLAUDECODE")}
env["GORYNYCH_PROBE_MARK"] = "marker-from-adapter"

cmd = ["claude", "-p", "Reply with just the word ok.", "--output-format", "stream-json", "--verbose",
       "--mcp-config", mcp, "--settings", settings, "--model", "haiku", "--permission-mode", "default"]
proc = subprocess.Popen(cmd, cwd=work, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, shell=False)
print("claude pid", proc.pid)
out, err = proc.communicate(timeout=180)
lines = [json.loads(l) for l in out.decode("utf-8", "replace").splitlines() if l.strip().startswith("{")]
init = next((l for l in lines if l.get("type") == "system" and l.get("subtype") == "init"), None)
result = next((l for l in lines if l.get("type") == "result"), None)
print("exit", proc.returncode)
if init:
    print("mcp_servers", json.dumps(init.get("mcp_servers")))
    print("probe tools", [t for t in init.get("tools", []) if "probe" in t or "gorynych" in t])
    print("tool count", len(init.get("tools", [])))
print("result", (result or {}).get("result"), (result or {}).get("is_error"))
if err:
    print("stderr", err.decode("utf-8", "replace")[-1500:])
