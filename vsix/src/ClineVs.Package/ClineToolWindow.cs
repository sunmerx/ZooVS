using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClineVs.Package
{
	/// <summary>
	/// 承载 WPF 版 WebView2 的 ClineVS 聊天工具窗口。
	/// VS2022 工具窗格对 WPF 元素(Content 路径)支持最完善,
	/// WinForms 控件经 Window/IWin32Window 挂接在 AsyncPackage 下不可靠。
	/// </summary>
	[Guid(Guids.ToolWindowString)]
	public sealed class ClineToolWindow : ToolWindowPane
	{
		private readonly Grid _root = new Grid();
		private TextBlock _placeholder;

		public ClineToolWindow() : base(null)
		{
			Caption = "ClineVS Chat";
			_placeholder = new TextBlock
			{
				Text = "ClineVS 正在启动…",
				Foreground = Brushes.Gainsboro,
				FontSize = 14,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
			};
			_root.Children.Add(_placeholder);
			Content = _root;
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
