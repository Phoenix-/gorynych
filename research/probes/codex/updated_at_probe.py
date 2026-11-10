"""Probe: does thread/list's updatedAt move when a turn ends (Codex 0.167), in the app-server that ran
it and in another one?

Usage: python -I updated_at_probe.py <codex.exe> <scratch folder> <log file>

A starts a thread in the scratch folder and runs two short turns a few seconds apart. After each,
A and a second app-server B list it (thread/list, sortKey updated_at, with and without
useStateDbOnly), and the probe logs its updatedAt. The thread is deleted at the end; only codex
itself writes under ~/.codex.
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
log = open(log_path, "a", encoding="utf-8")


def note(text):
    print(text, flush=True)
    log.write(f"### {text}\n")
    log.flush()


class Server:
    def __init__(self, name):
        self.name, self.next_id, self.pending, self.notes = name, 0, {}, queue.Queue()
        self.proc = subprocess.Popen([exe, "app-server", "-c", "thread_unload_delay_secs=0"], cwd=ws, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        threading.Thread(target=self.read, daemon=True).start()
        self.call("initialize", {"clientInfo": {"name": "gorynych_probe", "title": "Gorynych probe", "version": "0.0.1"},
                                 "capabilities": {"experimentalApi": True}})
        self.send({"method": "initialized", "params": {}})

    def read(self):
        for line in self.proc.stdout:
            log.write(f"IN [{self.name}] {line}")
            m = json.loads(line)
            if "method" in m and "id" in m:
                self.send({"id": m["id"], "error": {"code": -32601, "message": "probe: no approvals"}})
            elif "method" in m:
                self.notes.put(m)
            elif m.get("id") in self.pending:
                self.pending.pop(m["id"]).put(m)

    def send(self, m):
        log.write(f"OUT[{self.name}] {json.dumps(m)}\n")
        self.proc.stdin.write(json.dumps(m) + "\n")
        self.proc.stdin.flush()

    def call(self, method, params, timeout=120):
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

    def stop(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=30)


def updated(server, tid, state_db_only):
    listed = server.call("thread/list", {"limit": 30, "sortKey": "updated_at", "useStateDbOnly": state_db_only, "cwd": ws})
    row = next((t for t in (listed.get("data") or []) if t.get("id") == tid), None)
    return row.get("updatedAt") if row else ("error" if "__error" in listed else "not listed")


a = Server("A")
b = Server("B")
tid = a.call("thread/start", {"cwd": ws, "approvalPolicy": "untrusted", "sandbox": "read-only"})["thread"]["id"]
note(f"thread {tid} started at {int(time.time())}")
try:
    for n in (1, 2):
        if n == 2:
            time.sleep(5)
        a.call("turn/start", {"threadId": tid, "effort": "low", "input": [{"type": "text", "text": f"Reply with exactly the word {n} and nothing else."}]})
        done = a.until("turn/completed")
        now = int(time.time())
        note(f"turn {n} {(done or {}).get('turn', {}).get('status')} at {now}: "
             f"A db-only {updated(a, tid, True)}, A full {updated(a, tid, False)}, B db-only {updated(b, tid, True)}, B full {updated(b, tid, False)}")
finally:
    note(f"thread/delete -> {a.call('thread/delete', {'threadId': tid})}")
    for s in (b, a):
        s.stop()
