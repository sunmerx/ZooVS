using System;
using System.IO;
using System.Threading.Tasks;
using ClineVs.Bridge;
using ClineVs.Daemon;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;

namespace ClineVs.Package
{
	/// <summary>
	/// webview ↔ bridge 的消息路由:
	///   webview 出站(经注入的 standalonePostMessage,字符串被 WebView2 编码为 JSON 字符串)
	///     {type:"grpc_request", grpc_request:{service, method, message(对象), request_id, is_streaming}}
	///     → 校验 is_streaming 与 proto 契约一致后,经 CoreConnectionChannel 以 message_json 透传给 cline-core;
	///   core 响应(分块 JSON 已由通道重组)→ 回发
	///     {type:"grpc_response", grpc_response:{request_id, message?(对象), error?, is_streaming}}。
	/// </summary>
	public sealed class ClineWebviewController
	{
		private const string VirtualHost = "clinevs.app";

		private static readonly string BridgeScript =
			"(() => {" +
			"  'use strict';" +
			"  chrome.webview.addEventListener('message', e => { window.postMessage(e.data, '*'); });" +
			"  window.standalonePostMessage = (json) => { chrome.webview.postMessage(json); };" +
			// 主题注入:Cline 前端全部颜色来自 --vscode-* 变量,宿主必须提供(否则视觉空白)
			"  const applyTheme = () => {" +
			"    try {" +
			"      const vars = JSON.parse(" + NewtonsoftJson(WebviewTheme.VarsJson) + ");" +
			"      Object.entries(vars).forEach(([k, v]) => document.documentElement.style.setProperty(k, v));" +
			"      document.documentElement.classList.add('dark');" +
			"      document.body.style.backgroundColor = vars['--vscode-editor-background'];" +
			"      document.body.style.color = vars['--vscode-editor-foreground'];" +
			"    } catch (e) {}" +
			"  };" +
			"  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', applyTheme); } else { applyTheme(); }" +
			// 诊断:前端 console 与全局异常转发到宿主日志
			"  const post = (level, text) => { try { chrome.webview.postMessage(JSON.stringify({ type: 'clinevs_console', level: level, text: String(text).slice(0, 400) })); } catch (e) {} };" +
			"  ['log', 'info', 'warn', 'error'].forEach(level => {" +
			"    const orig = console[level] ? console[level].bind(console) : function() {};" +
			"    console[level] = (...args) => {" +
			"      try { post(level, args.map(a => typeof a === 'string' ? a : JSON.stringify(a)).join(' ')); } catch (e) {}" +
			"      orig.apply(console, args);" +
			"    };" +
			"  });" +
			"  window.addEventListener('error', e => post('error', 'JS error: ' + e.message + ' @' + (e.filename || '?') + ':' + e.lineno));" +
			"  window.addEventListener('unhandledrejection', e => post('error', 'Unhandled rejection: ' + ((e.reason && e.reason.stack) || e.reason)));" +
			"  console.log('[ClineVS] bridge installed');" +
			"})();";

		/// <summary>把 JSON 字面量安全嵌入 JS 字符串(转义引号与反斜杠)。</summary>
		private static string NewtonsoftJson(string json)
		{
			return "\"" + json.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
		}

		private readonly AsyncPackage _package;
		private readonly VsBridgeHost _bridgeHost;
		private readonly DaemonProcessManager _daemon;
		private Microsoft.Web.WebView2.Wpf.WebView2 _webView;
		private StreamingMethodRegistry _streamingMethods;
		private volatile bool _initialized;

		public ClineWebviewController(AsyncPackage package, VsBridgeHost bridgeHost, DaemonProcessManager daemon)
		{
			_package = package;
			_bridgeHost = bridgeHost;
			_daemon = daemon;
		}

		/// <summary>由 Package.ShowToolWindowAsync 在窗口创建后调用;幂等。</summary>
		public async Task InitializeAsync()
		{
			if (_initialized) return;
			_initialized = true;

			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

			_streamingMethods = new StreamingMethodRegistry();
			_bridgeHost.Log("[package] 流式方法契约:" + _streamingMethods.Count + " 个");

			var toolWindow = _package.FindToolWindow(typeof(ClineToolWindow), 0, true) as ClineToolWindow;
			if (toolWindow == null) return;

			var extensionDir = GetExtensionDirectory();
			var webviewDir = Path.Combine(extensionDir, "assets", "webview");
			if (!File.Exists(Path.Combine(webviewDir, "index.html")))
			{
				toolWindow.ShowMessage(
					"ClineVS webview assets missing.\nRun build-js.ps1 to produce assets/webview.");
				return;
			}

			// 前端加载后会立即发出全部初始订阅且不会重试,必须等 daemon 反连就绪再加载页面,
			// 否则出现白屏(时序竞争)。
			toolWindow.ShowMessage("正在等待 cline-core 启动…");
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
			while ((_daemon.Channel == null || !_daemon.Channel.IsConnected) && DateTime.UtcNow < deadline)
			{
				await Task.Delay(250);
			}
			if (_daemon.Channel == null || !_daemon.Channel.IsConnected)
			{
				toolWindow.ShowMessage(
					"cline-core 未能在 90 秒内连入宿主桥,请查看 ClineVS 输出窗格排查。");
				return;
			}
			_bridgeHost.Log("[package] core 已就绪,加载 webview");

			var webView = new Microsoft.Web.WebView2.Wpf.WebView2
			{
				CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
				{
					UserDataFolder = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						"ClineVS", "WebView2"),
				},
				DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 30),
			};
			_webView = webView;
			toolWindow.AttachWebView(webView);

