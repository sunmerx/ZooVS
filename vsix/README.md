# ClineVS — Cline 的 Visual Studio 2022 移植

Cline(开源 AI 编程代理,Apache 2.0)的 VS2022 社区移植。
架构:**cline-core Node 守护进程(上游原封不动)+ C# hostbridge(VSIX)+ WebView2 工具窗口**。

```
VS 2022
└─ ClineVs.Package (C#, VSIX)
   ├─ ToolWindow(WebView2)── 承载 webview standalone 构建(上游原封不动)
   ├─ ClineWebviewController ── grpc_request/grpc_response JSON 桥
   ├─ BridgeServer (gRPC 127.0.0.1) ── daemon 反连:CoreConnection 双向流 + 39 个 host RPC
   └─ DaemonProcessManager ── 拉起/监控/回收 node cline-core.js
```

## 构建步骤

```powershell
# 1. JS 产物(daemon + standalone webview,首次全量跑,后续加 -SkipInstall)
powershell -ExecutionPolicy Bypass -File build-js.ps1

# 2. C# + VSIX
"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" ClineVs.sln -p:Configuration=Release

# 产物:src\ClineVs.Package\bin\Release\net472\ClineVs.Package.vsix
```

## 安装 / 卸载

安装前需关闭所有 VS 实例(VSIXInstaller 在 VS 运行时会拒绝静默安装):

```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\VSIXInstaller.exe" /quiet ClineVs.Package.vsix
```

卸载:

```cmd
VSIXInstaller.exe /uninstall:ClineVS
```

## 使用

1. 关闭 VS → 安装 VSIX → 启动 VS。
2. 菜单 `Tools > ClineVS > Open ClineVS Chat`(或 `View > Other Windows > ClineVS Chat`)。
3. 首次打开会拉起 cline-core daemon(需要系统 node 在 PATH,M2 将内置 Node 22 运行时)。
4. 在聊天面板配置 API Provider(OpenRouter/Anthropic 等)后即可对话。
5. 日志查看:输出窗口(Output)> "ClineVS" 窗格。

## 工程结构

| 项目 | 职责 |
|---|---|
| ClineVs.Protos | host/*.proto → C# 绑定;cline/*.proto 嵌入为资源(流式方法契约解析) |
| ClineVs.Bridge | gRPC 服务端:CoreConnection 双向流(JSON 透传)+ 39 host RPC 实现 + 令牌校验 |
| ClineVs.Daemon | daemon 进程管理(一次性令牌、健康检查、崩溃重启、进程树回收) |
| ClineVs.Package | VSIX:AsyncPackage、WebView2 工具窗口、消息桥、VSSDK 宿主能力(VsBridgeHost) |
| ClineVs.SmokeTest | 无 VS 全链路冒烟:BridgeServer → daemon 反连认证 → getLatestState 调用 |

上游源码在 `../cline`(只读,commit b51c27b);proto 定版副本在 `proto/`。

## 当前状态(M1 PoC 完成,2026-09-25 全链路验证通过)

- [x] C# 五项目编译通过;VSIX 打包(36MB)
- [x] 冒烟测试:daemon 反连令牌认证成功;cline.StateService.getLatestState 返回完整状态;
      core→host 反向 RPC(showMessage)实测到达宿主
- [x] daemon 产物(cline-core.js + node_modules + vscode stub + extension 元数据)
- [x] webview standalone 构建产物
- [x] VSIX 已安装到 VS2022 Enterprise(与 VS2026);VS 内日志全绿:
      hostbridge 监听 → daemon 反连认证 → webview 消息路由零失败 → 流式契约 17 个

### 已知问题(M2 处理)

1. ripgrep(rg.exe)未进 daemon 目录(上游 download-ripgrep 脚本网络失败),代码搜索工具降级
2. `openDiff` 用临时文件对 + IVsDifferenceService 实现,滚动/截断为 no-op
3. 诊断(getDiagnostics)返回空集;终端 RPC 降级为输出窗格(上游最新核心已无调用方)
4. System.Text.Json 用 9.0(Microsoft.VisualStudio.SDK 元包传递依赖所致)

### 移植中排掉的坑(经验存档)

1. Grpc.Tools 保留 proto 原始方法名(clipboardWriteText 而非 ClipboardWriteText)
2. VSSDK.BuildTools NuGet 不自动导入 Microsoft.VsSDK.targets;在 csproj 内 Import 会因早求值
   导致 TargetPath/IntermediateOutputPath 为空(VSSDK1032/1202)——必须放进 Directory.Build.targets
3. Grpc.HealthCheck 包钉死 Google.Protobuf 3.19.5,VSIX 精确绑定下与 3.27.4 冲突——
   改用官方 health.proto 自行生成绑定并实现
4. daemon 打包三件套:runtime-files 的 vscode stub 必须放 node_modules/、
   extension/package.json 元数据目录、数据目录用 --config 参数(CLINE_DATA_DIR 无效)
5. EmbeddedResource LinkBase 的 '-' 会被替换为 '_'(cline-proto → cline_proto)
6. WebView2 COM 事件参数(WebMessageReceived 的 e)只能在 UI 线程访问,
   PostWebMessageAsJson 也必须切回 UI 线程,否则报 Unable to cast to ICoreWebView2...
7. Git Bash 调用 VSIXInstaller/taskkill 时 /quiet /PID 会被转成路径——需 MSYS_NO_PATHCONV=1
8. VSIXInstaller 在 devenv.exe 残留(无主窗口)时同样拒绝安装
