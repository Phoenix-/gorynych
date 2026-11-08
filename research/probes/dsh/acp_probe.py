"""Probe of DeepSeek Harness's ACP server (`dsh --profile acp`) for the DSH adapter.

Usage: python acp_probe.py <dsh package dir> <scratch folder> <wire log> <check> [<check> ...]

<dsh package dir> is an installed `@deepseek-ai/dsh` (the folder with lib/bin.js); the probe
runs `node lib/bin.js --profile acp` directly, with no npm shim in between. Every launch gets:

- `DSH_HOME` = the value of the environment variable of that name if the caller set one, else
  <scratch folder>/home, so `~/.dsh` is never touched unless the caller points DSH_HOME there;
- `DSH_TELEMETRY_DISABLED=1`;
- the launch folder <scratch folder>/launch (empty: no `.env` for DSH's project layer);
- a `--patch` overlay when a check needs one (written to <scratch folder>, never to DSH_HOME).

Sessions use <scratch folder>/ws as `cwd` and get the throwaway stdio MCP server
`../outside/probe_server.py` under the name `gorynych`. The wire goes to <wire log> as
`{"from": "client"|"agent", "message": ...}` lines, the format of the recorded Kimi fixture; it
holds the host's paths and is not kept as is.

Keyless checks (no model call): boot, init, new, list, config, close, resume, eof, kill, cost.
Checks that prompt (need the user's DeepSeek key in DSH_HOME): see the CHECKS table.
"""
import ctypes
import json
import os
import queue
import subprocess
import sys
import threading
import time
from ctypes import wintypes

pkg, scratch, log_path = sys.argv[1:4]
checks = sys.argv[4:]
bin_js = os.path.join(pkg, "lib", "bin.js")
home = os.environ.get("DSH_HOME") or os.path.join(scratch, "home")
launch = os.path.join(scratch, "launch")
ws = os.path.join(scratch, "ws")
for d in (home, launch, ws):
    os.makedirs(d, exist_ok=True)
server_log = os.path.join(scratch, "mcp_server.jsonl")
probe_server = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "outside", "probe_server.py"))
log = open(log_path, "a", encoding="utf-8")


def note(text):
    print(text, flush=True)
    log.write(json.dumps({"note": text}) + "\n")
    log.flush()


def gorynych_server():
    return {"name": "gorynych", "command": sys.executable,
            "args": [probe_server, "--name", "gorynych", "--log", server_log], "env": []}


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD),
                ("cntThreads", wintypes.DWORD), ("th32ParentProcessID", wintypes.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD), ("szExeFile", ctypes.c_wchar * 260)]


def processes():
    k = ctypes.windll.kernel32
    k.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snap = k.CreateToolhelp32Snapshot(0x2, 0)
    e = PROCESSENTRY32W()
    e.dwSize = ctypes.sizeof(PROCESSENTRY32W)
    out = {}
    ok = k.Process32FirstW(snap, ctypes.byref(e))
    while ok:
        out[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile)
        ok = k.Process32NextW(snap, ctypes.byref(e))
    k.CloseHandle(snap)
    return out


def descendants(pid):
    procs = processes()
    found, frontier = [], [pid]
    while frontier:
        p = frontier.pop()
        for child, (parent, exe) in procs.items():
            if parent == p and child != p:
                found.append((child, exe, p))
                frontier.append(child)
    return found


def alive(pids):
    procs = processes()
    return [p for p in pids if p in procs]


