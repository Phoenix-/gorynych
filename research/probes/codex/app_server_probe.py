"""Probe of `codex app-server` (Codex 0.167) for the Codex adapter: threads, turns, approvals, MCP, history.

Usage: python app_server_probe.py <codex.exe> <scratch folder> <log file> [<thread id>]

With a thread id it skips thread/start and the first five turns and resumes that thread
instead (without the per-thread config, to see what the thread kept).

Runs about six short model turns (lowest reasoning effort) in <scratch folder>/ws with the user's
own login, gives the thread a throwaway MCP server (../outside/probe_server.py) and developer
instructions per thread (`config` and `developerInstructions` in thread/start), never touching
~/.codex. Prints a summary; the full wire log goes to <log file>, which may hold the host's
details and is not kept. Deletes the probe thread at the end.
"""
import json
import os
import queue
import subprocess
import sys
import threading
import time

exe, scratch, log_path = sys.argv[1:4]
existing = sys.argv[4] if len(sys.argv) > 4 else None
ws = os.path.join(scratch, "ws")
os.makedirs(ws, exist_ok=True)
server_log = os.path.join(scratch, "mcp_server.jsonl")
probe_server = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "outside", "probe_server.py")
log = open(log_path, "a", encoding="utf-8")


def note(text):
    print(text, flush=True)
    log.write(f"### {text}\n")
    log.flush()


