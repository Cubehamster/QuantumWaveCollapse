//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using UnityEngine;

namespace ChocDino.PartyIO.Demos
{
	/// <summary>
	/// Demonstration of the MouseDeviceManager API which just logs some state changes of the mice.
	/// </remarks>
	public class LowLevelDemo : MonoBehaviour
	{
		void Start()
		{
			MouseDeviceManager.ChangedConnectionState += OnChangedMouseConnectionState;
		}

		void OnChangedMouseConnectionState(MouseDevice mouse)
		{
			if (mouse.ConnectionState == MouseConnectionState.Connected)
			{
				Debug.Log("Connected mouse #" + mouse.DeviceId);
			}
			else if (mouse.ConnectionState == MouseConnectionState.Disconnected)
			{
				Debug.Log("Disconnected mouse #" + mouse.DeviceId);
			}
		}

		void Update()
		{
			MouseDeviceManager.Instance.Update();
			foreach (MouseDevice mouse in MouseDeviceManager.Instance.All)
			{
				if (mouse.WasPressedThisFrame(MouseButton.Left))
				{
					Debug.Log("Left down mouse # " + mouse.DeviceId);
				}
				if (mouse.WasReleasedThisFrame(MouseButton.Left))
				{
					Debug.Log("Left up mouse # " + mouse.DeviceId);
				}
				if (mouse.PositionDelta != Vector3.zero)
				{
					Debug.Log("Movement " + mouse.PositionDelta + " mouse # " + mouse.DeviceId);
				}
				if (mouse.ScrollDelta != Vector2.zero)
				{
					Debug.Log("Scroll wheel " + mouse.ScrollDelta + " mouse # " + mouse.DeviceId);
				}
			}
		}

		void OnDestroy()
		{
			MouseDeviceManager.Instance.Dispose();
		}
	}
}