"""Throwaway stdio MCP server: logs what its host gives it (env, parent chain, cwd, initialize)."""
import ctypes
import json
import os
import sys
import time
from ctypes import wintypes

args = sys.argv[1:]
name = args[args.index("--name") + 1]
log_path = args[args.index("--log") + 1]


def log(kind, **data):
    with open(log_path, "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": time.time(), "server": name, "kind": kind, **data}) + "\n")


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", ctypes.c_wchar * 260),
    ]


def ancestors():
    k = ctypes.windll.kernel32
    k.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snap = k.CreateToolhelp32Snapshot(0x2, 0)
    entry = PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
    procs = {}
    ok = k.Process32FirstW(snap, ctypes.byref(entry))
    while ok:
        procs[entry.th32ProcessID] = (entry.th32ParentProcessID, entry.szExeFile)
        ok = k.Process32NextW(snap, ctypes.byref(entry))
    k.CloseHandle(snap)
    chain, pid = [], os.getpid()
    while pid in procs and len(chain) < 12:
        ppid, exe = procs[pid]
        chain.append({"pid": pid, "exe": exe})
        if ppid == pid or ppid in [c["pid"] for c in chain]:
            break
        pid = ppid
    return chain


interesting = {k: v for k, v in os.environ.items()
               if k.upper().startswith(("GORYNYCH", "CLAUDE", "KIMI", "MCP", "ANTHROPIC"))}
log("start", pid=os.getpid(), ppid=os.getppid(), cwd=os.getcwd(), argv=sys.argv,
    env_keys=sorted(os.environ.keys()), env_interesting=interesting, ancestors=ancestors())

out = sys.stdout.buffer


def send(msg):
    out.write((json.dumps(msg) + "\n").encode("utf-8"))
    out.flush()


modern = "--modern" in args
client_caps = {}
for raw in sys.stdin.buffer:
    line = raw.decode("utf-8").strip()
    if not line:
        continue
    msg = json.loads(line)
    method = msg.get("method")
    if method:
        log("request" if "id" in msg else "note", method=method, params=msg.get("params"))
    if method == "server/discover" and modern:
        send({"jsonrpc": "2.0", "id": msg["id"], "result": {
            "resultType": "complete", "supportedVersions": ["2026-07-28"],
            "capabilities": {"tools": {}},
            "_meta": {"io.modelcontextprotocol/serverInfo": {"name": name, "version": "0"}},
        }})
        continue
    if method == "initialize":
        p = msg.get("params") or {}
        client_caps = p.get("capabilities") or {}
        log("initialize", params=p)
        send({"jsonrpc": "2.0", "id": msg["id"], "result": {
            "protocolVersion": p.get("protocolVersion", "2025-06-18"),
            "capabilities": {"tools": {}},
            "serverInfo": {"name": name, "version": "0"},
        }})
    elif method == "notifications/initialized":
        log("initialized")
        if "roots" in client_caps:
            send({"jsonrpc": "2.0", "id": "roots-1", "method": "roots/list"})
    elif method == "tools/list":
        log("tools/list")
        result = {"tools": [{
            "name": "probe_echo",
            "description": "Probe tool; returns the server name.",
            "inputSchema": {"type": "object", "properties": {}},
        }]}
        if modern:
            result["resultType"] = "complete"
        send({"jsonrpc": "2.0", "id": msg["id"], "result": result})
    elif method == "tools/call":
        log("tools/call", params=msg.get("params"))
        token = ((msg.get("params") or {}).get("_meta") or {}).get("progressToken")
        if token is not None:
            send({"jsonrpc": "2.0", "method": "notifications/progress",
                  "params": {"progressToken": token, "progress": 1, "message": "probe progress"}})
        result = {"content": [{"type": "text", "text": name}]}
        if modern:
            result["resultType"] = "complete"
        send({"jsonrpc": "2.0", "id": msg["id"], "result": result})
    elif method == "ping":
        send({"jsonrpc": "2.0", "id": msg["id"], "result": {}})
    elif "id" in msg and method is None:
        log("response", msg=msg)
    elif "id" in msg:
        log("unknown-request", method=method)
        send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": "not found"}})
    else:
        log("notification", method=method)
log("eof")
