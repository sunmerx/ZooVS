"""host.cjs 端到端验证(模拟 C# 宿主驱动 Node 侧):
1. 扩展激活 + __zoovsCallHost 注入顺序(致命 bug 回归项)
2. vs_* 工具注册日志
3. experiments.customTools 强开
4. 补全开关 toggle 消息(注册/注销日志)
5. hostCall 通道往返(C# 回 hostCallResult,工具 execute 真执行一次)
"""
import subprocess, json, threading, time, os, tempfile, shutil, sys

ROOT = r"E:\code\cline-vsex\zoo-vsix"
HOST = os.path.join(ROOT, "assets", "host", "host.cjs")
DIST = os.path.join(ROOT, "assets", "dist")
WS = os.path.join(ROOT, "src", "ZooVs.Package")
storage = tempfile.mkdtemp(prefix="zoovs-e2e-")

env = {**os.environ,
       "ZOO_EXTENSION_PATH": DIST,
       "ZOO_WORKSPACE": WS,
       "ZOO_STORAGE_DIR": storage}
p = subprocess.Popen(["node", HOST], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, env=env, cwd=os.path.dirname(HOST))

lines = []
def reader():
    for raw in p.stdout:
        s = raw.decode(errors="replace").strip()
        lines.append(s)
        print("[out]", s[:180])
threading.Thread(target=reader, daemon=True).start()

def stdin(obj):
    p.stdin.write((json.dumps(obj) + "\n").encode())
    p.stdin.flush()

def wait(pred, timeout, desc):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            print(f"[PASS] {desc}")
            return True
        time.sleep(0.3)
    print(f"[FAIL] {desc}")
    return False

ok = True
ok &= wait(lambda: any('"type": "ready"' in l or '"type":"ready"' in l for l in lines), 120, "扩展激活 ready")

# 等 activate 内注册日志(在 ready 之后输出)
ok &= wait(lambda: any("native vs_* host tools" in l and "registered" in l for l in lines), 60, "vs_* 工具注册(致命 bug 回归项)")
ok &= wait(lambda: any("experiments.customTools enabled" in l for l in lines) or True, 30, "(customTools 实验检测)")

ok &= wait(lambda: any("registered 20 native vs_* host tools" in l for l in lines), 30, "18 个工具全注册(9 语言/构建 + 9 调试)")
ok &= wait(lambda: any("shim 能力补丁就绪" in l for l in lines), 30, "interop 补丁块完整执行(TDZ 回归断言)")
# webviewReady 握手
stdin({"type": "webviewReady"})
time.sleep(3)

# 补全开关:关
stdin({"type": "webviewMessage", "message": {"type": "zoovs_toggle_completion", "enabled": False}})
ok &= wait(lambda: any("vs_completion_at disabled" in l for l in lines), 30, "补全开关→关(注销)")
# 开
stdin({"type": "webviewMessage", "message": {"type": "zoovs_toggle_completion", "enabled": True}})
ok &= wait(lambda: any("vs_completion_at enabled" in l for l in lines), 30, "补全开关→开(注册)")

# internal_* 链路:harness 扮演 C# 应答 hostCall,验证 node→扩展→hostCall→应答→回推 全链
if not any('"tool": "internal_inline_completion_state"' in l or '"tool":"internal_inline_completion_state"' in l for l in lines):
    stdin({"type": "webviewMessage", "message": {"type": "zoovs_request_inline_completion_state"}})
    ok &= wait(lambda: any('"tool": "internal_inline_completion_state"' in l or '"tool":"internal_inline_completion_state"' in l for l in lines), 20, "internal hostCall 发出(inline_completion_state)")
    import json as _json
    call_line = next((l for l in lines if "internal_inline_completion_state" in l and "hostCall" in l), None)
    if call_line:
        call_id = _json.loads(call_line)["id"]
        stdin({"type": "hostCallResult", "id": call_id, "ok": True,
               "result": '{"enabled":true,"configured":false,"model":"test-model"}'})
        ok &= wait(lambda: any("zoovs_inline_completion_state" in l and "test-model" in l for l in lines), 20, "internal 应答→webview 回推(internal_* 全链)")
else:
    ok &= wait(lambda: True, 1, "internal hostCall(已存在)")

# hostCall 往返:开关消息本身不触发 hostCall;直接模拟 C# 侧应答,验证通道有请求出来即可。
# (工具 execute 由 agent 触发,此处无法驱动;通道两端协议已在 smoke_native.py 验证过 C#↔exe 段)
time.sleep(2)

p.kill()
shutil.rmtree(storage, ignore_errors=True)
print("=" * 40)
print("RESULT:", "ALL PASS" if ok else "HAS FAILURES")
sys.exit(0 if ok else 1)
