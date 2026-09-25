using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClineVs.Bridge
{
	/// <summary>
	/// 宿主(VS Package)注入的能力集。Bridge 库不引用 Visual Studio SDK 与 WinForms,
	/// 一切 IDE 相关操作经由本接口回调(Package 侧在 UI 线程上实现)。
	/// </summary>
	public interface IBridgeHost
	{
		/// <summary>在 VS UI 线程上执行并返回结果。</summary>
		Task<T> InvokeOnUIThreadAsync<T>(Func<T> func);

		/// <summary>在 VS UI 线程上执行。</summary>
		Task InvokeOnUIThreadAsync(Action action);

		/// <summary>写入 VS 输出窗口的 ClineVS 窗格。</summary>
		void Log(string message);

		// ---- env ----

		Task SetClipboardTextAsync(string text);

		Task<string> GetClipboardTextAsync();

		/// <summary>返回 (宿主平台名, 宿主版本号),如 ("Visual Studio", "17.14")。</summary>
		Task<(string Platform, string Version)> GetHostVersionAsync();

		// ---- window ----

		Task ShowTextDocumentAsync(string path);

		/// <summary>文件选择对话框。返回选中的路径数组(取消时为空)。</summary>
		Task<string[]> ShowOpenDialogAsync(bool canSelectMany, string openLabel, string[] fileFilters);

		/// <summary>消息通知。返回用户选中项(未选则为 null)。</summary>
		Task<string> ShowMessageBoxAsync(string message, int type, bool modal, string detail, string[] items);

		/// <summary>文本输入框。返回 null 表示取消。</summary>
		Task<string> ShowInputBoxAsync(string title, string prompt, string value);

		/// <summary>保存文件对话框。返回 null 表示取消。</summary>
		Task<string> ShowSaveDialogAsync(string defaultPath, Dictionary<string, string[]> filters);

		Task OpenFileAsync(string path);

		Task OpenSettingsAsync(string query);

		Task<string> GetActiveEditorPathAsync();

		Task<string[]> GetOpenDocumentPathsAsync();

		// ---- workspace ----

		/// <summary>返回 (工作区 id, 根路径列表)。</summary>
		Task<(string Id, string[] Paths)> GetWorkspaceInfoAsync();

		Task<bool> SaveDocumentIfDirtyAsync(string filePath);

		Task OpenProblemsPanelAsync();

		Task OpenInFileExplorerAsync(string path);

		Task OpenClinePanelAsync();

		Task<bool> OpenFolderAsync(string path, bool newWindow);

		// ---- diff(临时文件对 + VS 内建比较视图) ----

		Task OpenComparisonWindowAsync(string diffId, string leftPath, string rightPath, string caption);

		Task CloseComparisonWindowAsync(string diffId);

		Task CloseAllComparisonWindowsAsync();

		/// <summary>核心请求宿主关闭(env.shutdown)时触发。</summary>
		event Action ShutdownRequested;

		/// <summary>由 Bridge 服务实现调用,转发 env.shutdown 请求。</summary>
		void NotifyShutdownRequested();
	}
}
