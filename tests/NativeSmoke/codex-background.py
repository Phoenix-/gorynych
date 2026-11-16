import json, subprocess, queue, threading, time, os
from pathlib import Path
matches = list((Path(os.environ['APPDATA']) / 'npm/node_modules/@openai/codex').glob('node_modules/@openai/codex-win32-*/vendor/*/bin/codex.exe'))
if len(matches) != 1: raise RuntimeError('Could not uniquely locate the native Codex executable')
exe = str(matches[0])
p = subprocess.Popen([exe, 'app-server'], cwd=os.getcwd(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, encoding='utf-8', errors='replace', creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
lines = queue.Queue()
threading.Thread(target=lambda: [lines.put(line) for line in p.stdout], daemon=True).start()
pending = {}; next_id = 0; foreground_ended = False
def send(method, params):
    global next_id
    next_id += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':next_id,'method':method,'params':params})+'\n'); p.stdin.flush()
    return next_id
def receive(deadline):
    global foreground_ended
    while time.monotonic() < deadline:
        try: msg=json.loads(lines.get(timeout=1))
        except queue.Empty: continue
        except ValueError: continue
        if msg.get('method') == 'turn/completed': foreground_ended = True; print('foreground ended', flush=True)
        if 'id' in msg and ('result' in msg or 'error' in msg): pending[msg['id']] = msg
        return
    raise TimeoutError('native response timeout')
def request(method, params, timeout=30):
    rid=send(method,params); end=time.monotonic()+timeout
    while rid not in pending: receive(end)
    msg=pending.pop(rid)
    if 'error' in msg: raise RuntimeError(str(msg['error']))
    return msg['result']
try:
    request('initialize',{'clientInfo':{'name':'gorynych-background-smoke','version':'1'},'capabilities':{'experimentalApi':True}})
    p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'initialized','params':{}})+'\n'); p.stdin.flush()
    config=request('config/read',{}).get('config',{})
    servers=config.get('mcp_servers',{}) or {}
    overrides={name:{'enabled':False} for name in servers}
    thread=request('thread/start',{'cwd':os.getcwd(),'ephemeral':True,'approvalPolicy':'never',
                                  'config':{'mcp_servers':overrides}})['thread']['id']
    request('turn/start',{'threadId':thread,'input':[{'type':'text','text':'Run exactly one shell command `Start-Sleep -Seconds 15` with a one second yield, letting it continue in the background, then respond immediately. Do not use any other tools.'}]})
    seen=False; success=False; deadline=time.monotonic()+100
    while time.monotonic()<deadline:
        result=request('thread/backgroundTerminals/list',{'threadId':thread,'limit':100},timeout=15)
        count=len(result['data']); print('terminals:',count,'foreground ended:',foreground_ended,flush=True)
        if count: seen=True
        if seen and count==0: print('PASS: nonempty then empty',flush=True); success=True; break
        end=min(deadline,time.monotonic()+1)
        while time.monotonic()<end:
            try: receive(end)
            except TimeoutError: break
    if not success: raise RuntimeError('No complete nonempty-to-empty Codex terminal lifecycle observed')
finally:
    p.stdin.close()
    try: p.wait(timeout=5)
    except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=5)
