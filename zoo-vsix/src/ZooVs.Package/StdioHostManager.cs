using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ZooVs.Bridge;
using ZooVs.Package;

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
		private string _workspacePath;
		private readonly int _maxRestarts = 3;

		private Process _process;
		private readonly object _stdinLock = new object();
		private volatile bool _stopped;
		private int _restartCount;

		/// <summary>扩展激活完成(webview 可加载)。</summary>
		public volatile bool IsReady;

		/// <summary>会话进行中(存在待批准 ask)——此时重启宿主会销毁运行中的任务,须推迟。</summary>
		public volatile bool ConversationActive;

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
			KillOrphanHosts();
			StartHostProcess();
			return Task.CompletedTask;
		}

		/// <summary>
		/// 工作区变化(用户切换解决方案)时重启宿主:node 的 ZOO_WORKSPACE 在 spawn 时固化,
		/// 不重启会让 @ 文件搜索等一直搜旧目录。IsReady 复位;webview 由调用方在就绪后重发握手。
		/// </summary>
		public async Task RestartWithWorkspaceAsync(string newWorkspace)
		{
			if (string.Equals(_workspacePath, newWorkspace, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			_workspacePath = newWorkspace;
			_stopped = true; // 抑制 OnProcessExited 的自动重启
			IsReady = false;
			try
			{
				if (_process != null && !_process.HasExited)
				{
					KillProcessTree(_process.Id);
					_process.WaitForExit(5000);
				}
			}
			catch { }
			_process = null;
			_stopped = false;
			_restartCount = 0;
			_host.Log("[host] 工作区变更,重启宿主:" + newWorkspace);
			StartHostProcess();
			await Task.CompletedTask;
		}

		/// <summary>
		/// 清理上次 VS 会话遗留的本扩展宿主进程(强杀/崩溃场景)。
		/// 孤儿判定 = 命令行含 host.cjs 全路径 且 父进程已退出——父链上仍有活跃 devenv 的
		/// 宿主属于另一个 VS 实例,误杀会导致对方实例瘫痪(多实例场景)。
		/// 注意:多实例仍共享同一数据目录,状态可能互踩(已知限制,日志提示)。
		/// </summary>
		private void KillOrphanHosts()
		{
			try
			{
				var marker = Path.Combine(_hostDirectory, "host.cjs").ToLowerInvariant();
				foreach (var o in new System.Management.ManagementObjectSearcher(
					"SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'").Get())
				{
					var mo = (System.Management.ManagementBaseObject)o;
					var cmd = (mo["CommandLine"] as string ?? "").ToLowerInvariant();
					if (cmd.Contains(marker))
					{
						var pid = Convert.ToInt32(mo["ProcessId"]);
						var ppid = Convert.ToInt32(mo["ParentProcessId"]);
						if (IsProcessAlive(ppid))
						{
							_host.Log("[host] 检测到另一活跃 VS 实例的宿主(pid=" + pid + "),跳过清理;注意多实例共享数据目录可能互踩状态");
							continue;
						}
						_host.Log("[host] 清理遗留宿主进程 pid=" + pid + "(父进程 " + ppid + " 已退出)");
						try { Process.GetProcessById(pid)?.Kill(); } catch { }
					}
				}
			}
			catch (Exception ex)
			{
				_host.Log("[host] 孤儿进程清理检查失败:" + ex.Message);
			}
		}

		private static bool IsProcessAlive(int pid)
		{
			if (pid <= 0) return false;
			try
			{
				return !Process.GetProcessById(pid).HasExited;
			}
			catch
			{
				return false; // 进程不存在
			}
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
			BindToShutdownJob(process);
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			_process = process;
		}

		private void BindToShutdownJob(Process process)
		{
			VsJobObject.Bind(process, _host.Log);
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
									ConversationActive = true;
									_ = _host.SetStatusBarAsync("Zoo Code 等待你在 ZooVS 面板中批准操作(如读取文件)");
									_host.Log("[ask] agent 已暂停,等待你在 ZooVS 面板中批准/拒绝操作");
								}
								else if (json.Contains("\"say\":") || json.Contains("\"type\":\"state\""))
								{
									ConversationActive = false;
								}
								_ = _host.InvokeOnUIThreadAsync(() => PostToWebview(json));
							}
							break;

						case "hostCall":
							// 扩展内 vs_* 工具请求 → 路由(如 Roslyn 子进程)→ 回写 hostCallResult
							if (root.TryGetProperty("id", out var callId) &&
								root.TryGetProperty("tool", out var toolEl))
							{
								var id = callId.GetInt64();
								var tool = toolEl.GetString() ?? "";
								var argsJson = root.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}";
								_ = Task.Run(async () =>
								{
									string ok; string payload;
									try
									{
										var router = HostCallRouter;
										if (router == null) throw new InvalidOperationException("宿主未注册工具路由");
										payload = await router(tool, argsJson);
										ok = "true";
									}
									catch (Exception ex)
									{
										payload = ex.Message;
										ok = "false";
									}
									var reply = new System.Text.Json.Nodes.JsonObject
									{
										["type"] = "hostCallResult",
										["id"] = id,
										["ok"] = ok == "true",
									};
									if (ok == "true")
									{
										reply["result"] = payload ?? "";
									}
									else
									{
										reply["error"] = payload ?? "";
									}
									await SendAsync(reply.ToJsonString());
								});
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

		/// <summary>vs_* 工具执行路由(由 Package 注册到 RoslynHostService 等)。</summary>
		public Func<string, string, Task<string>> HostCallRouter { get; set; }

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
