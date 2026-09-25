using Cline;
using Host;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Grpc.Core;

namespace ClineVs.Bridge.Services
{
	/// <summary>
	/// host.DiffService —— 基于"临时文件对 + VS 内建比较视图"的实现。
	/// 原文件为左栏,提议内容写入 %TEMP%\ClineVS\diffs\{id}.ext 为右栏;
	/// 用户在 VS 比较视图中对右栏的编辑会直接落到临时文件,saveDocument 时回写原文件。
	/// </summary>
	public sealed class DiffServiceImpl : Host.DiffService.DiffServiceBase
	{
		private sealed class DiffEntry
		{
			public string RealPath;
			public string TempPath;
		}

		private readonly IBridgeHost _host;
		private readonly ConcurrentDictionary<string, DiffEntry> _diffs =
			new ConcurrentDictionary<string, DiffEntry>(StringComparer.Ordinal);

		public DiffServiceImpl(IBridgeHost host)
		{
			_host = host;
		}

		private static string TempDir
		{
			get
			{
				var dir = Path.Combine(Path.GetTempPath(), "ClineVS", "diffs");
				Directory.CreateDirectory(dir);
				return dir;
			}
		}

		public override async Task<OpenDiffResponse> openDiff(OpenDiffRequest request, ServerCallContext context)
		{
			if (!request.HasPath || string.IsNullOrEmpty(request.Path))
			{
				throw new RpcException(new Status(StatusCode.InvalidArgument, "openDiff 需要 path"));
			}
			if (!File.Exists(request.Path))
			{
				throw new RpcException(new Status(StatusCode.NotFound, "文件不存在:" + request.Path));
			}

			var id = Guid.NewGuid().ToString("N");
			var tempPath = Path.Combine(TempDir, id + Path.GetExtension(request.Path));
			File.WriteAllText(tempPath, request.HasContent ? request.Content : string.Empty, Encoding.UTF8);

			_diffs[id] = new DiffEntry { RealPath = request.Path, TempPath = tempPath };

			var caption = "Cline \u2022 " + Path.GetFileName(request.Path);
			await _host.OpenComparisonWindowAsync(id, request.Path, tempPath, caption);

			return new OpenDiffResponse
			{
				DiffId = id,
			};
		}

		public override Task<GetDocumentTextResponse> getDocumentText(
			GetDocumentTextRequest request, ServerCallContext context)
		{
			var entry = Require(request.DiffId);
			var content = File.Exists(entry.TempPath) ? File.ReadAllText(entry.TempPath, Encoding.UTF8) : string.Empty;
			return Task.FromResult(new GetDocumentTextResponse
			{
				Content = content,
			});
		}

		public override Task<ReplaceTextResponse> replaceText(ReplaceTextRequest request, ServerCallContext context)
		{
			var entry = Require(request.DiffId);
			var content = request.Content ?? string.Empty;

			if (request.HasStartLine && request.HasEndLine)
			{
				// 行区间替换(0 基,含头不含尾,与 VS Code TextDocument 语义一致)
				var lines = File.Exists(entry.TempPath)
					? File.ReadAllLines(entry.TempPath, Encoding.UTF8)
					: new string[0];
				var start = Math.Max(0, Math.Min(request.StartLine, lines.Length));
				var end = Math.Max(start, Math.Min(request.EndLine, lines.Length));
				var replacement = content.Length == 0
					? new string[0]
					: content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
						.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
				var builder = new System.Collections.Generic.List<string>(lines.Length);
				for (var i = 0; i < start; i++) builder.Add(lines[i]);
				builder.AddRange(replacement);
				for (var i = end; i < lines.Length; i++) builder.Add(lines[i]);
				File.WriteAllLines(entry.TempPath, builder, Encoding.UTF8);
			}
			else
			{
				File.WriteAllText(entry.TempPath, content, Encoding.UTF8);
			}
			return Task.FromResult(new ReplaceTextResponse());
		}

		public override Task<ScrollDiffResponse> scrollDiff(ScrollDiffRequest request, ServerCallContext context)
		{
			// M1:no-op(需要找到帧内文本视图并滚动,M2 完善)
			return Task.FromResult(new ScrollDiffResponse());
		}

		public override Task<TruncateDocumentResponse> truncateDocument(
			TruncateDocumentRequest request, ServerCallContext context)
		{
			var entry = Require(request.DiffId);
			if (request.HasEndLine && File.Exists(entry.TempPath))
			{
				var lines = File.ReadAllLines(entry.TempPath, Encoding.UTF8);
				var end = Math.Max(0, Math.Min(request.EndLine, lines.Length));
				var kept = new string[end];
				Array.Copy(lines, kept, end);
				File.WriteAllLines(entry.TempPath, kept, Encoding.UTF8);
			}
			return Task.FromResult(new TruncateDocumentResponse());
		}

		public override async Task<SaveDocumentResponse> saveDocument(
			SaveDocumentRequest request, ServerCallContext context)
		{
			var entry = Require(request.DiffId);
			var content = File.Exists(entry.TempPath)
				? File.ReadAllText(entry.TempPath, Encoding.UTF8)
				: string.Empty;

			// 保证宿主编辑器中的旧缓冲不会覆盖回写
			await _host.SaveDocumentIfDirtyAsync(entry.RealPath);
			File.WriteAllText(entry.RealPath, content, Encoding.UTF8);
			_host.Log($"[bridge] diff 已保存:{entry.RealPath}");
			return new SaveDocumentResponse();
		}

		public override async Task<CloseAllDiffsResponse> closeAllDiffs(
			CloseAllDiffsRequest request, ServerCallContext context)
		{
			// 语义:未保存的比较视图不应被强行关闭 —— 交给宿主实现按需处理
			await _host.CloseAllComparisonWindowsAsync();
			_diffs.Clear();
			return new CloseAllDiffsResponse();
		}

		public override async Task<OpenMultiFileDiffResponse> openMultiFileDiff(
			OpenMultiFileDiffRequest request, ServerCallContext context)
		{
			// M1:逐文件打开比较窗口(左栏内容落临时文件,右栏同理)。
			foreach (var diff in request.Diffs)
			{
				var id = Guid.NewGuid().ToString("N");
				var leftPath = Path.Combine(TempDir, id + ".left" + Path.GetExtension(diff.FilePath ?? ".txt"));
				var rightPath = Path.Combine(TempDir, id + ".right" + Path.GetExtension(diff.FilePath ?? ".txt"));
				File.WriteAllText(leftPath, diff.LeftContent ?? string.Empty, Encoding.UTF8);
				File.WriteAllText(rightPath, diff.RightContent ?? string.Empty, Encoding.UTF8);
				_diffs[id] = new DiffEntry { RealPath = diff.FilePath ?? leftPath, TempPath = rightPath };
				await _host.OpenComparisonWindowAsync(id, leftPath, rightPath,
					(request.HasTitle ? request.Title : "Cline") + " \u2022 " + (diff.FilePath ?? "file"));
			}
			return new OpenMultiFileDiffResponse();
		}

		private DiffEntry Require(string diffId)
		{
			if (!string.IsNullOrEmpty(diffId) && _diffs.TryGetValue(diffId, out var entry))
			{
				return entry;
			}
			throw new RpcException(new Status(StatusCode.NotFound, "未知 diff_id:" + diffId));
		}
	}
}
