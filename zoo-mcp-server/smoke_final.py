import subprocess, json, threading, time, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

sln = r"E:\code\cline-vsex\zoo-mcp-server\testdata\DemoApp\DemoApp.csproj"
calls = [
    (3, "get_solution_model", {"solutionPath": sln}),
    (4, "symbol_search", {"pattern": "Greeter", "solutionPath": sln}),
    (5, "find_references", {"symbolRef": "DemoApp.IGreeter.Greet", "solutionPath": sln}),
    (6, "hierarchy", {"typeRef": "DemoApp.IGreeter", "solutionPath": sln}),
    (7, "find_implementations", {"symbolRef": "DemoApp.IGreeter.Greet", "solutionPath": sln}),
    (8, "build", {"solutionPath": sln}),
]
p = subprocess.Popen([r"bin\Release\net8.0\ZooMcpServer.exe"],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace")
responses = {}
def reader():
    for line in p.stdout:
        try:
            obj = json.loads(line)
            if obj.get("id") is not None:
                responses[obj["id"]] = obj
        except Exception:
            pass
threading.Thread(target=reader, daemon=True).start()
def send(mid, name, args):
    try:
        p.stdin.write(json.dumps({"jsonrpc":"2.0","id":mid,"method":"tools/call","params":{"name":name,"arguments":args}}) + "\n")
        p.stdin.flush()
    except OSError: pass
p.stdin.write(json.dumps({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"s","version":"1"}}}) + "\n")
p.stdin.flush()
p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()
time.sleep(0.5)
deadline = time.time() + 300; idx = 0
while idx < len(calls) and time.time() < deadline:
    rid, name, args = calls[idx]
    if rid in (5, 7) and 4 not in responses:
        time.sleep(0.5); continue
    send(rid, name, args)
    wd = time.time() + 90
    while rid not in responses and time.time() < wd:
        time.sleep(0.5)
    idx += 1
out = []
for rid, name, _ in calls:
    obj = responses.get(rid)
    content = (obj or {}).get("result", {}).get("content", [])
    text = content[0]["text"] if content else "无响应"
    out.append(f"=== {name} ===\n{text[:400]}\n")
p.kill()
open(r"E:\code\cline-vsex\zoo-mcp-server\smoke-final.log", "w", encoding="utf-8").write("\n".join(out))
print("written")
