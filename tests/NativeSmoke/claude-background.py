import json, subprocess, queue, threading, time, os
exe = r'C:\Users\micha\AppData\Local\Microsoft\WinGet\Links\claude.exe'
args = [exe, '-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose',
        '--no-session-persistence', '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}',
        '--tools', 'Bash', '--permission-mode', 'bypassPermissions']
p = subprocess.Popen(args, cwd=os.getcwd(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, encoding='utf-8', errors='replace', creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
lines = queue.Queue()
threading.Thread(target=lambda: [lines.put(line) for line in p.stdout], daemon=True).start()
threading.Thread(target=lambda: [None for _ in p.stderr], daemon=True).start()
seen = False
success = False
deadline = time.monotonic() + 90
try:
    p.stdin.write(json.dumps({'type':'user','message':{'role':'user','content':'Use the Bash tool to run exactly `sleep 12` with run_in_background set to true. Respond immediately after starting it. Do not use any other tool.'}}) + '\n')
    p.stdin.flush()
    while time.monotonic() < deadline:
        if p.poll() is not None and lines.empty(): break
        try: line = lines.get(timeout=1)
        except queue.Empty: continue
        try: msg = json.loads(line)
        except ValueError: continue
        if msg.get('type') == 'system' and msg.get('subtype') == 'background_tasks_changed':
            tasks = msg.get('tasks')
            print('background snapshot:', json.dumps(tasks), flush=True)
            if isinstance(tasks, list) and tasks: seen = True
            if seen and tasks == []: print('PASS: nonempty then empty', flush=True); success = True; break
        if msg.get('type') == 'result': print('foreground result:', msg.get('subtype'), flush=True)
    if not success: raise RuntimeError('No complete nonempty-to-empty Claude lifecycle observed')
finally:
    p.stdin.close()
    try: p.wait(timeout=5)
    except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=5)
