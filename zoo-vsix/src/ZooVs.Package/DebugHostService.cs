using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace ZooVs.Package
{
	/// <summary>
	/// ZooVS 自主调试能力(C5):通过 EnvDTE Debugger 自动化(DTE.Debugger)驱动 VS 调试器,
	/// 进程内实现、零子进程。API 组合已由 test/dte_debug_smoke.ps1 在真实 devenv 上逐一验证
	/// (Breakpoints.Add / SolutionBuild.Debug / CurrentMode / Locals / GetExpression /
	/// StackFrames / StepOver / TerminateAll)。
	/// 注意:该 Debugger 对象的 IDispatch 暴露的是 CurrentMode(Debugger2+ 语义)而非
	/// DebugMode,PowerShell 晚绑定下后者取不到——C# 侧统一用 EnvDTE80.Debugger2.CurrentMode。
	/// 所有 DTE 访问在 UI 主线程;等待用"主线程采样 + Task.Delay"轮询,不挂 COM 事件
	/// (避免事件接收器生命周期问题)。工具调用串行化(一个 _lock)。
	/// </summary>
	public sealed class DebugHostService : IDisposable
	{
		private readonly AsyncPackage _package;
		private readonly Action<string> _log;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		private EnvDTE.DTE _dte;

		public DebugHostService(AsyncPackage package, Action<string> log)
		{
			_package = package;
			_log = log;
		}

		/// <summary>agent 侧 hostCall 入口:tool=vs_debug_*,argsJson 为 camelCase 参数对象。</summary>
		public async Task<string> CallToolAsync(string tool, string argsJson)
		{
			await _lock.WaitAsync();
			try
			{
				System.Text.Json.Nodes.JsonObject args;
				try
				{
					args = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson)
						as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
				}
				catch
				{
					args = new System.Text.Json.Nodes.JsonObject();
				}

				await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
				var dbg = Debugger();

				switch (tool)
				{
					case "vs_debug_start": return await StartAsync(dbg, GetInt(args, "waitSeconds", 180));
					case "vs_debug_stop": return await StopAsync(dbg);
					case "vs_debug_break": return await BreakAsync(dbg, GetInt(args, "waitSeconds", 30));
					case "vs_debug_set_breakpoint": return SetBreakpoint(dbg,
						GetString(args, "file"), GetInt(args, "line", 0), GetString(args, "condition"));
					case "vs_debug_remove_breakpoint": return RemoveBreakpoint(dbg,
						GetString(args, "file"), GetInt(args, "line", 0));
					case "vs_debug_run": return await RunAsync(dbg,
						GetString(args, "action") ?? "continue", GetInt(args, "waitSeconds", 60));
					case "vs_debug_inspect": return Inspect(dbg, GetString(args, "expression"));
					case "vs_debug_stack": return Stack(dbg);
					case "vs_debug_state": return State(dbg);
					default: return "未知调试工具:" + tool;
				}
			}
			catch (Exception ex)
			{
				_log("[debug] " + tool + " 失败:" + ex.Message);
				return "调试操作失败: " + ex.Message;
			}
			finally
			{
				_lock.Release();
			}
		}

		// ---- 工具实现 ----

		private async Task<string> StartAsync(EnvDTE80.Debugger2 dbg, int waitSeconds)
		{
			var mode = dbg.CurrentMode;
			if (mode == EnvDTE.dbgDebugMode.dbgBreakMode) return "已在调试中(断点暂停)。用 vs_debug_run 继续执行,或 vs_debug_stop 停止。";
			if (mode == EnvDTE.dbgDebugMode.dbgRunMode) return "已在调试运行中。用 vs_debug_break 暂停或等待断点。";

			try
			{
				_dte.Solution.SolutionBuild.Debug();
			}
			catch (Exception ex)
			{
				return "启动调试失败(请确认解决方案已加载且设置了启动项目): " + ex.Message;
			}

			// 先等进入调试(离开 design),再等离开 run(命中断点或程序退出)
			mode = await WaitUntilAsync(dbg, m => m != EnvDTE.dbgDebugMode.dbgDesignMode, 60);
			if (mode == EnvDTE.dbgDebugMode.dbgDesignMode)
			{
				return "启动调试超时:60 秒内未开始执行。请检查项目是否能构建运行。";
			}

			mode = await WaitUntilAsync(dbg, m => m != EnvDTE.dbgDebugMode.dbgRunMode, waitSeconds);
			if (mode == EnvDTE.dbgDebugMode.dbgBreakMode)
			{
				await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
				return "调试已启动并命中停点。当前: " + DescribeBreak(dbg);
			}
			if (mode == EnvDTE.dbgDebugMode.dbgDesignMode)
			{
				return "调试已启动,程序已运行结束退出(未命中断点)。";
			}
			return "调试运行中,等待 " + waitSeconds + " 秒未命中断点(仍在运行)。可用 vs_debug_break 暂停或再次 vs_debug_run 等待。";
		}

		private async Task<string> StopAsync(EnvDTE80.Debugger2 dbg)
		{
			if (dbg.CurrentMode == EnvDTE.dbgDebugMode.dbgDesignMode) return "当前不在调试中。";
			try { dbg.TerminateAll(); } catch { }
			var mode = await WaitUntilAsync(dbg, m => m == EnvDTE.dbgDebugMode.dbgDesignMode, 15);
			if (mode != EnvDTE.dbgDebugMode.dbgDesignMode)
			{
				try { _dte.ExecuteCommand("Debug.StopDebugging"); } catch { }
				mode = await WaitUntilAsync(dbg, m => m == EnvDTE.dbgDebugMode.dbgDesignMode, 15);
			}
			return mode == EnvDTE.dbgDebugMode.dbgDesignMode
				? "调试已停止,回到设计模式。"
				: "停止指令已发出,但 " + ModeName(mode) + " 状态未完全退出(可能有进程仍在终止中)。";
		}

		private async Task<string> BreakAsync(EnvDTE80.Debugger2 dbg, int waitSeconds)
		{
			if (dbg.CurrentMode != EnvDTE.dbgDebugMode.dbgRunMode)
			{
				return "当前状态是 " + ModeName(dbg.CurrentMode) + ",只有运行中才能暂停。";
			}
			try { dbg.Break(false); } catch (Exception ex) { return "暂停失败: " + ex.Message; }
			var mode = await WaitUntilAsync(dbg, m => m != EnvDTE.dbgDebugMode.dbgRunMode, waitSeconds);
			if (mode == EnvDTE.dbgDebugMode.dbgBreakMode)
			{
				await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
				return "已暂停。当前: " + DescribeBreak(dbg);
			}
			return ModeName(mode) == "design" ? "程序在暂停前已退出。" : "暂停等待超时,仍为 " + ModeName(mode) + "。";
		}

		private string SetBreakpoint(EnvDTE80.Debugger2 dbg, string file, int line, string condition)
		{
			if (string.IsNullOrEmpty(file) || line <= 0) return "参数错误:需要 file(绝对路径)和 line(1 基行号)。";
			try
			{
				dbg.Breakpoints.Add(File: file, Line: line, Condition: condition ?? "");
				return "断点已设置: " + Path.GetFileName(file) + ":" + line +
					(string.IsNullOrEmpty(condition) ? "" : " 条件=" + condition) +
					"。当前断点数 " + dbg.Breakpoints.Count + "。";
			}
			catch (Exception ex)
			{
				return "设置断点失败: " + ex.Message;
			}
		}

		private string RemoveBreakpoint(EnvDTE80.Debugger2 dbg, string file, int line)
		{
			if (string.IsNullOrEmpty(file) || line <= 0) return "参数错误:需要 file 和 line。";
			var removed = 0;
			foreach (EnvDTE.Breakpoint bp in dbg.Breakpoints)
			{
				if (bp.FileLine == line &&
					string.Equals(bp.File, file, StringComparison.OrdinalIgnoreCase))
				{
					try { bp.Delete(); removed++; } catch { }
				}
			}
			return removed > 0
				? "已删除 " + removed + " 个断点(" + Path.GetFileName(file) + ":" + line + ")。"
				: "未找到匹配的断点。现有断点:\n" + ListBreakpoints(dbg);
		}

		private async Task<string> RunAsync(EnvDTE80.Debugger2 dbg, string action, int waitSeconds)
		{
			var mode = dbg.CurrentMode;
			if (mode == EnvDTE.dbgDebugMode.dbgDesignMode)
			{
				return "当前不在调试中。用 vs_debug_start 启动调试。";
			}
			if (mode == EnvDTE.dbgDebugMode.dbgRunMode)
			{
				// 已在运行:直接等待停点
			}
			else
			{
				try
				{
					switch (action)
					{
						case "continue": dbg.Go(false); break;
						case "step_into": dbg.StepInto(false); break;
						case "step_over": dbg.StepOver(false); break;
						case "step_out": dbg.StepOut(false); break;
						default: return "未知 action: " + action + "(continue/step_into/step_over/step_out)";
					}
				}
				catch (Exception ex)
				{
					return "执行 " + action + " 失败: " + ex.Message;
				}
			}

			var finalMode = await WaitUntilAsync(dbg, m => m != EnvDTE.dbgDebugMode.dbgRunMode, waitSeconds);
			if (finalMode == EnvDTE.dbgDebugMode.dbgBreakMode)
			{
				await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
				return action + " 完成,当前停点: " + DescribeBreak(dbg);
			}
			if (finalMode == EnvDTE.dbgDebugMode.dbgDesignMode)
			{
				return action + " 后程序运行结束并退出调试(没有更多断点)。";
			}
			return action + " 已发出,仍在运行(" + waitSeconds + " 秒内未停)。可继续等待或 vs_debug_break 暂停。";
		}

		private string Inspect(EnvDTE80.Debugger2 dbg, string expression)
		{
			if (dbg.CurrentMode != EnvDTE.dbgDebugMode.dbgBreakMode)
			{
				return "当前状态 " + ModeName(dbg.CurrentMode) + ",只有在断点暂停时才能检查变量。";
			}
			var sb = new StringBuilder();
			// 模型可能把可选参数传成字面量 "null"(optional 参数常见误用)——按"无表达式"处理
			if (!string.IsNullOrEmpty(expression))
			{
				var trimmedExpr = expression.Trim();
				if (trimmedExpr == "null" || trimmedExpr == "none" || trimmedExpr == "<none>" || trimmedExpr == "undefined")
				{
					expression = null;
				}
			}
			if (!string.IsNullOrEmpty(expression))
			{
				try
				{
					var expr = dbg.GetExpression(expression, false, 3000);
					sb.AppendLine(expr.IsValidValue
						? expression + " = " + expr.Value + "  (" + expr.Type + ")"
						: "表达式无法求值: " + expression);
				}
				catch (Exception ex)
				{
					sb.AppendLine("求值异常: " + ex.Message);
				}
				return sb.ToString().TrimEnd();
			}

			var frame = dbg.CurrentStackFrame;
			sb.AppendLine("frame: " + DescribeBreak(dbg));
			try
			{
				var locals = frame.Locals.Cast<EnvDTE.Expression>().Take(40)
					.Select(e => SafeNameValue(e)).Where(s => s != null).ToList();
				sb.AppendLine(locals.Count > 0 ? "locals: " + string.Join(", ", locals) : "locals: (无)");
			}
			catch (Exception ex) { sb.AppendLine("locals 读取失败: " + ex.Message); }
			try
			{
				var argsList = frame.Arguments.Cast<EnvDTE.Expression>().Take(40)
					.Select(e => SafeNameValue(e)).Where(s => s != null).ToList();
				sb.AppendLine(argsList.Count > 0 ? "args: " + string.Join(", ", argsList) : "args: (无)");
			}
			catch { /* 有些栈帧无参数集合 */ }
			return sb.ToString().TrimEnd();
		}

		private string Stack(EnvDTE80.Debugger2 dbg)
		{
			if (dbg.CurrentMode != EnvDTE.dbgDebugMode.dbgBreakMode)
			{
				return "当前状态 " + ModeName(dbg.CurrentMode) + ",只有在断点暂停时才能看调用栈。";
			}
			var sb = new StringBuilder();
			try
			{
				var frames = dbg.CurrentThread.StackFrames.Cast<EnvDTE.StackFrame>().Take(25).ToList();
				sb.AppendLine("stack (" + frames.Count + " frames, top first):");
				for (int i = 0; i < frames.Count; i++)
				{
					sb.AppendLine("  #" + i + " " + frames[i].FunctionName);
				}
			}
			catch (Exception ex) { sb.AppendLine("栈读取失败: " + ex.Message); }
			// 注:本 interop 的 Debugger 接口未暴露 Threads 集合,线程列表省略
			return sb.ToString().TrimEnd();
		}

		private string State(EnvDTE80.Debugger2 dbg)
		{
			var sb = new StringBuilder();
			sb.AppendLine("mode: " + ModeName(dbg.CurrentMode));
			try
			{
				var procs = dbg.DebuggedProcesses.Cast<EnvDTE.Process>().ToList();
				sb.AppendLine("processes (" + procs.Count + "): " +
					string.Join("; ", procs.Select(p => Path.GetFileName(p.Name) + "(pid " + p.ProcessID + ")")));
			}
			catch { }
			sb.Append(ListBreakpoints(dbg));
			if (dbg.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode)
			{
				sb.AppendLine().Append("current: " + DescribeBreak(dbg));
			}
			return sb.ToString().TrimEnd();
		}

		// ---- 辅助 ----

		private EnvDTE80.Debugger2 Debugger()
		{
			if (_dte == null)
			{
				_dte = (EnvDTE.DTE)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
			}
			if (_dte == null) throw new InvalidOperationException("无法获取 DTE 服务");
			return (EnvDTE80.Debugger2)_dte.Debugger;
		}

		/// <summary>主线程采样 + 后台等待的轮询:每轮切回主线程读模式,谓词满足即返回。</summary>
		private async Task<EnvDTE.dbgDebugMode> WaitUntilAsync(
			EnvDTE80.Debugger2 dbg, Func<EnvDTE.dbgDebugMode, bool> predicate, int seconds)
		{
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, Math.Min(seconds, 600)));
			while (DateTime.UtcNow < deadline)
			{
				await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
				if (predicate(dbg.CurrentMode)) return dbg.CurrentMode;
				await Task.Delay(250);
			}
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			return dbg.CurrentMode;
		}

		private string DescribeBreak(EnvDTE80.Debugger2 dbg)
		{
			try
			{
				var frame = dbg.CurrentStackFrame;
				var location = "";
				var f2 = frame as EnvDTE90a.StackFrame2;
				var fileName = f2 != null ? f2.FileName : null;
				int line = 0;
				// 行号来源1:最后命中断点(命中场景精确;单步后可能陈旧,需与文件名一致才采用)
				try
				{
					var last = dbg.BreakpointLastHit;
					if (last != null && !string.IsNullOrEmpty(last.File) &&
						string.Equals(Path.GetFileName(last.File), Path.GetFileName(fileName ?? last.File), StringComparison.OrdinalIgnoreCase))
					{
						line = last.FileLine;
					}
				}
				catch { }
				// 行号来源2:VS 断点暂停时编辑器会跳到当前语句 → 活动文档选区当前行
				if (line == 0)
				{
					try
					{
						var doc = _dte?.ActiveDocument;
						var sel = doc?.Selection as EnvDTE.TextSelection;
						if (sel != null)
						{
							var docName = Path.GetFileName(doc.FullName ?? "");
							if (string.IsNullOrEmpty(fileName) || string.Equals(docName, Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase))
							{
								line = sel.CurrentLine;
								if (string.IsNullOrEmpty(fileName)) fileName = doc.FullName;
							}
						}
					}
					catch { }
				}
				if (line > 0)
				{
					location = Path.GetFileName(fileName) + ":" + line + " ";
				}
				else if (!string.IsNullOrEmpty(fileName))
				{
					location = Path.GetFileName(fileName) + " ";
				}
				return location + "in " + frame.FunctionName;
			}
			catch
			{
				return "(栈帧信息不可用)";
			}
		}

		private string ListBreakpoints(EnvDTE80.Debugger2 dbg)
		{
			try
			{
				var bps = dbg.Breakpoints.Cast<EnvDTE.Breakpoint>().ToList();
				if (bps.Count == 0) return "breakpoints: (无)";
				return "breakpoints (" + bps.Count + "):\n" + string.Join("\n", bps.Select(bp =>
					"  " + (string.IsNullOrEmpty(bp.File) ? bp.FunctionName : Path.GetFileName(bp.File) + ":" + bp.FileLine) +
					(bp.Enabled ? "" : " [disabled]") +
					(string.IsNullOrEmpty(bp.Condition) ? "" : " 条件=" + bp.Condition)));
			}
			catch
			{
				return "breakpoints: (读取失败)";
			}
		}

		private static string SafeNameValue(EnvDTE.Expression e)
		{
			try { return e.Name + "=" + Truncate(e.Value, 80); }
			catch { return null; }
		}

		private static string Truncate(string s, int max)
		{
			if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
			return s.Substring(0, max) + "…";
		}

		private static string ModeName(EnvDTE.dbgDebugMode mode)
		{
			switch (mode)
			{
				case EnvDTE.dbgDebugMode.dbgDesignMode: return "design(未调试)";
				case EnvDTE.dbgDebugMode.dbgBreakMode: return "break(断点暂停)";
				case EnvDTE.dbgDebugMode.dbgRunMode: return "run(运行中)";
				default: return mode.ToString();
			}
		}

		private static string GetString(System.Text.Json.Nodes.JsonObject args, string key)
		{
			return args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv
				? jv.ToString() : null;
		}

		private static int GetInt(System.Text.Json.Nodes.JsonObject args, string key, int fallback)
		{
			if (args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv)
			{
				return (int)(double)jv; // JSON 数值统一按 double 取
			}
			return fallback;
		}

		public void Dispose()
		{
			_dte = null;
		}
	}
}
