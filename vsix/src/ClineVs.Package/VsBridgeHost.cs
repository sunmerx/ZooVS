using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClineVs.Bridge;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClineVs.Package
{
	/// <summary>
	/// IBridgeHost 的 VSSDK 实现:一切 IDE 能力(编辑器/对话框/比较视图/输出窗格)
	/// 都经由 DTE2 与 IVs* 服务在 VS 主线程上完成。宿主桥的 gRPC 线程通过
	/// JoinableTaskFactory 切回主线程。
	/// </summary>
	public sealed class VsBridgeHost : IBridgeHost
	{
		// 供 host.DiffService 使用的比较窗口登记表(UI 线程专用)
		private readonly Dictionary<string, IVsWindowFrame> _diffFrames =
			new Dictionary<string, IVsWindowFrame>(StringComparer.Ordinal);

		private readonly ClineVsPackage _package;
		private IVsOutputWindowPane _outputPane;
		private readonly object _outputPaneLock = new object();

		public event Action ShutdownRequested;

		public void NotifyShutdownRequested()
		{
			ShutdownRequested?.Invoke();
		}

		public VsBridgeHost(ClineVsPackage package)
		{
			_package = package;
		}

		private async Task<EnvDTE80.DTE2> GetDteAsync()
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			return (EnvDTE80.DTE2)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
		}

		// ---------- 调度与日志 ----------

		public async Task<T> InvokeOnUIThreadAsync<T>(Func<T> func)
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			return func();
		}

		public async Task InvokeOnUIThreadAsync(Action action)
		{
			await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
			action();
		}

		public void Log(string message)
		{
			ThreadHelper.Generic.BeginInvoke(() =>
			{
				try
				{
					EnsureOutputPane();
					_outputPane?.OutputStringThreadSafe(message + Environment.NewLine);
				}
				catch { /* 日志尽力而为 */ }
			});
		}

		private void EnsureOutputPane()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (_outputPane != null) return;
			lock (_outputPaneLock)
			{
				if (_outputPane != null) return;
				var outputWindow = (IVsOutputWindow)ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow));
				if (outputWindow == null) return;

				var paneGuid = new Guid("a9c4c2f1-6b7d-4e2a-9b3e-1f0d8c7a6e5b");
				outputWindow.CreatePane(ref paneGuid, "ClineVS", 1, 0);
				outputWindow.GetPane(ref paneGuid, out _outputPane);
				_outputPane?.Activate();
			}
		}

		// ---------- env ----------

		public Task SetClipboardTextAsync(string text)
		{
			return InvokeOnUIThreadAsync(() =>
			{
				Clipboard.SetText(text ?? string.Empty);
			});
		}

		public Task<string> GetClipboardTextAsync()
		{
			return InvokeOnUIThreadAsync(() =>
				Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
		}

		public async Task<(string Platform, string Version)> GetHostVersionAsync()
		{
			var dte = await GetDteAsync();
			return ("Visual Studio", dte != null ? dte.Version : "17.0");
		}

		// ---------- window ----------

		public async Task ShowTextDocumentAsync(string path)
		{
			var dte = await GetDteAsync();
			await InvokeOnUIThreadAsync(() =>
			{
				dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindPrimary);
			});
		}

		public async Task<string[]> ShowOpenDialogAsync(bool canSelectMany, string openLabel, string[] fileFilters)
		{
			return await InvokeOnUIThreadAsync(() =>
			{
				using (var dialog = new OpenFileDialog
				{
					Title = string.IsNullOrEmpty(openLabel) ? "Open" : openLabel,
					Multiselect = canSelectMany,
					CheckFileExists = true,
				})
				{
					dialog.Filter = BuildFilter(fileFilters);
					if (dialog.ShowDialog() == DialogResult.OK)
					{
						return dialog.FileNames;
					}
					return new string[0];
				}
			});
		}

		public async Task<string> ShowMessageBoxAsync(string message, int type, bool modal, string detail, string[] items)
		{
			var fullMessage = string.IsNullOrEmpty(detail) ? message : message + Environment.NewLine + detail;
			Log("[message] " + fullMessage);

			// 有按钮项或显式 modal 时才弹窗;否则走状态栏(VS Code 的非模态通知语义)
			if (!modal && (items == null || items.Length == 0))
			{
				await SetStatusBarAsync(fullMessage);
				return null;
			}

			return await InvokeOnUIThreadAsync(() =>
			{
				var icon = type == 0 ? MessageBoxIcon.Error
					: type == 2 ? MessageBoxIcon.Warning
					: MessageBoxIcon.Information;
				MessageBox.Show(fullMessage, "ClineVS", MessageBoxButtons.OK, icon);
				return items != null && items.Length > 0 ? items[0] : null;
			});
		}

		public async Task<string> ShowInputBoxAsync(string title, string prompt, string value)
		{
			return await InvokeOnUIThreadAsync(() =>
			{
				using (var form = new Form
				{
					Text = string.IsNullOrEmpty(title) ? "ClineVS" : title,
					Width = 420,
					Height = 170,
					FormBorderStyle = FormBorderStyle.FixedDialog,
					StartPosition = FormStartPosition.CenterParent,
					MaximizeBox = false,
					MinimizeBox = false,
				})
				{
					var label = new Label
					{
						Text = prompt ?? string.Empty,
						Dock = DockStyle.Top,
						Padding = new Padding(10, 10, 10, 4),
						AutoSize = false,
						Height = 44,
					};
					var textBox = new TextBox { Dock = DockStyle.Top, Text = value ?? string.Empty };
					var buttons = new FlowLayoutPanel
					{
						Dock = DockStyle.Bottom,
						FlowDirection = FlowDirection.RightToLeft,
						Padding = new Padding(10),
						Height = 44,
					};
					var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
					var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
					buttons.Controls.Add(ok);
					buttons.Controls.Add(cancel);
					form.Controls.Add(buttons);
					form.Controls.Add(textBox);
					form.Controls.Add(label);
					form.AcceptButton = ok;
					form.CancelButton = cancel;

					return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
				}
			});
		}

		public async Task<string> ShowSaveDialogAsync(string defaultPath, Dictionary<string, string[]> filters)
		{
			return await InvokeOnUIThreadAsync(() =>
			{
				using (var dialog = new SaveFileDialog { Title = "Save As" })
				{
					if (!string.IsNullOrEmpty(defaultPath))
					{
						dialog.InitialDirectory = Path.GetDirectoryName(defaultPath) ?? defaultPath;
						dialog.FileName = Path.GetFileName(defaultPath);
					}
					dialog.Filter = BuildFilter(filters?.Values.SelectMany(v => v).ToArray());
					if (dialog.ShowDialog() == DialogResult.OK)
					{
						return dialog.FileName;
					}
					return null;
				}
			});
		}

		public async Task OpenFileAsync(string path)
		{
			var dte = await GetDteAsync();
			await InvokeOnUIThreadAsync(() =>
			{
				dte.ItemOperations.OpenFile(path);
			});
		}

		public async Task OpenSettingsAsync(string query)
		{
			var dte = await GetDteAsync();
			await InvokeOnUIThreadAsync(() =>
			{
				try
				{
					// M1:忽略 query,直接打开选项页;M2 可映射到 Tools.ImportExportSettings 或具体页
					dte.ExecuteCommand("Tools.Options");
				}
				catch (Exception ex)
				{
					Log("[bridge] openSettings 失败:" + ex.Message);
				}
			});
		}

		public async Task<string> GetActiveEditorPathAsync()
		{
			var dte = await GetDteAsync();
			return await InvokeOnUIThreadAsync(() =>
			{
				try
				{
					return dte.ActiveDocument != null ? dte.ActiveDocument.FullName : null;
				}
				catch
				{
					return null;
				}
			});
		}

		public async Task<string[]> GetOpenDocumentPathsAsync()
		{
			var dte = await GetDteAsync();
			return await InvokeOnUIThreadAsync(() =>
			{
				var paths = new List<string>();
				foreach (EnvDTE.Document document in dte.Documents)
				{
					try
					{
						if (!string.IsNullOrEmpty(document.FullName))
						{
							paths.Add(document.FullName);
						}
					}
					catch { /* 无文件路径的文档 */ }
				}
				return paths.ToArray();
			});
		}

		// ---------- workspace ----------

		public async Task<(string Id, string[] Paths)> GetWorkspaceInfoAsync()
		{
			var dte = await GetDteAsync();
			return await InvokeOnUIThreadAsync(() =>
			{
				try
				{
					var solution = dte.Solution;
					if (solution != null && !string.IsNullOrEmpty(solution.FullName))
					{
						var solutionDir = Path.GetDirectoryName(solution.FullName);
						return (Id: solution.FullName, Paths: new[] { solutionDir });
					}
				}
				catch { /* 无打开解决方案 */ }

				string fallback = null;
				try
				{
					fallback = dte.ActiveDocument != null
						? Path.GetDirectoryName(dte.ActiveDocument.FullName)
						: null;
				}
				catch { }

				if (string.IsNullOrEmpty(fallback))
				{
					fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				}
				return (Id: fallback, Paths: new[] { fallback });
			});
		}

		public async Task<bool> SaveDocumentIfDirtyAsync(string filePath)
		{
			var dte = await GetDteAsync();
			return await InvokeOnUIThreadAsync(() =>
			{
				try
				{
					foreach (EnvDTE.Document document in dte.Documents)
					{
						if (string.Equals(document.FullName, filePath, StringComparison.OrdinalIgnoreCase))
						{
							if (!document.Saved)
							{
								document.Save();
								return true;
							}
							return false;
						}
					}
				}
				catch (Exception ex)
				{
					Log("[bridge] saveOpenDocumentIfDirty 失败:" + ex.Message);
				}
				return false;
			});
		}

		public async Task OpenProblemsPanelAsync()
		{
			var dte = await GetDteAsync();
			await InvokeOnUIThreadAsync(() =>
			{
				try
				{
					dte.ExecuteCommand("View.ErrorList");
				}
				catch (Exception ex)
				{
					Log("[bridge] openProblemsPanel 失败:" + ex.Message);
				}
			});
		}

		public Task OpenInFileExplorerAsync(string path)
		{
			return InvokeOnUIThreadAsync(() =>
			{
				try
				{
					if (Directory.Exists(path))
					{
						Process.Start(new ProcessStartInfo
						{
							FileName = "explorer.exe",
							Arguments = "\"" + path + "\"",
							UseShellExecute = true,
						});
					}
					else if (File.Exists(path))
					{
						Process.Start(new ProcessStartInfo
						{
							FileName = "explorer.exe",
							Arguments = "/select,\"" + path + "\"",
							UseShellExecute = true,
						});
					}
				}
				catch (Exception ex)
				{
					Log("[bridge] openInFileExplorerPanel 失败:" + ex.Message);
				}
			});
		}

		public async Task OpenClinePanelAsync()
		{
			await _package.ShowToolWindowAsync();
		}

		public async Task<bool> OpenFolderAsync(string path, bool newWindow)
		{
			return await InvokeOnUIThreadAsync(() =>
			{
				try
				{
					var vsAppIdDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
					if (string.IsNullOrEmpty(vsAppIdDir))
					{
						Log("[bridge] openFolder:未找到 VS 安装目录环境变量");
						return false;
					}
					var devenv = Path.Combine(vsAppIdDir, "devenv.exe");
					if (!File.Exists(devenv)) return false;

					Process.Start(new ProcessStartInfo
					{
						FileName = devenv,
						Arguments = "\"" + path + "\"",
						UseShellExecute = true,
					});
					return true;
				}
				catch (Exception ex)
				{
					Log("[bridge] openFolder 失败:" + ex.Message);
					return false;
				}
			});
		}

		// ---------- diff(比较视图管理) ----------

		public async Task OpenComparisonWindowAsync(string diffId, string leftPath, string rightPath, string caption)
		{
			await InvokeOnUIThreadAsync(() =>
			{
				ThreadHelper.ThrowIfNotOnUIThread();
				var differenceService = (IVsDifferenceService)ServiceProvider.GlobalProvider.GetService(
					typeof(SVsDifferenceService));
				if (differenceService == null)
				{
					Log("[bridge] 无法获取 IVsDifferenceService");
					return;
				}

				var frame = differenceService.OpenComparisonWindow2(
					leftPath,
					rightPath,
					Path.GetFileName(leftPath) + " (Original)",
					Path.GetFileName(leftPath) + " (Cline Proposed)",
					caption,
					caption,
					"Cline Diff",
					"Diff",
					0);

				if (frame != null)
				{
					_diffFrames[diffId] = frame;
					Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
				}
				else
				{
					Log("[bridge] 比较视图打开失败:" + caption);
				}
			});
		}

		public Task CloseComparisonWindowAsync(string diffId)
		{
			return InvokeOnUIThreadAsync(() =>
			{
				ThreadHelper.ThrowIfNotOnUIThread();
				if (_diffFrames.TryGetValue(diffId, out var frame))
				{
					_diffFrames.Remove(diffId);
					try
					{
						frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
					}
					catch (Exception ex)
					{
						Log("[bridge] 关闭比较视图失败:" + ex.Message);
					}
				}
			});
		}

		public Task CloseAllComparisonWindowsAsync()
		{
			return InvokeOnUIThreadAsync(() =>
			{
				ThreadHelper.ThrowIfNotOnUIThread();
				foreach (var frame in _diffFrames.Values)
				{
					try
					{
						frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
					}
					catch { /* 用户可能已手动关闭 */ }
				}
				_diffFrames.Clear();
			});
		}

		// ---------- 内部 ----------

		private async Task SetStatusBarAsync(string message)
		{
			await InvokeOnUIThreadAsync(() =>
			{
				ThreadHelper.ThrowIfNotOnUIThread();
				var statusBar = (IVsStatusbar)ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar));
				statusBar?.SetText(message);
			});
		}

		private static string BuildFilter(string[] extensions)
		{
			if (extensions == null || extensions.Length == 0)
			{
				return "All Files (*.*)|*.*";
			}
			var patterns = string.Join(";",
				extensions.Select(ext => ext.StartsWith(".") ? "*" + ext : "*." + ext.TrimStart('*')));
			return "Matching Files (" + patterns + ")|" + patterns + "|All Files (*.*)|*.*";
		}
	}
}