class AppServer:
    """One `codex app-server` over stdio: requests, notifications, and server requests answered by a policy."""

    def __init__(self, name):
        self.name = name
        self.proc = subprocess.Popen([exe, "app-server"], cwd=ws, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        self.next_id = 0
        self.pending = {}
        self.events = queue.Queue()  # notifications and server requests, in order
        self.answers = {}  # method -> function(params) -> result
        threading.Thread(target=self.read, daemon=True).start()
        threading.Thread(target=self.drain_stderr, daemon=True).start()

    def drain_stderr(self):
        for line in self.proc.stderr:
            log.write(f"ERR[{self.name}] {line}")

    def read(self):
        for line in self.proc.stdout:
            log.write(f"IN [{self.name}] {line}")
            log.flush()
            try:
                m = json.loads(line)
            except json.JSONDecodeError:
                continue
            if "method" in m and "id" in m:
                self.events.put(("request", m))
                answer = self.answers.get(m["method"])
                if answer is None:
                    self.write({"id": m["id"], "error": {"code": -32601, "message": "probe: not handled"}})
                else:
                    self.write({"id": m["id"], "result": answer(m["params"])})
            elif "method" in m:
                self.events.put(("note", m))
            elif m.get("id") in self.pending:
                self.pending.pop(m["id"]).put(m)

    def write(self, m):
        line = json.dumps(m)
        log.write(f"OUT[{self.name}] {line}\n")
        self.proc.stdin.write(line + "\n")
        self.proc.stdin.flush()

    def call(self, method, params=None, timeout=60):
        self.next_id += 1
        box = queue.Queue()
        self.pending[self.next_id] = box
        self.write({"id": self.next_id, "method": method, "params": params or {}})
        m = box.get(timeout=timeout)
        if "error" in m:
            return {"__error": m["error"]}
        return m["result"]

    def notify(self, method, params=None):
        self.write({"method": method, "params": params or {}})

    def drain(self):
        out = []
        while not self.events.empty():
            out.append(self.events.get())
        return out

    def wait_turn(self, thread_id, timeout=180, on_event=None):
        """Events until turn/completed for the thread; returns (events, completed turn)."""
        seen = []
        end = time.time() + timeout
        while time.time() < end:
            try:
                kind, m = self.events.get(timeout=1)
            except queue.Empty:
                continue
            seen.append((kind, m))
            if on_event:
                on_event(kind, m)
            if kind == "note" and m["method"] == "turn/completed" and m["params"]["threadId"] == thread_id:
                return seen, m["params"]["turn"]
        return seen, None

    def stop(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


def initialize(server):
    r = server.call("initialize", {"clientInfo": {"name": "gorynych_probe", "title": "Gorynych probe", "version": "0.0.1"},
                                   "capabilities": {"experimentalApi": True}})
    server.notify("initialized")
    return r


def methods(events):
    """The sequence of notification and request methods, runs of the same one collapsed."""
    out = []
    for kind, m in events:
        name = m["method"] + ("?" if kind == "request" else "")
        if m["method"] in ("item/started", "item/completed"):
            name += f"({m['params']['item']['type']})"
        if out and out[-1][0] == name:
            out[-1][1] += 1
        else:
            out.append([name, 1])
    return " ".join(n if c == 1 else f"{n}x{c}" for n, c in out)


def items_of(turn):
    return [(i["type"], {k: i.get(k) for k in ("status", "phase", "text", "command", "exitCode", "aggregatedOutput", "server", "tool", "changes")
                         if i.get(k) not in (None, [], "")}) for i in turn.get("items", [])]


def short(x, n=600):
    s = json.dumps(x, ensure_ascii=False)
    return s if len(s) <= n else s[:n] + "…"


a = AppServer("A")
init = initialize(a)
note(f"initialize -> {short({k: v for k, v in init.items() if k != 'codexHome'})}")

cfg = a.call("config/read", {"cwd": ws})
mcp = (cfg.get("config") or {}).get("mcp_servers") or {}
note(f"the user's own MCP servers seen by app-server (names only): {sorted(mcp)}")

models = a.call("model/list", {})
default = next((m for m in models.get("data", []) if m.get("isDefault")), (models.get("data") or [{}])[0])
efforts = [e.get("reasoningEffort") for e in default.get("supportedReasoningEfforts", [])]
note(f"model/list: {[m['id'] for m in models.get('data', [])]}; default {default.get('id')}, efforts {efforts}, default effort {default.get('defaultReasoningEffort')}")
effort = efforts[0] if efforts else None

DEV = "When the user's whole message is the word PING, answer with exactly PONG-7319 and nothing else."


def step(label, fn):
    try:
        fn()
    except Exception as e:  # a probe goes on to clean up whatever one step does
        note(f"{label}: failed: {type(e).__name__}: {e}")


if existing:
    tid = existing
    res = a.call("thread/resume", {"threadId": tid, "excludeTurns": True})
    note(f"A thread/resume (no config) -> model {res.get('model')}, approval {res.get('approvalPolicy')}, "
         f"sandbox {short(res.get('sandbox'), 100)}, status {res.get('thread', {}).get('status')}, keys {sorted(res)}")
    st = a.call("mcpServerStatus/list", {"threadId": tid})
    note("A after resume, gorynych_probe: " + short([(x.get("name"), x.get("runtimeStatus")) for x in st.get("data", []) if x.get("name") == "gorynych_probe"]))
start = {"__skip": True} if existing else a.call("thread/start", {
    "cwd": ws,
    "approvalPolicy": "untrusted",
    "sandbox": "read-only",
    "developerInstructions": DEV,
    "config": {"mcp_servers": {"gorynych_probe": {
        "command": sys.executable, "args": [os.path.abspath(probe_server), "--name", "gorynych_probe", "--log", server_log]}}},
})
if "__error" in start:
    note(f"thread/start failed: {start}")
    sys.exit(1)
if not existing:
    thread = start["thread"]
    tid = thread["id"]
    note(f"thread/start -> id {tid}, model {start['model']}, effort {start.get('reasoningEffort')}, approval {start['approvalPolicy']}, "
     f"sandbox {short(start['sandbox'], 120)}, source {thread.get('source')}, originator {thread.get('originator')}, status {thread.get('status')}")
    note(f"events after thread/start: {methods(a.drain())}")
    status = a.call("mcpServerStatus/list", {"threadId": tid})
    note("mcpServerStatus/list: " + short([(x.get("name"), x.get("runtimeStatus"), len(x.get("tools") or {})) for x in status.get("data", [])], 900))


def turn(server, text, label, answers=None, interrupt_on=None, client_id=None):
    server.answers = answers or {}
    r = server.call("turn/start", {"threadId": tid, "input": [{"type": "text", "text": text}], **({"effort": effort} if effort else {}),
                                   **({"clientUserMessageId": client_id} if client_id else {})})
    if "__error" in r:
        note(f"{label}: turn/start failed {r}")
        return None
    turn_id = r["turn"]["id"]

    def on_event(kind, m):
        if interrupt_on and interrupt_on(kind, m):
            note(f"{label}: interrupting on {m['method']}")
            server.call("turn/interrupt", {"threadId": tid, "turnId": turn_id})

    events, done = server.wait_turn(tid, on_event=on_event)
    note(f"{label}: {methods(events)}")
    for kind, m in events:
        if kind == "request":
            note(f"{label}: server request {m['method']} {short(m['params'], 700)}")
        if m["method"] in ("turn/plan/updated", "thread/tokenUsage/updated", "serverRequest/resolved", "thread/status/changed", "error", "warning"):
            note(f"{label}: {m['method']} {short(m['params'], 400)}")
    if done:
        note(f"{label}: status {done['status']} error {done.get('error')} items {short(items_of(done), 900)}")
        if not done.get("items"):
            completed = [m["params"]["item"] for k, m in events if k == "note" and m["method"] == "item/completed"]
            note(f"{label}: completed items {short([(i['type'], i.get('status'), i.get('text') or i.get('command') or i.get('tool')) for i in completed], 900)}")
    deltas = [m for k, m in events if k == "note" and m["method"] == "item/agentMessage/delta"]
    note(f"{label}: {len(deltas)} agentMessage deltas, item ids {sorted({m['params']['itemId'] for m in deltas})}")
    return done


deleted = False


def cleanup():
    """Whatever failed on the way, the probe thread does not stay behind."""
    if deleted:
        return
    try:
        note(f"cleanup: thread/delete -> {a.call('thread/delete', {'threadId': tid}, timeout=30)}")
    except Exception as e:
        note(f"cleanup failed, delete {tid} by hand: {e}")


import atexit
atexit.register(cleanup)

accept = lambda p: {"decision": "accept"}
decline = lambda p: {"decision": "decline"}
elicit = lambda p: {"action": "accept", "content": {}}

if not existing:
  turn(a, "PING", "turn 1 (developer instructions)")
  turn(a, "Run exactly this shell command and tell me its output: python -c \"print('probe-ok')\"", "turn 2 (exec approval)",
     {"item/commandExecution/requestApproval": accept, "execCommandApproval": lambda p: {"decision": "approved"}})
  turn(a, "Create a file named note.txt that contains the word hi.", "turn 3 (patch approval, declined)",
     {"item/fileChange/requestApproval": decline, "applyPatchApproval": lambda p: {"decision": "denied"},
      "item/commandExecution/requestApproval": decline})
  turn(a, "Call the probe_echo tool of the gorynych_probe MCP server once and tell me what it returned.", "turn 4 (MCP tool)",
     {"mcpServer/elicitation/request": elicit, "item/commandExecution/requestApproval": decline})
  turn(a, "Run exactly this shell command: python -c \"import time; time.sleep(60)\"", "turn 5 (interrupt at the approval)",
     {}, interrupt_on=lambda kind, m: kind == "request" and m["method"] == "item/commandExecution/requestApproval")

r = a.call("thread/name/set", {"threadId": tid, "name": "Gorynych probe"})
time.sleep(0.5)
note(f"thread/name/set -> {r}; events {methods(a.drain())}")

read = a.call("thread/read", {"threadId": tid, "includeTurns": True})
t = read["thread"]
note(f"thread/read: name {t.get('name')!r} preview {t.get('preview')!r} source {t.get('source')} originator {t.get('originator')} "
     f"status {t.get('status')} turns {len(t['turns'])}")
for i, tr in enumerate(t["turns"]):
    note(f"  turn {i}: {tr['status']} items {[(it['type'], it.get('id'), it.get('clientId')) for it in tr['items']]}")
turns_page = a.call("thread/turns/list", {"threadId": tid, "limit": 2})
note(f"thread/turns/list limit 2: {len(turns_page.get('data', []))} turns, nextCursor {bool(turns_page.get('nextCursor'))}, keys {sorted(turns_page)}")
items_page = a.call("thread/items/list", {"threadId": tid, "limit": 4})
note(f"thread/items/list limit 4: {short([(i['item']['type'], i['item'].get('id'), i.get('turnId')) for i in items_page.get('data', [])])}, keys {sorted(items_page)}")

page1 = a.call("thread/list", {"cwd": ws, "limit": 1})
note(f"thread/list cwd limit 1: {[(x['id'], x.get('name'), x.get('source')) for x in page1.get('data', [])]} nextCursor {bool(page1.get('nextCursor'))}")
mine = a.call("thread/list", {"cwd": ws, "sourceKinds": ["appServer"]})
note(f"thread/list cwd sourceKinds appServer: {[x['id'] for x in mine.get('data', [])]}")
loaded = a.call("thread/loaded/list", {})
note(f"thread/loaded/list: {short(loaded)}")

# A second app-server process: does it see the first one's thread live, and keep the per-thread config?
b = AppServer("B")
initialize(b)
resumed_a = a.call("thread/resume", {"threadId": tid, "excludeTurns": True})
resumed_b = b.call("thread/resume", {"threadId": tid, "excludeTurns": True})
note(f"B thread/resume -> {short({k: resumed_b.get(k) for k in ('model', 'approvalPolicy', 'reasoningEffort', 'cwd')})}, status {resumed_b.get('thread', {}).get('status')}")
status_b = b.call("mcpServerStatus/list", {"threadId": tid})
note("B mcpServerStatus/list: " + short([(s.get("name"), s.get("runtimeStatus")) for s in status_b.get("data", [])], 600))
a.drain()
turn(b, "PING", "turn 6 (in B: developer instructions kept on resume?)", client_id="gorynych-msg-probe-1")
last = b.call("thread/turns/list", {"threadId": tid, "limit": 1, "itemsView": "full"})
note(f"B last turn's user message clientId: {[(i['type'], i.get('clientId')) for t2 in last.get('data', []) for i in t2.get('items', [])]}")
time.sleep(1)
note(f"A saw while B ran a turn: {methods(a.drain()) or 'nothing'}")

uns = a.call("thread/unsubscribe", {"threadId": tid})
time.sleep(0.5)
note(f"A thread/unsubscribe -> {uns}; events {methods(a.drain())}; loaded {short(a.call('thread/loaded/list', {}))}")
b.stop()

r = a.call("thread/archive", {"threadId": tid})
note(f"thread/archive -> {r}; events {methods(a.drain())}")
arch = a.call("thread/list", {"cwd": ws, "archived": True})
note(f"thread/list archived: {[x['id'] for x in arch.get('data', [])]}")
r = a.call("thread/unarchive", {"threadId": tid})
note(f"thread/unarchive -> {short(r, 200)}; events {methods(a.drain())}")
r = a.call("thread/delete", {"threadId": tid})
deleted = "__error" not in r
time.sleep(0.5)
note(f"thread/delete -> {r}; events {methods(a.drain())}")
gone = a.call("thread/read", {"threadId": tid})
note(f"thread/read after delete -> {short(gone, 300)}")
left = a.call("thread/list", {"cwd": ws}), a.call("thread/list", {"cwd": ws, "archived": True})
note(f"left in the scratch folder: {[x['id'] for x in left[0].get('data', [])]} archived {[x['id'] for x in left[1].get('data', [])]}")

if os.path.exists(server_log):
    for line in open(server_log, encoding="utf-8"):
        e = json.loads(line)
        if e["kind"] == "start":
            codex_vars = [k for k in e.get("env_keys", []) if k.upper().startswith("CODEX")]
            note(f"MCP server start: cwd {e['cwd']}, ancestors {short(e.get('ancestors'), 500)}, CODEX_* variables {codex_vars}")
        elif e["kind"] in ("initialize", "tools/call"):
            note(f"MCP server {e['kind']}: {short(e.get('params'), 500)}")
a.stop()
