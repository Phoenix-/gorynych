"""Probe of what a Codex thread keeps across app-server processes (Codex 0.167), in two short turns.

Usage: python resume_probe.py <codex.exe> <scratch folder> <log file>

Process A starts a thread with developer instructions and an MCP server given per thread, runs one
turn with clientUserMessageId, and unsubscribes. Process B then resumes the thread without them and
runs a second turn: do the instructions, the MCP server and the client message IDs survive, and can
B take a thread A has let go of? Deletes the thread at the end; never touches ~/.codex.
"""
import json
import os
import queue
import subprocess
import sys
import threading
import time

exe, scratch, log_path = sys.argv[1:4]
ws = os.path.join(scratch, "ws")
os.makedirs(ws, exist_ok=True)
probe_server = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "outside", "probe_server.py")
log = open(log_path, "a", encoding="utf-8")
DEV = "When the user's whole message is the word PING, answer with exactly PONG-7319 and nothing else."
MCP = {"mcp_servers": {"gorynych_probe": {"command": sys.executable,
                                          "args": [os.path.abspath(probe_server), "--name", "gorynych_probe",
                                                   "--log", os.path.join(scratch, "mcp_server2.jsonl")]}}}


def note(text):
    print(text, flush=True)
    log.write(f"### {text}\n")
    log.flush()


class Server:
    def __init__(self, name):
        self.name, self.next_id, self.pending, self.notes = name, 0, {}, queue.Queue()
        self.proc = subprocess.Popen([exe, "app-server"], cwd=ws, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        threading.Thread(target=self.read, daemon=True).start()
        self.call("initialize", {"clientInfo": {"name": "gorynych_probe", "title": "Gorynych probe", "version": "0.0.1"},
                                 "capabilities": {"experimentalApi": True}})
        self.send({"method": "initialized", "params": {}})

    def read(self):
        for line in self.proc.stdout:
            log.write(f"IN [{self.name}] {line}")
            m = json.loads(line)
            if "method" in m and "id" in m:
                self.send({"id": m["id"], "error": {"code": -32601, "message": "probe: no tools in this probe"}})
            elif "method" in m:
                self.notes.put(m)
            elif m.get("id") in self.pending:
                self.pending.pop(m["id"]).put(m)

    def send(self, m):
        log.write(f"OUT[{self.name}] {json.dumps(m)}\n")
        self.proc.stdin.write(json.dumps(m) + "\n")
        self.proc.stdin.flush()

    def call(self, method, params, timeout=60):
        self.next_id += 1
        box = queue.Queue()
        self.pending[self.next_id] = box
        self.send({"id": self.next_id, "method": method, "params": params})
        m = box.get(timeout=timeout)
        return m.get("result", {"__error": m.get("error")})

    def until(self, method, timeout=180):
        end = time.time() + timeout
        while time.time() < end:
            try:
                m = self.notes.get(timeout=1)
            except queue.Empty:
                continue
            if m["method"] == method:
                return m["params"]
        return None

    def ping(self, tid, client_id):
        r = self.call("turn/start", {"threadId": tid, "effort": "low", "clientUserMessageId": client_id,
                                     "input": [{"type": "text", "text": "PING"}]})
        if "__error" in r:
            return f"turn/start failed: {r['__error']}"
        done = self.until("turn/completed")
        return [i.get("text") for i in (done or {}).get("turn", {}).get("items", []) if i["type"] == "agentMessage"]

    def stop(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=15)


a = Server("A")
start = a.call("thread/start", {"cwd": ws, "approvalPolicy": "untrusted", "sandbox": "read-only",
                                "developerInstructions": DEV, "config": MCP})
tid = start["thread"]["id"]
note(f"A thread/start {tid}")
try:
    note(f"A turn: {a.ping(tid, 'gorynych-msg-1')}")
    note(f"A thread/unsubscribe -> {a.call('thread/unsubscribe', {'threadId': tid})}")
    closed = a.until("thread/closed", timeout=60)
    note(f"A after unsubscribe: thread/closed {closed is not None}; loaded {a.call('thread/loaded/list', {})}")

    b = Server("B")
    taken = b.call("thread/resume", {"threadId": tid, "excludeTurns": True})
    note(f"B thread/resume (A has let go, no config, no instructions) -> {'error ' + str(taken['__error']) if '__error' in taken else 'ok'}")
    st = b.call("mcpServerStatus/list", {"threadId": tid})
    note(f"B gorynych_probe after a resume without config: {[x.get('runtimeStatus') for x in st.get('data', []) if x.get('name') == 'gorynych_probe']}")
    note(f"B turn (instructions kept?): {b.ping(tid, 'gorynych-msg-2')}")
    again = b.call("thread/resume", {"threadId": tid, "excludeTurns": True, "config": MCP, "developerInstructions": DEV})
    st = b.call("mcpServerStatus/list", {"threadId": tid})
    note(f"B resume again while loaded, with config -> {'error ' + str(again['__error']) if '__error' in again else 'ok'}; "
         f"gorynych_probe {[x.get('runtimeStatus') for x in st.get('data', []) if x.get('name') == 'gorynych_probe']}")
    a2 = a.call("thread/resume", {"threadId": tid, "excludeTurns": True})
    note(f"A thread/resume while B holds it -> {a2.get('__error') or 'ok'}")
    read = b.call("thread/read", {"threadId": tid, "includeTurns": True})
    note("B history: " + json.dumps([(i["type"], i.get("clientId"), (i.get("text") or "")[:40])
                                     for t in read["thread"]["turns"] for i in t["items"]], ensure_ascii=False))
finally:
    for s in (locals().get("b"), a):
        if s is not None:
            r = s.call("thread/delete", {"threadId": tid})
            if "__error" not in r:
                note(f"thread/delete -> {r}")
                break
    left = a.call("thread/list", {"cwd": ws}), a.call("thread/list", {"cwd": ws, "archived": True})
    note(f"left in the scratch folder: {[x['id'] for x in left[0].get('data', [])]} archived {[x['id'] for x in left[1].get('data', [])]}")
