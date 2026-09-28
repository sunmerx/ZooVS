# ZooVS — Zoo Code AI 编程助手 · Visual Studio 2022 移植

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

ZooVS 把 Zoo Code(源自 Roo Code / Cline 血统)的 AI 编程助手完整移植到 **Visual Studio 2022**:上游扩展逻辑运行在 Node 宿主进程(vscode-shim),官方 Web UI 承载于 WebView2 工具窗口,并新增了一批 Visual Studio 原生集成能力。自带 API Key(BYOK),无账号、无遥测。

## 功能

- **完整 agent 对话** —— 上游 Zoo Code 界面(View → Other Windows → ZooVS Chat),`@` 引用文件、`/` 命令、模式切换、Skills/Workflows/MCP
- **20 个 `vs_*` 原生 agent 工具** —— 解决方案模型、符号搜索/引用/继承链/调用链、补全查询、诊断、构建运行,以及完整的自主调试套件(设断点/单步/检查)
- **行内 AI 补全** —— 流式 ghost text(Tab 接受 / Esc 关闭),自动复用 Zoo 配置的 provider;DeepSeek 官方 FIM 端点可用时自动启用(亚秒首字)
- **原生 diff 对比窗口** —— agent 的编辑经 Visual Studio 真实比较窗口呈现
- **VS 深度集成** —— 跟随解决方案切换、主题、编辑器上下文;Job Object 自动清理孤儿宿主进程

## 版权声明(License)

本项目按 **[Apache License 2.0](LICENSE)** 发布。

- ZooVS 是 Zoo Code 的社区移植衍生作品;Zoo Code 派生自 Roo Code 与 Cline(均为 Apache-2.0)
- 上游修改与新增内容说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md),协议全文随源码与本页同目录分发
- Roo Code 与 Cline 的名称与标识归各自所有者;**本项目为独立社区作品,与上述项目无隶属或背书关系**

## 构建

前置:VS2022 + .NET Framework 4.7.2 开发包 + Node.js + pnpm + Python 3(上游源码另需自行获取,见 THIRD-PARTY-NOTICES.md)。

```
# 1) 上游资产:在 Zoo-Code 中构建 daemon(host)与 webview
# 2) 同步资产(含手工资产 material-icons/images/ripgrep)
python zoo-vsix/sync-assets.py
# 3) 打包 VSIX
dotnet build zoo-vsix/src/ZooVs.Package/ZooVs.Package.csproj -c Release
# 产物:zoo-vsix/src/ZooVs.Package/bin/Release/net472/ZooVs.Package.vsix
```

## 已知限制

- write_to_file 新建文件的 diff 预览等待在本移植层存在 10s 超时问题(修复中,见 issue 区)
- 行内补全延迟取决于所配 provider;推理模型建议关闭思考或使用 DeepSeek 官方 FIM
- 多 VS 实例共享 Zoo 数据目录(上游设计)

## 致谢

- 血统上游:[Roo Code](https://github.com/RooVetGit/Roo-Code) 与 [Cline](https://github.com/cline/cline)(Apache-2.0),以及它们之间的 Zoo Code 社区分支
- 所有上游贡献者
