//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using UnityEngine;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using ChocDino.PartyIO.Internal;

namespace ChocDino.PartyIO
{
	public enum MouseButton
	{
		Left = 0,
		Right = 1,
		Middle = 2,
		Back = 3,
		Forward = 4,
	}

	public enum MouseConnectionState : byte
	{
		Dormant,
		Connected,
		Disconnected,
	}

	public class MouseDevice
	{
		public int DeviceId => _state.deviceId;
		public string FriendlyName => _specs.friendlyName;
		public string ManufacturerName => _specs.manufacturerName;
		public string InstanceId => _specs.instanceId;
		public bool IsInstanceIdUnique => _specs.isInstanceIdUnique;
		
		public MouseConnectionState ConnectionState => _state.connectionState;
		public Vector3 PositionDelta => _positionDelta;
		public Vector2 ScrollDelta => _scrollDelta;

		public bool IsPressed(MouseButton button)
		{
			return IsButtonBitSet(_state.buttons, button);
		}
		
		public bool WasPressedThisFrame(MouseButton button)
		{
			return IsButtonBitSet(_state.buttonsDown, button);
		}

		public bool WasReleasedThisFrame(MouseButton button)
		{
			return IsButtonBitSet(_state.buttonsUp, button);
		}

		public bool IsPositionAbsolute()
		{
			return ((_state.other & (byte)MouseStateOtherFlags.AbsolutePosition) != 0);
		}

		public bool IsVirtualDesktopPosition()
		{
			return ((_state.other & (byte)MouseStateOtherFlags.VirtualDesktop) != 0);
		}

		private bool IsButtonBitSet(byte bitfield, MouseButton button)
		{
			if (button >= MouseButton.Left && button <= MouseButton.Forward)
			{
				byte mask = (byte)(1 << (byte)button);
				return (bitfield & mask) != 0;
			}
			return false;
		}

		internal MouseDevice(MouseSpecs specs, MouseState state)
		{
			_specs = specs;
			_state.deviceId = state.deviceId;
			Debug.Assert(_specs.deviceId == _state.deviceId);
			Update(state);
		}

		private MouseDevice() {}

		internal void Update(MouseState state)
		{
			Debug.Assert(state.deviceId == _state.deviceId);
			_state = state;

			// Copy some state for public properties
			_positionDelta.x = state.deltaX;
			_positionDelta.y = state.deltaY;
			_scrollDelta.x = state.scrollDeltaX;
			_scrollDelta.y = state.scrollDeltaY;
		}

		internal void ResetFrameState()
		{
			_state.buttonsUp = 0;
			_state.buttonsDown = 0;
		}

		internal MouseState State => _state;

		private MouseSpecs _specs;
		private MouseState _state;
		private Vector3 _position;
		private Vector3 _positionDelta;
		private Vector2 _scrollDelta;
	}
}