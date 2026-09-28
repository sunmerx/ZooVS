using System;
using System.Runtime.InteropServices;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ZooVs.Package
{
	/// <summary>
	/// 承载 WPF 版 WebView2 的 ZooVS 工具窗口。
	/// WPF 元素走 ToolWindowPane.Content 路径(WinForms 控件经 Window/IWin32Window
	/// 在 AsyncPackage 下不可靠——ClineVS 移植 M1 的教训)。
	/// </summary>
	[Guid(Guids.ToolWindowString)]
	public sealed class ZooToolWindow : ToolWindowPane
	{
		private readonly Grid _root = new Grid();
		private TextBlock _placeholder;

		public ZooToolWindow() : base(null)
		{
			Caption = "ZooVS Chat";
			_placeholder = new TextBlock
			{
				Text = "ZooVS 正在启动…",
				Foreground = Brushes.Gainsboro,
				FontSize = 14,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
			};
			_root.Children.Add(_placeholder);
			Content = _root;
		}

		/// <summary>
		/// VS 启动恢复窗口布局时会直接创建本窗格(不经菜单命令)——
		/// 必须在这里自拉起宿主,否则占位页永远停在"正在启动…",用户须再点一次菜单。
		/// 派发到 ApplicationIdle:避免在本窗格创建过程中重入 FindToolWindow。
		/// </summary>
		protected override void Initialize()
		{
			base.Initialize();
			System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
				new Action(() =>
				{
					try
					{
						// Package 属性在 Initialize 早期可能尚未赋值,静态 Instance 兜底
						var pkg = Package as ZooVsPackage ?? ZooVsPackage.Instance;
						if (pkg != null)
						{
							_ = pkg.AutoStartAsync();
						}
					}
					catch { /* 自拉起失败不影响手动打开 */ }
				}),
				System.Windows.Threading.DispatcherPriority.ApplicationIdle);
		}

		public void ShowMessage(string text)
		{
			if (!ReferenceEquals(_root.Children.Count > 0 ? _root.Children[0] : null, _placeholder))
			{
				_root.Children.Clear();
				_root.Children.Add(_placeholder);
			}
			_placeholder.Text = text;
		}

		/// <summary>用 WPF 版 WebView2 替换占位内容。</summary>
		public void AttachWebView(Microsoft.Web.WebView2.Wpf.WebView2 webView)
		{
			_root.Children.Clear();
			webView.HorizontalAlignment = HorizontalAlignment.Stretch;
			webView.VerticalAlignment = VerticalAlignment.Stretch;
			_root.Children.Add(webView);
		}
	}
}
