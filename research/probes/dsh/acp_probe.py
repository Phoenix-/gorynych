"""Probe of DeepSeek Harness's ACP server (`dsh --profile acp`) for the DSH adapter.

Usage: python acp_probe.py <dsh package dir> <scratch folder> <wire log> [--real-home] <check> [<check> ...]

<dsh package dir> is an installed `@deepseek-ai/dsh` (the folder with lib/bin.js); the probe
runs `node lib/bin.js --profile acp` directly, with no npm shim in between. Every launch gets:

- `DSH_HOME` = <scratch folder>/home, so `~/.dsh` is never touched; with `--real-home` the
  variable is removed instead and DSH uses the user's own `~/.dsh` (its key, its sessions).
  The probe itself never writes there; it only reads the projection cache of its own sessions;
- `DSH_TELEMETRY_DISABLED=1`;
- the launch folder <scratch folder>/launch (empty: no `.env` for DSH's project layer);
- a `--patch` overlay from `overlays/` when a check needs one.

Sessions use folders under <scratch folder> as `cwd`, never a user repository, and get the
throwaway stdio MCP server `../outside/probe_server.py` under the name `gorynych`. The wire
goes to <wire log> as `{"from": "client"|"agent", "message": ...}` lines, the format of the
recorded Kimi fixture; it holds the host's paths and is not kept as is. Live sessions are
appended to <scratch folder>/live_sessions.txt (DSH cannot delete them over ACP).

Keyless checks (no model call): boot, sessions, other, kill, cost.
Live checks (model turns on deepseek-v4-flash at effort low, need the user's key):
basics, plain, default, readonly, off. See each function's docstring.
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
real_home = "--real-home" in checks
checks = [c for c in checks if c != "--real-home"]
bin_js = os.path.join(pkg, "lib", "bin.js")
home = os.path.join(os.path.expanduser("~"), ".dsh") if real_home else os.path.join(scratch, "home")
overlays = os.path.join(os.path.dirname(os.path.abspath(__file__)), "overlays")
launch = os.path.join(scratch, "launch")
ws = os.path.join(scratch, "ws")
for d in ([launch, ws] if real_home else [home, launch, ws]):
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
        env.pop("DSH_HOME", None)
        env.pop("DSH_PERMISSION_MODE", None)
        if not real_home:
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
        self.asked = []
        self.held = []
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
            self.asked.append((round(time.monotonic() - self.started, 3), m))
            result = self.permission(m["params"])
            if result is None:
                self.held.append(m["id"])  # answered later (or withdrawn by the agent)
                return
            self.send({"jsonrpc": "2.0", "id": m["id"], "result": result})
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


# ---------------------------------------------------------------- live checks (model turns)

ALLOW = {"outcome": {"outcome": "selected", "optionId": "allow-once"}}
REJECT = {"outcome": {"outcome": "selected", "optionId": "reject-once"}}
CANCELLED = {"outcome": {"outcome": "cancelled"}}


def workspace(name, tree=False):
    """A fresh folder under the scratch folder; with tree, a few subfolders and files to see propagation."""
    path = os.path.join(scratch, name, "ws")
    os.makedirs(path, exist_ok=True)
    if tree:
        for sub in ("sub", os.path.join("sub", "inner"), "other"):
            os.makedirs(os.path.join(path, sub), exist_ok=True)
            with open(os.path.join(path, sub, "file.txt"), "w") as f:
                f.write("probe\n")
    return path


def record(sid, purpose):
    with open(os.path.join(scratch, "live_sessions.txt"), "a", encoding="utf-8") as f:
        f.write(f"{sid}\t{purpose}\n")


def sddl(path):
    """The owner-independent part of path's security descriptor as SDDL: DACL and mandatory label (reading only)."""
    adv = ctypes.windll.advapi32
    sd = ctypes.c_void_p()
    DACL, LABEL = 0x4, 0x10
    err = adv.GetNamedSecurityInfoW(ctypes.c_wchar_p(path), 1, DACL | LABEL, None, None, None, None, ctypes.byref(sd))
    if err:
        return f"GetNamedSecurityInfo error {err}"
    s = ctypes.c_wchar_p()
    adv.ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, DACL | LABEL, ctypes.byref(s), None)
    text = s.value
    ctypes.windll.kernel32.LocalFree(sd)
    ctypes.windll.kernel32.LocalFree(s)
    return text


def acl(path):
    """path's DACL and label as SDDL (reading only), and whether a FullControl open of it succeeds for us."""
    out = sddl(path)
    k = ctypes.windll.kernel32
    k.CreateFileW.restype = wintypes.HANDLE
    h = k.CreateFileW(path, 0x10000000, 0x7, None, 3, 0x02000000, None)  # GENERIC_ALL, share all, OPEN_EXISTING, BACKUP_SEMANTICS
    if h == wintypes.HANDLE(-1).value:
        full = f"denied (error {k.GetLastError()})"
    else:
        k.CloseHandle(h)
        full = "ok"
    return out, full


