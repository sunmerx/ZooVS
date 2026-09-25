using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel.Design;
using ClineVs.Bridge;
using ClineVs.Daemon;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClineVs.Package
{
	/// <summary>
	/// 扩展入口。按需启动 daemon/桥接(首次打开工具窗口时),VS 关闭时回收。
	/// </summary>
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	[Guid(Guids.PackageString)]
	[ProvideToolWindow(typeof(ClineToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.Outputwindow)]
	[ProvideMenuResource("Menus.ctmenu", 1)]
	public sealed class ClineVsPackage : AsyncPackage
	{
		private VsBridgeHost _bridgeHost;
		private DaemonProcessManager _daemon;
		private ClineWebviewController _controller;
		private bool _started;

		public VsBridgeHost BridgeHost => _bridgeHost;
		public DaemonProcessManager Daemon => _daemon;
		public ClineWebviewController Controller => _controller;

		protected override async Task InitializeAsync(
			CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			await base.InitializeAsync(cancellationToken, progress);

			await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

			// VS 主菜单 Tools > ClineVS > Open ClineVS Chat
			var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
			if (commandService != null)
			{
				var openCommand = new OleMenuCommand(
					(s, e) => _ = ShowToolWindowAsync(),
					new CommandID(Guids.CommandSet, CommandIds.OpenClineToolWindow));
				openCommand.BeforeQueryStatus += (s, e) => { /* 始终可见 */ };
				commandService.AddCommand(openCommand);

				// View > Other Windows > ClineVS Chat
				var viewCommand = new OleMenuCommand(
					(s, e) => _ = ShowToolWindowAsync(),
					new CommandID(Guids.CommandSet, CommandIds.OpenClineToolWindowView));
				commandService.AddCommand(viewCommand);
			}
		}

		/// <summary>确保 daemon/桥接/控制器已启动(幂等)。</summary>
		public async Task EnsureStartedAsync()
		{
			if (_started) return;
			_started = true;

			await JoinableTaskFactory.SwitchToMainThreadAsync();

			var extensionDir = GetExtensionInstallDirectory();
			var daemonDir = System.IO.Path.Combine(extensionDir, "assets", "daemon");
			var dataDir = System.IO.Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
				"ClineVS", "data");

			_bridgeHost = new VsBridgeHost(this);
			_bridgeHost.ShutdownRequested += () => _ = StopAsync();

			_daemon = new DaemonProcessManager(_bridgeHost, daemonDir, dataDir);
			_daemon.Connected += () => _bridgeHost.Log("[package] cline-core 已连入宿主桥");
			_daemon.Exited += abnormal =>
			{
				if (abnormal)
				{
					_bridgeHost.Log("[package] cline-core 异常退出且重试耗尽,请查看输出窗格");
				}
			};

			_controller = new ClineWebviewController(this, _bridgeHost, _daemon);

			try
			{
				await _daemon.StartAsync();
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[package] daemon 启动失败:" + ex.Message);
				_started = false;
				throw;
			}
		}

		public async Task ShowToolWindowAsync()
		{
			await EnsureStartedAsync();

			await JoinableTaskFactory.SwitchToMainThreadAsync();

			var window = (ToolWindowPane)await FindToolWindowAsync(
				typeof(ClineToolWindow), 0, true, DisposalToken);
			if (window?.Frame == null)
			{
				throw new NotSupportedException("无法创建 ClineVS 工具窗口");
			}

			var frame = (IVsWindowFrame)window.Frame;
			Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

			await _controller.InitializeAsync();
		}

		private string GetExtensionInstallDirectory()
		{
			var uri = new Uri(typeof(ClineVsPackage).Assembly.CodeBase ?? typeof(ClineVsPackage).Assembly.Location);
			return System.IO.Path.GetDirectoryName(uri.LocalPath);
		}

		private async Task StopAsync()
		{
			try
			{
				if (_controller != null) await _controller.DisposeAsync();
				if (_daemon != null) await _daemon.StopAsync();
			}
			catch { /* 关闭时尽力而为 */ }
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				try { StopAsync().GetAwaiter().GetResult(); } catch { }
			}
			base.Dispose(disposing);
		}
	}
}