			await webView.EnsureCoreWebView2Async();

			var core = webView.CoreWebView2;
			core.Settings.AreDefaultContextMenusEnabled = false;
			core.Settings.AreDevToolsEnabled = true; // 开发期保留,界面异常时可右键检查

			core.SetVirtualHostNameToFolderMapping(
				VirtualHost, webviewDir,
				CoreWebView2HostResourceAccessKind.Allow);

			await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

			core.WebMessageReceived += OnWebMessageReceived;

			webView.Source = new Uri("https://" + VirtualHost + "/index.html");

			// 等首轮导航结束后抓取页面实际状态,判断前端是否渲染
			core.NavigationCompleted += async (s, e) =>
			{
				try
				{
					await Task.Delay(2500); // 留给 React 挂载

					// 控件尺寸诊断:宽高为 0 = 控件不可见(布局问题),而非页面问题
					var hostEl = webView.Parent as System.Windows.FrameworkElement;
					_bridgeHost.Log(string.Format(
						"[webview:layout] WebView2 实际尺寸={0}x{1} 可见={2} 宿主容器={3}x{4}",
						webView.ActualWidth, webView.ActualHeight,
						webView.Visibility,
						hostEl?.ActualWidth ?? -1, hostEl?.ActualHeight ?? -1));

					var diag = await core.ExecuteScriptAsync(
						"JSON.stringify({" +
						"href: location.href," +
						"title: document.title," +
						"rootChildren: document.getElementById('root')?.children.length ?? -1," +
						"bodyText: (document.body?.innerText || '').slice(0, 120)," +
						"htmlClass: document.documentElement.className," +
						"bodyBg: getComputedStyle(document.body).backgroundColor," +
						"bodyColor: getComputedStyle(document.body).color," +
						"hasBridge: typeof window.standalonePostMessage === 'function'" +
						"})");
					_bridgeHost.Log("[webview:state] " + Truncate(diag, 400));

					// 兜底:DOM 就绪后补注入主题(防止 BridgeScript 时机过早失效)
					await core.ExecuteScriptAsync(
						"(() => { try { const vars = JSON.parse(" + NewtonsoftJson(WebviewTheme.VarsJson) + ");" +
						"Object.entries(vars).forEach(([k, v]) => document.documentElement.style.setProperty(k, v));" +
						"document.documentElement.classList.add('dark');" +
						"document.body.style.backgroundColor = vars['--vscode-editor-background'];" +
						"document.body.style.color = vars['--vscode-editor-foreground']; } catch (e) {} })();");

					// 页面实际截图(所见即所得),供宿主侧直接查看
					var shot = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{}");
					using (var json = System.Text.Json.JsonDocument.Parse(shot))
					{
						var base64 = json.RootElement.GetProperty("data").GetString();
						var pngPath = Path.Combine(
							Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
							"ClineVS", "webview-screenshot.png");
						Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
						File.WriteAllBytes(pngPath, Convert.FromBase64String(base64));
						_bridgeHost.Log("[webview:screenshot] " + pngPath);
					}
				}
				catch (Exception ex)
				{
					_bridgeHost.Log("[webview:state] 诊断失败:" + ex.Message);
				}
			};