def show_acl(label, path):
    lines, full = acl(path)
    note(f"{label}: {os.path.basename(path) or path}: FullControl open {full}; ACL: {lines}")


def live_session(d, cwd, purpose, servers=None, effort="low"):
    m, took = d.request("session/new", {"cwd": cwd, "mcpServers": [gorynych_server()] if servers is None else servers})
    if "error" in m:
        note(f"new failed: {summary(m)}")
        return None
    sid = m["result"]["sessionId"]
    record(sid, purpose)
    if effort:
        m, _ = d.request("session/set_config_option", {"sessionId": sid, "configId": "reasoning_effort", "value": effort})
        note(f"session {sid} ({purpose}); effort {effort}: {'ok' if 'result' in m else summary(m)}; models offered: "
             f"{[o['name'] for g in m.get('result', {}).get('configOptions', [{}])[0].get('options', []) for o in g.get('options', [g])]}")
    return sid


def compact(update, at):
    u = update.get("update", {})
    kind = u.get("sessionUpdate")
    out = {"at": at, "kind": kind}
    for key in ("messageId", "toolCallId", "title", "status", "used", "size"):
        if key in u:
            out[key] = u[key]
    if kind == "tool_call":
        out["toolKind"] = u.get("kind")
        out["locations"] = u.get("locations")
        out["rawInput"] = json.dumps(u.get("rawInput"))[:300]
    if "content" in u:
        c = u["content"]
        if isinstance(c, dict):
            out["text"] = (c.get("text") or "")[:160]
        elif isinstance(c, list):
            out["content"] = json.dumps(c)[:300]
    if kind == "config_option_update":
        out["options"] = [o.get("currentValue") for o in u.get("configOptions", [])]
    extra = set(u) - {"sessionUpdate", "messageId", "toolCallId", "title", "status", "kind", "rawInput", "content",
                      "used", "size", "configOptions", "locations"}
    if extra:
        out["other"] = sorted(extra)
    return out


def prompt(d, sid, text, cancel_when=None, cancel_after=0.0, timeout=240, cancel_with="cancel"):
    """Send one prompt; collect updates (compacted, with seconds since the prompt) until its answer.

    cancel_when(update) -> True sends session/cancel (or, with cancel_with="close", session/close)
    cancel_after seconds after that update."""
    def stop():
        if cancel_with == "close":
            d.next_id += 1
            q = queue.Queue()
            d.pending[d.next_id] = q
            d.send({"jsonrpc": "2.0", "id": d.next_id, "method": "session/close", "params": {"sessionId": sid}})
            threading.Thread(target=lambda: note(f"   close answered: {summary(q.get(timeout=60))}"), daemon=True).start()
        else:
            d.notify("session/cancel", {"sessionId": sid})
    d.next_id += 1
    rid = d.next_id
    q = queue.Queue()
    d.pending[rid] = q
    t0 = time.monotonic()
    d.send({"jsonrpc": "2.0", "id": rid, "method": "session/prompt",
            "params": {"sessionId": sid, "prompt": [{"type": "text", "text": text}]}})
    seen, cancelled_at, other = [], None, []
    while True:
        try:
            m = q.get_nowait()
            break
        except queue.Empty:
            pass
        if time.monotonic() - t0 > timeout:
            m = {"timeout": True}
            break
        try:
            kind, ev = d.events.get(timeout=0.2)
        except queue.Empty:
            continue
        at = round(time.monotonic() - t0, 2)
        if kind == "notification" and ev.get("method") == "session/update":
            c = compact(ev["params"], at)
            seen.append(c)
            if cancel_when and cancelled_at is None and cancel_when(c):
                time.sleep(cancel_after)
                stop()
                cancelled_at = round(time.monotonic() - t0, 2)
        elif kind == "request":
            other.append({"at": at, "request": ev["method"], "params": json.dumps(ev["params"])[:400]})
            if cancel_when and cancelled_at is None and cancel_when({"at": at, "kind": "request", "method": ev["method"]}):
                time.sleep(cancel_after)
                stop()
                cancelled_at = round(time.monotonic() - t0, 2)
        else:
            other.append({"at": at, kind: ev})
    took = round(time.monotonic() - t0, 2)
    note(f"prompt {text[:70]!r}: {took}s, cancel sent at {cancelled_at}: {summary(m) if 'timeout' not in m else 'TIMEOUT'}")
    for c in seen:
        note(f"   {json.dumps(c)}")
    for o in other:
        note(f"   {json.dumps(o)[:500]}")
    if "error" in m and ("no api key" in json.dumps(m).lower() or "401" in json.dumps(m)):
        note("STOP: the turn failed for want of a usable DeepSeek key in this DSH home")
        raise SystemExit(2)
    late = d.updates(1.0)
    for kind, ev in late:
        if kind == "notification" and ev.get("method") == "session/update":
            note(f"   after the answer: {json.dumps(compact(ev['params'], 'late'))}")
        else:
            note(f"   after the answer: {kind} {json.dumps(ev)[:300]}")
    return m, seen


