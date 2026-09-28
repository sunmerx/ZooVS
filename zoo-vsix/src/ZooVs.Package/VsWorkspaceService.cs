using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using CWT = Microsoft.CodeAnalysis.Workspace;

namespace ZooVs.Package
{
	/// <summary>
	/// 进程内 VS SDK 能力实现(解决方案/项目模型 + 语言功能)。
	/// - 解决方案/项目/打开文档:DTE + RunningDocumentTable,反映 VS 真实状态
	/// - 语言查询:VS 自己的 Roslyn VisualStudioWorkspace(含未保存修改)。**强类型访问**:
	///   编译期引用 Microsoft.CodeAnalysis.*(ExcludeAssets=runtime 不随 VSIX 分发,运行时由
	///   devenv 绑定重定向统一到 VS 自带版本)。此前 dynamic 派发在符号对象上不可靠——
	///   自测实测补全通而符号链全断((?)/回退子进程),故全部改为编译期类型。
	/// - 工作区不可用或异常时回退 RoslynHostService 子进程;vs_build 仍走子进程 MSBuild。
	/// </summary>
	public sealed class VsWorkspaceService
	{
		private readonly AsyncPackage _package;
		private readonly Action<string> _log;
		private readonly RoslynHostService _fallback;
		private readonly System.Threading.SemaphoreSlim _lock = new System.Threading.SemaphoreSlim(1, 1);

		private bool _workspaceResolved;
		private CWT _workspace;

		public VsWorkspaceService(AsyncPackage package, Action<string> log, RoslynHostService fallback)
		{
			_package = package;
			_log = log;
			_fallback = fallback;
		}

		/// <summary>语言/解决方案工具入口:tool 为 vs_*(除 vs_debug_* / vs_build)。</summary>
		public async Task<string> CallToolAsync(string tool, string argsJson)
		{
			if (tool == "vs_build") return await _fallback.CallToolAsync(tool, argsJson);
			if (tool == "vs_solution_model") return await SolutionModelAsync();

			await _lock.WaitAsync();
			try
			{
				System.Text.Json.Nodes.JsonObject args;
				try
				{
					args = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson)
						as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
				}
				catch { args = new System.Text.Json.Nodes.JsonObject(); }

				var ws = await GetWorkspaceAsync();
				if (ws == null)
				{
					return await _fallback.CallToolAsync(tool, argsJson);
				}
				var solution = ws.CurrentSolution;

				switch (tool)
				{
					case "vs_symbol_search": return await SymbolSearchAsync(solution, GetString(args, "pattern") ?? "");
					case "vs_find_references": return await FindReferencesAsync(solution, GetString(args, "symbolRef") ?? "", callers: false);
					case "vs_find_implementations": return await FindImplementationsAsync(solution, GetString(args, "symbolRef") ?? "");
					case "vs_type_hierarchy": return await HierarchyAsync(solution, GetString(args, "typeRef") ?? "");
					case "vs_find_calls": return await FindCallsAsync(solution, GetString(args, "direction") ?? "callers", GetString(args, "symbolRef") ?? "");
					case "vs_find_referencing_files": return await FindReferencingFilesAsync(solution, GetString(args, "symbolRef") ?? "");
					case "vs_completion_at": return await CompletionAtAsync(solution,
						GetString(args, "filePath"), GetInt(args, "line", 1), GetInt(args, "column", 1));
					default: return await _fallback.CallToolAsync(tool, argsJson);
				}
			}
			catch (Exception ex)
			{
				_log("[vs-workspace] " + tool + " 进程内执行失败,回退子进程:" + ex.GetType().Name + ": " + ex.Message);
				try { return await _fallback.CallToolAsync(tool, argsJson); }
				catch (Exception ex2) { return "工具执行失败(进程内与子进程均失败): " + ex.Message + " / " + ex2.Message; }
			}
			finally { _lock.Release(); }
		}

		// ---- 解决方案/项目/文档模型(纯 SDK,与 Roslyn 无关) ----

