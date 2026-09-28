using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZooVs.Package
{
	/// <summary>
	/// ZooVS 内化的语言/构建能力宿主:以最小 MCP stdio 客户端驱动 ZooMcpServer.exe
	/// (Roslyn + MSBuild 子进程,与 devenv 程序集隔离——刻意不进 VS 进程,避免
	/// Microsoft.CodeAnalysis 版本冲突)。agent 侧通过自定义工具(vs_*)→ hostCall
	/// 通道到达这里;solutionPath 由本服务按当前 VS 解决方案自动注入,agent 无需知晓。
	/// 进程加入 VsJobObject:VS 关闭(含强杀)自动回收。
	/// </summary>
	public sealed class RoslynHostService : IDisposable
	{
		/// <summary>agent 工具名 → MCP 工具名。</summary>
		private static readonly Dictionary<string, string> ToolMap = new Dictionary<string, string>
		{
			{ "vs_solution_model", "get_solution_model" },
			{ "vs_symbol_search", "symbol_search" },
			{ "vs_find_references", "find_references" },
			{ "vs_find_implementations", "find_implementations" },
			{ "vs_type_hierarchy", "hierarchy" },
			{ "vs_find_calls", "find_calls" },
			{ "vs_find_referencing_files", "find_referencing_files" },
			{ "vs_build", "build" },
			{ "vs_completion_at", "completion_at" },
		};

		private readonly string _exePath;
		private string _workspacePath;
		private readonly Action<string> _log;
		private readonly Func<Task<string>> _solutionResolver;

		private Process _process;
		private readonly SemaphoreSlim _callLock = new SemaphoreSlim(1, 1);
		private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending =
			new ConcurrentDictionary<long, TaskCompletionSource<string>>();
		private long _seq; // MCP 请求 id(1=initialize 预占)
		private volatile bool _disposed;

		public RoslynHostService(string exePath, string workspacePath, Action<string> log, Func<Task<string>> solutionResolver)
		{
			_exePath = exePath;
			_workspacePath = workspacePath;
			_log = log;
			_solutionResolver = solutionResolver;
		}

		/// <summary>工作区变化(切换解决方案)时同步:文件夹模式 csproj 递归回退扫描用。</summary>
		public void UpdateWorkspace(string workspacePath)
		{
			_workspacePath = workspacePath;
		}

		/// <summary>agent 侧 hostCall 入口:tool=vs_*,argsJson 为 camelCase 参数对象。</summary>
		public async Task<string> CallToolAsync(string tool, string argsJson)
		{
			if (_disposed) throw new ObjectDisposedException("RoslynHostService");
			if (!ToolMap.TryGetValue(tool, out var mcpName))
			{
				return "未知工具:" + tool;
			}

			var solutionPath = await ResolveSolutionPathAsync();
			if (string.IsNullOrEmpty(solutionPath))
			{
				return "当前没有打开的解决方案(或工作区中未找到 .sln/.slnx),无法执行语言工具。";
			}

			await _callLock.WaitAsync();
			try
			{
				await EnsureStartedAsync();

				System.Text.Json.Nodes.JsonObject args;
				try
				{
					args = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) as System.Text.Json.Nodes.JsonObject
						?? new System.Text.Json.Nodes.JsonObject();
				}
				catch
				{
					args = new System.Text.Json.Nodes.JsonObject();
				}
				args["solutionPath"] = solutionPath;

				var response = await RequestAsync("tools/call", new System.Text.Json.Nodes.JsonObject
				{
					["name"] = mcpName,
					["arguments"] = args,
				}, TimeSpan.FromMinutes(5));

				return ExtractToolText(response) ?? "(空结果)";
			}
			finally
			{
				_callLock.Release();
			}
		}

		private async Task<string> ResolveSolutionPathAsync()
		{
			try
			{
				var fromDte = await _solutionResolver();
				if (!string.IsNullOrEmpty(fromDte)) return fromDte;
			}
			catch { }
			// DTE 拿不到时扫工作区:sln 在根目录;文件夹模式下 csproj 递归找
			// (跳过 node_modules/bin/obj,取第一个)——"打开文件夹"场景 vs_build/语言工具的退路。
			// 兜底目录(MyDocuments)禁止递归:全量扫 Documents 树可能挂死工具调用且持锁阻塞后续调用
			try
			{
				var isFallbackDir = string.Equals(_workspacePath,
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					StringComparison.OrdinalIgnoreCase);
				if (!string.IsNullOrEmpty(_workspacePath) && Directory.Exists(_workspacePath) && !isFallbackDir)
				{
					var sln = Directory.GetFiles(_workspacePath, "*.sln").FirstOrDefault()
						?? Directory.GetFiles(_workspacePath, "*.slnx").FirstOrDefault();
					if (sln != null) return sln;
					var csproj = Directory.EnumerateFiles(_workspacePath, "*.csproj", SearchOption.AllDirectories)
						.Where(f => !f.Contains("\\node_modules\\") && !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
						.FirstOrDefault();
					if (csproj != null) return csproj;
				}
			}
			catch { }
			return null;
		}

		// ---- MCP stdio 客户端 ----

		private async Task EnsureStartedAsync()
		{
			if (_process != null && !_process.HasExited) return;

			if (!File.Exists(_exePath))
			{
				throw new FileNotFoundException("未找到 Roslyn 宿主:" + _exePath);
			}

			var info = new ProcessStartInfo
			{
				FileName = _exePath,
				Arguments = "",
				WorkingDirectory = Path.GetDirectoryName(_exePath),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			_log("[roslyn] 启动语言服务子进程:" + _exePath);
			var process = new Process { StartInfo = info, EnableRaisingEvents = true };
			process.OutputDataReceived += OnMcpLine;
			process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log("[roslyn:stderr] " + e.Data); };
			// 子进程退出:立即失败全部挂起请求(否则只能等超时),并给出诊断线索
			process.Exited += (_, _) =>
			{
				var code = SafeExitCode(process);
				_log("[roslyn] 子进程退出(code=" + code + ")" +
					(code != 0 && string.IsNullOrEmpty(_lastStartError) ? ",疑似 .NET 8 运行时缺失或依赖损坏" : ""));
				foreach (var kv in _pending)
				{
					kv.Value.TrySetException(new InvalidOperationException(
						"Roslyn 子进程已退出(code=" + code + ")。若反复出现请检查是否安装 .NET 8 运行时。"));
				}
				_pending.Clear();
			};
			process.Start();
			VsJobObject.Bind(process, _log);
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			_process = process;
			_seq = 1;

			// MCP 握手
			await RequestAsync("initialize", new System.Text.Json.Nodes.JsonObject
			{
				["protocolVersion"] = "2024-11-05",
				["capabilities"] = new System.Text.Json.Nodes.JsonObject(),
				["clientInfo"] = new System.Text.Json.Nodes.JsonObject
				{
					["name"] = "zoovs",
					["version"] = "1.0",
				},
			}, TimeSpan.FromSeconds(60), isInitialize: true);
			WriteLine(new System.Text.Json.Nodes.JsonObject
			{
				["jsonrpc"] = "2.0",
				["method"] = "notifications/initialized",
			});
			_log("[roslyn] MCP 握手完成");
		}

		private async Task<System.Text.Json.JsonElement> RequestAsync(
			string method, System.Text.Json.Nodes.JsonObject paramss, TimeSpan timeout, bool isInitialize = false)
		{
			var process = _process;
			if (process == null || process.HasExited)
			{
				throw new InvalidOperationException("Roslyn 子进程未运行");
			}

			var id = isInitialize ? 1 : System.Threading.Interlocked.Increment(ref _seq);
			var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			_pending[id] = tcs;

			var request = new System.Text.Json.Nodes.JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["method"] = method,
			};
			if (paramss != null) request["params"] = paramss;
			WriteLine(request);

			var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
			if (winner != tcs.Task)
			{
				_pending.TryRemove(id, out _);
				throw new TimeoutException(method + " 超时(" + (int)timeout.TotalSeconds + "s)");
			}

			var json = await tcs.Task;
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			var root = doc.RootElement.Clone();
			if (root.TryGetProperty("error", out var err) && err.ValueKind != System.Text.Json.JsonValueKind.Null)
			{
				var msg = err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText();
				throw new InvalidOperationException(method + " 返回错误:" + msg);
			}
			return root.Clone(); // 调用方在 dispose 后使用 result 字段
		}

		private void OnMcpLine(object sender, DataReceivedEventArgs e)
		{
			var line = e.Data;
			if (string.IsNullOrEmpty(line)) return;
			try
			{
				using var doc = System.Text.Json.JsonDocument.Parse(line);
				var root = doc.RootElement;
				if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
				if (!root.TryGetProperty("id", out var idEl)) return; // 通知/日志忽略
				var id = idEl.ValueKind == System.Text.Json.JsonValueKind.Number ? idEl.GetInt64() : -1;
				if (_pending.TryRemove(id, out var tcs))
				{
					tcs.TrySetResult(line);
				}
			}
			catch
			{
				// 非 JSON 行(SDK 横幅等)忽略
			}
		}

		private static string ExtractToolText(System.Text.Json.JsonElement response)
		{
			if (!response.TryGetProperty("result", out var result)) return null;
			// content: [{type:"text", text:"..."}]
			if (result.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				var parts = content.EnumerateArray()
					.Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text")
					.Select(c => c.TryGetProperty("text", out var tx) ? tx.GetString() : null)
					.Where(t => t != null);
				var joined = string.Join("\n", parts);
				if (joined.Length > 0) return joined;
			}
			return result.GetRawText();
		}

		private void WriteLine(System.Text.Json.Nodes.JsonObject obj)
		{
			var process = _process;
			if (process == null || process.HasExited) return;
			try
			{
				process.StandardInput.WriteLine(obj.ToJsonString());
				process.StandardInput.Flush();
			}
			catch (Exception ex)
			{
				_log("[roslyn] stdin 写入失败:" + ex.Message);
			}
		}

		private static int SafeExitCode(Process process)
		{
			try { return process.ExitCode; } catch { return -1; }
		}

		/// <summary>string 字段留给诊断线索(如启动即退的场景),暂未使用。</summary>
		private string _lastStartError;

		public void Dispose()
		{
			_disposed = true;
			try
			{
				if (_process != null && !_process.HasExited)
				{
					// vs_build 会拉起 MSBuild 子进程:net472 无 Kill(bool),用 taskkill /T 杀树;
					// Job Object 兜底 VS 整体退出场景
					try
					{
						using (var killer = Process.Start(new ProcessStartInfo
						{
							FileName = "taskkill",
							Arguments = "/PID " + _process.Id + " /T /F",
							CreateNoWindow = true,
							UseShellExecute = false,
						}))
						{
							killer?.WaitForExit(5000);
						}
					}
					catch
					{
						try { _process.Kill(); } catch { }
					}
				}
				_process = null;
			}
			catch { }
		}
	}
}