def read_title(sid):
    """The title row of DSH's own projection cache for a probe session (reading only)."""
    path = os.path.join(home, "storages", "session_projcache", "sessions", f"{sid}.json")
    try:
        rows = json.load(open(path, encoding="utf-8"))["record"]["rows"]
        return rows.get("title", {}).get("val"), rows.get("sandboxMode", {}).get("val"), rows.get("approvalState", {}).get("val")
    except (OSError, KeyError, ValueError) as e:
        return f"unreadable: {e}", None, None


def check_basics():
    """Probes 8, 11, 12, 13, 15 in one session, launched with overlays/basics.yml (persona suffix with a
    probe word, model titles back on): the probe word and the tool list; an MCP call and the ask tool;
    a subagent. Then the title, if any, in DSH's projection cache."""
    d = Dsh("basics", ["--patch", os.path.join(overlays, "basics.yml")])
    d.initialize()
    cwd = workspace("basics")
    sid = live_session(d, cwd, "basics: probe word, tools, MCP, ask tool, subagent, title")
    if not sid:
        return
    d.permission = lambda p: ALLOW
    prompt(d, sid, "What is the probe word? Answer it, then on a new line list the exact names of all tools you can call, comma-separated. No other text.")
    prompt(d, sid, "Call the tool mcp__gorynych__probe_echo once. Then, if you have a tool for asking the user a question, use it to ask me to pick red or blue; if you have none, say NO-ASK-TOOL. Keep it short.")
    prompt(d, sid, "Use your subagent tool to start one child agent whose only task is to reply with the word OK. Then tell me in one word what it said.")
    time.sleep(5)
    note(f"basics: permission requests: {len(d.asked)}; title/sandbox/approval rows: {read_title(sid)}")
    m, _ = d.request("session/list", {"cwd": cwd})
    note(f"basics: list while open: {summary(m)}")
    d.request("session/close", {"sessionId": sid})
    m, _ = d.request("session/list", {"cwd": cwd})
    note(f"basics: list after close (children left out?): {summary(m)}")
    d.stop()
    with open(os.path.join(scratch, "basics_session.txt"), "w") as f:
        f.write(f"{sid}\n{cwd}")


def check_plain():
    """Probe 12, second half: a launch without the overlay resumes the basics session and asks for the probe word."""
    sid, cwd = open(os.path.join(scratch, "basics_session.txt")).read().split("\n")
    d = Dsh("plain")
    d.initialize()
    m, took = d.request("session/resume", {"sessionId": sid, "cwd": cwd, "mcpServers": [gorynych_server()]})
    note(f"plain: resume {took:.2f}s: {'ok' if 'result' in m else summary(m)}")
    prompt(d, sid, "What is the probe word? One word; say UNKNOWN if you were not told one.")
    note(f"plain: title/sandbox/approval rows: {read_title(sid)}")
    d.request("session/close", {"sessionId": sid})
    d.stop()


def check_default():
    """Probes 8, 9 and sandbox (a): the default sandbox (workspace-write, ask) in a scratch tree: a pwsh
    write and a file-tool write, with the tree's ACL and label before and after; then a cancel during a
    long pwsh command, the processes left, and a turn after the cancel."""
    cwd = workspace("sbx-default", tree=True)
    for p in (cwd, os.path.join(cwd, "sub"), os.path.join(cwd, "sub", "inner")):
        show_acl("default: before", p)
    d = Dsh("default")
    d.initialize()
    sid = live_session(d, cwd, "default sandbox: writes, ACL, cancel")
    if not sid:
        return
    d.permission = lambda p: ALLOW
    t = time.monotonic()
    prompt(d, sid, "Run this with your pwsh tool: Set-Content -Path probe.txt -Value x . Then create probe2.txt containing y with your file-writing tool. Reply DONE.")
    note(f"default: permission requests: {len(d.asked)}")
    for p in (cwd, os.path.join(cwd, "sub"), os.path.join(cwd, "sub", "inner"), os.path.join(cwd, "sub", "inner", "file.txt")):
        show_acl("default: after", p)
    note(f"default: files: {sorted(os.listdir(cwd))}")
    before = {p for p, _, _ in descendants(d.proc.pid)}
    prompt(d, sid, "Run this with your pwsh tool and wait for it to finish: Start-Sleep -Seconds 60",
           cancel_when=lambda c: c["kind"] == "tool_call", cancel_after=2.0, timeout=120)
    time.sleep(2)
    left = [(p, e) for p, e, _ in descendants(d.proc.pid) if p not in before]
    note(f"default: processes under node 2s after the cancelled turn: {left}")
    prompt(d, sid, "Reply with the single word OK.")
    note(f"default: title/sandbox/approval rows: {read_title(sid)}")
    prompt(d, sid, "Run this with your pwsh tool and wait for it to finish: Start-Sleep -Seconds 60",
           cancel_when=lambda c: c["kind"] == "tool_call", cancel_after=2.0, timeout=120, cancel_with="close")
    time.sleep(2)
    note(f"default: processes under node after a close during a turn: {[(p, e) for p, e, _ in descendants(d.proc.pid) if p not in before]}")
    d.stop()
    tmp = os.environ.get("TEMP", "")
    note(f"default: dsh-like folders left in %TEMP%: {[n for n in os.listdir(tmp) if n.lower().startswith('dsh')][:10]}")


