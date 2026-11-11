# Does Task Scheduler stop the service's hosts when the service it started exits (a restart), and does
# the logon task start the next service while those hosts still run in its job?
# Usage: task_job_probe.ps1 <install dir> <data dir>. A test task on a pipe of its own, removed at the end.
param([string]$Install, [string]$Data)
$ErrorActionPreference = 'Continue'
$exe = Join-Path $Install 'Gorynych.Service.exe'
$pipe = 'gorynych-jobtest-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$task = "Gorynych.Service.$pipe"
if (Test-Path $Data) { Remove-Item -Recurse -Force $Data }
Add-Type -Namespace Probe -Name Job -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
public static string In(int pid) {
  var h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return "gone";
  bool r; IsProcessInJob(h, IntPtr.Zero, out r); CloseHandle(h); return r ? "in a job" : "no job"; }
'@
function Say($m) { "{0:HH:mm:ss} {1}" -f (Get-Date), $m }
function PipeUp { Test-Path "\\.\pipe\$pipe" }
function Procs { Get-CimInstance Win32_Process -Filter "Name = 'Gorynych.Service.exe'" | Where-Object { $_.CommandLine -like "*$pipe*" } }
function Show {
  foreach ($p in Procs) { $kind = if ($p.CommandLine -like '* host *') { 'host ' + ($p.CommandLine -replace '.*--adapter (\S+).*', '$1') } else { 'service' }
    Say ("  {0,-12} pid {1,-6} parent {2,-6} {3}" -f $kind, $p.ProcessId, $p.ParentProcessId, [Probe.Job]::In($p.ProcessId)) }
  Say ("  task: " + ((schtasks /Query /TN $task /V /FO LIST | Select-String 'Status:|Состояние:' | Select-Object -First 1) -replace '\s+', ' '))
}
function WaitPipe($seconds) { for ($i = 0; $i -lt $seconds * 4; $i++) { if (PipeUp) { return $true }; Start-Sleep -Milliseconds 250 }; return $false }

try {
  & $exe task install --pipe $pipe --data $Data | Out-Null
  Say "installed $task"
  schtasks /Run /TN $task | Out-Null
  Say ("service up: " + (WaitPipe 20))
  # A session with Kimi starts its host (a session/new is enough).
  $py = @'
import json, subprocess, sys
exe, pipe, agent = sys.argv[1:4]
b = subprocess.Popen([exe, 'acp', '--agent', agent, '--pipe', pipe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
def call(i, m, p):
    b.stdin.write((json.dumps({'jsonrpc': '2.0', 'id': i, 'method': m, 'params': p}) + '\n').encode()); b.stdin.flush()
    while True:
        r = json.loads(b.stdout.readline())
        if r.get('id') == i and 'method' not in r: return r
call(1, 'initialize', {'protocolVersion': 1, 'clientCapabilities': {}})
print(json.dumps(call(2, 'session/new', {'cwd': sys.argv[4], 'mcpServers': []}).get('result', {}).get('sessionId')))
b.stdin.close(); b.wait(30)
'@
  $pyFile = Join-Path $Data 'new.py'; Set-Content $pyFile $py
  $cwd = Join-Path $Data 'cwd'; New-Item -ItemType Directory -Force $cwd | Out-Null
  Say ("kimi session: " + (python -I $pyFile $exe $pipe kimi $cwd))
  Start-Sleep 2
  Say "before the restart:"; Show
  $hosts = @(Procs | Where-Object { $_.CommandLine -like '* host *' } | ForEach-Object ProcessId)
  & $exe restart --pipe $pipe 2>&1 | ForEach-Object { Say "  restart: $_" }
  foreach ($wait in 1, 5, 15) {
    Start-Sleep $wait
    Say "after the restart (+$wait s): hosts alive: $(@($hosts | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count) of $($hosts.Count)"; Show
  }
  Say "the logon task again, as a bridge runs it when the pipe is missing:"
  schtasks /Run /TN $task 2>&1 | ForEach-Object { Say "  schtasks: $_" }
  Say ("next service up: " + (WaitPipe 20)); Start-Sleep 3; Show
  & $exe status --pipe $pipe 2>&1 | Select-Object -First 12 | ForEach-Object { Say "  status: $_" }
  Say "the mystery process:"; Procs | Where-Object { $_.CommandLine -notlike '* host *' } | ForEach-Object { Say ("  pid {0}: {1}" -f $_.ProcessId, $_.CommandLine) }
  Say "a second host, started by the next service (a Codex session):"; Say ("codex session: " + (python -I $pyFile $exe $pipe codex $cwd)); Start-Sleep 2; Show
  Say "schtasks /End:"; schtasks /End /TN $task 2>&1 | ForEach-Object { Say "  schtasks: $_" }
  Start-Sleep 3; Show
  Say "service log:"; Get-Content (Join-Path $Data 'logs\service-*.log') | Select-String 'job|breaking|starting, pid|host .* connected|hosts' | Select-Object -Last 14 | ForEach-Object { "    $_" }
}
finally {
  & $exe shutdown --pipe $pipe 2>&1 | Out-Null
  Start-Sleep 2
  foreach ($p in Procs) { Say "  stopping leftover pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
  & $exe task uninstall --pipe $pipe | Out-Null
  Say ("task removed: " + -not (schtasks /Query /TN $task 2>$null))
}
