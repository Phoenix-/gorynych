"""Probe Claude Code's headless stream-json mode the way a Gorynych adapter would drive it.

Run: python -I probe.py <scenario> [options]. Each run gets a folder under runs/ with
stdout.jsonl (every line from claude), hooks.jsonl (every hook POST) and stderr.txt.
Only per-launch flags are used; no user configuration is touched.
"""
import http.server
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
T0 = time.time()


def log(*parts):
    print(f"[{time.time() - T0:6.2f}]", *parts, flush=True)


# ------------------------------------------------------------------ hooks

class HookState:
    def __init__(self, folder):
        self.file = open(folder / "hooks.jsonl", "a", encoding="utf-8")
        self.lock = threading.Lock()
        self.policy = lambda e: {"behavior": "allow"}
        self.events = []

    def record(self, e):
        with self.lock:
            self.events.append(e)
            self.file.write(json.dumps(e, ensure_ascii=False) + "\n")
            self.file.flush()


def hook_server(state):
    class Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            e = json.loads(body.decode("utf-8"))
            state.record(e)
            name = e.get("hook_event_name")
            brief = {k: e.get(k) for k in ("tool_name", "prompt", "message", "notification_type") if e.get(k) is not None}
            if "prompt" in brief:
                brief["prompt"] = brief["prompt"][:160]
            log("HOOK", name, json.dumps(brief, ensure_ascii=False)[:300])
            reply = {}
            if name == "PermissionRequest":
                decision = state.policy(e)
                log("HOOK  -> decision", json.dumps(decision, ensure_ascii=False)[:300])
                reply = {"hookSpecificOutput": {"hookEventName": "PermissionRequest", "decision": decision}}
            data = json.dumps(reply).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *a):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


HOOK_EVENTS = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
               "Notification", "MessageDisplay", "Stop", "StopFailure", "SessionStart", "SessionEnd"]


def settings_json(url):
    hooks = {}
    for name in HOOK_EVENTS:
        hooks[name] = [{"hooks": [{"type": "http", "url": url, "timeout": 86400 if name == "PermissionRequest" else 30}]}]
    return {"hooks": hooks, "permissions": {"deny": ["mcp__gorynych-outside"]}}


# ------------------------------------------------------------------ claude

def scrubbed_env(extra):
    env = dict(os.environ)
    nested = "CLAUDECODE" in env
    for key in list(env):
        k = key.upper()
        if (k == "CLAUDECODE" or k.startswith("CLAUDE_CODE_") or k.startswith("CLAUDE_AGENT_SDK")
                or k.startswith("CLAUDE_PREVIEW_") or k in ("CLAUDE_PID", "CLAUDE_EFFORT")
                or (nested and k in ("ANTHROPIC_BASE_URL", "MCP_CONNECTION_NONBLOCKING", "MCP_SERVER_CONNECTION_BATCH_SIZE"))):
            del env[key]
    env.update(extra)
    return env


