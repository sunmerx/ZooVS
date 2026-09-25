using Cline;
using Host;
using System;
using System.Threading.Tasks;
using Grpc.Core;

namespace ClineVs.Bridge
{
	/// <summary>
	/// host.CoreConnectionService —— 唯一需要真正走 gRPC 协议的服务。
	/// daemon 主动反连本服务;认证后,该双向流承载全部 cline/* 调用(JSON 透传)。
	/// </summary>
	public sealed class CoreConnectionServiceImpl : Host.CoreConnectionService.CoreConnectionServiceBase
	{
		private readonly CoreConnectionChannel _channel;

		public CoreConnectionServiceImpl(CoreConnectionChannel channel)
		{
			_channel = channel;
		}

		public override Task connect(
			IAsyncStreamReader<CoreConnectionMessage> requestStream,
			IServerStreamWriter<CoreConnectionMessage> responseStream,
			ServerCallContext context)
		{
			return _channel.RunAsync(requestStream, responseStream, context);
		}
	}
}
