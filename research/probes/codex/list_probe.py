"""Probe of thread/list over the user's own Codex threads (Codex 0.167): counts only, no titles or folders.

Usage: python list_probe.py <codex.exe>
Shows how threads of other Codex clients (the desktop app, the CLI, VS Code) look to an app-server
client: their source and originator, and how paging and the archived filter behave.
"""
import collections
import json
import subprocess
import sys

proc = subprocess.Popen([sys.argv[1], "app-server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                        text=True, encoding="utf-8", bufsize=1)
next_id = 0


def call(method, params):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"id": next_id, "method": method, "params": params}) + "\n")
    proc.stdin.flush()
    for line in proc.stdout:
        m = json.loads(line)
        if m.get("id") == next_id and "method" not in m:
            return m.get("result", {"__error": m.get("error")})


call("initialize", {"clientInfo": {"name": "gorynych_probe", "title": "Gorynych probe", "version": "0.0.1"}})
proc.stdin.write(json.dumps({"method": "initialized", "params": {}}) + "\n")
proc.stdin.flush()
for archived in (False, True):
    seen, cursor, pages = [], None, 0
    while pages < 10:
        r = call("thread/list", {"limit": 20, "archived": archived, "cursor": cursor, "sortKey": "updated_at", "useStateDbOnly": True})
        if "__error" in r:
            print("error", r)
            break
        seen += r["data"]
        pages += 1
        cursor = r.get("nextCursor")
        if not cursor:
            break
    by = collections.Counter((json.dumps(t.get("source")), t.get("originator"), bool(t.get("name"))) for t in seen)
    print(f"archived={archived}: {len(seen)} threads in {pages} pages (more: {bool(cursor)})")
    for (source, originator, named), n in by.most_common():
        print(f"  source {source} originator {originator} named {named}: {n}")
every = call("thread/list", {"limit": 20, "sourceKinds": ["cli", "vscode", "exec", "appServer", "subAgent", "unknown"], "useStateDbOnly": True})
print(f"with every source kind: {len(every.get('data', []))} on the first page")
proc.stdin.close()
proc.wait(timeout=10)