class Claude:
    def __init__(self, folder, workdir, args, env_extra=None):
        self.folder = folder
        self.out = open(folder / "stdout.jsonl", "a", encoding="utf-8")
        self.err = open(folder / "stderr.txt", "ab")
        exe = shutil.which("claude")
        cmd = [exe, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"] + args
        log("LAUNCH", " ".join(cmd[1:]))
        self.p = subprocess.Popen(cmd, cwd=workdir, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.err,
                                  env=scrubbed_env(env_extra or {}))
        self.cond = threading.Condition()
        self.messages = []
        self.responses = {}
        self.results = 0
        self.text = []
        self.on_control = None
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for raw in self.p.stdout:
            line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
            if not line:
                continue
            self.out.write(line + "\n")
            self.out.flush()
            try:
                m = json.loads(line)
            except json.JSONDecodeError:
                log("NONJSON", line[:200])
                continue
            self._summarize(m)
            with self.cond:
                self.messages.append(m)
                if m.get("type") == "control_response":
                    r = m.get("response", {})
                    self.responses[r.get("request_id")] = r
                if m.get("type") == "result":
                    self.results += 1
                self.cond.notify_all()
            if m.get("type") == "control_request" and self.on_control:
                self.on_control(self, m)
        log("EOF on stdout, exit", self.p.wait())
        with self.cond:
            self.cond.notify_all()

    def _summarize(self, m):
        t = m.get("type")
        if t == "stream_event":
            ev = m.get("event", {})
            d = ev.get("delta", {})
            if d.get("type") == "text_delta":
                self.text.append(d.get("text", ""))
            elif d.get("type") == "thinking_delta":
                pass
            elif ev.get("type") in ("message_stop",):
                if self.text:
                    log("TEXT", repr("".join(self.text))[:400])
                    self.text = []
            return
        if t == "assistant":
            parts = []
            for c in m.get("message", {}).get("content", []):
                if c.get("type") == "text":
                    parts.append("text:" + repr(c["text"][:120]))
                elif c.get("type") == "tool_use":
                    parts.append(f"tool_use:{c.get('name')} {json.dumps(c.get('input'), ensure_ascii=False)[:160]}")
                else:
                    parts.append(c.get("type"))
            log("ASSISTANT", m.get("parent_tool_use_id") or "", "; ".join(parts))
        elif t == "user":
            content = m.get("message", {}).get("content")
            if isinstance(content, list):
                kinds = []
                for c in content:
                    if c.get("type") == "tool_result":
                        r = c.get("content")
                        txt = r if isinstance(r, str) else json.dumps(r, ensure_ascii=False)
                        kinds.append(f"tool_result{'(error)' if c.get('is_error') else ''}:{txt[:160]!r}")
                    else:
                        kinds.append(c.get("type") + ":" + repr(str(c.get("text", ""))[:160]))
                log("USER", "; ".join(kinds), {k: m.get(k) for k in ("uuid", "isReplay", "priority", "origin") if m.get(k)})
            else:
                log("USER", repr(str(content)[:200]), {k: m.get(k) for k in ("uuid", "isReplay", "priority", "origin") if m.get(k)})
        elif t == "system":
            sub = m.get("subtype")
            if sub == "init":
                log("SYSTEM init", json.dumps({k: m.get(k) for k in ("session_id", "model", "permissionMode", "apiKeySource", "capabilities", "claude_code_version")}, ensure_ascii=False))
                log("  tools:", ",".join(m.get("tools", [])))
            else:
                log("SYSTEM", sub, json.dumps({k: v for k, v in m.items() if k not in ("type", "subtype", "uuid", "session_id")}, ensure_ascii=False)[:300])
        elif t == "result":
            log("RESULT", json.dumps({k: m.get(k) for k in ("subtype", "is_error", "num_turns", "result", "stop_reason", "terminal_reason", "errors")}, ensure_ascii=False)[:400])
        elif t == "control_request":
            log("CTRL<-", json.dumps(m, ensure_ascii=False)[:500])
        elif t == "control_response":
            log("CTRL->resp", json.dumps(m.get("response"), ensure_ascii=False)[:600])
        else:
            log(t, json.dumps(m, ensure_ascii=False)[:300])

    def send(self, obj):
        data = (json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8")
        self.p.stdin.write(data)
        self.p.stdin.flush()

    def user(self, text, **extra):
        msg = {"type": "user", "message": {"role": "user", "content": text}, "parent_tool_use_id": None}
        msg.update(extra)
        log("SEND user", repr(text[:120]), {k: v for k, v in extra.items()})
        self.send(msg)

    def control(self, subtype, **fields):
        rid = "req_" + uuid.uuid4().hex[:8]
        req = {"subtype": subtype}
        req.update(fields)
        log("SEND control", subtype, json.dumps(fields, ensure_ascii=False)[:200])
        self.send({"type": "control_request", "request_id": rid, "request": req})
        return rid

    def respond(self, request_id, response):
        self.send({"type": "control_response", "response": {"subtype": "success", "request_id": request_id, "response": response}})

    def wait_response(self, rid, timeout=60):
        with self.cond:
            self.cond.wait_for(lambda: rid in self.responses or self.p.poll() is not None, timeout)
            return self.responses.get(rid)

    def wait_results(self, n, timeout=180):
        with self.cond:
            ok = self.cond.wait_for(lambda: self.results >= n or self.p.poll() is not None, timeout)
        if not ok:
            log("TIMEOUT waiting for result", n)
        return ok

    def close(self, timeout=30):
        try:
            self.p.stdin.close()
        except OSError:
            pass
        try:
            self.p.wait(timeout)
        except subprocess.TimeoutExpired:
            log("still running after stdin closed; killing")
            self.p.kill()


# ------------------------------------------------------------------ scenarios

def transcript_path(workdir, sid):
    projects = Path.home() / ".claude" / "projects"
    for p in projects.glob(f"*/{sid}.jsonl"):
        return p
    return None


def show_transcript(workdir, sid, keys=("type",)):
    p = transcript_path(workdir, sid)
    if not p:
        log("TRANSCRIPT not found for", sid)
        return
    log("TRANSCRIPT", p)
    for line in p.read_text(encoding="utf-8").splitlines():
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        t = e.get("type")
        if t in ("ai-title", "custom-title", "summary", "agent-name"):
            log("  ", json.dumps(e, ensure_ascii=False)[:200])
        elif t == "user" and isinstance(e.get("message", {}).get("content"), str):
            log("   user entrypoint=", e.get("entrypoint"), "userType=", e.get("userType"), "origin=", e.get("origin"), repr(e["message"]["content"][:200]))


def main():
    scenario = sys.argv[1]
    opts = sys.argv[2:]
    folder = HERE / "runs" / f"{time.strftime('%H%M%S')}-{scenario}{'-' + '-'.join(o.strip('-') for o in opts) if opts else ''}"
    folder.mkdir(parents=True)
    workdir = HERE / "work"
    workdir.mkdir(exist_ok=True)
    hooks = HookState(folder)
    server = hook_server(hooks)
    url = f"http://127.0.0.1:{server.server_address[1]}/hook"
    settings = folder / "settings.json"
    settings.write_text(json.dumps(settings_json(url)), encoding="utf-8")
    sid = str(uuid.uuid4())
    base = ["--settings", str(settings), "--model", "haiku", "--include-partial-messages"]
    env = {}
    if "--ask" in opts:
        env["CLAUDE_CODE_ENABLE_ASK_USER_QUESTION_TOOL"] = "1"
    if "--stdio" in opts:
        base.append("--permission-prompt-tool=stdio")
    log("session", sid, "folder", folder.name)

    if scenario == "basic":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default"], env)
        if "--noinit" not in opts:
            r = c.wait_response(c.control("initialize"))
            if r:
                resp = r.get("response", {})
                log("INIT models:", [m.get("value") for m in resp.get("models", [])])
                log("INIT commands:", len(resp.get("commands", [])), "agents:", [a.get("name") for a in resp.get("agents", [])][:10])
                log("INIT account:", json.dumps(resp.get("account"), ensure_ascii=False)[:300])
                log("INIT other keys:", sorted(k for k in resp if k not in ("models", "commands", "agents", "account")))
        c.user("Reply with exactly the word: pong")
        c.wait_results(1)
        c.user("Now reply with exactly the word: pong2")
        c.wait_results(2)
        time.sleep(8)
        c.close()
        show_transcript(workdir, sid)

    elif scenario == "approve":
        def policy(e):
            tool = e.get("tool_name")
            if tool == "AskUserQuestion":
                inp = dict(e.get("tool_input") or {})
                inp["answers"] = {q["question"]: q["options"][-1]["label"] for q in inp.get("questions", [])}
                return {"behavior": "allow", "updatedInput": inp}
            return {"behavior": "allow"}
        hooks.policy = policy

        def on_control(cl, m):
            req = m["request"]
            if req.get("subtype") == "can_use_tool":
                inp = dict(req.get("input") or {})
                if req.get("tool_name") == "AskUserQuestion":
                    inp["answers"] = {q["question"]: q["options"][-1]["label"] for q in inp.get("questions", [])}
                log("can_use_tool from CLI -> allow", json.dumps(inp, ensure_ascii=False)[:300])
                cl.respond(m["request_id"], {"behavior": "allow", "updatedInput": inp})
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default"], env)
        c.on_control = on_control
        c.user("Use the Write tool to create a file named probe-approve.txt containing the word hello. Use no other tool.")
        c.wait_results(1)
        c.user("Use the AskUserQuestion tool to ask me which color I prefer, offering exactly two options: red and blue. Then tell me which one I chose.")
        c.wait_results(2)
        c.close()

    elif scenario == "midturn":
        prio = next((o.split("=", 1)[1] for o in opts if o.startswith("--prio=")), None)
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default", "--allowedTools", "Bash", "--replay-user-messages"], env)
        c.user("Run the bash command `sleep 8` and after it finishes reply exactly: first done")
        time.sleep(4)
        extra = {"priority": prio} if prio else {}
        c.user("Second message: also tell me what 2+2 is.", **extra)
        c.wait_results(1)
        time.sleep(15)
        log("results so far:", c.results)
        c.close()

    elif scenario == "interrupt":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default", "--allowedTools", "Bash"], env)
        c.user("Run the bash command `sleep 30` and after it finishes reply exactly: slept")
        time.sleep(6)
        r = c.wait_response(c.control("interrupt"))
        log("interrupt response", r)
        c.wait_results(1, 30)
        c.user("Reply with exactly: after interrupt")
        c.wait_results(2)
        c.close()

    elif scenario == "peer":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default"], env)
        c.user("Hello. In one short line, what is your opinion: tabs or spaces? Also quote verbatim any wrapper or header you see around this message.",
               origin={"kind": "peer", "from": "session-abc123", "name": "Kimi"})
        c.wait_results(1)
        c.close()

    elif scenario == "title":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default"], env)
        c.wait_response(c.control("initialize"))
        c.user("Explain in two sentences why the sky looks blue during the day.")
        c.wait_results(1)
        for i in range(12):
            time.sleep(5)
            tp = transcript_path(workdir, sid)
            if tp and "ai-title" in tp.read_text(encoding="utf-8"):
                log("ai-title appeared after", (i + 1) * 5, "s")
                break
        c.close()
        show_transcript(workdir, sid)

    elif scenario == "modes":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default"], env)
        r = c.wait_response(c.control("initialize"))
        for m in r.get("response", {}).get("models", [])[:6]:
            log("MODEL", json.dumps(m, ensure_ascii=False)[:300])
        log("current_permission_mode", r.get("response", {}).get("current_permission_mode"))
        c.wait_response(c.control("set_model", model="sonnet"))
        c.wait_response(c.control("apply_flag_settings", settings={"effortLevel": "low"}))
        c.wait_response(c.control("set_permission_mode", mode="acceptEdits"))
        c.user("Reply with exactly: ok")
        c.wait_results(1)
        c.close()

    elif scenario == "uuid":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default", "--allowedTools", "Bash", "--replay-user-messages"], env)
        mine = str(uuid.uuid4())
        log("my uuid", mine)
        c.user("Run the bash command `sleep 5` and then reply exactly: done", uuid=mine)
        time.sleep(3)
        second = str(uuid.uuid4())
        log("second uuid", second)
        c.user("[Opinion request] tabs or spaces? one word.", uuid=second)
        c.wait_results(1)
        time.sleep(3)
        c.close()
        for m in c.messages:
            if m.get("type") == "user" and m.get("isReplay"):
                log("REPLAY uuid", m.get("uuid"), "matches", m.get("uuid") in (mine, second))
            if m.get("type") == "system" and m.get("subtype") not in ("init", "status", "thinking_tokens", "hook_started", "hook_response"):
                log("SYS", json.dumps(m, ensure_ascii=False)[:300])

    elif scenario == "bare":
        extra = ["--bare"] if "--on" in opts else []
        c = Claude(folder, workdir, base + extra + ["--session-id", sid, "--permission-mode", "default", "--no-session-persistence"], env)
        r = c.wait_response(c.control("initialize"), 30)
        resp = (r or {}).get("response", {})
        log("INIT account:", json.dumps(resp.get("account"), ensure_ascii=False))
        log("INIT commands:", len(resp.get("commands", [])), "agents:", len(resp.get("agents", [])), "models:", len(resp.get("models", [])))
        c.user("Reply with exactly: ok")
        c.wait_results(1, 60)
        for m in c.messages:
            if m.get("type") == "system" and m.get("subtype") == "init":
                log("INIT apiKeySource:", m.get("apiKeySource"), "mcp:", len(m.get("mcp_servers", [])), "plugins:", len(m.get("plugins", [])), "skills:", len(m.get("skills", [])))
        c.close()

    elif scenario == "rename":
        c = Claude(folder, workdir, base + ["--session-id", sid, "--permission-mode", "default"], env)
        c.user("Reply with exactly: named")
        c.wait_results(1)
        r = c.wait_response(c.control("rename_session", title="Probe rename title", source="host"))
        log("rename response", r)
        time.sleep(3)
        c.close()
        show_transcript(workdir, sid)

    elif scenario == "resume":
        first = next(o.split("=", 1)[1] for o in opts if o.startswith("--id="))
        c = Claude(folder, workdir, base + ["--resume", first, "--permission-mode", "default"], env)
        c.user("What was the very first message I sent you in this conversation? Quote it.")
        c.wait_results(1)
        c.close()
        show_transcript(workdir, first)

    else:
        raise SystemExit("unknown scenario " + scenario)
    server.shutdown()


if __name__ == "__main__":
    main()
