"""The build-phase checks of the Codex adapter spec (research/codex-adapter-spec.md), Codex 0.167.

Usage: python build_checks_probe.py <codex.exe> <scratch folder> <log file> [check ...]

Checks (all by default): wire, cost, config, sandbox, name, instructions, unsubscribe, patch,
mcp, reviewer. About seven short turns at effort low, in <scratch folder>/ws; every thread made is
deleted. Never touches ~/.codex, and never answers with an option that would write it.
"""
import json
import os
import queue
import subprocess
import sys
import threading
import time

exe, scratch, log_path = sys.argv[1:4]
only = set(sys.argv[4:])
ws = os.path.join(scratch, "ws")
os.makedirs(ws, exist_ok=True)
probe_server = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "outside", "probe_server.py")
log = open(log_path, "a", encoding="utf-8")
made = []


def note(text):
    print(text, flush=True)
    log.write(f"### {text}\n")
    log.flush()


def mcp(name="gorynych_probe", **extra):
    return {"mcp_servers": {name: {"command": sys.executable, "args": [os.path.abspath(probe_server), "--name", name, "--log",
                                                                       os.path.join(scratch, "mcp_checks.jsonl")], **extra}}}


class Server:
    def __init__(self, name, jsonrpc_field=False):
        self.name, self.next_id, self.pending, self.events = name, 0, {}, queue.Queue()
        self.jsonrpc_field = jsonrpc_field
        self.answers = {}
        t0 = time.time()
        self.proc = subprocess.Popen([exe, "app-server"], cwd=ws, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        threading.Thread(target=self.read, daemon=True).start()
        self.init = self.call("initialize", {"clientInfo": {"name": "gorynych_probe", "title": "Gorynych probe", "version": "0.0.1"}})
        self.started_in = time.time() - t0
        self.send({"method": "initialized", "params": {}})

    def read(self):
        for line in self.proc.stdout:
            log.write(f"IN [{self.name}] {line}")
            m = json.loads(line)
            if "method" in m and "id" in m:
                self.events.put(("request", m))
                answer = self.answers.get(m["method"])
                self.send({"id": m["id"], "result": answer(m["params"])} if answer else
                          {"id": m["id"], "error": {"code": -32601, "message": "probe: not handled"}})
            elif "method" in m:
                self.events.put(("note", m))
            elif m.get("id") in self.pending:
                self.pending.pop(m["id"]).put(m)

    def send(self, m):
        if self.jsonrpc_field:
            m = {"jsonrpc": "2.0", **m}
        log.write(f"OUT[{self.name}] {json.dumps(m)}\n")
        self.proc.stdin.write(json.dumps(m) + "\n")
        self.proc.stdin.flush()

    def call(self, method, params, timeout=90):
        self.next_id += 1
        box = queue.Queue()
        self.pending[self.next_id] = box
        self.send({"id": self.next_id, "method": method, "params": params})
        m = box.get(timeout=timeout)
        return m.get("result", {"__error": m.get("error")})

    def start(self, **params):
        r = self.call("thread/start", {"cwd": ws, **params})
        if "__error" not in r:
            made.append(r["thread"]["id"])
        return r

    def turn(self, tid, text, **params):
        while not self.events.empty():
            self.events.get()
        r = self.call("turn/start", {"threadId": tid, "effort": "low", "input": [{"type": "text", "text": text}], **params})
        if "__error" in r:
            return None, [], r
        events = []
        end = time.time() + 240
        while time.time() < end:
            try:
                kind, m = self.events.get(timeout=1)
            except queue.Empty:
                continue
            events.append((kind, m))
            if kind == "note" and m["method"] == "turn/completed" and m["params"]["threadId"] == tid:
                return m["params"]["turn"], events, r
        return None, events, r

    def stop(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=15)
        except Exception:
            self.proc.kill()


def texts(turn):
    return [i.get("text") for i in (turn or {}).get("items", []) if i["type"] == "agentMessage"]


def kinds(events):
    return [m["method"] + ("?" if k == "request" else "") for k, m in events
            if m["method"] not in ("item/agentMessage/delta", "hook/started", "hook/completed", "account/rateLimits/updated",
                                   "thread/tokenUsage/updated", "turn/moderationMetadata", "mcpServer/startupStatus/updated",
                                   "item/commandExecution/outputDelta", "thread/readState/changed")]


def check(name):
    return not only or name in only


a = Server("A", jsonrpc_field=check("wire"))
try:
    if check("wire"):
        note(f"wire: initialize with a jsonrpc field -> {'ok' if 'userAgent' in a.init else a.init}; started and initialized in {a.started_in:.2f} s")

    if check("cost"):
        r = a.start()
        tid = r["thread"]["id"]
        for i in range(3):
            a.call("thread/unsubscribe", {"threadId": tid})
            time.sleep(0.3)
            t0 = time.time()
            res = a.call("thread/resume", {"threadId": tid, "excludeTurns": True})
            resumed = time.time() - t0
            t1 = time.time()
            st = a.call("mcpServerStatus/list", {"threadId": tid})
            ready = time.time() - t1
            note(f"cost {i}: thread/resume {resumed:.2f} s ({res.get('__error') or 'ok'}); mcpServerStatus/list after it {ready:.2f} s; "
                 f"servers {[(s['name'], s.get('runtimeStatus')) for s in st.get('data', [])]}")
        t0 = time.time()
        b = Server("B")
        note(f"cost: a second app-server process starts and initializes in {b.started_in:.2f} s")
        b.stop()

    if check("config"):
        for label, cfg in [("dotted key", {"mcp_servers.gorynych_none.enabled": False}),
                           ("nested, a server the user does not have", {"mcp_servers": {"gorynych_none": {"enabled": False}}})]:
            r = a.start(config=cfg)
            note(f"config {label}: thread/start -> {r.get('__error') or 'ok'}")
        names = sorted(((a.call("config/read", {"cwd": ws}).get("config") or {}).get("mcp_servers") or {}))
        if names:
            r = a.start(config={f"mcp_servers.{names[0]}.enabled": False})
            st = a.call("mcpServerStatus/list", {"threadId": r["thread"]["id"]}) if "__error" not in r else {}
            note(f"config dotted key on a user's server: {r.get('__error') or 'ok'}; status {[s.get('runtimeStatus') for s in st.get('data', []) if s['name'] == names[0]]}")

    if check("sandbox"):
        note(f"sandbox: windowsSandbox/readiness -> {a.call('windowsSandbox/readiness', {})}")
        r = a.start(sandbox="workspace-write", approvalPolicy="on-request")
        note(f"sandbox: thread/start workspace-write -> {r.get('__error') or ('ok, sandbox ' + json.dumps(r['sandbox']))}")

    if check("name") or check("patch"):
        r = a.start(approvalPolicy="untrusted", sandbox="read-only")
        tid = r["thread"]["id"]
        note(f"name: thread/name/set before the first turn -> {a.call('thread/name/set', {'threadId': tid, 'name': 'Named before its first turn'})}")
        a.answers = {"item/fileChange/requestApproval": lambda p: {"decision": "decline"},
                     "item/commandExecution/requestApproval": lambda p: {"decision": "decline"}}
        turn, events, _ = a.turn(tid, "Use your apply_patch tool, not the shell, to create a file note.txt containing the word hi.")
        note(f"patch: {kinds(events)}")
        for k, m in events:
            if k == "request":
                note(f"patch: request {m['method']} {json.dumps(m['params'])[:900]}")
            if m["method"] in ("item/started", "item/completed") and m["params"]["item"]["type"] == "fileChange":
                note(f"patch: {m['method']} fileChange {json.dumps(m['params']['item'])[:700]}")
        note(f"patch: turn {turn and turn['status']}; answer {texts(turn)}")
        read = a.call("thread/read", {"threadId": tid})
        note(f"name: after the first turn the thread's name is {read['thread'].get('name')!r}")

    if check("instructions"):
        r = a.start(developerInstructions="Your codes are PONG-1111. When asked for your codes, list them.")
        tid = r["thread"]["id"]
        turn, _, _ = a.turn(tid, "Which codes do your developer instructions give you? Answer with the codes only.")
        note(f"instructions at start: {texts(turn)}")
        a.call("thread/unsubscribe", {"threadId": tid})
        time.sleep(0.5)
        while not a.events.empty():
            a.events.get()
        t0 = time.time()
        res = a.call("thread/resume", {"threadId": tid, "excludeTurns": True,
                                       "developerInstructions": "Your codes are PONG-2222. When asked for your codes, list them."})
        resumed = time.time() - t0
        settled, last = {}, t0
        while time.time() - t0 < 60:
            try:
                k, m = a.events.get(timeout=3)
            except queue.Empty:
                break
            if m["method"] == "mcpServer/startupStatus/updated":
                settled[m["params"].get("name")] = m["params"].get("status")
                last = time.time()
        note(f"cost of a load: thread/resume {resumed:.2f} s; the user's MCP servers settled {last - t0:.2f} s after it: {settled}")
        turn, _, _ = a.turn(tid, "Which codes do your developer instructions give you now? Answer with the codes only.")
        note(f"instructions resent on resume ({res.get('__error') or 'ok'}): {texts(turn)}")

    if check("unsubscribe"):
        r = a.start(approvalPolicy="never", sandbox="read-only")
        tid = r["thread"]["id"]
        a.call("turn/start", {"threadId": tid, "effort": "low", "input": [{"type": "text", "text":
               "Run exactly this shell command and then say done: python -c \"import time; time.sleep(25)\""}]})
        end = time.time() + 120
        while time.time() < end:
            k, m = a.events.get(timeout=120)
            if m["method"] == "item/started" and m["params"]["item"]["type"] == "commandExecution":
                break
        note(f"unsubscribe during a running command -> {a.call('thread/unsubscribe', {'threadId': tid})}; "
             f"loaded {a.call('thread/loaded/list', {})}")
        time.sleep(35)
        later = [m["method"] for k, m in list(a.events.queue)]
        note(f"unsubscribe: notifications after it: {sorted(set(later))}; loaded {a.call('thread/loaded/list', {})}")
        res = a.call("thread/resume", {"threadId": tid})
        note(f"unsubscribe: last turn after resume: {[(t['status'], [i['type'] for i in t['items']]) for t in res.get('thread', {}).get('turns', [])[-1:]]}")

    if check("mcp"):
        r = a.start(approvalPolicy="on-request", sandbox="read-only", config=mcp())
        tid = r["thread"]["id"]
        asked = []
        a.answers = {"mcpServer/elicitation/request": lambda p: (asked.append(p.get("_meta", {}).get("tool_name")),
                                                                  {"action": "accept", "content": {}, "_meta": {"persist": "session"}})[1]}
        turn, events, _ = a.turn(tid, "Call the probe_echo tool of the gorynych_probe server, then call it a second time, and say done.")
        note(f"mcp persist session: approvals asked {len(asked)} for {asked}; calls "
             f"{sum(1 for k, m in events if m['method'] == 'item/completed' and m['params']['item']['type'] == 'mcpToolCall')}")
        r = a.start(approvalPolicy="on-request", sandbox="read-only", config=mcp("gorynych_auto", default_tools_approval_mode="approve"))
        if "__error" in r:
            note(f"mcp default_tools_approval_mode: thread/start -> {r['__error']}")
        else:
            asked.clear()
            turn, events, _ = a.turn(r["thread"]["id"], "Call the probe_echo tool of the gorynych_auto server once and say done.")
            note(f"mcp default_tools_approval_mode=approve: approvals asked {len(asked)}; calls "
                 f"{sum(1 for k, m in events if m['method'] == 'item/completed' and m['params']['item']['type'] == 'mcpToolCall')}")

    if check("reviewer"):
        r = a.start(approvalPolicy="on-request", sandbox="read-only", approvalsReviewer="auto_review")
        if "__error" in r:
            note(f"reviewer: thread/start auto_review -> {r['__error']}")
        else:
            note(f"reviewer: thread/start -> approvalsReviewer {r.get('approvalsReviewer')}")
            a.answers = {"item/commandExecution/requestApproval": lambda p: {"decision": "decline"}}
            turn, events, _ = a.turn(r["thread"]["id"], "Run exactly this shell command and tell me its output: python -c \"print(6*7)\"")
            note(f"reviewer: {kinds(events)}")
            for k, m in events:
                if m["method"].startswith("item/autoApprovalReview") or k == "request":
                    note(f"reviewer: {m['method']} {json.dumps(m['params'])[:700]}")
            note(f"reviewer: answer {texts(turn)}")
finally:
    for tid in made:
        r = a.call("thread/delete", {"threadId": tid})
        note(f"delete {tid} -> {r.get('__error') or 'ok'}")
    left = a.call("thread/list", {"cwd": ws}), a.call("thread/list", {"cwd": ws, "archived": True})
    note(f"left: {[x['id'] for x in left[0].get('data', [])]} archived {[x['id'] for x in left[1].get('data', [])]}")
    a.stop()
