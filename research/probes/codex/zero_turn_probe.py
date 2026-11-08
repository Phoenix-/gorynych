"""Probe of three thread questions for the Codex adapter that need no model turn (Codex 0.167).

Usage: python zero_turn_probe.py <codex.exe> <scratch folder> <one of the user's MCP server names>

1. Is a thread that never had a turn on disk and listed, and with what name and preview?
2. Can an archived thread be resumed?
3. Does `config` in thread/start turn off one of the user's own MCP servers for that thread only
   (`{"mcp_servers": {"<name>": {"enabled": false}}}`), without its command?
Deletes the probe threads; never touches ~/.codex.
"""
import json
import os
import subprocess
import sys
import time

exe, scratch, user_server = sys.argv[1:4]
ws = os.path.join(scratch, "ws0")
os.makedirs(ws, exist_ok=True)
proc = subprocess.Popen([exe, "app-server"], cwd=ws, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                        text=True, encoding="utf-8", bufsize=1)
next_id = 0


def call(method, params):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"id": next_id, "method": method, "params": params}) + "\n")
    proc.stdin.flush()
    for line in proc.stdout:
        m = json.loads(line)
        if "method" in m and "id" in m:  # a server request: none expected here
            proc.stdin.write(json.dumps({"id": m["id"], "error": {"code": -32601, "message": "probe"}}) + "\n")
            proc.stdin.flush()
        elif m.get("id") == next_id and "method" not in m:
            return m.get("result", {"__error": m.get("error")})


def listed(archived=False):
    return {t["id"]: (t.get("name"), t.get("preview"), t.get("status")) for t in call("thread/list", {"cwd": ws, "archived": archived})["data"]}


call("initialize", {"clientInfo": {"name": "gorynych_probe", "title": "Gorynych probe", "version": "0.0.1"}})
proc.stdin.write(json.dumps({"method": "initialized", "params": {}}) + "\n")
proc.stdin.flush()
made = []
try:
    t = call("thread/start", {"cwd": ws, "config": {"mcp_servers": {user_server: {"enabled": False}}}})
    if "__error" in t:
        print(f"thread/start with {user_server}.enabled=false -> {t['__error']}")
        t = call("thread/start", {"cwd": ws})
    tid = t["thread"]["id"]
    made.append(tid)
    print(f"1. started {tid}: path {t['thread'].get('path')!r}, on disk {bool(t['thread'].get('path')) and os.path.exists(t['thread']['path'])}")
    print(f"   listed while loaded: {listed().get(tid)}")
    st = call("mcpServerStatus/list", {"threadId": tid})
    print(f"3. {user_server} for this thread: {[(x.get('name'), x.get('runtimeStatus')) for x in st.get('data', []) if x.get('name') == user_server]}")
    print(f"   unsubscribe -> {call('thread/unsubscribe', {'threadId': tid})}; listed after: {listed().get(tid)}; "
          f"read: {str(call('thread/read', {'threadId': tid}).get('__error') or 'ok')[:160]}")

    t2 = call("thread/start", {"cwd": ws})
    tid2 = t2["thread"]["id"]
    made.append(tid2)
    print(f"2. archive an unprompted thread -> {call('thread/archive', {'threadId': tid2})}")
    call("thread/unsubscribe", {"threadId": tid2})
    time.sleep(0.5)
    r = call("thread/resume", {"threadId": tid2, "excludeTurns": True})
    print(f"   resume archived -> {r.get('__error') or ('ok, status ' + json.dumps(r['thread'].get('status')))}; "
          f"archived list has it: {tid2 in listed(archived=True)}")
finally:
    for tid in made:
        print(f"delete {tid} -> {call('thread/delete', {'threadId': tid})}")
    print(f"left: {list(listed())} archived {list(listed(archived=True))}")
    proc.stdin.close()
    proc.wait(timeout=15)
