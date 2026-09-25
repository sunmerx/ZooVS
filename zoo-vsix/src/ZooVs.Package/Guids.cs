using System;

namespace ZooVs.Package
{
	/// <summary>本扩展所有 GUID 的唯一出处。</summary>
	internal static class Guids
	{
		public const string PackageString = "c3d9e2a1-7f4b-4c68-9a5d-8e1b2c3d4f6a";
		public const string ToolWindowString = "d4e8f3b2-8a5c-4d79-be6f-9f2c3d4e5a7b";
		public const string CommandSetString = "e5f9a4c3-9b6d-4e8a-cf7a-0a3d4e5f6b8c";

		public static readonly Guid Package = new Guid(PackageString);
		public static readonly Guid ToolWindow = new Guid(ToolWindowString);
		public static readonly Guid CommandSet = new Guid(CommandSetString);
	}

	internal static class CommandIds
	{
		public const int OpenZooToolWindow = 0x0100;
		public const int OpenZooToolWindowView = 0x0101;
	}
}
