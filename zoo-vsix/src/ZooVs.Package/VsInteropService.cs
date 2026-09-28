using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ZooVs.Package
{
	/// <summary>
	/// ZooVS 交互桥(A3/A4/A6/A7 + A1 diff):
	/// - internal_* hostCall:对话框(IVsUIShell)、文件对话框、diff 比较窗口
	///   (IVsDifferenceService.OpenComparisonWindow2——ClineVS 已验证调用)、剪贴板、openExternal
	/// - 环境推送:定时把 编辑器上下文(A3,DTE ActiveDocument/Selection/缓冲文本)与
	///   诊断快照(A6,VS Roslyn 工作区)推给 host.cjs(host.ts 存入 globalThis,
	///   shim 的 activeTextEditor / languages.getDiagnostics 返回真实值,上游自动入上下文)
	/// 所有 DTE/VS 服务调用在 UI 主线程。
	/// </summary>
	public sealed class VsInteropService : IDisposable
	{
		private readonly AsyncPackage _package;
		private readonly Action<string> _log;
		private readonly VsWorkspaceService _workspace;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

		private Func<string, Task> _sendLine;
		private Timer _editorTimer;
		private Timer _diagTimer;
		private int _editorInFlight;
		private int _diagInFlight;
		private string _lastCheapSignature;
		private volatile string _lastEditorDiag;
		private DateTime _lastEditorPush = DateTime.MinValue;
		private DateTime _lastDiagPush = DateTime.MinValue;
		private bool _disposed;

		public Func<string, Task> SendLine
		{
			set { _sendLine = value; }
		}

		public VsInteropService(AsyncPackage package, Action<string> log, VsWorkspaceService workspace)
		{
			_package = package;
			_log = log;
			_workspace = workspace;
		}

		public void Start()
		{
			if (_editorTimer != null) return;
			_editorTimer = new Timer(_ => _ = PushEditorContextAsync(), null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));
			_diagTimer = new Timer(_ => _ = PushDiagnosticsAsync(), null, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40));
			_log("[interop] 编辑器上下文(6s)与诊断(40s)推送已启动");
		}

		public async Task<string> CallToolAsync(string tool, string argsJson)
		{
			System.Text.Json.Nodes.JsonObject args;
			try
			{
				args = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson)
					as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
			}
			catch { args = new System.Text.Json.Nodes.JsonObject(); }

			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			switch (tool)
			{
				case "vs_editor_context":
				case "internal_editor_context": return await EditorContextReportAsync();
				case "vs_diagnostics":
				case "internal_diagnostics": return await DiagnosticsReportAsync();
				case "internal_show_message": return ShowMessage(
					GetString(args, "level"), GetString(args, "message"), GetStringArray(args, "items"));
				case "internal_open_dialog": return OpenFileDialog(GetBool(args, "canSelectMany", false));
				case "internal_save_dialog": return SaveFileDialog(GetString(args, "defaultName"));
				case "internal_open_diff": return await OpenDiffAsync(
					GetString(args, "leftPath"), GetString(args, "rightPath"), GetString(args, "title"));
				case "internal_clipboard_read": return ClipboardRead();
				case "internal_clipboard_write": ClipboardWrite(GetString(args, "text") ?? ""); return "ok";
				case "internal_open_external": return OpenExternal(GetString(args, "uri"));
				case "internal_inline_completion_state":
					{
						var cfg = InlineCompletionSettings.Current;
						var state = new System.Text.Json.Nodes.JsonObject
						{
							["enabled"] = cfg.enabled,
							["configured"] = !string.IsNullOrEmpty(cfg.endpoint) && !string.IsNullOrEmpty(cfg.model),
							["model"] = cfg.model ?? "",
						};
						return state.ToJsonString();
					}
				case "internal_set_inline_completion":
					{
						var cfg = InlineCompletionSettings.Load();
						cfg.enabled = GetBool(args, "enabled", false);
						InlineCompletionSettings.Save(cfg);
						_log("[interop] 行内补全 " + (cfg.enabled ? "已启用" : "已关闭"));
						var state = new System.Text.Json.Nodes.JsonObject
						{
							["enabled"] = cfg.enabled,
							["configured"] = !string.IsNullOrEmpty(cfg.endpoint) && !string.IsNullOrEmpty(cfg.model),
							["model"] = cfg.model ?? "",
						};
						return state.ToJsonString();
					}
				default: return "未知交互工具:" + tool;
			}
		}

		// ---- A1:diff 比较窗口(ClineVS DiffServiceImpl+VsBridgeHost 已验证的调用) ----

		private async Task<string> OpenDiffAsync(string leftPath, string rightPath, string title)
		{
			if (string.IsNullOrEmpty(leftPath) || string.IsNullOrEmpty(rightPath) || !File.Exists(leftPath))
			{
				return "diff 参数无效(left=" + leftPath + ")";
			}
			// 右侧可能是提议内容尚未落盘:上游传文件路径,不存在时用空文件占位
			if (!File.Exists(rightPath))
			{
				try { File.WriteAllText(rightPath, "", Encoding.UTF8); } catch { }
			}
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			try
			{
				var differenceService = (IVsDifferenceService)await _package.GetServiceAsync(typeof(SVsDifferenceService));
				if (differenceService == null) return "无法获取 IVsDifferenceService";
				var caption = string.IsNullOrEmpty(title) ? "ZooVS Diff" : title;
				var frame = differenceService.OpenComparisonWindow2(
					leftPath,
					rightPath,
					Path.GetFileName(leftPath) + " (Original)",
					Path.GetFileName(leftPath) + " (Proposed)",
					caption,
					caption,
					"ZooVS Diff",
					"Diff",
					0);
				if (frame != null)
				{
					Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
					return "diff 窗口已打开:" + caption;
				}
				return "比较窗口打开失败:" + caption;
			}
			catch (Exception ex)
			{
				return "打开 diff 失败: " + ex.Message;
			}
		}

		// ---- A4:消息框与文件对话框 ----

		private string ShowMessage(string level, string message, string[] items)
		{
			try
			{
				items = items ?? new string[0];

				// VS Code:showXxxMessage(...items) resolve 点击的 item 字符串;Cancel → undefined
				System.Windows.Forms.MessageBoxButtons buttons;
				string[] mapping;
				if (items.Length <= 1)
				{
					buttons = System.Windows.Forms.MessageBoxButtons.OKCancel;
					mapping = new[] { items.Length == 1 ? items[0] : "OK", null };
				}
				else
				{
					buttons = System.Windows.Forms.MessageBoxButtons.YesNoCancel;
					mapping = new[] { items[0], items[1], null };
				}

				var icon = level == "error" ? System.Windows.Forms.MessageBoxIcon.Error
					: level == "warning" ? System.Windows.Forms.MessageBoxIcon.Warning
					: System.Windows.Forms.MessageBoxIcon.Information;

				var result = System.Windows.Forms.MessageBox.Show(message ?? "", "ZooVS", buttons, icon);
				return result == System.Windows.Forms.DialogResult.OK || result == System.Windows.Forms.DialogResult.Yes
					? mapping[0]
					: result == System.Windows.Forms.DialogResult.No && mapping.Length > 1 ? mapping[1] : null;
			}
			catch (Exception ex)
			{
				_log("[interop] ShowMessageBox 失败: " + ex.Message);
				return null;
			}
		}

		private string OpenFileDialog(bool canSelectMany)
		{
			try
			{
				var dlg = new Microsoft.Win32.OpenFileDialog
				{
					Multiselect = canSelectMany,
					CheckFileExists = true,
				};
				if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return "[]";
				var sb = new StringBuilder("[");
				foreach (var f in dlg.FileNames)
				{
					sb.Append("{\"fsPath\":").Append(System.Text.Json.JsonEncodedText.Encode(f).ToString()).Append("},");
				}
				if (sb[sb.Length - 1] == ',') sb.Length--;
				sb.Append("]");
				return sb.ToString();
			}
			catch (Exception ex)
			{
				_log("[interop] OpenFileDialog 失败: " + ex.Message);
				return "[]";
			}
		}

		private string SaveFileDialog(string defaultName)
		{
			try
			{
				var dlg = new Microsoft.Win32.SaveFileDialog { FileName = defaultName ?? "" };
				if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.FileName)) return "null";
				return "{\"fsPath\":" + System.Text.Json.JsonEncodedText.Encode(dlg.FileName).ToString() + "}";
			}
			catch
			{
				return "null";
			}
		}

		// ---- A7:剪贴板与外部浏览器 ----

		private string ClipboardRead()
		{
			try
			{
				var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
				return System.Text.Json.JsonEncodedText.Encode(text).ToString();
			}
			catch { return "\"\""; }
		}

		private void ClipboardWrite(string text)
		{
			try { System.Windows.Clipboard.SetText(text ?? ""); }
			catch (Exception ex) { _log("[interop] 剪贴板写入失败: " + ex.Message); }
		}

		private string OpenExternal(string uri)
		{
			try
			{
				if (string.IsNullOrEmpty(uri)) return "false";
				Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
				return "true";
			}
			catch (Exception ex)
			{
				_log("[interop] openExternal 失败: " + ex.Message);
				return "false";
			}
		}

		// ---- A3:编辑器上下文 ----

		private async Task<string> EditorContextReportAsync()
		{
			var node = await BuildEditorContextAsync();
			if (node == null)
			{
				return "当前 VS 没有打开的文档。" + (_lastEditorDiag != null ? " [" + _lastEditorDiag + "]" : "");
			}
			var sb = new StringBuilder();
			sb.AppendLine("active document: " + (string)node["path"] +
				((bool)node["isDirty"] ? "  (未保存)" : ""));
			var sel = node["selection"] as System.Text.Json.Nodes.JsonObject;
			if (sel != null && sel["text"] != null)
			{
				var text = (string)sel["text"];
				sb.AppendLine("selection (L" + sel["activeLine"] + "): " + (text.Length > 600 ? text.Substring(0, 600) + "…" : text));
			}
			sb.Append("open documents: " + string.Join(", ", (node["openDocuments"] as System.Text.Json.Nodes.JsonArray)
				.Select(v => (string)v)));
			return sb.ToString();
		}

		private async Task<System.Text.Json.Nodes.JsonObject> BuildEditorContextAsync()
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			try
			{
				var dte = (EnvDTE.DTE)await _package.GetServiceAsync(typeof(EnvDTE.DTE));
				var doc = dte?.ActiveDocument;
				// 回退:某些窗口状态下 ActiveDocument 为空而 ActiveWindow.Document 可用
				if (doc == null || string.IsNullOrEmpty(doc.FullName))
				{
					try { doc = dte?.ActiveWindow?.Document ?? doc; } catch { }
				}
				if (doc == null || string.IsNullOrEmpty(doc.FullName))
				{
					int rdtCount = 0;
					try
					{
						var rdt = new Microsoft.VisualStudio.Shell.RunningDocumentTable(_package);
						rdtCount = rdt.Cast<object>().Count();
					}
					catch { }
					_lastEditorDiag = "ActiveDocument 为空(dte=" + (dte != null ? "ok" : "null") +
						",RDT 文档 " + rdtCount + " 项)——若 VS 中确实打开了代码文件,请把此句发回";
					return null;
				}
				_lastEditorDiag = null;

				var node = new System.Text.Json.Nodes.JsonObject
				{
					["path"] = doc.FullName,
					["language"] = doc.Language ?? "",
					["isDirty"] = !doc.Saved,
				};

				string bufferText = null;
				int lineCount = 0;
				try
				{
					var td = doc.Object("TextDocument") as EnvDTE.TextDocument;
					if (td != null)
					{
						var ep = td.StartPoint.CreateEditPoint();
						bufferText = ep.GetText(td.EndPoint);
						lineCount = ep.Line;
						try { lineCount = td.EndPoint.Line; } catch { }
					}
				}
				catch { }
				if (bufferText == null && File.Exists(doc.FullName))
				{
					try { bufferText = File.ReadAllText(doc.FullName, Encoding.UTF8); } catch { }
				}
				node["lineCount"] = lineCount;
				// 缓冲文本(<512KB)供 shim 的 document.getText() 使用
				node["text"] = bufferText != null && bufferText.Length <= 512 * 1024 ? bufferText : null;

				var selObj = new System.Text.Json.Nodes.JsonObject();
				try
				{
					var sel = doc.Selection as EnvDTE.TextSelection;
					if (sel != null)
					{
						selObj["activeLine"] = sel.CurrentLine - 1;
						selObj["activeChar"] = sel.CurrentColumn - 1;
						selObj["anchorLine"] = sel.AnchorPoint.Line - 1;
						selObj["anchorChar"] = 0;
						var selText = sel.Text;
						selObj["text"] = selText != null && selText.Length <= 64 * 1024 ? selText : null;
					}
				}
				catch { }
				node["selection"] = selObj;

				// 其它打开文档(去当前)
				var openList = new System.Text.Json.Nodes.JsonArray();
				try
				{
					var rdt = new Microsoft.VisualStudio.Shell.RunningDocumentTable(_package);
					var current = doc.FullName;
					foreach (var entry in rdt)
					{
						try
						{
							var p = entry.Moniker;
							if (!string.IsNullOrEmpty(p) && File.Exists(p) &&
								!string.Equals(p, current, StringComparison.OrdinalIgnoreCase) &&
								IsCodeLike(p))
							{
								openList.Add(p);
								if (openList.Count >= 15) break;
							}
						}
						catch { }
					}
				}
				catch { }
				node["openDocuments"] = openList;

				return node;
			}
			catch (Exception ex)
			{
				_log("[interop] 编辑器上下文读取失败: " + ex.Message);
				return null;
			}
		}

		private static bool IsCodeLike(string path)
		{
			var ext = Path.GetExtension(path).ToLowerInvariant();
			switch (ext)
			{
				case ".cs": case ".vb": case ".cpp": case ".h": case ".ts": case ".js": case ".py":
				case ".fs": case ".xaml": case ".csproj": case ".sln": case ".json": case ".xml":
				case ".razor": case ".cshtml": case ".md":
					return true;
				default: return false;
			}
		}

		// ---- A6:诊断 ----

		private async Task<string> DiagnosticsReportAsync()
		{
			var sb = new StringBuilder();
			var diags = await _workspace.GetOpenDocumentDiagnosticsAsync();
			if (diags == null)
			{
				return "语言工作区不可用(未连接 VS Roslyn 工作区),无法读取诊断。";
			}
			if (diags.Count == 0)
			{
				return "当前打开的文档没有编译错误或警告(注:仅统计 Roslyn 工作区认定的已打开文档)。";
			}
			int errors = 0, warnings = 0;
			foreach (var file in diags)
			{
				sb.AppendLine(Path.GetFileName(file.Key) + " (" + file.Value.Count + "):");
				foreach (var d in file.Value)
				{
					if (d.Severity == 0) errors++; else warnings++;
					sb.AppendLine("  [" + (d.Severity == 0 ? "error" : "warning") + "] L" + d.Line + ": " + Truncate(d.Message, 240));
				}
			}
			sb.Insert(0, "打开文档的诊断: " + errors + " 错误 / " + warnings + " 警告\n");
			return sb.ToString().TrimEnd();
		}

		// ---- 推送循环 ----

		/// <summary>廉价签名:仅 DTE 元数据,不读缓冲文本。</summary>
		private async Task<string> BuildCheapEditorSignatureAsync()
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			try
			{
				var dte = (EnvDTE.DTE)await _package.GetServiceAsync(typeof(EnvDTE.DTE));
				var doc = dte?.ActiveDocument;
				if (doc == null || string.IsNullOrEmpty(doc.FullName)) return "(none)";
				var sel = doc.Selection as EnvDTE.TextSelection;
				if (sel != null)
				{
					return doc.FullName + "|" + doc.Saved + "|" + sel.CurrentLine + "|" + sel.CurrentColumn + "|" + sel.AnchorPoint.Line;
				}
				return doc.FullName + "|" + doc.Saved;
			}
			catch { return "(error)"; }
		}

		private async Task PushEditorContextAsync()
		{
			if (_disposed || Interlocked.CompareExchange(ref _editorInFlight, 1, 0) != 0) return;
			try
			{
				// 廉价签名门控:先只读 DTE 元数据(路径/脏/选区行列),未变化则不做
				// 全量缓冲读取与 hash——否则大文件每 6 秒一次整读是 CPU/内存尖峰
				var cheap = await BuildCheapEditorSignatureAsync();
				var cheapChanged = cheap != _lastCheapSignature;
				if (!cheapChanged && DateTime.UtcNow - _lastEditorPush <= TimeSpan.FromSeconds(60))
				{
					return; // 无变化:跳过全量构建
				}
				_lastCheapSignature = cheap;

				var node = await BuildEditorContextAsync();
				if (node != null)
				{
					_lastEditorPush = DateTime.UtcNow;
					var push = new System.Text.Json.Nodes.JsonObject { ["type"] = "envPush", ["editor"] = node };
					await SendAsync(push);
				}
				else if (cheapChanged)
				{
					_lastEditorPush = DateTime.UtcNow;
					await SendAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "envPush", ["editor"] = null });
				}
			}
			catch (Exception ex)
			{
				_log("[interop] 编辑器推送失败: " + ex.Message);
			}
			finally { Interlocked.Exchange(ref _editorInFlight, 0); }
		}

		private async Task PushDiagnosticsAsync()
		{
			if (_disposed || Interlocked.CompareExchange(ref _diagInFlight, 1, 0) != 0) return;
			try
			{
				var diags = await _workspace.GetOpenDocumentDiagnosticsAsync();
				if (diags == null) return;
				var arr = new System.Text.Json.Nodes.JsonArray();
				foreach (var file in diags)
				{
					var list = new System.Text.Json.Nodes.JsonArray();
					foreach (var d in file.Value)
					{
						list.Add(new System.Text.Json.Nodes.JsonObject
						{
							["line"] = d.Line,
							["character"] = d.Character,
							["endLine"] = d.EndLine,
							["endCharacter"] = d.EndCharacter,
							["severity"] = d.Severity, // 0=Error 1=Warning(VS Code 语义)
							["message"] = d.Message,
							["source"] = "roslyn",
						});
					}
					arr.Add(new System.Text.Json.Nodes.JsonArray { file.Key, list });
				}
				var push = new System.Text.Json.Nodes.JsonObject { ["type"] = "envPush", ["diagnostics"] = arr };
				await SendAsync(push);
				_lastDiagPush = DateTime.UtcNow;
			}
			catch (Exception ex)
			{
				_log("[interop] 诊断推送失败: " + ex.Message);
			}
			finally { Interlocked.Exchange(ref _diagInFlight, 0); }
		}

		private async Task SendAsync(System.Text.Json.Nodes.JsonObject node)
		{
			var send = _sendLine;
			if (send != null) await send(node.ToJsonString());
		}

		private static string Truncate(string s, int max)
		{
			if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
			return s.Substring(0, max) + "…";
		}

		private static string GetString(System.Text.Json.Nodes.JsonObject args, string key)
		{
			return args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv
				? jv.ToString() : null;
		}

		private static bool GetBool(System.Text.Json.Nodes.JsonObject args, string key, bool fallback)
		{
			if (args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
			return fallback;
		}

		private static string[] GetStringArray(System.Text.Json.Nodes.JsonObject args, string key)
		{
			if (args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonArray arr)
			{
				return arr.Where(x => x is System.Text.Json.Nodes.JsonValue).Select(x => x.ToString()).ToArray();
			}
			return null;
		}

		public void Dispose()
		{
			_disposed = true;
			try { _editorTimer?.Dispose(); _diagTimer?.Dispose(); } catch { }
			_editorTimer = null;
			_diagTimer = null;
		}
	}
}
