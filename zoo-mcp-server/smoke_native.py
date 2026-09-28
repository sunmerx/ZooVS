import subprocess, json, threading, time, os

exe = r"E:\code\cline-vsex\zoo-vsix\assets\roslyn\ZooMcpServer.exe"
env = {**os.environ, "VSINSTALLDIR": "C:\\Program Files\\Microsoft Visual Studio\\2022\\Enterprise\\"}
p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env)

def send(obj):
    p.stdin.write((json.dumps(obj) + "\n").encode())
    p.stdin.flush()

q = []
def reader():
    for line in p.stdout:
        q.append(line.decode(errors="replace").strip())
threading.Thread(target=reader, daemon=True).start()

send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
      "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                 "clientInfo": {"name": "zoovs", "version": "1.0"}}})
send({"jsonrpc": "2.0", "method": "notifications/initialized"})

t0 = time.time()
while time.time() - t0 < 60:
    if any(('"id": 1' in l or '"id":1' in l) for l in q):
        break
    time.sleep(0.2)
else:
    print("initialize: TIMEOUT"); p.kill(); raise SystemExit(1)
print("initialize: OK (%.1fs)" % (time.time() - t0))

send({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
      "params": {"name": "get_solution_model",
                 "arguments": {"solutionPath": r"E:\code\cline-vsex\zoo-vsix\src\ZooVs.Package\ZooVs.Package.csproj"}}})

t0 = time.time()
res = None
while time.time() - t0 < 180 and res is None:
    for l in list(q):
        try:
            d = json.loads(l)
            if d.get("id") == 2:
                res = d
                break
        except Exception:
            pass
    time.sleep(0.3)

if res:
    text = "".join(c.get("text", "") for c in res.get("result", {}).get("content", []))
    print("tools/call OK:", text[:200].replace("\n", " | "))
else:
    print("tools/call TIMEOUT, lines:", q[:5])
p.kill()
