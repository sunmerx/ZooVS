import subprocess, json, threading, time, os

exe = r"E:\code\cline-vsex\zoo-vsix\assets\roslyn\ZooMcpServer.exe"
env = {**os.environ, "VSINSTALLDIR": "C:\\Program Files\\Microsoft Visual Studio\\2022\\Enterprise\\"}
p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env)

def send(o):
    p.stdin.write((json.dumps(o) + "\n").encode())
    p.stdin.flush()

q = []
threading.Thread(target=lambda: [q.append(l.decode(errors="replace").strip()) for l in p.stdout], daemon=True).start()

send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}}})
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
time.sleep(2)

def call(mid, name, args):
    send({"jsonrpc": "2.0", "id": mid, "method": "tools/call", "params": {"name": name, "arguments": args}})
    t0 = time.time()
    while time.time() - t0 < 180:
        for l in list(q):
            try:
                d = json.loads(l)
                if d.get("id") == mid:
                    return "".join(c.get("text", "") for c in d.get("result", {}).get("content", []))
            except Exception:
                pass
        time.sleep(0.3)
    return "TIMEOUT"

sln = r"E:\code\cline-vsex\zoo-vsix\src\ZooVs.Package\ZooVs.Package.csproj"
send({"jsonrpc": "2.0", "id": 10, "method": "tools/list"})
time.sleep(2)
print("find_calls in tools/list:", any("find_calls" in l for l in q))
print("== find_calls callees:")
print(call(11, "find_calls", {"direction": "callees", "symbolRef": "ZooVs.Package.RoslynHostService.CallToolAsync", "solutionPath": sln})[:400])
print("== find_calls callers:")
print(call(12, "find_calls", {"direction": "callers", "symbolRef": "ExtractToolText", "solutionPath": sln})[:400])
print("== build 消毒(非法配置名应被拒):")
print(call(13, "build", {"solutionPath": sln, "configuration": "Debug /p:OutDir=c:\\x"})[:150])
p.kill()
