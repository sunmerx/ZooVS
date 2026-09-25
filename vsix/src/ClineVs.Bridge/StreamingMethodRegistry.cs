using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClineVs.Bridge
{
	/// <summary>
	/// 从嵌入的 cline/*.proto 解析出"服务.方法 → 是否服务端流式"的契约表,
	/// 用于校验 webview grpc_request 的 is_streaming 标志与 proto 一致
	/// (与上游 core-connection-protocol.ts 的 dispatchCoreConnectionRequest 行为对齐)。
	/// 服务名取自 proto 内的 service 声明(如 task.proto 中的 TaskService)。
	/// </summary>
	public sealed class StreamingMethodRegistry
	{
		private static readonly Regex ServiceRegex = new Regex(
			@"service\s+(\w+)\s*\{", RegexOptions.Compiled);

		private static readonly Regex RpcRegex = new Regex(
			@"rpc\s+(\w+)\s*\([^)]*\)\s*returns\s*\(\s*(stream)?\s*\w+\s*\)",
			RegexOptions.Compiled);

		private readonly HashSet<string> _streamingMethods = new HashSet<string>(StringComparer.Ordinal);

		public StreamingMethodRegistry()
		{
			// 嵌入的 cline/*.proto 在 ClineVs.Protos.dll 中
			var assembly = typeof(Cline.Metadata).Assembly;
			foreach (var name in assembly.GetManifestResourceNames()
				.Where(n => n.Contains(".cline_proto.")))
			{
				using (var stream = assembly.GetManifestResourceStream(name))
				using (var reader = new System.IO.StreamReader(stream))
				{
					Parse(reader.ReadToEnd());
				}
			}
		}

		private void Parse(string text)
		{
			foreach (Match serviceMatch in ServiceRegex.Matches(text))
			{
				var serviceName = serviceMatch.Groups[1].Value;
				var block = ExtractBlock(text, serviceMatch.Index + serviceMatch.Length);
				if (block == null) continue;

				foreach (Match rpcMatch in RpcRegex.Matches(block))
				{
					if (rpcMatch.Groups[2].Success)
					{
						_streamingMethods.Add(serviceName + "." + PascalCase(rpcMatch.Groups[1].Value));
					}
				}
			}
		}

		/// <summary>从 offset 起提取配对大括号内的内容(允许嵌套枚举/消息)。</summary>
		private static string ExtractBlock(string text, int offset)
		{
			var depth = 1;
			var start = offset;
			for (var i = offset; i < text.Length && depth > 0; i++)
			{
				switch (text[i])
				{
					case '{': depth++; break;
					case '}':
						depth--;
						if (depth == 0) return text.Substring(start, i - start);
						break;
				}
			}
			return null;
		}

		/// <summary>service 形如 "cline.TaskService",method 形如 "startTask"。</summary>
		public bool IsStreaming(string service, string method)
		{
			var shortService = service != null && service.StartsWith("cline.", StringComparison.Ordinal)
				? service.Substring("cline.".Length)
				: service;
			return _streamingMethods.Contains(shortService + "." + PascalCase(method ?? string.Empty));
		}

		public int Count => _streamingMethods.Count;

		private static string PascalCase(string name)
		{
			if (string.IsNullOrEmpty(name)) return name;
			return char.ToUpperInvariant(name[0]) + name.Substring(1);
		}
	}
}
