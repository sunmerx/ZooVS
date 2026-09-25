using Cline;
using Host;
using System.Threading.Tasks;
using Grpc.Core;

namespace ClineVs.Bridge.Services
{
	/// <summary>host.TestingService —— 上游集成测试专用,M1 返回空。</summary>
	public sealed class TestingServiceImpl : Host.TestingService.TestingServiceBase
	{
		private readonly IBridgeHost _host;

		public TestingServiceImpl(IBridgeHost host)
		{
			_host = host;
		}

		public override Task<GetWebviewHtmlResponse> getWebviewHtml(
			GetWebviewHtmlRequest request, ServerCallContext context)
		{
			return Task.FromResult(new GetWebviewHtmlResponse
			{
				Html = string.Empty,
			});
		}
	}
}
