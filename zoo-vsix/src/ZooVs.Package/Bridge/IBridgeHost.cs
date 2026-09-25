using System;
using System.Threading.Tasks;

namespace ZooVs.Bridge
{
	/// <summary>
	/// 宿主能力接口(精简版)。M1 中 vscode-shim 已本地处理文件系统/终端/存储,
	/// 宿主只需提供线程调度与日志;对话框类能力(M2)再扩展。
	/// </summary>
	public interface IBridgeHost
	{
		Task<T> InvokeOnUIThreadAsync<T>(Func<T> func);
		Task InvokeOnUIThreadAsync(Action action);
		void Log(string message);
	}

	/// <summary>无 VS 依赖的默认实现(冒烟/非 UI 场景)。</summary>
	public sealed class PassthroughBridgeHost : IBridgeHost
	{
		public Task<T> InvokeOnUIThreadAsync<T>(Func<T> func) => Task.FromResult(func());
		public Task InvokeOnUIThreadAsync(Action action) { action(); return Task.CompletedTask; }
		public void Log(string message) => System.Diagnostics.Debug.WriteLine(message);
	}
}
