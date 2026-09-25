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
	public sealed class ZooVsPackage : AsyncPackage
	{
		private VsBridgeHost _bridgeHost;
		private StdioHostManager _hostManager;
		private ZooWebviewController _controller;
		private bool _started;

		public VsBridgeHost BridgeHost => _bridgeHost;
		public StdioHostManager HostManager => _hostManager;
		public ZooWebviewController Controller => _controller;

		protected override async Task InitializeAsync(
			CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			await base.InitializeAsync(cancellationToken, progress);
			await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

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
			_hostManager = new StdioHostManager(_bridgeHost, hostDir, distDir, dataDir);
			_controller = new ZooWebviewController(this, _bridgeHost, _hostManager);
			_hostManager.WebviewPostHandler = _controller.PostExtensionMessageToWebview;

			try
			{
				await _hostManager.StartAsync();
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[package] Node 宿主启动失败:" + ex.Message);
				_started = false;
				throw;
			}
		}

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
					_hostManager?.StopAsync().GetAwaiter().GetResult();
				}
				catch { /* 关闭时尽力而为 */ }
			}
			base.Dispose(disposing);
		}
	}
}
