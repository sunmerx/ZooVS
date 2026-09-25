using System;
using System.IO;
using System.Threading.Tasks;
using ZooVs.Bridge;
using ZooVs.Daemon;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;

namespace ZooVs.Package
{
	/// <summary>
	/// webview ↔ Node 宿主(stdio)的消息桥。
	/// 协议即 VS Code webview 原生协议:
	///   页面 acquireVsCodeApi().postMessage(msg) → 桥脚本 chrome.webview.postMessage
	///     → C# 写 stdin 行 {"type":"webviewMessage","message":msg} → host 注入扩展;
	///   扩展 webview.postMessage(msg) → host stdout {"type":"extensionMessage","message":msg}
	///     → C# PostWebMessageAsJson(msg) → 桥脚本 window.postMessage → 前端 window message 监听。
	/// 前端(webview-ui 构建产物)零改动。
	/// </summary>
	public sealed class ZooWebviewController
	{
		private const string VirtualHost = "zoovs.app";

		private static readonly string BridgeScript =
			"(() => {" +
			"  'use strict';" +
			"  chrome.webview.addEventListener('message', e => { window.postMessage(e.data, '*'); });" +
			"  let __zooState = null;" +
			"  Object.defineProperty(window, 'acquireVsCodeApi', {" +
			"    value: () => ({" +
			"      postMessage: (msg) => { chrome.webview.postMessage(JSON.stringify({ type: 'webviewMessage', message: msg })); }," +
			"      getState: () => __zooState," +
			"      setState: (s) => { __zooState = s; }" +
			"    })," +
			"    configurable: false" +
			"  });" +
			// 主题注入:Roo 前端同样依赖 --vscode-* 变量;DOMContentLoaded 后执行(body 才存在)
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
			// 诊断:前端 console 与全局异常转发宿主
			"  const post = (level, text) => { try { chrome.webview.postMessage(JSON.stringify({ type: 'zoovs_console', level: level, text: String(text).slice(0, 400) })); } catch (e) {} };" +
			"  ['log', 'info', 'warn', 'error'].forEach(level => {" +
			"    const orig = console[level] ? console[level].bind(console) : function() {};" +
			"    console[level] = (...args) => {" +
			"      try { post(level, args.map(a => typeof a === 'string' ? a : JSON.stringify(a)).join(' ')); } catch (e) {}" +
			"      orig.apply(console, args);" +
			"    };" +
			"  });" +
			"  window.addEventListener('error', e => post('error', 'JS error: ' + e.message + ' @' + (e.filename || '?') + ':' + e.lineno));" +
			"  window.addEventListener('unhandledrejection', e => post('error', 'Unhandled rejection: ' + ((e.reason && e.reason.stack) || e.reason)));" +
			"  console.log('[ZooVS] bridge installed');" +
			"})();";

		private readonly AsyncPackage _package;
		private readonly VsBridgeHost _bridgeHost;
		private readonly StdioHostManager _hostManager;
		private Microsoft.Web.WebView2.Wpf.WebView2 _webView;
		private volatile bool _initialized;

		public ZooWebviewController(AsyncPackage package, VsBridgeHost bridgeHost, StdioHostManager hostManager)
		{
			_package = package;
			_bridgeHost = bridgeHost;
			_hostManager = hostManager;
		}

		/// <summary>StdioHostManager 的投递回调(gRPC/stdio 线程 → UI 线程 → webview)。</summary>
		public void PostExtensionMessageToWebview(string messageJson)
		{
			_ = _package.JoinableTaskFactory.RunAsync(async () =>
			{
				await PostToWebviewAsync(messageJson);
			});
		}

		/// <summary>由 Package.ShowToolWindowAsync 在窗口创建后调用;幂等。</summary>
		public async Task InitializeAsync()
		{
			if (_initialized) return;
			_initialized = true;

			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

			var toolWindow = _package.FindToolWindow(typeof(ZooToolWindow), 0, true) as ZooToolWindow;
			if (toolWindow == null) return;

			var extensionDir = GetExtensionDirectory();
			var webviewDir = Path.Combine(extensionDir, "assets", "webview");
			if (!File.Exists(Path.Combine(webviewDir, "index.html")))
			{
				toolWindow.ShowMessage("ZooVS webview assets missing. Run build-assets.ps1.");
				return;
			}

			// 时序:等宿主 ready(stdout {"type":"ready"})再加载页面(首批订阅不重试)
			toolWindow.ShowMessage("正在等待 Zoo Code 引擎启动…");
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
			while (!_hostManager.IsReady && DateTime.UtcNow < deadline)
			{
				await Task.Delay(250);
			}
			if (!_hostManager.IsReady)
			{
				toolWindow.ShowMessage("Zoo Code 引擎未能在 90 秒内就绪,请查看 ZooVS 输出窗格。");
				return;
			}
			_bridgeHost.Log("[package] 引擎就绪,加载 webview");

			var webView = new Microsoft.Web.WebView2.Wpf.WebView2
			{
				CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
				{
					UserDataFolder = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						"ZooVS", "WebView2"),
				},
				DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 30),
			};
			_webView = webView;
			toolWindow.AttachWebView(webView);