class Dsh:
    """One `dsh --profile acp` over stdio."""

    def __init__(self, name, extra_args=(), env_extra=None):
        self.name = name
        env = dict(os.environ)
        env["DSH_HOME"] = home
        env["DSH_TELEMETRY_DISABLED"] = "1"
        env.update(env_extra or {})
        self.started = time.monotonic()
        self.proc = subprocess.Popen(["node", bin_js, "--profile", "acp", *extra_args], cwd=launch, env=env,
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     text=True, encoding="utf-8", bufsize=1)
        self.next_id = 0
        self.pending = {}
        self.events = queue.Queue()
        self.impure = []
        self.stderr = []
        self.permission = lambda params: {"outcome": {"outcome": "selected", "optionId": "reject-once"}}
        threading.Thread(target=self.read, daemon=True).start()
        threading.Thread(target=self.drain, daemon=True).start()

    def drain(self):
        for line in self.proc.stderr:
            self.stderr.append(line.rstrip())
            log.write(json.dumps({"stderr": self.name, "line": line.rstrip()}) + "\n")

    def read(self):
        for line in self.proc.stdout:
            try:
                m = json.loads(line)
            except json.JSONDecodeError:
                self.impure.append(line.rstrip())
                log.write(json.dumps({"impure": self.name, "line": line.rstrip()}) + "\n")
                continue
            log.write(json.dumps({"from": "agent", "at": round(time.monotonic() - self.started, 3), "message": m}) + "\n")
            log.flush()
            if "id" in m and "method" not in m:
                q = self.pending.pop(m["id"], None)
                if q:
                    q.put(m)
            elif "id" in m:
                self.events.put(("request", m))
                threading.Thread(target=self.answer, args=(m,), daemon=True).start()
            else:
                self.events.put(("notification", m))
        self.events.put(("eof", None))

    def answer(self, m):
        if m["method"] == "session/request_permission":
            self.send({"jsonrpc": "2.0", "id": m["id"], "result": self.permission(m["params"])})
        else:
            self.send({"jsonrpc": "2.0", "id": m["id"], "error": {"code": -32601, "message": "not offered by the probe"}})

    def send(self, m):
        log.write(json.dumps({"from": "client", "at": round(time.monotonic() - self.started, 3), "message": m}) + "\n")
        log.flush()
        self.proc.stdin.write(json.dumps(m) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params, timeout=120):
        self.next_id += 1
        q = queue.Queue()
        self.pending[self.next_id] = q
        t = time.monotonic()
        self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        try:
            m = q.get(timeout=timeout)
        except queue.Empty:
            return {"timeout": True}, time.monotonic() - t
        return m, time.monotonic() - t

    def notify(self, method, params):
        self.send({"jsonrpc": "2.0", "method": method, "params": params})

    def initialize(self):
        m, took = self.request("initialize", {
            "protocolVersion": 1,
            "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False,
                                   "elicitation": {"form": {}}},
            "clientInfo": {"name": "gorynych-probe", "version": "0.1"}})
        return m, took

    def updates(self, wait=0.5):
        out = []
        end = time.monotonic() + wait
        while True:
            try:
                kind, m = self.events.get(timeout=max(0.0, end - time.monotonic()))
            except queue.Empty:
                return out
            out.append((kind, m))

    def stop(self, timeout=15):
        t = time.monotonic()
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return None
        return time.monotonic() - t


def summary(m):
    if "error" in m:
        return f"error {m['error'].get('code')} {m['error'].get('message')}"
    return json.dumps(m.get("result"))[:600]


def new_session(d, servers=None):
    m, took = d.request("session/new", {"cwd": ws, "mcpServers": [gorynych_server()] if servers is None else servers})
    return m, took


def mcp_starts():
    if not os.path.exists(server_log):
        return []
    with open(server_log, encoding="utf-8") as f:
        return [json.loads(l) for l in f if '"kind": "start"' in l or '"kind":"start"' in l]


def check_boot():
    """Startup time to the initialize answer, stdout purity, stderr, and what DSH_HOME holds afterwards."""
    d = Dsh("boot")
    m, took = d.initialize()
    note(f"boot: initialize answered {time.monotonic() - d.started:.2f}s after spawn (request {took:.2f}s): {summary(m)}")
    tree = descendants(d.proc.pid)
    note(f"boot: process tree under node pid {d.proc.pid}: {[(exe, pid) for pid, exe, _ in tree]}")
    gone = d.stop()
    note(f"boot: stdin EOF -> exit after {gone if gone is None else round(gone, 2)}s, code {d.proc.returncode}")
    note(f"boot: impure stdout lines: {d.impure[:5]}; stderr: {d.stderr[:10]}")
    note(f"boot: survivors of the tree: {alive([p for p, _, _ in tree])}")
    listing = []
    for root, dirs, files in os.walk(home):
        for f in files:
            listing.append(os.path.relpath(os.path.join(root, f), home))
    note(f"boot: files in DSH_HOME: {sorted(listing)[:40]}")


