"""Runs kimi acp, creates a session with a probe MCP server, then deletes the session."""
import json
import os
import queue
import subprocess
import sys
import threading
import time

here = os.path.dirname(os.path.abspath(__file__))
work = os.path.join(here, "kimi-work")
os.makedirs(work, exist_ok=True)
log = os.path.join(here, "kimi-servers.jsonl")
if os.path.exists(log):
    os.remove(log)
server = os.path.join(here, "probe_server.py")

env = {k: v for k, v in os.environ.items()
       if not (k.upper().startswith(("CLAUDE", "ANTHROPIC", "MCP_")) or k.upper() == "CLAUDECODE")}
env["GORYNYCH_PROBE_MARK"] = "marker-from-adapter"

proc = subprocess.Popen(["kimi", "acp"], cwd=work, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL)
print("kimi pid", proc.pid)
inbox = queue.Queue()


def reader():
    for raw in proc.stdout:
        line = raw.decode("utf-8", "replace").strip()
        if line.startswith("{"):
            inbox.put(json.loads(line))


threading.Thread(target=reader, daemon=True).start()
next_id = [0]


def request(method, params, timeout=60):
    next_id[0] += 1
    rid = next_id[0]
    proc.stdin.write((json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}) + "\n").encode())
    proc.stdin.flush()
    end = time.time() + timeout
    while time.time() < end:
        try:
            msg = inbox.get(timeout=1)
        except queue.Empty:
            continue
        if msg.get("id") == rid and "method" not in msg:
            return msg
        if "method" in msg and "id" in msg:  # agent -> client request: refuse
            proc.stdin.write((json.dumps({"jsonrpc": "2.0", "id": msg["id"],
                                          "error": {"code": -32601, "message": "probe"}}) + "\n").encode())
            proc.stdin.flush()
    raise TimeoutError(method)


init = request("initialize", {"protocolVersion": 1, "clientCapabilities": {}, "clientInfo": {"name": "probe", "version": "0"}})
print("initialize", json.dumps(init.get("result", init))[:300])
new = request("session/new", {"cwd": work, "mcpServers": [{
    "name": "gorynych-outside", "command": sys.executable,
    "args": [server, "--name", "kimi-acp", "--log", log], "env": []}]})
sid = (new.get("result") or {}).get("sessionId")
print("session/new", sid, new.get("error"))
time.sleep(8)
started = os.path.exists(log)
print("server started without a prompt:", started)
if not started and sid:
    r = request("session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": "Reply with just the word ok."}]}, 180)
    print("prompt", json.dumps(r)[:200])
    time.sleep(2)
if sid:
    print("delete", json.dumps(request("session/delete", {"sessionId": sid}))[:200])
proc.stdin.close()
try:
    proc.wait(10)
except subprocess.TimeoutExpired:
    subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
