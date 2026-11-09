"""Probe: how long a thread's writer lock outlives thread/unsubscribe (Codex 0.167), and who holds it.

Usage: python writer_lock_probe.py <codex.exe> <scratch folder> <log file> [scenario ...]

Scenarios (each a new thread in the scratch folder, one short turn at low effort):
  plain  - thread/start, a turn, thread/unsubscribe.
  named  - the same, with thread/name/set before the turn (no name for Codex to generate).
  mcp    - the same as plain, with an MCP server given per thread, as Gorynych gives its own.
A scenario may end in @<n>: the first app-server is then started with
`-c thread_unload_delay_secs=<n>`, Codex's delay before it unloads a thread nobody subscribes to.
After the unsubscribe it logs, every second for up to 120 s: the notifications that came, whether
the thread is still in thread/loaded/list, and the processes that have its writer-lock file open,
read without taking the lock (NtQueryInformationFile, class 47). A second app-server tries
thread/resume every two seconds and notes when it gets the thread. Deletes each thread at the
end; never writes ~/.codex.
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
scenarios = sys.argv[4:] or ["plain", "named", "mcp"]
ws = os.path.join(scratch, "ws")
os.makedirs(ws, exist_ok=True)
probe_server = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "outside", "probe_server.py")
locks = os.path.join(os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex"), "thread-writer-locks")
log = open(log_path, "a", encoding="utf-8")


def note(text):
    print(text, flush=True)
    log.write(f"### {text}\n")
    log.flush()


# ---------------------------------------------------------------- who has the lock file open (read-only)

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll")
k32.CreateFileW.restype = wt.HANDLE
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
k32.OpenProcess.restype = wt.HANDLE
k32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]


class IoStatus(ctypes.Structure):
    _fields_ = [("Status", ctypes.c_void_p), ("Information", ctypes.c_void_p)]


def holders(path):
    """The processes that have the file open, as (pid, image); None when it cannot be told."""
    h = k32.CreateFileW(path, 0x80, 7, None, 3, 0, None)  # FILE_READ_ATTRIBUTES, share all, OPEN_EXISTING
    if h in (None, wt.HANDLE(-1).value):
        return None
    try:
        buf = ctypes.create_string_buffer(64 * 1024)
        io = IoStatus()
        if ntdll.NtQueryInformationFile(h, ctypes.byref(io), buf, len(buf), 47) != 0:
            return None
        count = ctypes.c_ulong.from_buffer(buf, 0).value
        pids = [ctypes.c_size_t.from_buffer(buf, ctypes.sizeof(ctypes.c_size_t) * (1 + i)).value for i in range(count)]
        return [(pid, image(pid)) for pid in pids]
    finally:
        k32.CloseHandle(h)


def image(pid):
    p = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not p:
        return "?"
    try:
        size = wt.DWORD(1024)
        name = ctypes.create_unicode_buffer(1024)
        return os.path.basename(name.value) if k32.QueryFullProcessImageNameW(p, 0, name, ctypes.byref(size)) else "?"
    finally:
        k32.CloseHandle(p)


# ---------------------------------------------------------------- an app-server

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
                self.notes.put((time.time(), m))
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

    def drain(self):
        got = []
        while True:
            try:
                got.append(self.notes.get_nowait())
            except queue.Empty:
                return got

    def until(self, method, timeout=180):
        end = time.time() + timeout
        while time.time() < end:
            try:
                at, m = self.notes.get(timeout=1)
            except queue.Empty:
                continue
            if m["method"] == method:
                return m["params"]
        return None

    def stop(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=30)


def children(pid):
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={pid}' | ForEach-Object {{ \"$($_.ProcessId) $($_.Name)\" }}"],
                         capture_output=True, text=True).stdout.split()
    return " ".join(out)


def run(scenario):
    scenario, _, delay = scenario.partition("@")
    a = Server("A", ["-c", f"thread_unload_delay_secs={delay}"] if delay else [])
    scenario = scenario + (f"@{delay}" if delay else "")
    config = {"mcp_servers": {"gorynych_probe": {"command": sys.executable,
                                                  "args": [os.path.abspath(probe_server), "--name", "gorynych_probe",
                                                           "--log", os.path.join(scratch, "mcp_server.jsonl")]}}} if scenario.startswith("mcp") else None
    params = {"cwd": ws, "approvalPolicy": "untrusted", "sandbox": "read-only"}
    if config:
        params["config"] = config
    tid = a.call("thread/start", params)["thread"]["id"]
    lock = os.path.join(locks, tid + ".lock")
    note(f"[{scenario}] A (pid {a.proc.pid}) thread/start {tid}")
    b = None
    try:
        if scenario.startswith("named"):
            note(f"[{scenario}] thread/name/set -> {a.call('thread/name/set', {'threadId': tid, 'name': 'writer lock probe'})}")
        r = a.call("turn/start", {"threadId": tid, "effort": "low",
                                  "input": [{"type": "text", "text": "Reply with exactly the word pong and nothing else."}]})
        done = a.until("turn/completed")
        note(f"[{scenario}] turn done: {(done or {}).get('turn', {}).get('status')}; holders {holders(lock)}; A's children: {children(a.proc.pid)}")
        a.drain()
        t0 = time.time()
        note(f"[{scenario}] A thread/unsubscribe -> {a.call('thread/unsubscribe', {'threadId': tid})}")
        b = Server("B")
        released = taken = None
        for second in range(121):
            for at, m in a.drain():
                note(f"[{scenario}]   +{at - t0:5.1f}s A notification {m['method']} {json.dumps(m.get('params'))[:160]}")
            loaded = tid in (a.call("thread/loaded/list", {}).get("data") or [])
            held = holders(lock)
            if released is None and taken is None and held == []:
                released = time.time() - t0
            if second % 2 == 0 and taken is None:
                res = b.call("thread/resume", {"threadId": tid, "excludeTurns": True})
                if "__error" not in res:
                    taken = time.time() - t0
            note(f"[{scenario}]   +{time.time() - t0:5.1f}s loaded in A: {loaded}; holders: {held}; B has it: {taken is not None}")
            if taken is not None:
                break
            time.sleep(1)
        note(f"[{scenario}] RESULT: the lock file free after {released if released is not None else 'never (or not seen)'} s; "
             f"B took the thread after {taken if taken is not None else 'more than 120'} s")
    finally:
        for s in (b, a):
            if s is not None:
                r = s.call("thread/delete", {"threadId": tid})
                if "__error" not in r:
                    note(f"[{scenario}] thread/delete -> {r}")
                    break
        for s in (b, a):
            if s is not None:
                s.stop()


for scenario in scenarios:
    run(scenario)
