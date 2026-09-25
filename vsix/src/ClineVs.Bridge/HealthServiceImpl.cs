using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Health.V1;

namespace ClineVs.Bridge
{
	/// <summary>
	/// grpc.health.v1.Health 的自实现(daemon 启动时以 Check({service:""}) 探活)。
	/// 替代 Grpc.HealthCheck NuGet 包:该包钉死 Google.Protobuf 3.19.5,
	/// 与扩展内其余 protobuf 3.27.4 在 VSIX 精确绑定下冲突。
	/// </summary>
	public sealed class HealthServiceImpl : Health.HealthBase
	{
		private volatile HealthCheckResponse.Types.ServingStatus _status =
			HealthCheckResponse.Types.ServingStatus.Unknown;

		public void SetStatus(string host, HealthCheckResponse.Types.ServingStatus status)
		{
			_status = status;
		}

		public override Task<HealthCheckResponse> Check(HealthCheckRequest request, ServerCallContext context)
		{
			return Task.FromResult(new HealthCheckResponse { Status = _status });
		}

		public override async Task Watch(
			HealthCheckRequest request,
			IServerStreamWriter<HealthCheckResponse> responseStream,
			ServerCallContext context)
		{
			// daemon 不使用 Watch;推送当前状态后挂起至取消。
			await responseStream.WriteAsync(new HealthCheckResponse { Status = _status });
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
			}
			catch (OperationCanceledException) { }
		}
	}
}
