using Cline;
using Host;
using System;
using System.Threading;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;

namespace ClineVs.Bridge.Services
{
	/// <summary>host.EnvService —— 剪贴板/外链/日志/版本等环境能力(9 个 RPC)。</summary>
	public sealed class EnvServiceImpl : Host.EnvService.EnvServiceBase
	{
		private readonly IBridgeHost _host;

		public EnvServiceImpl(IBridgeHost host)
		{
			_host = host;
		}

		public override async Task<Empty> clipboardWriteText(StringRequest request, ServerCallContext context)
		{
			await _host.SetClipboardTextAsync(request.Value ?? string.Empty);
			return new Empty();
		}

		public override async Task<Cline.String> clipboardReadText(EmptyRequest request, ServerCallContext context)
		{
			return new Cline.String { Value = await _host.GetClipboardTextAsync() ?? string.Empty };
		}

		public override async Task<GetHostVersionResponse> getHostVersion(EmptyRequest request, ServerCallContext context)
		{
			var hostVersion = await _host.GetHostVersionAsync();
			var extVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
			return new GetHostVersionResponse
			{
				Platform = hostVersion.Platform,
				Version = hostVersion.Version,
				ClineType = "Visual Studio 2022 Extension",
				ClineVersion = extVersion,
			};
		}

		public override Task<Cline.String> getIdeRedirectUri(EmptyRequest request, ServerCallContext context)
		{
			// 返回空 = 本宿主不支持深层链接,核心会退回 localhost HTTP 回调(AuthHandler)。
			return Task.FromResult(new Cline.String { Value = string.Empty });
		}

		public override Task<GetTelemetrySettingsResponse> getTelemetrySettings(
			EmptyRequest request, ServerCallContext context)
		{
			// UNSUPPORTED = 本宿主不管理 IDE 遥测设置,避免 core 因
			// "扩展遥测开/IDE 遥测关" 不一致而弹窗;Cline 自身上报可在其设置页关闭。
			return Task.FromResult(new GetTelemetrySettingsResponse
			{
				IsEnabled = Setting.Unsupported,
			});
		}

		public override async Task subscribeToTelemetrySettings(
			EmptyRequest request,
			IServerStreamWriter<TelemetrySettingsEvent> responseStream,
			ServerCallContext context)
		{
			// 静态设置:挂起直到核心取消。
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
			}
			catch (OperationCanceledException) { }
		}

		public override Task<Empty> shutdown(EmptyRequest request, ServerCallContext context)
		{
			_host.Log("[bridge] core 请求宿主桥关闭(env.shutdown)");
			_host.NotifyShutdownRequested();
			return Task.FromResult(new Empty());
		}

		public override Task<Empty> debugLog(StringRequest request, ServerCallContext context)
		{
			_host.Log("[core] " + request.Value);
			return Task.FromResult(new Empty());
		}

		public override Task<Empty> openExternal(StringRequest request, ServerCallContext context)
		{
			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = request.Value,
					UseShellExecute = true,
				});
			}
			catch (Exception ex)
			{
				_host.Log("[bridge] openExternal 失败:" + ex.Message);
			}
			return Task.FromResult(new Empty());
		}
	}
}
