using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ZooVs.Bridge;

namespace ZooVs.Daemon
{
	/// <summary>
	/// Node 宿主(host.cjs)的进程管理 + 行式 JSON stdio 桥。
	/// 相比 gRPC:无需端口与令牌(stdio 无网络面)、单写者天然串行。
	/// </summary>
	public sealed class StdioHostManager : IDisposable
	{
		private readonly IBridgeHost _host;
		private readonly string _hostDirectory;   // host.cjs 所在(assets/host)
		private readonly string _extensionPath;   // 扩展 bundle(assets/dist)
		private readonly string _dataDirectory;
		private readonly string _nodeExecutable;
		private readonly string _workspacePath;
		private readonly int _maxRestarts = 3;

		private Process _process;
		private readonly object _stdinLock = new object();
		private volatile bool _stopped;
		private int _restartCount;

		/// <summary>扩展激活完成(webview 可加载)。</summary>
		public volatile bool IsReady;

		public event Action< long, string > UnknownMessage; // 预留

		public StdioHostManager(
			IBridgeHost host,
			string hostDirectory,
			string extensionPath,
			string dataDirectory,
			string workspacePath,
			string nodeExecutable = "node")
		{
			_host = host;
			_hostDirectory = hostDirectory;
			_extensionPath = extensionPath;
			_dataDirectory = dataDirectory;
			_workspacePath = workspacePath;
			_nodeExecutable = nodeExecutable;
		}

		public Task StartAsync()
		{
			_stopped = false;
			_restartCount = 0;
			Directory.CreateDirectory(_dataDirectory);
			StartHostProcess();
			return Task.CompletedTask;
		}

		private void StartHostProcess()
		{
			var entry = Path.Combine(_hostDirectory, "host.cjs");
			if (!File.Exists(entry))
			{
				throw new FileNotFoundException("未找到 host.cjs:" + entry);
			}

			var info = new ProcessStartInfo
			{
				FileName = _nodeExecutable,
				Arguments = "\"" + entry + "\"",
				WorkingDirectory = _hostDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				StandardOutputEncoding = Encoding.UTF8,
			};
			info.EnvironmentVariables["ZOO_EXTENSION_PATH"] = _extensionPath;
			info.EnvironmentVariables["ZOO_WORKSPACE"] = _workspacePath;
			info.EnvironmentVariables["ZOO_STORAGE_DIR"] = _dataDirectory;
			info.StandardOutputEncoding = Encoding.UTF8;

			_host.Log("[host] 启动 Node 宿主(" + _hostDirectory + ")");

			var process = new Process { StartInfo = info, EnableRaisingEvents = true };
			process.OutputDataReceived += OnStdoutLine;
			process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _host.Log("[host:stderr] " + e.Data); };
			process.Exited += OnProcessExited;

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			_process = process;
		}

		/// <summary>stdout 行解析:ready / extensionMessage / log。</summary>
		private void OnStdoutLine(object sender, DataReceivedEventArgs e)
		{
			var line = e.Data;
			if (string.IsNullOrEmpty(line)) return;
			try
			{
				using (var doc = System.Text.Json.JsonDocument.Parse(line))
				{
					var root = doc.RootElement;
					var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
					switch (type)
					{
						case "ready":
							IsReady = true;
							_host.Log("[host] 扩展激活完成,webview 可加载");
							break;

						case "extensionMessage":
							if (root.TryGetProperty("message", out var message))
							{
								var json = message.GetRawText();
								// ask(partial=false) = agent 停下等用户批准 —— 给出显式提示,
								// 否则用户以为"中断/卡死"(M1 实测教训)
								if (json.Contains("\"ask\":") && json.Contains("\"partial\":false"))
								{
									_ = _host.SetStatusBarAsync("Zoo Code 等待你在 ZooVS 面板中批准操作(如读取文件)");
									_host.Log("[ask] agent 已暂停,等待你在 ZooVS 面板中批准/拒绝操作");
								}
								_ = _host.InvokeOnUIThreadAsync(() => PostToWebview(json));
							}
							break;

						case "log":
							var text = root.TryGetProperty("text", out var txt) ? txt.GetString() : line;
							_host.Log("[host] " + text);
							break;

						default:
							_host.Log("[host?] " + line.Substring(0, Math.Min(line.Length, 200)));
							break;
					}
				}
			}
			catch (Exception ex)
			{
				_host.Log("[host] 行解析失败:" + ex.Message);
			}
		}

		/// <summary>host → webview 的最近一次投递回调(由 controller 注册)。</summary>
		public Action<string> WebviewPostHandler { get; set; }

		private void PostToWebview(string messageJson)
		{
			try
			{
				WebviewPostHandler?.Invoke(messageJson);
			}
			catch (Exception ex)
			{
				_host.Log("[bridge] webview 投递失败:" + ex.Message);
			}
		}

		/// <summary>向宿主 stdin 写一行 JSON(webviewMessage / webviewReady)。调用方须在 UI 线程之外。</summary>
		public Task SendAsync(string line)
		{
			var process = _process;
			if (process == null || process.HasExited || process.StandardInput == null)
			{
				_host.Log("[bridge] 宿主未运行,丢弃消息:" + line.Substring(0, Math.Min(line.Length, 120)));
				return Task.CompletedTask;
			}
			try
			{
				lock (_stdinLock)
				{
					process.StandardInput.WriteLine(line);
					process.StandardInput.Flush();
				}
			}
			catch (Exception ex)
			{
				_host.Log("[bridge] stdin 写入失败:" + ex.Message);
			}
			return Task.CompletedTask;
		}

		private void OnProcessExited(object sender, EventArgs e)
		{
			if (_stopped) return;
			_host.Log("[host] Node 宿主进程退出(exit=" + (sender as Process)?.ExitCode + ")");
			if (_restartCount < _maxRestarts)
			{
				_restartCount++;
				_host.Log("[host] 3 秒后自动重启(" + _restartCount + "/" + _maxRestarts + ")…");
				Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(t =>
				{
					if (_stopped) return;
					try { StartHostProcess(); }
					catch (Exception ex) { _host.Log("[host] 重启失败:" + ex.Message); }
				});
			}
		}

		public async Task StopAsync()
		{
			_stopped = true;
			try
			{
				if (_process != null && !_process.HasExited)
				{
					KillProcessTree(_process.Id);
					_process.WaitForExit(5000);
				}
			}
			catch (Exception ex)
			{
				_host.Log("[host] 停止失败:" + ex.Message);
			}
			_process = null;
			await Task.CompletedTask;
		}

		private static void KillProcessTree(int pid)
		{
			try
			{
				using (var killer = Process.Start(new ProcessStartInfo
				{
					FileName = "taskkill",
					Arguments = "/PID " + pid + " /T /F",
					CreateNoWindow = true,
					UseShellExecute = false,
				}))
				{
					killer?.WaitForExit(5000);
				}
			}
			catch
			{
				try { Process.GetProcessById(pid)?.Kill(); } catch { }
			}
		}

		public void Dispose()
		{
			StopAsync().Wait(TimeSpan.FromSeconds(5));
		}
	}
}
