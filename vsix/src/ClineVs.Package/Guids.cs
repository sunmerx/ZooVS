using System;

namespace ClineVs.Package
{
	/// <summary>本扩展所有 GUID 的唯一出处。</summary>
	internal static class Guids
	{
		public const string PackageString = "b7f7e14c-6a2e-4a1b-9d3f-2c8e5f0a91d7";
		public const string ToolWindowString = "e4a1c2d3-5b6f-4a7e-8c9d-0f1a2b3c4d5e";
		public const string CommandSetString = "f1e2d3c4-b5a6-4789-9a0b-1c2d3e4f5a6b";

		public static readonly Guid Package = new Guid(PackageString);
		public static readonly Guid ToolWindow = new Guid(ToolWindowString);
		public static readonly Guid CommandSet = new Guid(CommandSetString);
	}

	internal static class CommandIds
	{
		public const int OpenClineToolWindow = 0x0100;
		public const int OpenClineToolWindowView = 0x0101;
	}
}
