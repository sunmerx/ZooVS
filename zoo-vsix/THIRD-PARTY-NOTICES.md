# Third-Party Notices

## ZooVS

ZooVS 是将 Zoo Code(源自 Roo Code 与 Cline)AI 编程助手移植到 Visual Studio 2022 的社区作品,
按 Apache License 2.0 发布(完整文本见本目录 LICENSE 文件)。

本项目对上游代码做了大量修改与新增,主要包括:

- 以 Node 宿主进程 + vscode-shim 在 Visual Studio 2022 内运行上游扩展逻辑
- WebView2 工具窗口承载上游 Web UI,宿主注入 VS 主题变量
- 内化 20 个 `vs_*` 原生 agent 工具(语言/构建/调试/编辑器交互,基于 VS SDK 与 Roslyn)
- 自研行内补全引擎(装饰层 ghost text + OpenAI 兼容流式补全 + DeepSeek FIM 支持)
- Job Object 孤儿进程清理、解决方案热切换、原生 diff 对比窗口等集成

## 上游项目

| 项目 | 关系 | 许可证 |
|---|---|---|
| Zoo Code | 直接移植上游 | Apache License 2.0 |
| Roo Code | Zoo Code 的上游 | Apache License 2.0 |
| Cline | Roo Code 的上游 | Apache License 2.0 |

上游及其许可协议文本可在各自仓库获取。Roo Code 与 Cline 的名称与标识版权归各自所有者;
ZooVS 为独立社区移植,与上述项目均无隶属或背书关系。

## 其他组件

- vscode-material-icons、ripgrep(@vscode/ripregrep)等随上游分发的第三方资产,
  其许可见上游仓库对应声明。
- Microsoft.VisualStudio.* SDK 组件按微软相应条款使用。
