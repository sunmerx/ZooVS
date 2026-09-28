using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZooVs.Bridge;
using ZooVs.Daemon;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ZooVs.Package
{
	/// <summary>扩展入口:按需启动 Node 宿主(首次打开工具窗口),VS 关闭时回收。</summary>
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	[Guid(Guids.PackageString)]
	[ProvideToolWindow(typeof(ZooToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.Outputwindow)]
	[ProvideMenuResource("Menus.ctmenu", 1)]
	public sealed class ZooVsPackage : AsyncPackage, IVsSolutionEvents
	{
		private VsBridgeHost _bridgeHost;
		private StdioHostManager _hostManager;
		private ZooWebviewController _controller;
		private RoslynHostService _roslynHost;
		private DebugHostService _debugHost;
		private VsWorkspaceService _vsWorkspace;
		private VsInteropService _interop;
		private bool _started;

		/// <summary>包实例(工具窗口自拉起兜底:Initialize 时 Package 属性可能尚未赋值)。</summary>
		public static ZooVsPackage Instance { get; private set; }

		public VsBridgeHost BridgeHost => _bridgeHost;
		public StdioHostManager HostManager => _hostManager;
		public ZooWebviewController Controller => _controller;

		protected override async Task InitializeAsync(
			CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			await base.InitializeAsync(cancellationToken, progress);
			await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
			Instance = this;

			var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
			if (commandService != null)
			{
				commandService.AddCommand(new OleMenuCommand(
					(s, e) => _ = ShowToolWindowAsync(),
					new CommandID(Guids.CommandSet, CommandIds.OpenZooToolWindow)));

				commandService.AddCommand(new OleMenuCommand(
					(s, e) => _ = ShowToolWindowAsync(),
					new CommandID(Guids.CommandSet, CommandIds.OpenZooToolWindowView)));
			}
		}

		public async Task EnsureStartedAsync()
		{
			if (_started) return;
			_started = true;

			await JoinableTaskFactory.SwitchToMainThreadAsync();

			var extensionDir = GetExtensionInstallDirectory();
			var hostDir = System.IO.Path.Combine(extensionDir, "assets", "host");
			var distDir = System.IO.Path.Combine(extensionDir, "assets", "dist");
			var dataDir = System.IO.Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
				"ZooVS", "data");

			_bridgeHost = new VsBridgeHost(this);
			// 行内补全统一配置源:从 Zoo 数据目录读 provider(endpoint/key/model)
			InlineCompletionSettings.ZooStorageDir = dataDir;
			// ghost text 日志 → 输出窗格(区分 Zoo 与 VS 自带 IntelliCode)
			InlineCompletionSettings.Sink = _bridgeHost.Log;
			InlineCompletionSettings.Reload(); // MEF 首次访问可能早于注入且已缓存,注入后强制重载
			// 工作区 = 解决方案/文件夹模式根目录。VS 启动早期(自拉起)往往还没打开任何东西,
			// 此时解析会落到 MyDocuments 兜底并固化进 node env → @ 文件搜索搜错目录。
			// 故:兜底时轮询等待真实工作区(最多 120s),等不到才用兜底启动。
			var workspacePath = await ResolveWorkspaceForStartAsync();
			_currentWorkspace = workspacePath;
			_bridgeHost.Log("[package] 工作区:" + workspacePath);
			_hostManager = new StdioHostManager(_bridgeHost, hostDir, distDir, dataDir, workspacePath);
			// ZooVS 内化语言/构建能力:vs_* 走进程内 VS SDK(解决方案模型+VS Roslyn 工作区),
			// 拿不到工作区/构建时回退 Roslyn 子进程(见 VsWorkspaceService)
			var roslynExe = System.IO.Path.Combine(extensionDir, "assets", "roslyn", "ZooMcpServer.exe");
			_roslynHost = new RoslynHostService(roslynExe, workspacePath, _bridgeHost.Log, _bridgeHost.GetSolutionFilePathAsync);
			// 自主调试(C5):vs_debug_* 走进程内 DTE 自动化(见 DebugHostService)
			try { _debugHost = new DebugHostService(this, _bridgeHost.Log); }
			catch (Exception ex) { _bridgeHost.Log("[package] DebugHostService 创建失败(调试工具降级):" + ex.Message); }
			try { _vsWorkspace = new VsWorkspaceService(this, _bridgeHost.Log, _roslynHost); }
			catch (Exception ex) { _bridgeHost.Log("[package] VsWorkspaceService 创建失败(语言工具回退子进程,原因多为 Roslyn 解析):" + ex.GetType().Name + ": " + ex.Message); }
			// 交互桥(A1 diff/A3 编辑器上下文/A4 对话框/A6 诊断/A7 剪贴板):internal_* + 推送
			try
			{
				_interop = new VsInteropService(this, _bridgeHost.Log, _vsWorkspace);
				_interop.SendLine = line => _hostManager.SendAsync(line);
			}
			catch (Exception ex) { _bridgeHost.Log("[package] VsInteropService 创建失败(交互桥降级):" + ex.Message); }
			_hostManager.HostCallRouter = (tool, argsJson) =>
			{
				try
				{
					if (tool.StartsWith("vs_debug_", StringComparison.Ordinal) && _debugHost != null)
						return _debugHost.CallToolAsync(tool, argsJson);
					if (_interop != null && (tool.StartsWith("internal_", StringComparison.Ordinal) ||
						   tool == "vs_editor_context" || tool == "vs_diagnostics"))
						return _interop.CallToolAsync(tool, argsJson);
					if (_vsWorkspace != null) return _vsWorkspace.CallToolAsync(tool, argsJson);
					return _roslynHost != null
						? _roslynHost.CallToolAsync(tool, argsJson)
						: Task.FromResult("服务不可用");
				}
				catch (Exception ex)
				{
					return Task.FromResult("路由异常: " + ex.Message);
				}
			};
			_controller = new ZooWebviewController(this, _bridgeHost, _hostManager);
			_hostManager.WebviewPostHandler = _controller.PostExtensionMessageToWebview;

			try
			{
				await _hostManager.StartAsync();
				_interop?.Start();
				await AdviseSolutionEventsAsync();
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[package] Node 宿主启动失败:" + ex.Message);
				_started = false;
				throw;
			}
		}

		private string _currentWorkspace;
		private uint _solutionEventsCookie;
		private volatile bool _workspaceRestartInFlight;

		/// <summary>启动期工作区解析:兜底(MyDocuments)时等待真实工作区,最多 120 秒。</summary>
		private async Task<string> ResolveWorkspaceForStartAsync()
		{
			var workspacePath = await _bridgeHost.GetWorkspacePathAsync();
			if (!await _bridgeHost.IsWorkspaceFallbackAsync())
			{
				return workspacePath;
			}
			_bridgeHost.Log("[package] VS 尚未打开解决方案/文件夹,等待工作区就绪(最多 12s)…");
			for (int i = 0; i < 4; i++)
			{
				await Task.Delay(TimeSpan.FromSeconds(3));
				if (await _bridgeHost.IsWorkspaceFallbackAsync())
				{
					continue;
				}
				var resolved = await _bridgeHost.GetWorkspacePathAsync();
				_bridgeHost.Log("[package] 工作区已就绪:" + resolved);
				return resolved;
			}
			_bridgeHost.Log("[package] 等待超时,以兜底目录启动(MyDocuments)");
			return workspacePath;
		}

		private async Task AdviseSolutionEventsAsync()
		{
			try
			{
				var solution = (IVsSolution)await GetServiceAsync(typeof(SVsSolution));
				solution?.AdviseSolutionEvents(this, out _solutionEventsCookie);
			}
			catch (Exception ex)
			{
				_bridgeHost?.Log("[package] 订阅解决方案事件失败:" + ex.Message);
			}
		}

		/// <summary>解决方案打开/关闭后工作区变化 → 热更新推送(不重启宿主:
		/// 活 webview + 全新后端会导致前端不再握手、state 断供白屏,实测三次)。</summary>
		private async Task OnWorkspaceMaybeChangedAsync()
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(2)); // 等 DTE 状态稳定(事件连发)
				if (_workspaceRestartInFlight || _hostManager == null) return;
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				var next = await _bridgeHost.GetWorkspacePathAsync();
				if (string.Equals(next, _currentWorkspace, StringComparison.OrdinalIgnoreCase)) return;
				if (await _bridgeHost.IsWorkspaceFallbackAsync())
				{
					_bridgeHost.Log("[package] 工作区解析暂为兜底值,忽略本次切换");
					return;
				}
				_workspaceRestartInFlight = true;
				try
				{
					_currentWorkspace = next;
					_roslynHost?.UpdateWorkspace(next);
					await _hostManager.SendAsync("{\"type\":\"workspaceChanged\",\"workspace\":" +
						System.Text.Json.JsonEncodedText.Encode(next).ToString() + "}");
					_bridgeHost.Log("[package] 工作区已热切换:" + next);
				}
				finally
				{
					_workspaceRestartInFlight = false;
				}
			}
			catch (Exception ex)
			{
				_bridgeHost?.Log("[package] 工作区切换处理失败:" + ex.Message);
			}
		}

		public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
		{
			_ = OnWorkspaceMaybeChangedAsync();
			return Microsoft.VisualStudio.VSConstants.S_OK;
		}

		public int OnAfterCloseSolution(object pUnkReserved)
		{
			_ = OnWorkspaceMaybeChangedAsync();
			return Microsoft.VisualStudio.VSConstants.S_OK;
		}

		// ---- IVsSolutionEvents 其余成员(本扩展只关心开/关解决方案) ----
		public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnAfterMergeSolution(object pUnkReserved) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) { return Microsoft.VisualStudio.VSConstants.S_OK; }
		public int OnBeforeCloseSolution(object pUnkReserved) { return Microsoft.VisualStudio.VSConstants.S_OK; }

		public async Task ShowToolWindowAsync()
		{
			await EnsureStartedAsync();
			await JoinableTaskFactory.SwitchToMainThreadAsync();

			var window = (ToolWindowPane)await FindToolWindowAsync(
				typeof(ZooToolWindow), 0, true, DisposalToken);
			if (window?.Frame == null)
			{
				throw new NotSupportedException("无法创建 ZooVS 工具窗口");
			}
			var frame = (IVsWindowFrame)window.Frame;
			Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

			await _controller.InitializeAsync();
		}

		/// <summary>
		/// 工具窗口自恢复路径(VS 启动直接创建窗格时)调用:
		/// 只做启动宿主 + 初始化 webview,不强制 Show。
		/// </summary>
		public async Task AutoStartAsync()
		{
			try
			{
				await EnsureStartedAsync();
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				await _controller.InitializeAsync();
			}
			catch (Exception ex)
			{
				_bridgeHost?.Log("[package] 自拉起失败:" + ex.Message);
			}
		}

		private string GetExtensionInstallDirectory()
		{
			var uri = new Uri(typeof(ZooVsPackage).Assembly.CodeBase ?? typeof(ZooVsPackage).Assembly.Location);
			return System.IO.Path.GetDirectoryName(uri.LocalPath);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
					try
					{
						_controller?.DisposeAsync().GetAwaiter().GetResult();
						_interop?.Dispose();
						_debugHost?.Dispose();
						_roslynHost?.Dispose();
						_hostManager?.StopAsync().GetAwaiter().GetResult();
					}
				catch { /* 关闭时尽力而为 */ }
			}
			base.Dispose(disposing);
		}
	}
}
