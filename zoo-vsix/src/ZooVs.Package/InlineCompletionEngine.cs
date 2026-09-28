using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ZooVs.Package
{
	/// <summary>
	/// ZooVS 行内补全(B1 ghost text)—— VS2022 进程内实现。
	/// 策略复用开源 grom(VS Code 内联补全)验证过的模式:输入防抖、Tab 接受、Esc/继续输入消失、
	/// 逐词接受可后续增强;渲染层按 VS SDK 经典方案(装饰层 + FormattedText 前缀测量对齐光标)。
	/// 模型走 OpenAI 兼容 /chat/completions(FIM 语义 prompt),配置在
	/// %LOCALAPPDATA%\ZooVS\inline-completion.json;开关由 Customize 弹窗下发(internal hostCall)。
	/// </summary>
	internal static class InlineCompletionSettings
	{
		private static readonly string SettingsPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"ZooVS", "inline-completion.json");

		private static volatile Config _current;

		public static readonly string InlineLogPath = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"ZooVS", "log", "inline-completion.log");

		/// <summary>行内补全全流程文件日志(供各类共用)。</summary>
		public static void EngineLog(string message)
		{
			try
			{
				System.IO.File.AppendAllText(InlineCompletionSettings.InlineLogPath,
					DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
			}
			catch { }
		}

		/// <summary>Zoo 数据目录(%LOCALAPPDATA%\ZooVS\data),由 Package 注入;
		/// 行内补全的 endpoint/apiKey/model 默认从 Zoo 已配置的 provider 读取——统一配置一次。</summary>
		public static string ZooStorageDir;

		public sealed class Config
		{
			public bool enabled = false;
			public string endpoint = "";
			public string apiKey = "";
			public string model = "";
			public int debounceMs = 500;
			public int maxTokens = 96;
			public bool log = true; // 输出窗格记录 Zoo ghost text(区分 VS 自带 IntelliCode)
		}

		/// <summary>日志汇聚(Package 启动时接 VsBridgeHost.Log)。</summary>
		public static Action<string> Sink;

		/// <summary>从 Zoo 的 provider 配置(global-storage/secrets.json + global-state.json)
		/// 取 openai 兼容端点/密钥/模型。</summary>
		private static void FillFromZooProvider(Config c)
		{
			try
			{
				// dir 未注入(MEF 首次访问早于 Package 启动)时用固定默认路径——
				// dataDir 本就是 %LOCALAPPDATA%\ZooVS\data,不依赖注入时机
				var dir = ZooStorageDir;
				if (string.IsNullOrEmpty(dir))
				{
					dir = System.IO.Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZooVS", "data");
				}

				string profileJson = null;
				try
				{
					var secretsPath = Path.Combine(dir, "global-storage", "secrets.json");
					if (File.Exists(secretsPath))
					{
						using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(secretsPath)))
						{
							var root = doc.RootElement;
							System.Text.Json.JsonElement st = root;
							if (root.TryGetProperty("storage", out var stEl)) st = stEl;
							if (st.ValueKind == System.Text.Json.JsonValueKind.Object)
							{
								if (st.TryGetProperty("roo_cline_config_api_config", out var cfgEl) &&
									cfgEl.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									profileJson = cfgEl.GetString();
								}
								if (string.IsNullOrEmpty(c.apiKey) &&
									st.TryGetProperty("openAiApiKey", out var kEl) &&
									kEl.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									c.apiKey = kEl.GetString() ?? "";
								}
							}
						}
					}
				}
				catch { }

				if (!string.IsNullOrEmpty(profileJson))
				{
					if (string.IsNullOrEmpty(c.model))
					{
						var m = System.Text.RegularExpressions.Regex.Match(profileJson, "\"openAiModelId\"\\s*:\\s*\"([^\"]*)\"");
						if (m.Success) c.model = m.Groups[1].Value;
					}
					if (string.IsNullOrEmpty(c.endpoint))
					{
						var m = System.Text.RegularExpressions.Regex.Match(profileJson, "\"openAiBaseUrl\"\\s*:\\s*\"([^\"]*)\"");
						if (m.Success) c.endpoint = m.Groups[1].Value;
					}
					if (string.IsNullOrEmpty(c.apiKey))
					{
						var m = System.Text.RegularExpressions.Regex.Match(profileJson, "\"openAiApiKey\"\\s*:\\s*\"([^\"]*)\"");
						if (m.Success) c.apiKey = m.Groups[1].Value;
					}
				}

				if (string.IsNullOrEmpty(c.endpoint))
				{
					try
					{
						var statePath = Path.Combine(dir, "global-storage", "global-state.json");
						if (File.Exists(statePath))
						{
							using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath)))
							{
								if (doc.RootElement.TryGetProperty("storage", out var st) &&
									st.TryGetProperty("openAiBaseUrl", out var b) &&
									b.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									c.endpoint = b.GetString() ?? "";
								}
							}
						}
					}
					catch { }
				}
			}
			catch { }
		}

		public static Config Current
		{
			get { return _current ?? (_current = Load()); }
		}

		/// <summary>强制重载(Package 注入 ZooStorageDir 后调用——首次访问可能早于注入且被缓存)。</summary>
		public static void Reload()
		{
			_current = Load();
		}

		public static Config Load()
		{
			var c = new Config();
			bool jsonHadEnabled = false;
			try
			{
				if (File.Exists(SettingsPath))
				{
					using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath)))
					{
						var r = doc.RootElement;
						if (r.TryGetProperty("enabled", out var e))
						{
							jsonHadEnabled = true;
							if (e.ValueKind == System.Text.Json.JsonValueKind.True) c.enabled = true;
						}
						if (r.TryGetProperty("endpoint", out var ep) && ep.ValueKind == System.Text.Json.JsonValueKind.String) c.endpoint = ep.GetString() ?? "";
						if (r.TryGetProperty("apiKey", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String) c.apiKey = k.GetString() ?? "";
						if (r.TryGetProperty("model", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String) c.model = m.GetString() ?? "";
						if (r.TryGetProperty("debounceMs", out var d) && d.TryGetInt32(out var di)) c.debounceMs = Math.Max(150, Math.Min(di, 3000));
						if (r.TryGetProperty("maxTokens", out var t) && t.TryGetInt32(out var ti)) c.maxTokens = Math.Max(16, Math.Min(ti, 512));
						if (r.TryGetProperty("log", out var lg) && lg.ValueKind == System.Text.Json.JsonValueKind.False) c.log = false;
					}
				}
			}
			catch { }
			// 未显式配置 endpoint/model/key 时,自动采用 Zoo 已配置的 provider(统一配置一次)
			bool jsonHadEndpointOrModel = !string.IsNullOrEmpty(c.endpoint) || !string.IsNullOrEmpty(c.model);
			if (string.IsNullOrEmpty(c.endpoint) || string.IsNullOrEmpty(c.apiKey) || string.IsNullOrEmpty(c.model))
			{
				FillFromZooProvider(c);
			}
			EngineLog("配置加载: enabled=" + c.enabled + " endpoint=" + (string.IsNullOrEmpty(c.endpoint) ? "无" : new Uri(c.endpoint).Host) +
				" model=" + (string.IsNullOrEmpty(c.model) ? "无" : c.model) +
				" key=" + (string.IsNullOrEmpty(c.apiKey) ? "无" : "有") +
				" debounce=" + c.debounceMs + "ms json曾存enabled=" + jsonHadEnabled);
			// 默认启用仅限"json 从未存过 enabled"的初始状态;用户 toggle 过(无论开关)一律尊重——
			// 否则自动启用会覆盖手动关闭(实测:关闭重启后又被拉回开启)
			if (!jsonHadEnabled && !string.IsNullOrEmpty(c.endpoint) && !string.IsNullOrEmpty(c.model))
			{
				c.enabled = true;
			}
			return c;
		}

		public static void Save(Config c)
		{
			_current = c;
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
				// 只持久化开关与调参——端点/密钥/模型始终源自 Zoo provider,避免密钥二次落盘
				var minimal = new { enabled = c.enabled, debounceMs = c.debounceMs, maxTokens = c.maxTokens, log = c.log };
				File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(minimal,
					new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
			}
			catch { }
		}

		public static bool IsConfigured
		{
			get { var c = Current; return c.enabled && !string.IsNullOrEmpty(c.endpoint) && !string.IsNullOrEmpty(c.model); }
		}
	}

	/// <summary>补全请求引擎(HTTP,静态共享)。</summary>
	internal static class InlineCompletionEngine
	{
		private static readonly HttpClient Client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(15),
		};

		private static DateTime _lastHint = DateTime.MinValue;

		/// <summary>网关拒绝关思考参数组(400)则记住,后续请求直接裸发,省掉每次 1~1.4s 的注定失败试探。</summary>
		private static bool _gatewayRejectsEffort;

		/// <summary>FIM(DeepSeek 官方补全,beta/completions)探测:-1 未探测,0/1 可用 URL 序号,-2 不可用。</summary>
		private static int _fimUrlIndex = -1;
		/// <summary>FIM 探测成功的模型名(可能去掉厂商前缀 deepseek/xxx → xxx)。</summary>
		private static string _fimModel;

		private static bool IsDeepseekModel(string model)
		{
			return !string.IsNullOrEmpty(model) && model.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>DeepSeek 官方 FIM 补全(beta/completions,prompt+suffix 分离参数,SSE 流式)。
		/// FIM 是基座能力、不做思考,首字亚秒级——治推理模型 chat 补全"思考 7s 才出正文"的延迟。
		/// 依次探测 {/beta/completions,/completions} × {原模型名,去厂商前缀模型名},失败回退 chat。</summary>
		private static async Task<string> TryFimStreamingAsync(string prefix, string suffix, CancellationToken ct, Action<string> onDelta)
		{
			var cfg = InlineCompletionSettings.Current;
			var baseUrls = new[] { cfg.endpoint.TrimEnd('/') + "/beta/completions", cfg.endpoint.TrimEnd('/') + "/completions" };
			var modelNames = new System.Collections.Generic.List<string> { cfg.model };
			var slash = cfg.model.IndexOf('/');
			if (slash >= 0 && slash + 1 < cfg.model.Length)
			{
				var stripped = cfg.model.Substring(slash + 1);
				if (!modelNames.Contains(stripped)) modelNames.Add(stripped);
			}

			var attempts = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
			if (_fimUrlIndex >= 0 && _fimModel != null)
			{
				attempts.Add(new System.Collections.Generic.KeyValuePair<string, string>(baseUrls[_fimUrlIndex], _fimModel));
			}
			else
			{
				foreach (var u in baseUrls) foreach (var m in modelNames) attempts.Add(new System.Collections.Generic.KeyValuePair<string, string>(u, m));
			}

			for (int i = 0; i < attempts.Count; i++)
			{
				var url = attempts[i].Key;
				var model = attempts[i].Value;
				var httpStart = DateTime.UtcNow;
				try
				{
					var body = new System.Collections.Generic.Dictionary<string, object>
					{
						["model"] = model,
						["prompt"] = prefix,
						["suffix"] = suffix,
						["max_tokens"] = Math.Max(cfg.maxTokens, 64),
						["temperature"] = 0.2,
						["stream"] = true,
					};
					using (var req = new HttpRequestMessage(HttpMethod.Post, url))
					{
						req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
						if (!string.IsNullOrEmpty(cfg.apiKey))
						{
							req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.apiKey);
						}
						InlineCompletionSettings.EngineLog("FIM请求[" + (i + 1) + "/" + attempts.Count + "] " + url + " 模型=" + model);
						using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
						{
							InlineCompletionSettings.EngineLog("FIM响应头: " + (int)resp.StatusCode + " 耗时=" + (int)(DateTime.UtcNow - httpStart).TotalMilliseconds + "ms");
							int code = (int)resp.StatusCode;
							// 鉴权/服务端错误:与 URL/模型组合无关,直接放弃 FIM
							if (code == 401 || code == 403 || code >= 500) { _fimUrlIndex = -2; return null; }
							if (!resp.IsSuccessStatusCode) { InlineCompletionSettings.EngineLog("FIM组合不可用: " + code); continue; }

							var acc = new System.Text.StringBuilder();
							int deltas = 0;
							using (var stream = await resp.Content.ReadAsStreamAsync())
							using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
							{
								string line;
								while ((line = await reader.ReadLineAsync()) != null)
								{
									if (ct.IsCancellationRequested) break;
									if (!line.StartsWith("data:")) continue;
									var payload = line.Substring(5).Trim();
									if (payload == "[DONE]") break;
									try
									{
										using (var doc = System.Text.Json.JsonDocument.Parse(payload))
										{
											// FIM 流式增量在 choices[0].text(legacy completions);兼容 delta.content
											string piece = null;
											if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
											{
												if (ch[0].TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String) piece = t.GetString();
												else if (ch[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c)
													&& c.ValueKind == System.Text.Json.JsonValueKind.String) piece = c.GetString();
											}
											if (!string.IsNullOrEmpty(piece))
											{
												acc.Append(piece);
												deltas++;
												if (deltas == 1)
												{
													InlineCompletionSettings.EngineLog("FIM首字: +" + (int)(DateTime.UtcNow - httpStart).TotalMilliseconds + "ms [" + (piece.Length > 30 ? piece.Substring(0, 30) : piece) + "]");
												}
												if (onDelta != null) onDelta(acc.ToString());
											}
										}
									}
									catch { }
								}
							}
							if (_fimUrlIndex < 0)
							{
								_fimUrlIndex = Array.IndexOf(baseUrls, url);
								_fimModel = model;
								InlineCompletionSettings.EngineLog("FIM组合已记住: " + url + " 模型=" + model);
							}
							InlineCompletionSettings.EngineLog("FIM结束: " + deltas + "段 " + acc.Length + "字");
							return acc.Length == 0 ? null : Normalize(acc.ToString());
						}
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					InlineCompletionSettings.EngineLog("FIM异常: " + ex.GetType().Name + ": " + ex.Message);
					continue;
				}
			}
			_fimUrlIndex = -2;
			InlineCompletionSettings.EngineLog("FIM 全组合不可用,本会话回退 chat 补全");
			return null;
		}

		public static async Task<string> CompleteAsync(string prefix, string suffix, CancellationToken ct)
		{
			var cfg = InlineCompletionSettings.Current;
			if (string.IsNullOrEmpty(cfg.endpoint) || string.IsNullOrEmpty(cfg.model))
			{
				InlineCompletionSettings.EngineLog("HTTP跳过: endpoint/model 缺失");
				if ((DateTime.UtcNow - _lastHint).TotalSeconds > 300)
				{
					_lastHint = DateTime.UtcNow;
					System.Diagnostics.Debug.WriteLine("[zoovs-inline] 未配置 endpoint/model:" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZooVS", "inline-completion.json"));
				}
				return null;
			}

			try
			{
				var url = cfg.endpoint.TrimEnd('/') + "/chat/completions";
				var messages = new object[]
				{
					new { role = "system", content = "You are a code completion engine. The user gives code with <CURSOR> marking the caret. Reply with ONLY the code that should be inserted at <CURSOR> - no markdown, no fences, no explanation." },
					new { role = "user", content = prefix + "\n<CURSOR>\n" + suffix },
				};
					System.Func<string, int, object> makeBody = (effort, maxTokens) =>
					{
						var dict = new System.Collections.Generic.Dictionary<string, object>
						{
							["model"] = cfg.model,
							["max_tokens"] = maxTokens,
							["temperature"] = 0.2,
							["messages"] = messages,
						};
						// 行内补全须压制思考:推理模型会把 token 全烧在 reasoning 上导致 content 为空
						// (实测 deepseek-v4.1-flash:reasoning 满篇、content="")。
						// 关思考参数组(thinking.type=disabled / enable_thinking=false / reasoning_effort=none):
						// 网关不认其中任一参数会 400,由降级裸发重试兜底。
						if (effort != null)
						{
							dict["reasoning_effort"] = "none";
							dict["enable_thinking"] = false;
							dict["thinking"] = new System.Collections.Generic.Dictionary<string, object> { ["type"] = "disabled" };
						}
						return dict;
					};
				// 两级尝试:minimal 思考 → 400 则裸发(靠 maxTokens 余量容纳默认思考);
				// 网关一旦确认拒绝 effort 参数就跳过第一级
				string[] efforts = _gatewayRejectsEffort ? new string[] { null } : new[] { "none", null };
				for (int attempt = 0; attempt < 2; attempt++)
				{
					// 裸发(第 2 次)时网关会强制思考:思考先吃 token,余量才给正文——
					// 提到至少 1024 保证 content 有空间(用户 Zoo 聊天能用同模型即因无此紧上限)
					var attemptMaxTokens = attempt == 0 ? cfg.maxTokens : Math.Max(cfg.maxTokens, 1024);
				var httpStart = DateTime.UtcNow;
				using (var req = new HttpRequestMessage(HttpMethod.Post, url))
				{
					req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(makeBody(efforts[attempt], attemptMaxTokens)), Encoding.UTF8, "application/json");
					if (!string.IsNullOrEmpty(cfg.apiKey))
					{
						req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.apiKey);
					}
					InlineCompletionSettings.EngineLog("HTTP请求(" + (attempt + 1) + "/2, effort=" + (efforts[attempt] ?? "默认") + ") 模型=" + cfg.model + " prefix=" + prefix.Length + "字");
					using (var resp = await Client.SendAsync(req, ct))
					{
						InlineCompletionSettings.EngineLog("HTTP响应: " + (int)resp.StatusCode + " 耗时=" + (int)(DateTime.UtcNow - httpStart).TotalMilliseconds + "ms");
						if ((int)resp.StatusCode == 400 && attempt == 0 && !_gatewayRejectsEffort)
						{
							_gatewayRejectsEffort = true;
							InlineCompletionSettings.EngineLog("HTTP 400: 网关不认 reasoning_effort,已记住,本次降级裸发(后续直接裸发)");
							continue;
						}
						if (!resp.IsSuccessStatusCode)
						{
							InlineCompletionSettings.EngineLog("HTTP失败: " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
							return null;
						}
						var json = await resp.Content.ReadAsStringAsync();
						InlineCompletionSettings.EngineLog("HTTP原文: " + (json.Length > 500 ? json.Substring(0, 500) + "..." : json));
						using (var doc = System.Text.Json.JsonDocument.Parse(json))
						{
							var root = doc.RootElement;
							if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
							{
								var msg = choices[0];
								if (msg.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var content)
									&& content.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									return Normalize(content.GetString());
								}
								if (msg.TryGetProperty("text", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									return Normalize(text.GetString());
								}
							}
						}
						InlineCompletionSettings.EngineLog("响应解析: 未命中 message.content/text 字段");
						} // using resp
					} // using req
				} // for attempt
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				InlineCompletionSettings.EngineLog("HTTP取消: 请求被新输入取消(正常防抖行为)");
			}
			catch (OperationCanceledException)
			{
				// .NET Framework 下 HttpClient 超时也抛 TaskCanceledException(ct 未触发);
				// 曾被误记为"新输入取消",6s 超时把 3-5s 的正常慢响应整条掐死
				InlineCompletionSettings.EngineLog("HTTP超时: 客户端 " + Client.Timeout.TotalSeconds + "s 无响应(网关慢,非用户取消)");
			}
			catch (Exception ex)
			{
				InlineCompletionSettings.EngineLog("HTTP异常: " + ex.GetType().Name + ": " + ex.Message);
			}
			return null;
		}

		/// <summary>流式补全(SSE):首个内容增量即回调 onDelta(累计文本),边流边渲染 ghost text。
		/// 网关普遍 3~10s 才出整包,非流式下灰字要等打字停止后数秒才出现,用户观感即"不生效"。
		/// deepseek 模型优先走官方 FIM(beta/completions,无思考、亚秒首字),不可用回退 chat。</summary>
		public static async Task<string> CompleteStreamingAsync(string prefix, string suffix, CancellationToken ct, Action<string> onDelta)
		{
			var cfg = InlineCompletionSettings.Current;
			if (string.IsNullOrEmpty(cfg.endpoint) || string.IsNullOrEmpty(cfg.model))
			{
				InlineCompletionSettings.EngineLog("HTTP跳过: endpoint/model 缺失");
				return null;
			}
			if (IsDeepseekModel(cfg.model) && _fimUrlIndex != -2)
			{
				var fim = await TryFimStreamingAsync(prefix, suffix, ct, onDelta);
				if (fim != null) return fim;
			}
			var url = cfg.endpoint.TrimEnd('/') + "/chat/completions";
			var messages = new object[]
			{
				new { role = "system", content = "You are a code completion engine. The user gives code with <CURSOR> marking the caret. Reply with ONLY the code that should be inserted at <CURSOR> - no markdown, no fences, no explanation." },
				new { role = "user", content = prefix + "\n<CURSOR>\n" + suffix },
			};
			try
			{
				string[] efforts = _gatewayRejectsEffort ? new string[] { null } : new[] { "none", null };
				for (int attempt = 0; attempt < efforts.Length; attempt++)
				{
					var attemptMaxTokens = efforts[attempt] == null ? Math.Max(cfg.maxTokens, 1024) : cfg.maxTokens;
					var dict = new System.Collections.Generic.Dictionary<string, object>
					{
						["model"] = cfg.model,
						["max_tokens"] = attemptMaxTokens,
						["temperature"] = 0.2,
						["messages"] = messages,
						["stream"] = true,
					};
					if (efforts[attempt] != null)
					{
						// 关思考参数组:deepseek 官方 thinking.type=disabled;兼容 enable_thinking(Qwen 系网关)与 reasoning_effort=none(OpenAI 系)
						dict["reasoning_effort"] = "none";
						dict["enable_thinking"] = false;
						dict["thinking"] = new System.Collections.Generic.Dictionary<string, object> { ["type"] = "disabled" };
					}
					var httpStart = DateTime.UtcNow;
					using (var req = new HttpRequestMessage(HttpMethod.Post, url))
					{
						req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(dict), Encoding.UTF8, "application/json");
						if (!string.IsNullOrEmpty(cfg.apiKey))
						{
							req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.apiKey);
						}
						InlineCompletionSettings.EngineLog("流式请求(" + (attempt + 1) + "/" + efforts.Length + ", effort=" + (efforts[attempt] ?? "默认") + ") 模型=" + cfg.model + " prefix=" + prefix.Length + "字");
						using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
						{
							InlineCompletionSettings.EngineLog("流式响应头: " + (int)resp.StatusCode + " 耗时=" + (int)(DateTime.UtcNow - httpStart).TotalMilliseconds + "ms");
							if ((int)resp.StatusCode == 400 && attempt == 0 && !_gatewayRejectsEffort)
							{
								_gatewayRejectsEffort = true;
								InlineCompletionSettings.EngineLog("HTTP 400: 网关不认 reasoning_effort,已记住,降级裸发重试");
								continue;
							}
							if (!resp.IsSuccessStatusCode)
							{
								InlineCompletionSettings.EngineLog("流式失败: " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
								return null;
							}
							var acc = new System.Text.StringBuilder();
							var raw = new System.Text.StringBuilder();
							int deltas = 0;
							using (var stream = await resp.Content.ReadAsStreamAsync())
							using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
							{
								string line;
								while ((line = await reader.ReadLineAsync()) != null)
								{
									if (ct.IsCancellationRequested) break;
									if (line.StartsWith("data:"))
									{
										var payload = line.Substring(5).Trim();
										if (payload == "[DONE]") break;
										try
										{
											using (var doc = System.Text.Json.JsonDocument.Parse(payload))
											{
												// 只取 delta.content;推理模型的 reasoning_content 增量忽略
												if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
													&& ch[0].TryGetProperty("delta", out var d)
													&& d.TryGetProperty("content", out var c)
													&& c.ValueKind == System.Text.Json.JsonValueKind.String)
												{
													var piece = c.GetString();
													if (!string.IsNullOrEmpty(piece))
													{
														acc.Append(piece);
														deltas++;
														if (deltas == 1)
														{
															InlineCompletionSettings.EngineLog("流式首字: +" + (int)(DateTime.UtcNow - httpStart).TotalMilliseconds + "ms [" + (piece.Length > 30 ? piece.Substring(0, 30) : piece) + "]");
														}
														if (onDelta != null) onDelta(acc.ToString());
													}
												}
											}
										}
										catch { }
									}
									else if (line.Length > 0 && line[0] == '{' && raw.Length < 65536)
									{
										// 网关可能无视 stream 参数回整包 JSON,兜底收集
										raw.Append(line);
									}
								}
							}
							if (acc.Length == 0 && raw.Length > 0)
							{
								InlineCompletionSettings.EngineLog("流式降级: 网关回整包而非 SSE,按非流式解析");
								try
								{
									using (var doc = System.Text.Json.JsonDocument.Parse(raw.ToString()))
									{
										if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
											&& ch[0].TryGetProperty("message", out var m)
											&& m.TryGetProperty("content", out var content)
											&& content.ValueKind == System.Text.Json.JsonValueKind.String)
										{
											var whole = content.GetString();
											if (!string.IsNullOrEmpty(whole) && onDelta != null) onDelta(whole);
											return Normalize(whole);
										}
									}
								}
								catch { }
								return null;
							}
							InlineCompletionSettings.EngineLog("流式结束: " + deltas + "段 " + acc.Length + "字");
							return acc.Length == 0 ? null : Normalize(acc.ToString());
						}
					}
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				InlineCompletionSettings.EngineLog("流式取消: 新输入/视图关闭");
			}
			catch (OperationCanceledException)
			{
				InlineCompletionSettings.EngineLog("流式超时: 客户端 " + Client.Timeout.TotalSeconds + "s");
			}
			catch (Exception ex)
			{
				InlineCompletionSettings.EngineLog("流式异常: " + ex.GetType().Name + ": " + ex.Message);
			}
			return null;
		}

		/// <summary>去掉 markdown 围栏/说明性开头,截断到首个三空行,限长。</summary>
		private static string Normalize(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return null;
			var s = raw.TrimStart('\n', '\r');
			if (s.StartsWith("```")) s = s.Substring(s.IndexOf('\n') + 1);
			var fence = s.IndexOf("```");
			if (fence >= 0) s = s.Substring(0, fence);
			var cut = s.IndexOf("\n\n\n");
			if (cut >= 0) s = s.Substring(0, cut);
			if (s.Length > 320) s = s.Substring(0, 320);
			var preview = s.Length > 60 ? s.Substring(0, 60) + "..." : s;
			InlineCompletionSettings.EngineLog("结果规范化: " + s.Length + "字 [" + preview.Replace(Convert.ToChar(13), ' ').Replace(Convert.ToChar(10), ' ') + "]");
			return s;
		}
	}

	/// <summary>装饰层定义(属性导出:AdornmentLayerDefinition 在本 interop 中为 sealed)。</summary>
	internal static class InlineCompletionLayerDefinition
	{
		public const string LayerName = "ZooVS Inline Completion";
	}

	internal sealed class InlineCompletionLayerPart
	{
		[Export(typeof(AdornmentLayerDefinition))]
		[Name(InlineCompletionLayerDefinition.LayerName)]
		[Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Selection)]
		public AdornmentLayerDefinition Definition { get; } = new AdornmentLayerDefinition();
	}

	/// <summary>文本视图监听(MEF):每个可编辑视图挂一个控制器。</summary>
	[Export(typeof(IWpfTextViewCreationListener))]
	[ContentType("text")]
	[TextViewRole(PredefinedTextViewRoles.Editable)]
	internal sealed class InlineCompletionViewListener : IWpfTextViewCreationListener
	{
		public void TextViewCreated(IWpfTextView textView)
		{
			// 即便当前未启用也挂控制器(开关可热切换);触发时再判断
			var c = new InlineCompletionController(textView);
			System.IO.File.AppendAllText(InlineCompletionSettings.InlineLogPath,
				DateTime.Now.ToString("HH:mm:ss.fff") + " 视图挂接: contentType=" +
				(textView.TextBuffer?.ContentType?.TypeName ?? "?") + " layer=" + (c.HasLayer ? "ok" : "NULL") + Environment.NewLine);
		}
	}

	/// <summary>单视图 ghost text 控制器:防抖触发、渲染、Tab/Esc 交互。</summary>
	internal sealed class InlineCompletionController
	{
		private readonly IWpfTextView _view;
		private readonly IAdornmentLayer _layer;
		private readonly DispatcherTimer _debounce;

		private TextBlock _ghost;
		private string _suggestion;
		private SnapshotPoint _suggestionPoint;
		private CancellationTokenSource _cts;
		private bool _applying;
		private bool _dismissedByEmptyLine;
		private volatile bool _isClosed;

		/// <summary>装饰层是否获取成功(诊断用)。</summary>
		public bool HasLayer { get { return _layer != null; } }

		public InlineCompletionController(IWpfTextView view)
		{
			_view = view;
			try { _layer = view.GetAdornmentLayer(InlineCompletionLayerDefinition.LayerName); }
			catch { _layer = null; }
			if (_layer == null) return;

			_debounce = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(InlineCompletionSettings.Current.debounceMs),
			};
			_debounce.Tick += (_, __) => { _debounce.Stop(); _ = TriggerAsync(); };

			view.TextBuffer.Changed += OnBufferChanged;
			// 打字本身会移动光标/触发布局:这里只能清灰字,绝不能打断防抖定时器
			// (旧版调 Dismiss() 把每一次触发都杀了——行内补全从未真正发起过请求,日志实测定位)
			view.Caret.PositionChanged += (_, __) => { if (!_applying) RemoveGhost(); };
			view.LayoutChanged += (_, __) => { if (!_applying) RemoveGhost(); };
			view.Closed += (_, __) => { _isClosed = true; Teardown(); };
			view.VisualElement.PreviewKeyDown += OnPreviewKeyDown;
			// 失焦只清灰字,不取消在途请求(实测 VS 非前台时焦点会异步迁移,
			// 若在此 Dismiss 会把慢请求整条杀掉——日志两次"请求被新输入取消"实为此因);
			// 结果返回后仍有光标/缓冲校验兜底
			view.VisualElement.LostKeyboardFocus += (_, __) => { if (!_applying) RemoveGhost(); };
		}

		private void OnBufferChanged(object sender, Microsoft.VisualStudio.Text.TextContentChangedEventArgs e)
		{
			if (_applying) return;
			try
			{
				// 诊断:DTE 打字后 ~6s 出现过来源不明的缓冲变更(把在途请求整条取消),记录变更内容定位来源
				var c = e.Changes.Count > 0 ? e.Changes[0] : null;
				var oldTxt = c == null ? "" : (c.OldText ?? "");
				var newTxt = c == null ? "" : (c.NewText ?? "");
				TryLog("缓冲变更@" + ViewFileLabel() + " n=" + e.Changes.Count + " old=[" +
					(oldTxt.Length > 40 ? oldTxt.Substring(0, 40) + ".." : oldTxt.Replace("\r", "\\r").Replace("\n", "\\n")) +
					"] new=[" + (newTxt.Length > 40 ? newTxt.Substring(0, 40) + ".." : newTxt.Replace("\r", "\\r").Replace("\n", "\\n")) + "]");
			}
			catch { }
			Dismiss();
			Schedule();
		}

		private void Schedule()
		{
			var cfg = InlineCompletionSettings.Current;
			if (!InlineCompletionSettings.IsConfigured)
			{
				TryLog("跳过触发@" + ViewFileLabel() + ": 未配置(enabled=" + cfg.enabled +
					", endpoint=" + (string.IsNullOrEmpty(cfg.endpoint) ? "无" : "有") +
					", model=" + (string.IsNullOrEmpty(cfg.model) ? "无" : cfg.model) + ")");
				return;
			}
			// 空行/纯空白行不触发(降噪)
			try
			{
				var line = _view.Caret.Position.BufferPosition.GetContainingLine();
				var textBefore = line.GetText().Substring(0, Math.Min(line.GetText().Length, CaretColumn(line)));
				_dismissedByEmptyLine = textBefore.Trim().Length == 0;
			}
			catch { _dismissedByEmptyLine = false; }
			if (_dismissedByEmptyLine)
			{
				TryLog("跳过触发@" + ViewFileLabel() + ": 当前行空白(降噪)");
				return;
			}
			TryLog("已排定防抖@" + ViewFileLabel() + ":" + CaretLine() + " (" + cfg.debounceMs + "ms)");

			_debounce.Interval = TimeSpan.FromMilliseconds(InlineCompletionSettings.Current.debounceMs);
			_debounce.Stop();
			_debounce.Start();
		}

		private int CaretColumn(ITextSnapshotLine line)
		{
			var caret = _view.Caret.Position.BufferPosition;
			return Math.Max(0, caret.Position - line.Start.Position);
		}

		private async Task TriggerAsync()
		{
			if (_dismissedByEmptyLine || !InlineCompletionSettings.IsConfigured) return;
			var caret = _view.Caret.Position.BufferPosition;
			if (caret.Position == 0) return;

			var snapshot = caret.Snapshot;
			var prefixStart = Math.Max(0, caret.Position - 3000);
			var prefix = snapshot.GetText(prefixStart, caret.Position - prefixStart);
			var suffixLen = Math.Min(1200, snapshot.Length - caret.Position);
			var suffix = suffixLen > 0 ? snapshot.GetText(caret.Position, suffixLen) : "";

			_cts?.Cancel();
			_cts?.Dispose();
			_cts = new CancellationTokenSource();

			TryLog("触发请求@" + ViewFileLabel() + ":" + CaretLine() + " prefix=" + prefix.Length + "字 suffix=" + suffix.Length + "字");
			var result = await InlineCompletionEngine.CompleteStreamingAsync(prefix, suffix, _cts.Token, partial =>
			{
				// 流式增量:插入点/缓冲仍一致才渲染,变了就地取消流(最终校验同款条件)
				if (_isClosed) { TryCancel(); return; }
				if (!_view.Caret.Position.BufferPosition.Position.Equals(caret.Position)) { TryCancel(); return; }
				try
				{
					if (!_view.TextBuffer.CurrentSnapshot.Version.Equals(snapshot.Version)) { TryCancel(); return; }
				}
				catch { TryCancel(); return; }
				Render(partial, caret);
			});
			if (result == null)
			{
				TryLog("请求无结果@" + ViewFileLabel() + ":" + CaretLine() + " (HTTP失败/超时/内容为空,详见下一条请求日志)");
				return;
			}

			// 光标已移动/缓冲已变 → 丢弃
			if (_isClosed) { TryLog("丢弃结果: 视图已关闭"); return; }
			if (!_view.Caret.Position.BufferPosition.Position.Equals(caret.Position)) { TryLog("丢弃结果: 光标已移动"); return; }
			try
			{
				if (!_view.TextBuffer.CurrentSnapshot.Version.Equals(snapshot.Version)) { TryLog("丢弃结果: 缓冲已变更"); return; }
			}
			catch { return; }

			Render(result, caret);
		}

		private void TryCancel()
		{
			try { _cts?.Cancel(); } catch { }
		}

		/// <summary>建议首行(多行加省略号);流式期间随增量反复计算。</summary>
		private static string DisplayOf(string suggestion)
		{
			var firstLineEnd = suggestion.IndexOf('\n');
			var display = firstLineEnd < 0 ? suggestion : suggestion.Substring(0, firstLineEnd);
			display = display.TrimStart('\r', '\n');
			if (display.Length == 0) return "";
			if (firstLineEnd >= 0) display += "  ⋯";
			return display;
		}

		private void Render(string suggestion, SnapshotPoint caret)
		{
			// 流式增量:插入点未变 → 原位刷新文本,不重建装饰(防闪烁)不重打日志
			if (_ghost != null && _suggestionPoint == caret)
			{
				_suggestion = suggestion;
				var d = DisplayOf(suggestion);
				if (d.Length == 0) { RemoveGhost(); return; }
				_ghost.Text = d;
				_ghost.ToolTip = "[ZooVS 行内补全]" + Environment.NewLine + suggestion;
				return;
			}
			RemoveGhost();
			try
			{
				var caretLine = _view.Caret.ContainingTextViewLine;
				if (caretLine == null) return;

				var props = _view.FormattedLineSource?.DefaultTextProperties;
				var typeface = props?.Typeface
					?? new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
				var fontFamily = typeface.FontFamily ?? new FontFamily("Consolas");
				double fontSize = props?.FontRenderingEmSize ?? 12.0;

				// 插入点视觉坐标:光标字符边界的右缘(已含缩进/变宽字体/DPI,无需再测前缀宽度)
				var caretBounds = caretLine.GetCharacterBounds(caret);

				// 只显示首行,多行用省略提示 + ToolTip
				var display = DisplayOf(suggestion);
				if (display.Length == 0) return;

				_ghost = new TextBlock
				{
					Text = display,
					FontFamily = fontFamily,
					FontSize = fontSize,
					Foreground = Brushes.DimGray,
					Opacity = 0.75,
					// 悬停可见的 ZooVS 标记:与 VS 自带 IntelliCode ghost text 区分
					ToolTip = "[ZooVS 行内补全]" + Environment.NewLine + suggestion,
				};
				TryLog($"[inline] 显示建议 @ {ViewFileLabel()}:{CaretLine()} ({display.Length} 字符, 模型 {InlineCompletionSettings.Current.model})");

				// OwnerControlled:层不做自身摆位(TextRelative 会按 span 把元素钉在行首、覆盖偏移,
				// 灰字曾因此叠在行首现有文本上不可见);层画布坐标系 = 视图坐标,Canvas.SetXxx 直接生效
				TryLog("渲染建议@" + ViewFileLabel() + ":" + CaretLine() + " x=" + caretBounds.Right.ToString("F0") + " y=" + caretLine.TextTop.ToString("F0") + " [" + (display.Length > 50 ? display.Substring(0, 50) + "..." : display) + "] 模型=" + InlineCompletionSettings.Current.model);
				Canvas.SetLeft(_ghost, caretBounds.Right);
				Canvas.SetTop(_ghost, caretLine.TextTop);
				_suggestion = suggestion;
				_suggestionPoint = caret;
				_layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, _ghost, _ghost, null);
			}
			catch { }
		}

		private void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (_suggestion == null) return;
			if (e.Key == Key.Escape)
			{
				TryLog("Esc 关闭建议@" + ViewFileLabel());
				Dismiss();
				e.Handled = true;
			}
			else if (e.Key == Key.Tab)
			{
				Accept();
				e.Handled = true;
			}
		}

		private void Accept()
		{
			if (_suggestion == null) return;
			TryLog($"[inline] 用户已接受 @ {ViewFileLabel()}:{CaretLine()}");
			var insertAt = _suggestionPoint;
			var text = _suggestion;
			_applying = true;
			try
			{
				using (var edit = _view.TextBuffer.CreateEdit())
				{
					edit.Insert(insertAt, text);
					edit.Apply();
				}
				var end = new SnapshotPoint(_view.TextBuffer.CurrentSnapshot, Math.Min(insertAt.Position + text.Length, _view.TextBuffer.CurrentSnapshot.Length));
				_view.Caret.MoveTo(end);
			}
			catch { }
			finally
			{
				_applying = false;
				Dismiss();
			}
		}

		private string ViewFileLabel()
		{
			try
			{
				if (_view.TextBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.Text.ITextDocument>(typeof(Microsoft.VisualStudio.Text.ITextDocument), out var doc))
				{
					return System.IO.Path.GetFileName(doc.FilePath ?? "");
				}
			}
			catch { }
			return "?";
		}

		private int CaretLine()
		{
			try { return _view.Caret.Position.BufferPosition.GetContainingLine().LineNumber + 1; }
			catch { return 0; }
		}

		/// <summary>全流程文件日志(始终写文件;log 开关只控制输出窗格镜像)。</summary>
		private void TryLog(string message)
		{
			InlineCompletionSettings.EngineLog(message);
			try { if (InlineCompletionSettings.Current.log) InlineCompletionSettings.Sink?.Invoke("[inline] " + message); }
			catch { }
		}

		private void Dismiss()
		{
			_debounce?.Stop();
			_cts?.Cancel();
			RemoveGhost();
		}

		private void RemoveGhost()
		{
			_suggestion = null;
			if (_ghost != null)
			{
				try { _layer.RemoveAdornmentsByTag(_ghost); } catch { }
				_ghost = null;
			}
		}

		private void Teardown()
		{
			try
			{
				Dismiss();
				_view.TextBuffer.Changed -= OnBufferChanged;
				_view.VisualElement.PreviewKeyDown -= OnPreviewKeyDown;
				_cts?.Dispose();
			}
			catch { }
		}
	}
}
