using System;
using System.Threading.Tasks;
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
