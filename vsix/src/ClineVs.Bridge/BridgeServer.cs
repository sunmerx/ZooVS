using Cline;
using Host;
using System;
using System.Linq;
using System.Threading.Tasks;
using ClineVs.Bridge.Services;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace ClineVs.Bridge
{
	/// <summary>
	/// 宿主桥接 gRPC 服务端,绑定 127.0.0.1。
	/// 提供:grpc.health.v1.Health(daemon 启动探活)、host/* 服务组、
	/// CoreConnectionService(daemon 反连,承载 cline/* JSON 透传)。
	/// 除 duplex 流外,所有调用须携带一次性令牌(请求头 cline-hostbridge-token,
	/// 与上游 hosts/external/host-bridge-auth.ts 的 HOST_BRIDGE_TOKEN_HEADER 一致)。
	/// </summary>
	public sealed class BridgeServer : IDisposable
	{
		public const string TokenHeader = "cline-hostbridge-token";

		private readonly IBridgeHost _host;
		private readonly string _token;
		private Grpc.Core.Server _server;
		private readonly HealthServiceImpl _health = new HealthServiceImpl();

		public CoreConnectionChannel Channel { get; private set; }

		public int BoundPort { get; private set; }

		public BridgeServer(IBridgeHost host, string token)
		{
			_host = host;
			_token = token;
			Channel = new CoreConnectionChannel(token, expectedInstanceId: null);
			Channel.LogSink = message => host.Log(message);
		}

		public void Start(int preferredPort = 0)
		{
			var interceptor = new TokenInterceptor(_token, _host);

			_server = new Grpc.Core.Server();

			_server.Services.Add(Host.EnvService.BindService(new EnvServiceImpl(_host)).Intercept(interceptor));
			_server.Services.Add(Host.WindowService.BindService(new WindowServiceImpl(_host)).Intercept(interceptor));
			_server.Services.Add(Host.WorkspaceService.BindService(new WorkspaceServiceImpl(_host)).Intercept(interceptor));
			_server.Services.Add(Host.DiffService.BindService(new DiffServiceImpl(_host)).Intercept(interceptor));
			_server.Services.Add(Host.TestingService.BindService(new TestingServiceImpl(_host)).Intercept(interceptor));
			_server.Services.Add(Host.CoreConnectionService.BindService(new CoreConnectionServiceImpl(Channel)).Intercept(interceptor));
			_server.Services.Add(Grpc.Health.V1.Health.BindService(_health).Intercept(interceptor));

			var port = preferredPort != 0 ? preferredPort : FindFreePort();
			_server.Ports.Add("127.0.0.1", port, ServerCredentials.Insecure);
			_server.Start();
			BoundPort = port;
			_health.SetStatus("", Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving);
			_host.Log($"[bridge] hostbridge 已监听 127.0.0.1:{BoundPort}(7 个服务)");
		}

		public async Task StopAsync()
		{
			_health.SetStatus("", Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.NotServing);
			if (_server != null)
			{
				await _server.ShutdownAsync();
				_server = null;
			}
			Channel.Dispose();
		}

		private static int FindFreePort()
		{
			var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
			listener.Start();
			try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
			finally { listener.Stop(); }
		}

		public void Dispose()
		{
			try { StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { /* 关闭时尽力而为 */ }
		}

		/// <summary>校验每个入站调用的令牌头;duplex 流(CoreConnection.connect)的令牌在 Hello 消息体内校验。</summary>
		private sealed class TokenInterceptor : Interceptor
		{
			private readonly string _token;
			private readonly IBridgeHost _host;

			public TokenInterceptor(string token, IBridgeHost host)
			{
				_token = token;
				_host = host;
			}

			public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
				TRequest request, ServerCallContext context,
				UnaryServerMethod<TRequest, TResponse> continuation)
			{
				try
				{
					Validate(context);
					return await continuation(request, context);
				}
				catch (RpcException) { throw; }
				catch (Exception ex)
				{
					_host.Log($"[bridge] {context.Method} handler 异常:{ex}");
					throw;
				}
			}

			public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
				IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
				ClientStreamingServerMethod<TRequest, TResponse> continuation)
			{
				Validate(context);
				return continuation(requestStream, context);
			}

			public override Task ServerStreamingServerHandler<TRequest, TResponse>(
				TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
				ServerStreamingServerMethod<TRequest, TResponse> continuation)
			{
				Validate(context);
				return continuation(request, responseStream, context);
			}

			public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
				IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
				ServerCallContext context,
				DuplexStreamingServerMethod<TRequest, TResponse> continuation)
			{
				// CoreConnectionService.connect:流建立时拿不到 hello,令牌在消息体内校验。
				return continuation(requestStream, responseStream, context);
			}

			private void Validate(ServerCallContext context)
			{
				if (string.IsNullOrEmpty(_token)) return; // 未启用令牌(调试模式)
				var header = context.RequestHeaders.FirstOrDefault(h => h.Key == TokenHeader)?.Value;
				if (!string.Equals(header, _token, StringComparison.Ordinal))
				{
					_host.Log($"[bridge] 拒绝无令牌调用:{context.Method}");
					throw new RpcException(new Status(StatusCode.Unauthenticated, "missing or invalid cline-hostbridge-token"));
				}
			}
		}
	}
}
