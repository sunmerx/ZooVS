using Cline;
using Host;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;

namespace ClineVs.Bridge.Services
{
	/// <summary>host.WorkspaceService —— 工作区/诊断/终端降级/搜索(10 个 RPC)。</summary>
	public sealed class WorkspaceServiceImpl : Host.WorkspaceService.WorkspaceServiceBase
	{
		private readonly IBridgeHost _host;

		public WorkspaceServiceImpl(IBridgeHost host)
		{
			_host = host;
		}

		public override async Task<GetWorkspacePathsResponse> getWorkspacePaths(
			GetWorkspacePathsRequest request, ServerCallContext context)
		{
			var (id, paths) = await _host.GetWorkspaceInfoAsync();
			var response = new GetWorkspacePathsResponse
			{
				Id = id,
			};
			response.Paths.AddRange(paths ?? new string[0]);
			return response;
		}

		public override async Task<SaveOpenDocumentIfDirtyResponse> saveOpenDocumentIfDirty(
			SaveOpenDocumentIfDirtyRequest request, ServerCallContext context)
		{
			var saved = false;
			if (request.HasFilePath && !string.IsNullOrEmpty(request.FilePath))
			{
				saved = await _host.SaveDocumentIfDirtyAsync(request.FilePath);
			}
			return new SaveOpenDocumentIfDirtyResponse
			{
				WasSaved = saved,
			};
		}

		public override Task<GetDiagnosticsResponse> getDiagnostics(
			GetDiagnosticsRequest request, ServerCallContext context)
		{
			// M1:返回空诊断(核心对空集有容错)。M2 接入 Roslyn(仅 C#/VB)。
			return Task.FromResult(new GetDiagnosticsResponse());
		}

		public override async Task<OpenProblemsPanelResponse> openProblemsPanel(
			OpenProblemsPanelRequest request, ServerCallContext context)
		{
			await _host.OpenProblemsPanelAsync();
			return new OpenProblemsPanelResponse();
		}

		public override async Task<OpenInFileExplorerPanelResponse> openInFileExplorerPanel(
			OpenInFileExplorerPanelRequest request, ServerCallContext context)
		{
			await _host.OpenInFileExplorerAsync(request.Path);
			return new OpenInFileExplorerPanelResponse();
		}

		public override async Task<OpenClineSidebarPanelResponse> openClineSidebarPanel(
			OpenClineSidebarPanelRequest request, ServerCallContext context)
		{
			await _host.OpenClinePanelAsync();
			return new OpenClineSidebarPanelResponse();
		}

		public override Task<OpenTerminalResponse> openTerminalPanel(
			OpenTerminalRequest request, ServerCallContext context)
		{
			// 降级:VS2022 无公开终端自动化 API。提示到输出窗格查看命令输出。
			_host.Log("[bridge] openTerminalPanel:VS2022 无集成终端 API,命令输出见 ClineVS 输出窗格");
			return Task.FromResult(new OpenTerminalResponse());
		}

		public override Task<ExecuteCommandInTerminalResponse> executeCommandInTerminal(
			ExecuteCommandInTerminalRequest request, ServerCallContext context)
		{
			// 降级:核心的命令执行(run_commands 工具)在 daemon 进程内自行 spawn,
			// 不经宿主终端;此 RPC 仅是"可见性"便利,记入输出窗格即可。
			_host.Log("[terminal] $ " + request.Command);
			return Task.FromResult(new ExecuteCommandInTerminalResponse { Success = true });
		}

		public override async Task<OpenFolderResponse> openFolder(OpenFolderRequest request, ServerCallContext context)
		{
			var success = await _host.OpenFolderAsync(request.Path, request.NewWindow);
			return new OpenFolderResponse { Success = success };
		}