			await webView.EnsureCoreWebView2Async();

			var core = webView.CoreWebView2;
			core.Settings.AreDefaultContextMenusEnabled = false;
			core.Settings.AreDevToolsEnabled = true;

			core.SetVirtualHostNameToFolderMapping(
				VirtualHost, webviewDir,
				CoreWebView2HostResourceAccessKind.Allow);

			await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

			core.WebMessageReceived += OnWebMessageReceived;

			webView.Source = new Uri("https://" + VirtualHost + "/index.html");

			core.NavigationCompleted += async (s, e) =>
			{
				try
				{
					await Task.Delay(2500);
					// 兜底主题注入 + 页面状态快照
					await core.ExecuteScriptAsync(
						"(() => { try { const vars = JSON.parse(" + NewtonsoftJson(WebviewTheme.VarsJson) + ");" +
						"Object.entries(vars).forEach(([k, v]) => document.documentElement.style.setProperty(k, v));" +
						"document.documentElement.classList.add('dark');" +
						"document.body.style.backgroundColor = vars['--vscode-editor-background'];" +
						"} catch (err) {} })();");
					var diag = await core.ExecuteScriptAsync(
						"JSON.stringify({" +
						"rootChildren: document.getElementById('root')?.children.length ?? -1," +
						"bodyText: (document.body?.innerText || '').slice(0, 100)," +
						"hasVsCodeApi: typeof window.acquireVsCodeApi === 'function'" +
						"})");
					_bridgeHost.Log("[webview:state] " + Truncate(diag, 300));
				}
				catch (Exception ex)
				{
					_bridgeHost.Log("[webview:state] 诊断失败:" + ex.Message);
				}
			};

			_bridgeHost.Log("[package] webview 已加载");
		}

		private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
		{
			// COM 事件参数只在 UI 线程读取
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
			// 诊断通道
			if (webMessageAsJson != null && webMessageAsJson.Contains("zoovs_console"))
			{
				var level = ExtractJsonString(webMessageAsJson, "level");
				var text = ExtractJsonString(webMessageAsJson, "text");
				_bridgeHost.Log($"[webview:{level ?? "log"}] {Truncate(text, 400)}");
				return;
			}

			// {"type":"webviewMessage","message":{...}} → 原样写 stdin
			using (var outer = System.Text.Json.JsonDocument.Parse(webMessageAsJson))
			{
				System.Text.Json.JsonElement root;
				if (outer.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
				{
					using (var inner = System.Text.Json.JsonDocument.Parse(outer.RootElement.GetString()))
					{
						root = inner.RootElement.Clone();
					}
				}
				else
				{
					root = outer.RootElement.Clone();
				}

				if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
					root.TryGetProperty("type", out var typeEl) &&
					typeEl.GetString() == "webviewMessage" &&
					root.TryGetProperty("message", out var messageEl))
				{
					var payload = new System.Text.Json.Nodes.JsonObject
					{
						["type"] = "webviewMessage",
						["message"] = System.Text.Json.Nodes.JsonNode.Parse(messageEl.GetRawText()),
					};
					await _hostManager.SendAsync(payload.ToJsonString());
				}
			}
		}

		private async Task PostToWebviewAsync(string messageJson)
		{
			// COM 调用须在 UI 线程
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

			var webView = _webView;
			if (webView?.CoreWebView2 == null) return;

			try
			{
				// 页面桥脚本把 chrome.webview 消息转成 window message,前端零改动
				webView.CoreWebView2.PostWebMessageAsJson(messageJson);
			}
			catch (Exception ex)
			{
				_bridgeHost.Log("[webview] PostWebMessageAsJson 失败:" + ex.Message);
			}
			await Task.CompletedTask;
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

		private static string NewtonsoftJson(string json)
		{
			return "\"" + json.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
		}

		private static string Truncate(string text, int max)
		{
			if (text == null) return string.Empty;
			return text.Length <= max ? text : text.Substring(0, max) + " …";
		}

		private string GetExtensionDirectory()
		{
			var assembly = typeof(ZooWebviewController).Assembly;
			return Path.GetDirectoryName(new Uri(assembly.CodeBase ?? assembly.Location).LocalPath);
		}

		public async Task DisposeAsync()
		{
			if (_webView != null)
			{
				try { _webView.Dispose(); } catch { }
				_webView = null;
			}
			await Task.CompletedTask;
		}
	}
}
