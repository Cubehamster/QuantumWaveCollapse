//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using System.Runtime.InteropServices;

namespace ChocDino.PartyIO.Internal
{
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	internal struct MouseState
	{
		public byte deviceId;
		public MouseConnectionState connectionState;
		public int deltaX;
		public int deltaY;
		public float scrollDeltaX;
		public float scrollDeltaY;
		public byte buttonsUp;
		public byte buttonsDown;
		public byte buttons;
		public byte other;
	};

	internal enum MouseStateOtherFlags
	{
		AbsolutePosition = 1 << 0,
		VirtualDesktop = 2 << 0,
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
	internal struct MouseSpecs
	{
		public byte deviceId;
		
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string friendlyName;
		
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string manufacturerName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string instanceId;

		[MarshalAs(UnmanagedType.U1)]
		public bool isInstanceIdUnique;
	}

	#if UNITY_EDITOR_WIN || (!UNITY_EDITOR && UNITY_STANDALONE_WIN)

	internal static class NativeMousePlugin
	{
		internal const string ScriptVersion = "1.2.0";
	
		private const string PluginName = "MouseParty";

		[DllImport(PluginName)]
		[return: MarshalAs(UnmanagedType.LPStr)]
		internal static extern string GetVersionString();

		[DllImport(PluginName)]
		internal static extern bool Init();

		[DllImport(PluginName)]
		internal static extern void Deinit();

		[DllImport(PluginName)]
		internal static extern int PollState(MouseState[] states, int maxStates, bool hasFocus);

		//[DllImport(PluginName)]
		//internal static extern int PeekState(MouseState[] states, int maxStates);

		[DllImport(PluginName)]
		internal static extern bool GetDeviceSpecs(int deviceId, out MouseSpecs specs);
	}

	#endif
}