def check_readonly():
    """Probe 10 and sandbox (c): new sessions in read-only (overlays/read-only.yml). A pwsh write is
    denied and escalated: reject it. A file-tool write: hold the question and cancel the turn, to see
    whether DSH withdraws it ($/cancel_request), then answer cancelled. A pwsh write: allow it, and look
    at the tree's ACL after the approved escalation."""
    cwd = workspace("sbx-readonly", tree=True)
    show_acl("readonly: before", cwd)
    d = Dsh("readonly", ["--patch", os.path.join(overlays, "read-only.yml")])
    d.initialize()
    sid = live_session(d, cwd, "read-only sandbox: permission reject, withdraw, allow")
    if not sid:
        return
    d.permission = lambda p: REJECT
    prompt(d, sid, "Run this with your pwsh tool: Set-Content -Path probe.txt -Value x . If the sandbox denies it, retry it once as the tool result tells you. Then say what happened in one line.")
    note(f"readonly: questions so far: {len(d.asked)}; files: {sorted(os.listdir(cwd))}")
    d.permission = lambda p: None
    prompt(d, sid, "Create probe2.txt containing y with your file-writing tool. If the sandbox denies it, retry once as the result tells you.",
           cancel_when=lambda c: c["kind"] == "request", cancel_after=2.0, timeout=120)
    for rid in list(d.held):
        d.send({"jsonrpc": "2.0", "id": rid, "result": CANCELLED})
    d.held.clear()
    note(f"readonly: questions so far: {len(d.asked)}; files: {sorted(os.listdir(cwd))}")
    d.permission = lambda p: ALLOW
    prompt(d, sid, "Run this with your pwsh tool: Set-Content -Path probe.txt -Value x . If the sandbox denies it, retry it once as the tool result tells you. Reply DONE.")
    note(f"readonly: questions: {len(d.asked)}; files: {sorted(os.listdir(cwd))}")
    for at, q in d.asked:
        note(f"readonly: question at {at}: {json.dumps(q['params'])[:500]}")
    for p in (cwd, os.path.join(cwd, "sub")):
        show_acl("readonly: after", p)
    note(f"readonly: title/sandbox/approval rows: {read_title(sid)}")
    d.request("session/close", {"sessionId": sid})
    d.stop()


def check_off():
    """Sandbox (b): overlays/sandbox-off.yml (danger-full-access, approvals ask): does anything ask before
    a pwsh write, a file-tool write or a delete? And the tree's ACL stays as it was."""
    cwd = workspace("sbx-off", tree=True)
    show_acl("off: before", cwd)
    d = Dsh("off", ["--patch", os.path.join(overlays, "sandbox-off.yml")])
    d.initialize()
    sid = live_session(d, cwd, "sandbox off with approvals ask")
    if not sid:
        return
    d.permission = lambda p: ALLOW
    prompt(d, sid, "Run this with your pwsh tool: Set-Content -Path probe.txt -Value x . Then create probe2.txt containing y with your file-writing tool. Then run with pwsh: Remove-Item probe.txt . Reply DONE.")
    note(f"off: permission requests: {len(d.asked)}; files: {sorted(os.listdir(cwd))}")
    for p in (cwd, os.path.join(cwd, "sub")):
        show_acl("off: after", p)
    note(f"off: title/sandbox/approval rows: {read_title(sid)}")
    d.request("session/close", {"sessionId": sid})
    d.stop()


CHECKS = {
    "boot": check_boot,
    "sessions": check_sessions,
    "other": check_other_process,
    "kill": check_kill,
    "cost": check_cost,
    "basics": check_basics,
    "plain": check_plain,
    "default": check_default,
    "readonly": check_readonly,
    "off": check_off,
}

for name in checks:
    note(f"=== {name}")
    CHECKS[name]()
