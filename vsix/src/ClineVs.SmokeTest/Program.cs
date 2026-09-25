using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClineVs.Bridge;
using ClineVs.Daemon;

namespace ClineVs.SmokeTest
{
	/// <summary>
	/// 无 VS 全链路冒烟:启动 BridgeServer → 拉起 cline-core daemon → 等待认证连入 →
	/// 经双向流调用 cline.StateService.getLatestState → 打印响应 JSON。
	/// </summary>
	internal sealed class ConsoleBridgeHost : IBridgeHost
	{
		public event Action ShutdownRequested;

		public Task<T> InvokeOnUIThreadAsync<T>(Func<T> func) => Task.FromResult(func());

		public Task InvokeOnUIThreadAsync(Action action)
		{
			action();
			return Task.CompletedTask;
		}

		public void Log(string message) => Console.WriteLine(message);

		public void NotifyShutdownRequested() { }

		public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
		public Task<string> GetClipboardTextAsync() => Task.FromResult(string.Empty);
		public Task<(string Platform, string Version)> GetHostVersionAsync() => Task.FromResult(("Console", "1.0.0"));

		public Task ShowTextDocumentAsync(string path) => Task.CompletedTask;
		public Task<string[]> ShowOpenDialogAsync(bool many, string label, string[] filters) => Task.FromResult(new string[0]);
		public Task<string> ShowMessageBoxAsync(string message, int type, bool modal, string detail, string[] items)
		{
			Console.WriteLine("[msgbox] " + message);
			return Task.FromResult(items?.FirstOrDefault());
		}
		public Task<string> ShowInputBoxAsync(string title, string prompt, string value) => Task.FromResult<string>(null);
		public Task<string> ShowSaveDialogAsync(string defaultPath, Dictionary<string, string[]> filters) => Task.FromResult<string>(null);
		public Task OpenFileAsync(string path) => Task.CompletedTask;
		public Task OpenSettingsAsync(string query) => Task.CompletedTask;
		public Task<string> GetActiveEditorPathAsync() => Task.FromResult<string>(null);
		public Task<string[]> GetOpenDocumentPathsAsync() => Task.FromResult(new string[0]);

		public Task<(string Id, string[] Paths)> GetWorkspaceInfoAsync()
		{
			var cwd = Directory.GetCurrentDirectory();
			return Task.FromResult((Id: cwd, Paths: new[] { cwd }));
		}

		public Task<bool> SaveDocumentIfDirtyAsync(string filePath) => Task.FromResult(false);
		public Task OpenProblemsPanelAsync() => Task.CompletedTask;
		public Task OpenInFileExplorerAsync(string path) => Task.CompletedTask;
		public Task OpenClinePanelAsync() => Task.CompletedTask;
		public Task<bool> OpenFolderAsync(string path, bool newWindow) => Task.FromResult(false);

		public Task OpenComparisonWindowAsync(string diffId, string left, string right, string caption)
		{
			Console.WriteLine($"[diff] {caption}: {left} <-> {right}");
			return Task.CompletedTask;
		}
		public Task CloseComparisonWindowAsync(string diffId) => Task.CompletedTask;
		public Task CloseAllComparisonWindowsAsync() => Task.CompletedTask;
	}

	internal static class Program
	{
		private static int Main(string[] args)
		{
			var daemonDir = args.Length > 0 ? args[0] : Path.GetFullPath(
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "assets", "daemon"));
			var dataDir = Path.Combine(Path.GetTempPath(), "ClineVS-smoke", "data");

			Console.WriteLine("== ClineVS 冒烟测试 ==");
			Console.WriteLine("daemon 目录: " + daemonDir);
			if (!File.Exists(Path.Combine(daemonDir, "cline-core.js")))
			{
				Console.Error.WriteLine("FATAL: 未找到 cline-core.js —— 请先运行 build-js.ps1 构建 daemon 产物");
				return 2;
			}

			var host = new ConsoleBridgeHost();
			var manager = new DaemonProcessManager(host, daemonDir, dataDir);

			var connected = new ManualResetEventSlim(false);
			manager.Connected += () => connected.Set();

			var done = new ManualResetEventSlim(false);
			var exitCode = 0;

			try
			{
				manager.StartAsync().GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("FATAL: 启动失败:" + ex);
				return 1;
			}

			Console.WriteLine("等待 daemon 反连认证(最长 90 秒)…");
			if (!connected.Wait(TimeSpan.FromSeconds(90)))
			{
				Console.Error.WriteLine("FATAL: daemon 未在 90 秒内完成认证连入");
				Cleanup(manager);
				return 3;
			}
			Console.WriteLine("== PASS 1/2:daemon 连入并认证成功 ==");

			// 经双向流调用一次 cline/* 服务(getLatestState, unary)
			var responded = new ManualResetEventSlim(false);
			var error = (string)null;
			var responsePreview = (string)null;

			var sendTask = manager.Channel.SendRequestAsync(
				"cline.StateService", "getLatestState", "{}", isStreaming: false,
				onMessageJson: json =>
				{
					responsePreview = json;
					responded.Set();
				},
				onError: err =>
				{
					error = err;
					responded.Set();
				},
				onCompleted: () => { });

			if (!responded.Wait(TimeSpan.FromSeconds(60)))
			{
				Console.Error.WriteLine("FATAL: getLatestState 60 秒未响应");
				Cleanup(manager);
				return 4;
			}
			if (error != null)
			{
				Console.Error.WriteLine("FATAL: getLatestState 返回错误:" + error);
				Cleanup(manager);
				return 5;
			}

			Console.WriteLine("== PASS 2/2:getLatestState 响应 ==");
			Console.WriteLine(responsePreview != null && responsePreview.Length > 400
				? responsePreview.Substring(0, 400) + " …(截断)"
				: responsePreview);

			Cleanup(manager);
			Console.WriteLine("== 冒烟测试全部通过 ==");
			done.Wait(0);
			return exitCode;

			void Cleanup(DaemonProcessManager m)
			{
				try { m.Dispose(); } catch { }
			}
		}
	}
}