def check_sessions():
    """new (with the gorynych server), list, config, close, list, resume, close; costs of each."""
    d = Dsh("sessions")
    d.initialize()
    before = len(mcp_starts())
    m, took = new_session(d)
    note(f"new: {took:.2f}s: {summary(m)}")
    if "error" in m:
        d.stop()
        return
    sid = m["result"]["sessionId"]
    time.sleep(1.0)
    starts = mcp_starts()[before:]
    note(f"new: gorynych server starts: {len(starts)}; parent chain of the first: {starts[0].get('ancestors') if starts else None}")
    note(f"new: updates after new: {[x[1].get('params', {}).get('update', {}).get('sessionUpdate') for x in d.updates(1.0)]}")
    files = [os.path.relpath(os.path.join(r, f), home) for r, _, fs in os.walk(home) for f in fs]
    note(f"new: files in DSH_HOME after session/new: {sorted(files)[:40]}")
    for params in ({}, {"cwd": ws}, {"cwd": launch}):
        m, took = d.request("session/list", params)
        note(f"list {params}: {took:.2f}s: {summary(m)}")
    options = []
    m, _ = d.request("session/set_config_option", {"sessionId": sid, "configId": "model", "value": "deepseek-v4-pro"})
    note(f"config model: {summary(m)}")
    m, _ = d.request("session/set_config_option", {"sessionId": sid, "configId": "reasoning_effort", "value": "max"})
    note(f"config effort: {summary(m)}")
    m, _ = d.request("session/set_config_option", {"sessionId": sid, "configId": "model", "value": "no-such-model"})
    note(f"config unknown model: {summary(m)}")
    m, _ = d.request("session/set_config_option", {"sessionId": sid, "configId": "mode", "value": "x"})
    note(f"config unknown id: {summary(m)}")
    tree = descendants(d.proc.pid)
    note(f"sessions: tree: {[(exe, pid, parent) for pid, exe, parent in tree]}")
    m, took = d.request("session/close", {"sessionId": sid})
    note(f"close: {took:.2f}s: {summary(m)}; gorynych servers alive after close: {alive([p for p, e, _ in tree if e.lower().startswith('python')])}")
    m, took = d.request("session/list", {})
    note(f"list after close: {summary(m)}")
    before = len(mcp_starts())
    m, took = d.request("session/resume", {"sessionId": sid, "cwd": ws, "mcpServers": [gorynych_server()]})
    note(f"resume: {took:.2f}s: {summary(m)}; gorynych server starts: {len(mcp_starts()[before:])}")
    m, took = d.request("session/resume", {"sessionId": sid, "cwd": ws, "mcpServers": []})
    note(f"resume again while loaded: {summary(m)}")
    m, took = d.request("session/close", {"sessionId": sid})
    note(f"close again: {took:.2f}s: {summary(m)}")
    m, took = d.request("session/resume", {"sessionId": sid, "cwd": launch, "mcpServers": []})
    note(f"resume with another cwd: {summary(m)}")
    m, took = d.request("session/resume", {"sessionId": sid, "cwd": ws})
    note(f"resume without mcpServers: {took:.2f}s: {summary(m)}")
    m, took = d.request("session/close", {"sessionId": sid})
    for method, params in (("session/load", {"sessionId": sid, "cwd": ws, "mcpServers": []}),
                           ("session/delete", {"sessionId": sid}),
                           ("session/close", {"sessionId": "nope"}),
                           ("session/resume", {"sessionId": "nope", "cwd": ws, "mcpServers": []}),
                           ("_gorynych/note", {"sessionId": sid, "text": "x", "sender": "y"})):
        m, _ = d.request(method, params)
        note(f"{method} {params.get('sessionId')}: {summary(m)}")
    d.notify("session/cancel", {"sessionId": "nope"})
    gone = d.stop()
    note(f"sessions: exit after EOF {gone}s code {d.proc.returncode}; stderr: {d.stderr[:10]}")
    with open(os.path.join(scratch, "last_session.txt"), "w") as f:
        f.write(sid)


def check_other_process():
    """A second process lists and resumes a session the first one has open."""
    a = Dsh("a")
    a.initialize()
    m, _ = new_session(a, servers=[])
    sid = m["result"]["sessionId"]
    b = Dsh("b")
    b.initialize()
    m, _ = b.request("session/list", {"cwd": ws})
    note(f"other: B lists while A has {sid} open (no prompt yet): {sid in summary(m)}")
    m, took = b.request("session/resume", {"sessionId": sid, "cwd": ws, "mcpServers": []})
    note(f"other: B resumes it: {took:.2f}s: {summary(m)}")
    if "result" in m:
        m, _ = b.request("session/close", {"sessionId": sid})
    a.stop()
    b.stop()


def check_kill():
    """Kill the node process with a session and its MCP server open; does the MCP server outlive it?"""
    d = Dsh("kill")
    d.initialize()
    new_session(d)
    time.sleep(1.0)
    tree = descendants(d.proc.pid)
    note(f"kill: tree before: {[(exe, pid, parent) for pid, exe, parent in tree]}")
    d.proc.kill()
    d.proc.wait(10)
    time.sleep(2.0)
    note(f"kill: survivors 2s after TerminateProcess of node: {[(p, e) for p, e, _ in tree if p in alive([p for p, _, _ in tree])]}")


def check_cost():
    """Cold start plus resume of an existing session, three times."""
    path = os.path.join(scratch, "last_session.txt")
    if not os.path.exists(path):
        note("cost: run 'sessions' first")
        return
    sid = open(path).read().strip()
    for i in range(3):
        d = Dsh(f"cost{i}")
        _, took_init = d.initialize()
        boot = time.monotonic() - d.started
        m, took = d.request("session/resume", {"sessionId": sid, "cwd": ws, "mcpServers": [gorynych_server()]})
        m2, took_close = d.request("session/close", {"sessionId": sid})
        note(f"cost: spawn->initialize {boot:.2f}s, resume {took:.2f}s ({'ok' if 'result' in m else summary(m)}), close {took_close:.2f}s")
        d.stop()


CHECKS = {
    "boot": check_boot,
    "sessions": check_sessions,
    "other": check_other_process,
    "kill": check_kill,
    "cost": check_cost,
}

for name in checks:
    note(f"=== {name}")
    CHECKS[name]()