		public override Task<SearchWorkspaceItemsResponse> searchWorkspaceItems(
			SearchWorkspaceItemsRequest request, ServerCallContext context)
		{
			// 文件系统扫描实现(核心注释:无快速索引的宿主可返回未实现,调用方回退 ripgrep;
			// 这里给一个够用的实现,避免回退路径的额外依赖)。
			var limit = request.HasLimit && request.Limit > 0 ? request.Limit : 50;
			var query = (request.Query ?? string.Empty).Trim();
			var response = new SearchWorkspaceItemsResponse();
			if (query.Length == 0) return Task.FromResult(response);

			string root = request.HasWorkspacePath && !string.IsNullOrEmpty(request.WorkspacePath)
				? request.WorkspacePath
				: null;
			if (root == null)
			{
				var info = _host.GetWorkspaceInfoAsync().GetAwaiter().GetResult();
				root = info.Paths?.FirstOrDefault();
			}
			if (root == null || !Directory.Exists(root)) return Task.FromResult(response);

			var results = new List<SearchWorkspaceItemsResponse.Types.SearchItem>();
			try
			{
				var queryLower = query.ToLowerInvariant();
				var wantFiles = !request.HasSelectedType || request.SelectedType == SearchWorkspaceItemsRequest.Types.SearchItemType.File;
				var wantFolders = !request.HasSelectedType || request.SelectedType == SearchWorkspaceItemsRequest.Types.SearchItemType.Folder;

				if (wantFolders)
				{
					foreach (var dir in SafeEnumerate(root, true, limit * 4))
					{
						var name = Path.GetFileName(dir);
						if (name.Length > 0 && name.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase) >= 0)
						{
							results.Add(MakeItem(root, dir, SearchWorkspaceItemsRequest.Types.SearchItemType.Folder));
							if (results.Count >= limit) return Task.FromResult(response = ToResponse(results));
						}
					}
				}
				if (wantFiles)
				{
					foreach (var file in SafeEnumerate(root, false, limit * 4))
					{
						var name = Path.GetFileName(file);
						if (name.Length > 0 && name.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase) >= 0)
						{
							results.Add(MakeItem(root, file, SearchWorkspaceItemsRequest.Types.SearchItemType.File));
							if (results.Count >= limit) return Task.FromResult(response = ToResponse(results));
						}
					}
				}
			}
			catch (Exception ex)
			{
				_host.Log("[bridge] searchWorkspaceItems 失败:" + ex.Message);
			}
			return Task.FromResult(ToResponse(results));
		}

		private static SearchWorkspaceItemsResponse ToResponse(List<SearchWorkspaceItemsResponse.Types.SearchItem> items)
		{
			var response = new SearchWorkspaceItemsResponse();
			response.Items.AddRange(items);
			return response;
		}

		private static SearchWorkspaceItemsResponse.Types.SearchItem MakeItem(
			string root, string fullPath, SearchWorkspaceItemsRequest.Types.SearchItemType type)
		{
			var relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return new SearchWorkspaceItemsResponse.Types.SearchItem
			{
				Path = relative,
				Type = type,
				Label = Path.GetFileName(fullPath),
			};
		}

		private static readonly string[] ExcludedDirs =
		{
			".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", "dist",
			"build", "packages", ".cline", ".idea", "__pycache__", ".gradle",
		};

		private static IEnumerable<string> SafeEnumerate(string root, bool directories, int take)
		{
			var queue = new Queue<string>();
			queue.Enqueue(root);
			var yielded = 0;
			while (queue.Count > 0 && yielded < take)
			{
				var current = queue.Dequeue();
				IEnumerable<string> entries = null;
				try
				{
					entries = directories
						? Directory.EnumerateDirectories(current)
						: Directory.EnumerateFiles(current);
				}
				catch { /* 无权限/已删除,跳过 */ }
				if (entries == null) continue;

				foreach (var entry in entries)
				{
					if (directories)
					{
						var name = Path.GetFileName(entry);
						if (name.StartsWith(".", StringComparison.Ordinal) || ExcludedDirs.Contains(name.ToLowerInvariant()))
						{
							continue;
						}
					}
					yielded++;
					yield return entry;
					if (directories && yielded < take * 4)
					{
						queue.Enqueue(entry);
					}
					if (yielded >= take * 4) break;
				}
			}
		}
	}
}
