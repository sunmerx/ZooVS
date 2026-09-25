using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ClineVs.Bridge;

namespace ClineVs.Daemon
{
	/// <summary>
	/// cline-core daemon 的进程管理:挑选空闲端口、生成一次性令牌、启动 BridgeServer、
	/// 以约定的环境变量拉起 node cline-core.js,并监控其生命周期。
	/// 契约见上游 apps/vscode/src/standalone/cline-core.ts:
	///   HOST_BRIDGE_ADDRESS=host:port(必填,daemon 先探活再反连)
	///   CLINE_CORE_CONNECTION_TOKEN=token(提供后走 HostBridge 模式,不开 protobus)
	///   CLINE_CORE_INSTANCE_ID=guid(与 token 配套)
	/// </summary>
	public sealed class DaemonProcessManager : IDisposable
	{
		private readonly IBridgeHost _host;
		private readonly string _daemonDirectory;
		private readonly string _dataDirectory;
		private readonly string _nodeExecutable;
		private readonly int _maxRestarts = 3;

		private BridgeServer _bridge;
		private Process _process;
		private volatile bool _stopped;
		private int _restartCount;

		public event Action Connected;
		public event Action<bool> Exited; // 参数:是否异常退出

		public int BridgePort => _bridge?.BoundPort ?? 0;
		public bool IsDaemonRunning => _process != null && !_process.HasExited;
		public bool IsBridgeConnected => _bridge?.Channel.IsConnected ?? false;

		/// <summary>已认证的 core 连接通道(daemon 连入后可用)。</summary>
		public CoreConnectionChannel Channel => _bridge?.Channel;

		public DaemonProcessManager(
			IBridgeHost host,
			string daemonDirectory,
			string dataDirectory,
			string nodeExecutable = "node")
		{
			_host = host;
			_daemonDirectory = daemonDirectory;
			_dataDirectory = dataDirectory;
			_nodeExecutable = nodeExecutable;
		}

		/// <summary>启动宿主桥 + daemon。返回实际使用的桥接端口。</summary>
		public async Task StartAsync(int preferredBridgePort = 0)
		{
			_stopped = false;
			_restartCount = 0;

			var token = GenerateToken();
			_bridge = new BridgeServer(_host, token);
			_bridge.Channel.Connected += () => { try { Connected?.Invoke(); } catch { } };
			_bridge.Start(preferredBridgePort);

			Directory.CreateDirectory(_dataDirectory);
			StartDaemonProcess(token);

			await _host.InvokeOnUIThreadAsync(() => { });
		}

		private static string GenerateToken()
		{
			using (var rng = RandomNumberGenerator.Create())
			{
				var bytes = new byte[32];
				rng.GetBytes(bytes);
				return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
			}
		}

		private void StartDaemonProcess(string token)
		{
			var entry = Path.Combine(_daemonDirectory, "cline-core.js");
			if (!File.Exists(entry))
			{
				throw new FileNotFoundException("未找到 daemon 入口:" + entry);
			}

			var instanceId = Guid.NewGuid().ToString("N");
			var info = new ProcessStartInfo
			{
				FileName = _nodeExecutable,
				Arguments = "\"" + entry + "\" --config \"" + _dataDirectory + "\"",
				WorkingDirectory = _daemonDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			// 与上游 JetBrains 插件一致的令牌约定;daemon 会捕获后立即从环境中清除
			info.EnvironmentVariables["HOST_BRIDGE_ADDRESS"] = "127.0.0.1:" + _bridge.BoundPort;
			info.EnvironmentVariables["CLINE_CORE_CONNECTION_TOKEN"] = token;
			info.EnvironmentVariables["CLINE_CORE_INSTANCE_ID"] = instanceId;
			info.EnvironmentVariables["CLINE_DATA_DIR"] = _dataDirectory;

			_host.Log("[daemon] 启动 cline-core(bridge=127.0.0.1:" + _bridge.BoundPort + ", instance=" + instanceId + ")");

			var process = new Process { StartInfo = info, EnableRaisingEvents = true };
			process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _host.Log("[core] " + e.Data); };
			process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _host.Log("[core:err] " + e.Data); };
			process.Exited += OnProcessExited;

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			_process = process;
		}

		private void OnProcessExited(object sender, EventArgs e)
		{
			var abnormal = !_stopped;
			_host.Log("[daemon] cline-core 进程退出(exit=" + (sender as Process)?.ExitCode + ")");

			if (_stopped) { try { Exited?.Invoke(false); } catch { } return; }

			if (_restartCount < _maxRestarts)
			{
				_restartCount++;
				_host.Log("[daemon] 3 秒后自动重启(" + _restartCount + "/" + _maxRestarts + ")…");
				Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(t =>
				{
					if (_stopped) return;
					try
					{
						StartDaemonProcess(GenerateToken());
					}
					catch (Exception ex)
					{
						_host.Log("[daemon] 重启失败:" + ex.Message);
						try { Exited?.Invoke(true); } catch { }
					}
				});
			}
			else
			{
				try { Exited?.Invoke(true); } catch { }
			}
		}

		/// <summary>停止 daemon 与宿主桥。Kill 整个进程树(node 会派生 rg/git/MCP 子进程)。</summary>
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
				_host.Log("[daemon] 停止进程失败:" + ex.Message);
			}
			_process = null;

			if (_bridge != null)
			{
				await _bridge.StopAsync();
				_bridge = null;
			}
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
