"""Probe: can a second app-server read a thread another one holds, without taking it (Codex 0.167)?

Usage: python -I read_held_probe.py <codex.exe> <scratch folder> <log file>

App-server A starts a thread in the scratch folder and runs one short turn; A keeps it loaded, so A
holds its writer lock. App-server B then reads it without thread/resume: thread/list, thread/read,
thread/turns/list and thread/items/list. After each read it logs the processes that have the lock
file open (read without taking the lock: NtQueryInformationFile, class 47) and B's
thread/loaded/list. A runs a second turn while B reads (no conflict expected), B reads again (the
new turn should be there), B's thread/resume is refused (A holds it), and once A unsubscribes
(started with thread_unload_delay_secs=0) B's resume takes it. The thread is deleted at the end.
Only codex itself writes under ~/.codex (the thread's rollout and its deletion).
"""
import ctypes
import ctypes.wintypes as wt
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
locks = os.path.join(os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex"), "thread-writer-locks")
log = open(log_path, "a", encoding="utf-8")


def note(text):
    print(text, flush=True)
    log.write(f"### {text}\n")
    log.flush()


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll")
k32.CreateFileW.restype = wt.HANDLE
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]


class IoStatus(ctypes.Structure):
    _fields_ = [("Status", ctypes.c_void_p), ("Information", ctypes.c_void_p)]


def holders(path):
    """The process IDs that have the file open; None when it cannot be told (no file)."""
    h = k32.CreateFileW(path, 0x80, 7, None, 3, 0, None)  # FILE_READ_ATTRIBUTES, share all, OPEN_EXISTING
    if h in (None, wt.HANDLE(-1).value):
        return None
    try:
        buf = ctypes.create_string_buffer(64 * 1024)
        io = IoStatus()
        if ntdll.NtQueryInformationFile(h, ctypes.byref(io), buf, len(buf), 47) != 0:
            return None
        count = ctypes.c_ulong.from_buffer(buf, 0).value
        return sorted(ctypes.c_size_t.from_buffer(buf, ctypes.sizeof(ctypes.c_size_t) * (1 + i)).value for i in range(count))
    finally:
        k32.CloseHandle(h)


class Server:
    def __init__(self, name, extra=()):
        self.name, self.next_id, self.pending, self.notes = name, 0, {}, queue.Queue()
        self.proc = subprocess.Popen([exe, "app-server", *extra], cwd=ws, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
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
                self.send({"id": m["id"], "error": {"code": -32601, "message": "probe: no approvals in this probe"}})
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


def turn(server, tid, text):
    server.call("turn/start", {"threadId": tid, "effort": "low", "input": [{"type": "text", "text": text}]})
    done = server.until("turn/completed")
    return (done or {}).get("turn", {}).get("status")


a = Server("A", ["-c", "thread_unload_delay_secs=0"])
b = None
tid = a.call("thread/start", {"cwd": ws, "approvalPolicy": "untrusted", "sandbox": "read-only"})["thread"]["id"]
lock = os.path.join(locks, tid + ".lock")
note(f"A (pid {a.proc.pid}) thread/start {tid}")
try:
    note(f"A turn 1: {turn(a, tid, 'Reply with exactly the word one and nothing else.')}; lock held by {holders(lock)}")
    b = Server("B")
    note(f"B started (pid {b.proc.pid})")

    def reads(label):
        listed = b.call("thread/list", {"limit": 20, "sortKey": "updated_at"})
        row = next((t for t in (listed.get("data") or []) if t.get("id") == tid), None)
        note(f"[{label}] B thread/list: {'error ' + json.dumps(listed['__error']) if '__error' in listed else ('row updatedAt ' + str(row.get('updatedAt')) if row else 'not listed')}; "
             f"lock held by {holders(lock)}")
        read = b.call("thread/read", {"threadId": tid})
        note(f"[{label}] B thread/read: {'error ' + json.dumps(read['__error']) if '__error' in read else 'ok, status ' + json.dumps((read.get('thread') or {}).get('status'))}; "
             f"lock held by {holders(lock)}")
        turns = b.call("thread/turns/list", {"threadId": tid, "sortDirection": "desc", "itemsView": "notLoaded", "limit": 50})
        ids = [t.get("id") for t in (turns.get("data") or [])]
        note(f"[{label}] B thread/turns/list: {'error ' + json.dumps(turns['__error']) if '__error' in turns else str(len(ids)) + ' turns, statuses ' + str([t.get('status') for t in turns['data']])}; "
             f"lock held by {holders(lock)}")
        texts = []
        for turn_id in ids:
            items = b.call("thread/items/list", {"threadId": tid, "turnId": turn_id, "limit": 100})
            if "__error" in items:
                note(f"[{label}] B thread/items/list {turn_id}: error {json.dumps(items['__error'])}")
                continue
            texts += [i["item"].get("text") for i in items.get("data") or [] if i.get("item", {}).get("type") == "agentMessage"]
        note(f"[{label}] B thread/items/list: agent messages {texts}; lock held by {holders(lock)}")
        loaded = b.call("thread/loaded/list", {}).get("data") or []
        note(f"[{label}] B thread/loaded/list has it: {tid in loaded}")
        return len(ids)

    first = reads("while A holds it")
    note(f"A turn 2 while B has read it: {turn(a, tid, 'Reply with exactly the word two and nothing else.')}; lock held by {holders(lock)}")
    second = reads("after A's second turn")
    note(f"RESULT fresh: B saw {first} then {second} turns")
    res = b.call("thread/resume", {"threadId": tid, "excludeTurns": True})
    note(f"B thread/resume while A holds it: {'refused ' + json.dumps(res['__error']) if '__error' in res else 'TOOK IT'}")
    note(f"A thread/unsubscribe -> {a.call('thread/unsubscribe', {'threadId': tid})}")
    taken = None
    for i in range(20):
        time.sleep(0.5)
        res = b.call("thread/resume", {"threadId": tid, "excludeTurns": True})
        if "__error" not in res:
            taken = (i + 1) * 0.5
            break
    note(f"RESULT after A let go: B took it after {taken} s; lock held by {holders(lock)} (B is {b.proc.pid})")
finally:
    for s in (b, a):
        if s is not None:
            r = s.call("thread/delete", {"threadId": tid})
            if "__error" not in r:
                note(f"thread/delete -> {r}")
                break
    for s in (b, a):
        if s is not None:
            s.stop()
