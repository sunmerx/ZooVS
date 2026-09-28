using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZooVs.Package
{
	/// <summary>
	/// Windows Job Object(KILL_ON_JOB_CLOSE):进程级单例 job,把子进程绑到 VS 生命周期。
	/// devenv 无论正常退出/崩溃/taskkill,OS 关闭 job 句柄即自动终止 job 内全部进程
	/// (子进程自动继承)。句柄刻意保持打开、永不显式 Close。
	/// </summary>
	internal static class VsJobObject
	{
		private static IntPtr _handle;
		private static readonly object Lock = new object();

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, int cbInfo);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr hObject);

		private const int JobObjectExtendedLimitInformationClass = 9;
		private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

		[StructLayout(LayoutKind.Sequential)]
		private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
		{
			public long PerProcessUserTimeLimit;
			public long PerJobUserTimeLimit;
			public uint LimitFlags;
			public UIntPtr MinimumWorkingSetSize;
			public UIntPtr MaximumWorkingSetSize;
			public uint ActiveProcessLimit;
			public UIntPtr Affinity;
			public uint PriorityClass;
			public uint SchedulingClass;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct IO_COUNTERS
		{
			public ulong ReadOperationCount;
			public ulong WriteOperationCount;
			public ulong OtherOperationCount;
			public ulong ReadTransferCount;
			public ulong WriteTransferCount;
			public ulong OtherTransferCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
		{
			public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
			public IO_COUNTERS IoInfo;
			public UIntPtr ProcessMemoryLimit;
			public UIntPtr JobMemoryLimit;
			public UIntPtr PeakProcessMemoryUsed;
			public UIntPtr PeakJobMemoryUsed;
		}

		/// <summary>把进程加入 kill-on-close job;失败仅记日志(各处有兜底)。</summary>
		public static void Bind(Process process, Action<string> log)
		{
			try
			{
				lock (Lock)
				{
					if (_handle == IntPtr.Zero)
					{
						_handle = CreateJobObject(IntPtr.Zero, null);
						if (_handle == IntPtr.Zero)
						{
							log("[job] CreateJobObject 失败(err=" + Marshal.GetLastWin32Error() + "),跳过自动清理绑定");
							return;
						}
						var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
						info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
						if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
						{
							log("[job] SetInformationJobObject 失败(err=" + Marshal.GetLastWin32Error() + "),跳过自动清理绑定");
							CloseHandle(_handle);
							_handle = IntPtr.Zero;
							return;
						}
						log("[job] Job Object 已创建:VS 关闭(含强杀)时将自动清理子进程");
					}
					if (!AssignProcessToJobObject(_handle, process.Handle))
					{
						log("[job] 绑定进程失败(err=" + Marshal.GetLastWin32Error() + ",pid=" + process.Id + ")");
					}
				}
			}
			catch (Exception ex)
			{
				log("[job] 绑定异常:" + ex.Message);
			}
		}
	}
}