		private async Task<string> SolutionModelAsync()
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			var sb = new StringBuilder();
			try
			{
				var dte = (EnvDTE.DTE)await _package.GetServiceAsync(typeof(EnvDTE.DTE));
				var sol = dte.Solution;
				if (sol == null || string.IsNullOrEmpty(sol.FullName))
				{
					sb.AppendLine("solution: (VS 未打开解决方案)");
				}
				else
				{
					sb.AppendLine("solution: " + sol.FullName);
					sb.AppendLine("startup project: " + StartupProjectName(sol));
					var projectLines = new List<string>();
					foreach (EnvDTE.Project p in sol.Projects)
					{
						try
						{
							if (p == null || string.IsNullOrEmpty(p.Name)) continue;
							var kind = ProjectKindLabel(p);
							int itemCount = 0;
							try { itemCount = p.ProjectItems == null ? 0 : p.ProjectItems.Count; } catch { }
							projectLines.Add("- " + p.Name + " [" + kind + "] 顶层项 " + itemCount +
								(string.IsNullOrEmpty(p.FullName) ? "" : "  (" + p.FullName + ")"));
						}
						catch { }
					}
					sb.AppendLine("projects (" + projectLines.Count + "):");
					foreach (var line in projectLines) sb.AppendLine(line);
				}
				sb.AppendLine();
				sb.Append(await OpenDocumentsAsync(dte));
				return sb.ToString().TrimEnd();
			}
			catch (Exception ex)
			{
				return "读取解决方案模型失败: " + ex.Message;
			}
		}

		private static string StartupProjectName(EnvDTE.Solution sol)
		{
			try
			{
				var startups = sol.SolutionBuild?.StartupProjects as System.Collections.IEnumerable;
				var first = startups?.Cast<object>().FirstOrDefault();
				return first?.ToString() ?? "(未设置)";
			}
			catch { return "(未设置)"; }
		}

		private static string ProjectKindLabel(EnvDTE.Project p)
		{
			try
			{
				if (p.Kind == EnvDTE.Constants.vsProjectKindSolutionItems) return "solution folder";
				if (p.Kind == EnvDTE.Constants.vsProjectKindUnmodeled) return "unmodeled";
				var lang = p.CodeModel?.Language;
				if (!string.IsNullOrEmpty(lang)) return lang.Replace("EnvDTE.", "").Replace("CSharp", "C#").Replace("VB", "VB.NET");
			}
			catch { }
			return "project";
		}

		private async Task<string> OpenDocumentsAsync(EnvDTE.DTE dte)
		{
			var sb = new StringBuilder();
			try
			{
				var rdt = new Microsoft.VisualStudio.Shell.RunningDocumentTable(_package);
				var docs = new List<string>();
				foreach (var entry in rdt)
				{
					try
					{
						var path = entry.Moniker;
						if (!string.IsNullOrEmpty(path) && File.Exists(path)) docs.Add(path);
					}
					catch { }
				}
				var codeDocs = docs.Where(IsCodeFile).Take(40).ToList();
				sb.AppendLine("open documents (" + codeDocs.Count + " shown of " + docs.Count + " total):");
				foreach (var d in codeDocs) sb.AppendLine("  " + d);
			}
			catch (Exception ex) { sb.AppendLine("open documents: 读取失败 " + ex.Message); }

			try
			{
				var active = dte.ActiveDocument?.FullName;
				sb.AppendLine("active document: " + (string.IsNullOrEmpty(active) ? "(无)" : active));
			}
			catch { }
			return sb.ToString().TrimEnd();
		}

		private static bool IsCodeFile(string path)
		{
			var ext = Path.GetExtension(path).ToLowerInvariant();
			switch (ext)
			{
				case ".cs": case ".vb": case ".cpp": case ".cxx": case ".c": case ".h": case ".hpp":
				case ".ts": case ".tsx": case ".js": case ".jsx": case ".py": case ".fs": case ".xaml":
				case ".csproj": case ".vbproj": case ".sln": case ".slnx": case ".json": case ".xml":
				case ".resx": case ".cshtml": case ".razor":
					return true;
				default: return false;
			}
		}

		// ---- VisualStudioWorkspace 获取(强类型) ----

		private static readonly string[] WorkspaceContracts =
		{
			"Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem.VisualStudioWorkspace",
			"Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace",
			"Microsoft.VisualStudio.LanguageServices.RoslynVisualStudioWorkspace",
		};

		private async Task<CWT> GetWorkspaceAsync()
		{
			if (_workspaceResolved) return _workspace;
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			_workspaceResolved = true;
			try
			{
				var componentModel = (IComponentModel)await _package.GetServiceAsync(typeof(SComponentModel));
				var provider = componentModel?.DefaultExportProvider;
				if (provider == null) { _log("[vs-workspace] MEF 不可用,回退子进程"); return null; }

				// 优先按类型导出取(VisualStudioWorkspace 继承自 Workspace)
				try
				{
					var typed = provider.GetExports<CWT>().FirstOrDefault();
					if (typed != null)
					{
						_workspace = typed.Value;
						_log("[vs-workspace] 已连接 VS Roslyn 工作区(类型导出),语言查询进程内执行");
						return _workspace;
					}
				}
				catch (Exception ex) { _log("[vs-workspace] 类型导出获取失败:" + ex.Message); }

				foreach (var contract in WorkspaceContracts)
				{
					try
					{
						var export = provider.GetExports<object>(contract).FirstOrDefault();
						if (export != null)
						{
							_workspace = export.Value as CWT;
							if (_workspace != null)
							{
								_log("[vs-workspace] 已连接 VS Roslyn 工作区(" + contract + "),语言查询进程内执行");
								return _workspace;
							}
						}
					}
					catch { }
				}
				_log("[vs-workspace] VisualStudioWorkspace 导出不可用,语言查询回退 Roslyn 子进程");
			}
			catch (Exception ex)
			{
				_log("[vs-workspace] MEF 获取失败,回退子进程: " + ex.Message);
			}
			return null;
		}

		// ---- 语言查询(强类型 Roslyn) ----

		private async Task<string> SymbolSearchAsync(Solution solution, string pattern)
		{
			if (string.IsNullOrEmpty(pattern)) return "参数错误:需要 pattern。";
			var sb = new StringBuilder();
			var lines = new List<string>();
			foreach (var project in solution.Projects)
			{
				var comp = await project.GetCompilationAsync();
				if (comp == null) continue;
				foreach (var sym in comp.GetSymbolsWithName(pattern))
				{
					if (lines.Count >= 50) goto done;
					var line = DescribeSymbol(sym);
					if (line != null) lines.Add(line);
				}
			}
		done:
			if (lines.Count == 0) return "未找到匹配 '" + pattern + "' 的符号";
			sb.Append("匹配 '" + pattern + "' 的符号 (" + lines.Count + ",最多 50,格式: 类型 全名 @ 文件:行):\n");
			sb.Append(string.Join("\n", lines));
			return sb.ToString();
		}

		private async Task<string> FindReferencesAsync(Solution solution, string symbolRef, bool callers)
		{
			var symbol = await ResolveSymbolAsync(solution, symbolRef);
			if (symbol == null) return "未定位到符号: " + symbolRef + "(优先用 vs_symbol_search 返回的 文件:行:列;注意路径须为绝对路径)";

			var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
			var lines = new List<string>();
			var callersCount = new Dictionary<string, int>();
			foreach (var rs in refs)
			{
				foreach (var rl in rs.Locations)
				{
					try
					{
						var path = rl.Document.FilePath;
						var pos = rl.Location.SourceSpan.Start;
						var lp = rl.Location.GetLineSpan().StartLinePosition;
						if (callers)
						{
							var model = await rl.Document.GetSemanticModelAsync();
							if (model == null) continue;
							var caller = model.GetEnclosingSymbol(pos);
							if (caller == null || SymbolEqualityComparer.Default.Equals(caller, symbol)) continue;
							if (caller is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }) continue;
							var key = caller.ToDisplayString();
							callersCount[key] = callersCount.TryGetValue(key, out var n) ? n + 1 : 1;
						}
						else
						{
							lines.Add("  " + path + ":" + (lp.Line + 1) + ":" + (lp.Character + 1));
						}
					}
					catch { }
					if (!callers && lines.Count >= 80) break;
				}
			}
			var sb = new StringBuilder();
			if (callers)
			{
				sb.AppendLine("callers of " + symbol.ToDisplayString() + " (" + callersCount.Count + "):");
				foreach (var kv in callersCount.OrderByDescending(kv => kv.Value).Take(50))
					sb.AppendLine("  - " + kv.Key + "  (调用 " + kv.Value + " 次)");
				if (callersCount.Count == 0) sb.AppendLine("  (未找到调用方)");
			}
			else
			{
				sb.AppendLine("references of " + symbol.ToDisplayString() + " (" + lines.Count + "):");
				foreach (var l in lines) sb.AppendLine(l);
				if (lines.Count == 0) sb.AppendLine("  (无引用)");
			}
			return sb.ToString().TrimEnd();
		}

		private async Task<string> FindImplementationsAsync(Solution solution, string symbolRef)
		{
			var symbol = await ResolveSymbolAsync(solution, symbolRef);
			if (symbol == null) return "未定位到符号: " + symbolRef;
			var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution);
			var items = impls.Take(50).ToList();
			var sb = new StringBuilder();
			sb.AppendLine("implementations of " + symbol.ToDisplayString() + " (" + items.Count + "):");
			foreach (var s in items)
			{
				var d = DescribeSymbol(s);
				sb.AppendLine("  " + (d ?? s.ToDisplayString()));
			}
			if (items.Count == 0) sb.AppendLine("  (无实现)");
			return sb.ToString().TrimEnd();
		}

		private async Task<string> HierarchyAsync(Solution solution, string typeRef)
		{
			var symbol = await ResolveSymbolAsync(solution, typeRef);
			if (symbol == null) return "未定位到类型: " + typeRef;
			var named = symbol as INamedTypeSymbol;
			if (named == null) return "该符号不是类型: " + symbol.ToDisplayString() + "(若为成员,请用它的类型名查继承)";
			var sb = new StringBuilder();
			sb.AppendLine("type: " + named.ToDisplayString());
			var bt = named.BaseType;
			int guard = 0;
			while (bt != null && guard++ < 12) { sb.AppendLine("  base: " + bt.ToDisplayString()); bt = bt.BaseType; }
			foreach (var iface in named.Interfaces) sb.AppendLine("  implements: " + iface.ToDisplayString());
			var impls = await SymbolFinder.FindImplementationsAsync(named, solution);
			var items = impls.Take(50).ToList();
			if (items.Count > 0)
			{
				sb.AppendLine("derived / implementations (" + items.Count + "):");
				foreach (var s in items) sb.AppendLine("  " + (DescribeSymbol(s) ?? s.ToDisplayString()));
			}
			return sb.ToString().TrimEnd();
		}

		private async Task<string> FindCallsAsync(Solution solution, string direction, string symbolRef)
		{
			if (string.Equals(direction, "callees", StringComparison.OrdinalIgnoreCase))
			{
				return await CalleesAsync(solution, symbolRef);
			}
			return await FindReferencesAsync(solution, symbolRef, callers: true);
		}

		private async Task<string> CalleesAsync(Solution solution, string symbolRef)
		{
			var symbol = await ResolveSymbolAsync(solution, symbolRef);
			if (symbol == null) return "未定位到符号: " + symbolRef;
			var callees = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var loc in symbol.Locations)
			{
				if (!loc.IsInSource) continue;
				try
				{
					var doc = solution.GetDocument(loc.SourceTree);
					if (doc == null) continue;
					var model = await doc.GetSemanticModelAsync();
					var root = await doc.GetSyntaxRootAsync();
					if (model == null || root == null) continue;
					var declaration = root.FindNode(loc.SourceSpan);
					foreach (var inv in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
					{
						var target = model.GetSymbolInfo(inv).Symbol
							?? model.GetSymbolInfo(inv).CandidateSymbols.FirstOrDefault();
						if (target?.OriginalDefinition != null) callees.Add(target.OriginalDefinition.ToDisplayString());
						if (callees.Count >= 60) break;
					}
					foreach (var ctor in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
					{
						var m = model.GetSymbolInfo(ctor).Symbol as IMethodSymbol;
						if (m != null) callees.Add("new " + m.ContainingType.ToDisplayString());
						if (callees.Count >= 60) break;
					}
				}
				catch { }
				if (callees.Count >= 60) break;
			}
			var sb = new StringBuilder();
			sb.AppendLine("callees of " + symbol.ToDisplayString() + " (" + callees.Count + "):");
			foreach (var c in callees.Take(50)) sb.AppendLine("  - " + c);
			if (callees.Count == 0) sb.AppendLine("  (无调用点)");
			return sb.ToString().TrimEnd();
		}

		private async Task<string> FindReferencingFilesAsync(Solution solution, string symbolRef)
		{
			var symbol = await ResolveSymbolAsync(solution, symbolRef);
			if (symbol == null) return "未定位到符号: " + symbolRef;
			var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
			var files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var rs in refs)
			{
				foreach (var rl in rs.Locations)
				{
					try
					{
						var path = rl.Document.FilePath;
						files[path] = files.TryGetValue(path, out var n) ? n + 1 : 1;
					}
					catch { }
				}
			}
			var sb = new StringBuilder();
			sb.AppendLine("referencing files of " + symbol.ToDisplayString() + " (" + files.Count + "):");
			foreach (var kv in files.OrderByDescending(kv => kv.Value).Take(40))
				sb.AppendLine("  " + kv.Key + "  (引用 " + kv.Value + " 处)");
			if (files.Count == 0) sb.AppendLine("  (无)");
			return sb.ToString().TrimEnd();
		}

		private async Task<string> CompletionAtAsync(Solution solution, string filePath, int line, int column)
		{
			if (string.IsNullOrEmpty(filePath)) return "参数错误:需要 filePath(绝对路径) / line / column。";
			var doc = FindDocument(solution, filePath);
			if (doc == null) return "文件不在工作区中: " + filePath + "(需绝对路径;可用 vs_solution_model 查看打开的文档)";
			try
			{
				var text = await doc.GetTextAsync();
				var clampedLine = Math.Max(0, Math.Min(line - 1, text.Lines.Count - 1));
				var pos = text.Lines[clampedLine].Start + Math.Max(0, column - 1);
				var service = CompletionService.GetService(doc);
				if (service == null) return "此文档语言不支持补全服务。";
				var items = await GetCompletionsCappedAsync(service, doc, pos);
				if (items.Count == 0 && column > 1)
				{
					// 光标在语句末尾/分号后时 Roslyn 常返回空——回退前一列重试一次
					items = await GetCompletionsCappedAsync(service, doc, pos - 1);
				}
				var sb = new StringBuilder();
				foreach (var it in items) sb.AppendLine("  " + it);
				sb.Insert(0, "completions at " + Path.GetFileName(filePath) + ":" + line + ":" + column + " (" + items.Count + ",最多 40):\n");
				return sb.ToString().TrimEnd();
			}
			catch (Exception ex)
			{
				return "补全查询失败: " + ex.Message;
			}
		}

		private static async Task<List<string>> GetCompletionsCappedAsync(CompletionService service, Document doc, int pos)
		{
			var result = new List<string>();
			var list = await service.GetCompletionsAsync(doc, pos);
			if (list == null) return result;
			foreach (var item in list.Items)
			{
				result.Add(item.DisplayText);
				if (result.Count >= 40) break;
			}
			return result;
		}

		// ---- 符号解析(强类型) ----

		/// <summary>解析 File:Line:Col(1 基,文件须为 VS 工作区可匹配的路径——相对路径按
		/// 文件名在打开文档/项目文档中匹配)或 FQN(Namespace.Type[.Member])。</summary>
		private static async Task<ISymbol> ResolveSymbolAsync(Solution solution, string symbolRef)
		{
			if (string.IsNullOrEmpty(symbolRef)) return null;
			var parts = symbolRef.Split(':');
			if (parts.Length >= 3)
			{
				var filePath = string.Join(":", parts.Take(parts.Length - 2));
				int line, col;
				if (int.TryParse(parts[parts.Length - 2], out line) && int.TryParse(parts[parts.Length - 1], out col))
				{
					// agent 常给相对路径:先精确匹配,再按文件名匹配
					var doc = FindDocument(solution, filePath)
						?? FindDocumentByFileName(solution, Path.GetFileName(filePath));
					if (doc == null) return null;
					var text = await doc.GetTextAsync();
					var clampedLine = Math.Max(0, Math.Min(line - 1, text.Lines.Count - 1));
					var pos = text.Lines[clampedLine].Start + Math.Max(0, col - 1);
					var model = await doc.GetSemanticModelAsync();
					var root = await doc.GetSyntaxRootAsync();
					if (model == null || root == null) return null;
					// 解析顺序(实测教训:GetEnclosingSymbol 在"声明位置"返回的是容器——
					// 类名标识符→命名空间、方法名→类,即"上浮一层";引用位置才正确):
					// 1) 声明节点直接取声明符号 GetDeclaredSymbol(标识符 token 的父节点即声明节点)
					// 2) 引用位置取绑定符号 GetSymbolInfo
					// 3) 兜底 GetEnclosingSymbol(仅在标识符无法绑定时,如行首/空白附近)
					var token = root.FindToken(pos);
					try
					{
						var declared = model.GetDeclaredSymbol(token.Parent)
							?? model.GetDeclaredSymbol(token.Parent?.Parent);
						if (declared != null && !declared.IsImplicitlyDeclared) return declared.OriginalDefinition;
					}
					catch { }
					if (token.Parent != null)
					{
						var info = model.GetSymbolInfo(token.Parent);
						if (info.Symbol != null) return info.Symbol.OriginalDefinition;
						if (info.CandidateSymbols.Length > 0) return info.CandidateSymbols[0].OriginalDefinition;
					}
					var enclosing = model.GetEnclosingSymbol(pos);
					if (enclosing != null && !enclosing.IsImplicitlyDeclared) return enclosing.OriginalDefinition;
					return enclosing?.OriginalDefinition;
				}
				return null;
			}

			// FQN
			var memberName = symbolRef.Split('.').Last();
			var containerName = symbolRef.Substring(0, symbolRef.Length - memberName.Length).TrimEnd('.');
			if (containerName.Length == 0) containerName = memberName;
			foreach (var project in solution.Projects)
			{
				var comp = await project.GetCompilationAsync();
				if (comp == null) continue;
				var type = comp.GetTypeByMetadataName(containerName);
				if (type == null && containerName == memberName)
				{
					foreach (var s in comp.GetSymbolsWithName(memberName))
					{
						var head = HeadOf(s.ToDisplayString());
						if (head == symbolRef) return s;
					}
					continue;
				}
				if (type == null) continue;
				if (containerName == symbolRef) return type;
				var members = type.GetMembers(memberName).ToList();
				if (members.Count > 0) return members[0];
			}
			return null;
		}

		private static Document FindDocument(Solution solution, string filePath)
		{
			if (string.IsNullOrEmpty(filePath)) return null;
			return solution.Projects
				.SelectMany(p => p.Documents)
				.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>按文件名(忽略目录)匹配文档——agent 常传相对路径。</summary>
		private static Document FindDocumentByFileName(Solution solution, string fileName)
		{
			if (string.IsNullOrEmpty(fileName)) return null;
			return solution.Projects
				.SelectMany(p => p.Documents)
				.FirstOrDefault(d => string.Equals(Path.GetFileName(d.FilePath ?? ""), fileName, StringComparison.OrdinalIgnoreCase));
		}

		private static string DescribeSymbol(ISymbol sym)
		{
			try
			{
				var sb = new StringBuilder();
				sb.Append(sym.Kind).Append(' ').Append(sym.ToDisplayString());
				var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
				if (loc != null)
				{
					var span = loc.GetLineSpan();
					sb.Append("  @ ").Append(loc.SourceTree?.FilePath).Append(":").Append(span.StartLinePosition.Line + 1)
						.Append(":").Append(span.StartLinePosition.Character + 1);
				}
				return sb.ToString();
			}
			catch { return null; }
		}

		private static string HeadOf(string display)
		{
			var paren = display.IndexOf('(');
			return paren > 0 ? display.Substring(0, paren) : display;
		}

		// ---- 诊断(强类型) ----

		public sealed class DocDiagnostic
		{
			public int Line; public int Character; public int EndLine; public int EndCharacter;
			public int Severity; // 0=Error 1=Warning(VS Code 语义)
			public string Message;
		}

		/// <summary>打开文档的编译诊断(仅 Error/Warning);工作区不可用返回 null。
		/// 实现:按项目的 Compilation.GetDiagnostics() 过滤到"打开文档"的语法树——
		/// Document.GetSyntaxDiagnosticsAsync 是 4.14+ API,4.12 没有(上一版 dynamic 调用
		/// 静默失败导致"永远零诊断")。</summary>
		public async Task<Dictionary<string, List<DocDiagnostic>>> GetOpenDocumentDiagnosticsAsync()
		{
			var ws = await GetWorkspaceAsync();
			if (ws == null) return null;
			var solution = ws.CurrentSolution;

			// 打开文档:语法树 → 文件路径映射
			var openDocByTree = new Dictionary<SyntaxTree, string>();
			foreach (var project in solution.Projects)
			{
				foreach (var doc in project.Documents)
				{
					try
					{
						if (string.IsNullOrEmpty(doc.FilePath)) continue;
						if (!ws.IsDocumentOpen(doc.Id)) continue;
						var tree = await doc.GetSyntaxTreeAsync();
						if (tree != null) openDocByTree[tree] = doc.FilePath;
					}
					catch { }
				}
			}
			if (openDocByTree.Count == 0) return new Dictionary<string, List<DocDiagnostic>>();

			var result = new Dictionary<string, List<DocDiagnostic>>(StringComparer.OrdinalIgnoreCase);
			int total = 0;
			foreach (var project in solution.Projects)
			{
				if (total >= 300) break;
				Compilation comp = null;
				try { comp = await project.GetCompilationAsync(); } catch { }
				if (comp == null) continue;
				IEnumerable<Diagnostic> all = null;
				try { all = comp.GetDiagnostics(); } catch { }
				if (all == null) continue;
				foreach (var d in all)
				{
					if (total >= 300) break;
					try
					{
						if (d.Severity != DiagnosticSeverity.Error && d.Severity != DiagnosticSeverity.Warning) continue;
						var tree = d.Location?.SourceTree;
						if (tree == null) continue;
						string filePath;
						if (!openDocByTree.TryGetValue(tree, out filePath)) continue;
						var item = ToDiagnostic(d);
						if (item == null) continue;
						List<DocDiagnostic> list;
						if (!result.TryGetValue(filePath, out list)) { list = new List<DocDiagnostic>(); result[filePath] = list; }
						if (list.Count < 50) { list.Add(item); total++; }
					}
					catch { }
				}
			}
			return result;
		}

		private static DocDiagnostic ToDiagnostic(Diagnostic d)
		{
			try
			{
				if (d.Severity != DiagnosticSeverity.Error && d.Severity != DiagnosticSeverity.Warning) return null;
				var span = d.Location.GetLineSpan();
				var start = span.StartLinePosition;
				var end = span.EndLinePosition;
				return new DocDiagnostic
				{
					Line = start.Line + 1,
					Character = start.Character + 1,
					EndLine = end.Line + 1,
					EndCharacter = end.Character + 1,
					Severity = d.Severity == DiagnosticSeverity.Error ? 0 : 1,
					Message = d.GetMessage(),
				};
			}
			catch { return null; }
		}

		// ---- 参数辅助 ----

		private static string GetString(System.Text.Json.Nodes.JsonObject args, string key)
		{
			return args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv
				? jv.ToString() : null;
		}

		private static int GetInt(System.Text.Json.Nodes.JsonObject args, string key, int fallback)
		{
			if (args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv)
			{
				return (int)(double)jv;
			}
			return fallback;
		}
	}
}
