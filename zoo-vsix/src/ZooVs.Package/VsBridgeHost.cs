using System;
using System.Threading.Tasks;
// 消除 VS2019 SDK16 的 Microsoft.VisualStudio.Shell.Task 歧义(对 2022 无影响)
using Task = System.Threading.Tasks.Task;
using ZooVs.Bridge;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ZooVs.Package
{
	/// <summary>IBridgeHost 的 VSSDK 实现(UI 线程调度 + 输出窗格)。</summary>
	public sealed class VsBridgeHost : IBridgeHost
	{
		private readonly AsyncPackage _package;
		private IVsOutputWindowPane _outputPane;
		private readonly object _outputPaneLock = new object();

		public VsBridgeHost(AsyncPackage package)
		{
			_package = package;
		}

		public async Task<T> InvokeOnUIThreadAsync<T>(Func<T> func)
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			return func();
		}

		public async Task InvokeOnUIThreadAsync(Action action)
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			action();
		}

		public void Log(string message)
		{
			// 文件落地:输出窗格在启动卡死时不可见,日志文件是唯一可诊断通道
			try
			{
				var dir = System.IO.Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZooVS", "log");
				System.IO.Directory.CreateDirectory(dir);
				System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "zoovs.log"),
					DateTime.Now.ToString("HH:mm:ss.fff") + " [pid " + System.Diagnostics.Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
			}
			catch { }
			ThreadHelper.Generic.BeginInvoke(() =>
			{
				try
				{
					EnsureOutputPane();
					_outputPane?.OutputStringThreadSafe(message + Environment.NewLine);
				}
				catch { /* 日志尽力而为 */ }
			});
		}

		/// <summary>当前解决方案文件完整路径(.sln;无解决方案返回 null,由调用方兜底)。</summary>
		public async Task<string> GetSolutionFilePathAsync()
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			try
			{
				var dte = (EnvDTE80.DTE2)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
				var full = dte?.Solution?.FullName;
				if (!string.IsNullOrEmpty(full))
				{
					return full;
				}
			}
			catch { }
			return null;
		}

		public async Task<string> GetWorkspacePathAsync()
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			// 优先 IVsSolution.GetSolutionInfo:解决方案与"打开文件夹"模式都能给出根目录
			try
			{
				var sol = (IVsSolution)ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution));
				string solDir, solFile, userOpts;
				if (sol != null && sol.GetSolutionInfo(out solDir, out solFile, out userOpts) == 0 &&
					!string.IsNullOrEmpty(solDir))
				{
					if (!string.IsNullOrEmpty(solFile))
					{
						return System.IO.Path.GetDirectoryName(solFile);
					}
					return solDir; // 文件夹模式:无 sln 文件,目录即工作区
				}
			}
			catch { }
			try
			{
				var dte = (EnvDTE80.DTE2)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
				if (dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
				{
					return System.IO.Path.GetDirectoryName(dte.Solution.FullName);
				}
				var doc = dte?.ActiveDocument?.FullName;
				if (!string.IsNullOrEmpty(doc))
				{
					return System.IO.Path.GetDirectoryName(doc);
				}
			}
			catch { }
			return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		}

		/// <summary>工作区是否为兜底值(启动时 VS 尚未打开任何东西;此时不应冻结成 node 的 env)。</summary>
		public async Task<bool> IsWorkspaceFallbackAsync()
		{
			var resolved = await GetWorkspacePathAsync();
			return string.Equals(resolved,
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				StringComparison.OrdinalIgnoreCase);
		}

		public async Task SetStatusBarAsync(string message)
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			try
			{
				var statusBar = (IVsStatusbar)ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar));
				statusBar?.SetText(message);
			}
			catch { }
		}

		private void EnsureOutputPane()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (_outputPane != null) return;
			lock (_outputPaneLock)
			{
				if (_outputPane != null) return;
				var outputWindow = (IVsOutputWindow)ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow));
				if (outputWindow == null) return;

				var paneGuid = new Guid("b8e5d7c2-3f1a-4b96-8e2d-5c7f0a9b1e4d");
				outputWindow.CreatePane(ref paneGuid, "ZooVS", 1, 0);
				outputWindow.GetPane(ref paneGuid, out _outputPane);
				_outputPane?.Activate();
			}
		}
	}
}
