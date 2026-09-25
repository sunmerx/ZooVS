using Cline;
using Host;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;

namespace ClineVs.Bridge
{
	/// <summary>
	/// 一条已认证的 core 连接(单条 CoreConnectionService 双向流)。
	/// 宿主向 core 发出 cline/* 调用(JSON 透传),按 request_id 重组分块响应。
	/// </summary>
	public sealed class CoreConnectionChannel : IDisposable
	{
		// 与上游 forwardCoreControllerResponse 的 MAX_RESPONSE_CHUNK_BYTES 保持一致
		private const int ExpectedChunkSize = 1024 * 1024;

		private sealed class PendingRequest
		{
			public readonly StringBuilder Buffer = new StringBuilder();
			public Action<string> OnMessage;
			public Action<string> OnError;
			public Action OnCompleted;
		}

		private readonly ConcurrentDictionary<string, PendingRequest> _pending =
			new ConcurrentDictionary<string, PendingRequest>();

		private volatile IServerStreamWriter<CoreConnectionMessage> _writer;
		// gRPC 的 IServerStreamWriter 同一时刻只允许一个挂起写,并发到达的请求在此串行化
		private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
		private readonly string _expectedToken;
		private readonly string _expectedInstanceId;

		public event Action Connected;
		public event Action Disconnected;
		public Action<string> LogSink;

		public bool IsConnected => _writer != null;

		public string InstanceId { get; private set; }

		public CoreConnectionChannel(string expectedToken, string expectedInstanceId)
		{
			_expectedToken = expectedToken;
			_expectedInstanceId = expectedInstanceId;
		}

		/// <summary>CoreConnectionService.connect 的实现体:认证 Hello,然后循环处理响应。</summary>
		internal async Task RunAsync(
			IAsyncStreamReader<CoreConnectionMessage> requestStream,
			IServerStreamWriter<CoreConnectionMessage> responseStream,
			ServerCallContext context)
		{
			var authenticated = false;
			try
			{
				while (await requestStream.MoveNext(context.CancellationToken))
				{
					var message = requestStream.Current;
					switch (message.PayloadCase)
					{
						case CoreConnectionMessage.PayloadOneofCase.Hello:
							var hello = message.Hello;
							if (!string.Equals(hello.Token, _expectedToken, StringComparison.Ordinal))
							{
								LogSink?.Invoke($"[bridge] core 连入被拒绝:token 不匹配(instance={hello.InstanceId})");
								throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid token"));
							}
							if (!string.IsNullOrEmpty(_expectedInstanceId) &&
								!string.Equals(hello.InstanceId, _expectedInstanceId, StringComparison.Ordinal))
							{
								LogSink?.Invoke($"[bridge] core 连入被拒绝:instance_id 不匹配({hello.InstanceId})");
								throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid instance"));
							}
							authenticated = true;
							InstanceId = hello.InstanceId;
							_writer = responseStream;
							await WriteAsyncLocked(new CoreConnectionMessage
							{
								Ready = new CoreConnectionReady(),
							});
							LogSink?.Invoke($"[bridge] core 连入并认证成功(instance={hello.InstanceId})");
							try { Connected?.Invoke(); } catch (Exception ex) { LogSink?.Invoke("[bridge] Connected 回调异常: " + ex.Message); }
							break;

						case CoreConnectionMessage.PayloadOneofCase.Response:
							HandleResponse(message.Response);
							break;

						case CoreConnectionMessage.PayloadOneofCase.Request:
							LogSink?.Invoke($"[bridge] 收到未预期的反向 Request:{message.Request.Service}.{message.Request.Method}");
							break;

						default:
							break;
					}
				}
			}
			catch (OperationCanceledException) { /* 连接被取消(正常关闭) */ }
			finally
			{
				_writer = null;
				if (authenticated)
				{
					LogSink?.Invoke("[bridge] core 连接已断开");
					try { Disconnected?.Invoke(); } catch (Exception ex) { LogSink?.Invoke("[bridge] Disconnected 回调异常: " + ex.Message); }
				}
			}
		}

		private void HandleResponse(CoreConnectionResponse response)
		{
			if (!_pending.TryGetValue(response.RequestId, out var pending))
			{
				LogSink?.Invoke($"[bridge] 收到无主响应 request_id={response.RequestId}");
				return;
			}

			try
			{
				var hasPayload = false;

				if (response.HasMessageJson)
				{
					pending.Buffer.Clear();
					pending.Buffer.Append(response.MessageJson);
					hasPayload = true;
				}
				else if (response.HasMessageJsonChunk && response.MessageJsonChunk.Length > 0)
				{
					pending.Buffer.Append(Encoding.UTF8.GetString(response.MessageJsonChunk.ToByteArray()));
				}

				if (response.MessageJsonComplete && pending.Buffer.Length > 0)
				{
					hasPayload = true;
				}

				if (hasPayload)
				{
					pending.OnMessage?.Invoke(pending.Buffer.ToString());
					pending.Buffer.Clear();
				}

				if (response.HasError && !string.IsNullOrEmpty(response.Error))
				{
					pending.OnError?.Invoke(response.Error);
				}

				if (response.Completed)
				{
					_pending.TryRemove(response.RequestId, out _);
					pending.OnCompleted?.Invoke();
				}
			}
			catch (Exception ex)
			{
				LogSink?.Invoke($"[bridge] 处理响应失败({response.RequestId}):{ex.Message}");
				_pending.TryRemove(response.RequestId, out _);
			}
		}

		/// <summary>
		/// 向 core 发起一次 cline/* 调用。
		/// onMessageJson 对流式方法会回调多次(每个事件一次,参数为完整 JSON 字符串)。
		/// </summary>
		public Task SendRequestAsync(
			string service,
			string method,
			string messageJson,
			bool isStreaming,
			Action<string> onMessageJson,
			Action<string> onError,
			Action onCompleted)
		{
			var writer = _writer;
			if (writer == null)
			{
				onError?.Invoke("core 尚未连接");
				onCompleted?.Invoke();
				return Task.CompletedTask;
			}

			var requestId = Guid.NewGuid().ToString("N");
			_pending[requestId] = new PendingRequest
			{
				OnMessage = onMessageJson,
				OnError = onError,
				OnCompleted = onCompleted,
			};

			return WriteAsyncLocked(new CoreConnectionMessage
			{
				Request = new CoreConnectionRequest
				{
					RequestId = requestId,
					Service = service,
					Method = method,
					MessageJson = messageJson ?? "{}",
					IsStreaming = isStreaming,
				},
			});
		}

		/// <summary>串行化的流写入:gRPC 不允许同一流上并发挂起多个写。</summary>
		private async Task WriteAsyncLocked(CoreConnectionMessage message)
		{
			await _writeLock.WaitAsync();
			try
			{
				var writer = _writer;
				if (writer == null)
				{
					LogSink?.Invoke("[bridge] 流已断开,丢弃待发消息");
					return;
				}
				await writer.WriteAsync(message);
			}
			finally
			{
				_writeLock.Release();
			}
		}

		public void CancelRequest(string requestId)
		{
			if (!_pending.ContainsKey(requestId)) return;
			_ = WriteAsyncLocked(new CoreConnectionMessage
			{
				Cancel = new CoreConnectionCancel { RequestId = requestId },
			});
		}

		public void Dispose()
		{
			_writer = null;
			_pending.Clear();
		}
	}
}