			_bridgeHost.Log("[package] webview 已加载(standalone 构建)");
		}

		private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
		{
			// WebView2 COM 事件参数只能在创建它的 UI 线程上访问,
			// 先在当前线程取出 JSON 字符串,再转后台线程处理。
			string webMessageAsJson;
			try
			{
				webMessageAsJson = e.WebMessageAsJson;
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[webview] 读取消息失败:" + ex.Message);
				return;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await HandleWebMessageAsync(webMessageAsJson);
				}
				catch (Exception ex)
				{
					_bridgeHost.Log("[webview] 消息处理失败:" + ex.Message);
				}
			});
		}

		private async Task HandleWebMessageAsync(string webMessageAsJson)
		{
			// 诊断通道:前端 console/异常转发(见 BridgeScript)
			if (webMessageAsJson != null && webMessageAsJson.Contains("clinevs_console"))
			{
				var level = ExtractJsonString(webMessageAsJson, "level");
				var text = ExtractJsonString(webMessageAsJson, "text");
				_bridgeHost.Log($"[webview:{level ?? "log"}] {Truncate(text, 400)}");
				return;
			}

			var payload = ParseGrpcRequest(webMessageAsJson);
			if (payload == null)
			{
				_bridgeHost.Log("[webview] 非 grpc_request 消息:" + Truncate(webMessageAsJson, 200));
				return;
			}

			// 流式契约校验(与上游 dispatchCoreConnectionRequest 行为对齐)
			var declaredStreaming = _streamingMethods.IsStreaming(payload.Service, payload.Method);
			if (declaredStreaming != payload.IsStreaming)
			{
				var error = payload.Service + "." + payload.Method +
					" streaming mode does not match its proto contract";
				_bridgeHost.Log("[webview] " + error);
				await PostResponseAsync(payload.RequestId, messageJson: null, error: error, isStreaming: false);
				return;
			}

			var channel = _daemon.Channel;
			if (channel == null || !channel.IsConnected)
			{
				await PostResponseAsync(payload.RequestId, null, "cline-core 未连接", isStreaming: false);
				return;
			}

			await channel.SendRequestAsync(
				payload.Service,
				payload.Method,
				payload.MessageJson,
				payload.IsStreaming,
				onMessageJson: json =>
				{
					_ = _package.JoinableTaskFactory.RunAsync(async () =>
						await PostResponseAsync(payload.RequestId, json, null, payload.IsStreaming));
				},
				onError: err =>
				{
					_ = _package.JoinableTaskFactory.RunAsync(async () =>
						await PostResponseAsync(payload.RequestId, null, err, payload.IsStreaming));
				},
				onCompleted: () => { });
		}

		private async Task PostResponseAsync(string requestId, string messageJson, string error, bool isStreaming)
		{
			// PostWebMessageAsJson 是 COM 调用,必须回 UI 线程
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

			var webView = _webView;
			if (webView?.CoreWebView2 == null) return;

			var grpcResponse = new System.Text.Json.Nodes.JsonObject
			{
				["request_id"] = requestId,
				["is_streaming"] = isStreaming,
			};
			if (error != null)
			{
				grpcResponse["error"] = error;
			}
			else if (messageJson != null)
			{
				// message 以对象形式嵌入(前端 decodeMessage 期望已解析的 JSON)
				grpcResponse["message"] = System.Text.Json.Nodes.JsonNode.Parse(messageJson);
			}

			var root = new System.Text.Json.Nodes.JsonObject
			{
				["type"] = "grpc_response",
				["grpc_response"] = grpcResponse,
			};

			try
			{
				webView.CoreWebView2.PostWebMessageAsJson(root.ToJsonString());
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[webview] PostWebMessageAsJson 失败:" + ex.Message);
			}
			await Task.CompletedTask;
		}

		/// <summary>
		/// 解析出 grpc_request 载荷。WebMessageAsJson 可能是双重编码
		/// (webview 侧 postMessage 传的是 JSON 字符串),两种形态都接受。
		/// </summary>
		private GrpcRequestPayload ParseGrpcRequest(string webMessageAsJson)
		{
			try
			{
				System.Text.Json.JsonElement root;
				using (var outer = System.Text.Json.JsonDocument.Parse(webMessageAsJson))
				{
					if (outer.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
					{
						// 双重编码:先解出内层 JSON 字符串
						using (var inner = System.Text.Json.JsonDocument.Parse(outer.RootElement.GetString()))
						{
							root = inner.RootElement.Clone();
						}
					}
					else
					{
						root = outer.RootElement.Clone();
					}
				}

				if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
				if (!root.TryGetProperty("grpc_request", out var request)) return null;
				if (request.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

				return new GrpcRequestPayload
				{
					Service = GetString(request, "service"),
					Method = GetString(request, "method"),
					MessageJson = request.TryGetProperty("message", out var message)
						? message.GetRawText()
						: "{}",
					RequestId = GetString(request, "request_id"),
					IsStreaming = request.TryGetProperty("is_streaming", out var streaming)
						&& streaming.ValueKind == System.Text.Json.JsonValueKind.True,
				};
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[webview] 解析消息失败:" + ex.Message);
				return null;
			}
		}

		private static string GetString(System.Text.Json.JsonElement element, string name)
		{
			return element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
				? value.GetString()
				: null;
		}

		private static string ExtractJsonString(string json, string key)
		{
			try
			{
				using (var doc = System.Text.Json.JsonDocument.Parse(json))
				{
					if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
					{
						using (var inner = System.Text.Json.JsonDocument.Parse(doc.RootElement.GetString()))
						{
							return inner.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
								? v.GetString() : null;
						}
					}
					return doc.RootElement.TryGetProperty(key, out var v2) && v2.ValueKind == System.Text.Json.JsonValueKind.String
						? v2.GetString() : null;
				}
			}
			catch { return null; }
		}

		private static string Truncate(string text, int max)
		{
			if (text == null) return string.Empty;
			return text.Length <= max ? text : text.Substring(0, max) + " …";
		}

		private string GetExtensionDirectory()
		{
			var assembly = typeof(ClineWebviewController).Assembly;
			return Path.GetDirectoryName(new Uri(assembly.CodeBase ?? assembly.Location).LocalPath);
		}

		public async Task DisposeAsync()
		{
			if (_webView != null)
			{
				try { _webView.Dispose(); } catch { }
				_webView = null;
			}
		}

		private sealed class GrpcRequestPayload
		{
			public string Service;
			public string Method;
			public string MessageJson;
			public string RequestId;
			public bool IsStreaming;
		}
	}
}
