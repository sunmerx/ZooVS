using Cline;
using Host;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;

namespace ClineVs.Bridge.Services
{
	/// <summary>host.WindowService —— 编辑器/对话框/通知(10 个 RPC)。</summary>
	public sealed class WindowServiceImpl : Host.WindowService.WindowServiceBase
	{
		private readonly IBridgeHost _host;

		public WindowServiceImpl(IBridgeHost host)
		{
			_host = host;
		}

		public override async Task<TextEditorInfo> showTextDocument(ShowTextDocumentRequest request, ServerCallContext context)
		{
			await _host.ShowTextDocumentAsync(request.Path);
			return new TextEditorInfo
			{
				DocumentPath = request.Path,
				IsActive = true,
			};
		}

		public override async Task<SelectedResources> showOpenDialogue(ShowOpenDialogueRequest request, ServerCallContext context)
		{
			var filters = request.Filters?.Files?.ToArray() ?? new string[0];
			var paths = await _host.ShowOpenDialogAsync(request.CanSelectMany, request.OpenLabel, filters);
			var response = new SelectedResources();
			response.Paths.AddRange(paths ?? new string[0]);
			return response;
		}

		public override async Task<SelectedResponse> showMessage(ShowMessageRequest request, ServerCallContext context)
		{
			var options = request.Options;
			var selected = await _host.ShowMessageBoxAsync(
				request.Message,
				(int)request.Type,
				options?.Modal ?? false,
				options?.Detail ?? string.Empty,
				options?.Items?.ToArray() ?? new string[0]);
			return new SelectedResponse
			{
				SelectedOption = selected,
			};
		}

		public override async Task<ShowInputBoxResponse> showInputBox(ShowInputBoxRequest request, ServerCallContext context)
		{
			var response = await _host.ShowInputBoxAsync(request.Title, request.Prompt, request.Value);
			return new ShowInputBoxResponse
			{
				Response = response,
			};
		}

		public override async Task<ShowSaveDialogResponse> showSaveDialog(ShowSaveDialogRequest request, ServerCallContext context)
		{
			var filters = new System.Collections.Generic.Dictionary<string, string[]>();
			if (request.Options != null && request.Options.Filters != null)
			{
				foreach (var pair in request.Options.Filters)
				{
					filters[pair.Key] = pair.Value.Extensions.ToArray();
				}
			}
			var selected = await _host.ShowSaveDialogAsync(request.Options?.DefaultPath, filters);
			return new ShowSaveDialogResponse
			{
				SelectedPath = selected,
			};
		}

		public override async Task<OpenFileResponse> openFile(OpenFileRequest request, ServerCallContext context)
		{
			await _host.OpenFileAsync(request.FilePath);
			return new OpenFileResponse();
		}

		public override async Task<OpenSettingsResponse> openSettings(OpenSettingsRequest request, ServerCallContext context)
		{
			await _host.OpenSettingsAsync(request.Query);
			return new OpenSettingsResponse();
		}

		public override async Task<GetOpenTabsResponse> getOpenTabs(GetOpenTabsRequest request, ServerCallContext context)
		{
			var paths = await _host.GetOpenDocumentPathsAsync();
			var response = new GetOpenTabsResponse();
			response.Paths.AddRange(paths ?? new string[0]);
			return response;
		}

		public override async Task<GetVisibleTabsResponse> getVisibleTabs(GetVisibleTabsRequest request, ServerCallContext context)
		{
			// M1:可见标签与打开文档同集合。
			var paths = await _host.GetOpenDocumentPathsAsync();
			var response = new GetVisibleTabsResponse();
			response.Paths.AddRange(paths ?? new string[0]);
			return response;
		}

		public override async Task<GetActiveEditorResponse> getActiveEditor(GetActiveEditorRequest request, ServerCallContext context)
		{
			var path = await _host.GetActiveEditorPathAsync();
			return new GetActiveEditorResponse
			{
				FilePath = path,
			};
		}
	}
}
