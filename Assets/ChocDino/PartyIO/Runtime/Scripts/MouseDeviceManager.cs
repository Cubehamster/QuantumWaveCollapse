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
	public delegate void MouseEvent(MouseDevice mouse);

#if UNITY_EDITOR_WIN || (!UNITY_EDITOR && UNITY_STANDALONE_WIN)
	public class MouseDeviceManager : System.IDisposable
	{
		private const int MaxStateCount = 8;
		private MouseState[] _states = new MouseState[MaxStateCount];
		private List<MouseDevice> _mice = new List<MouseDevice>(MaxStateCount);

		public List<MouseDevice> All => _mice;
		public int LastFrameUpdated { get; private set; }
		public static event MouseEvent ChangedConnectionState;
		
		private static MouseDeviceManager _instance;
		public static MouseDeviceManager Instance
		{
			get
			{
				if (_instance == null)
				{
					_instance = new MouseDeviceManager();
				}
				return _instance;
			}
		}

		private MouseDeviceManager()
		{
			var pluginVersion = NativeMousePlugin.GetVersionString();
			Debug.Log("Initializing MouseParty v" + NativeMousePlugin.ScriptVersion + " (plugin v" + pluginVersion + ")");
			if (pluginVersion.Contains("-trial"))
			{
#if UNITY_EDITOR
				UnityEditor.EditorUtility.DisplayDialog("Mouse Party - Trial Verion", "Thanks for trying Mouse Party!\n\nThis is just a reminder that this is a trial version for evaluation purposes.\n\nThere is a 5 minute time limit for each Unity session.\n\nSimply restart Unity if you wish to try Mouse Party for another 5 minutes.", "Continue");
#endif
				Debug.LogWarning("[MouseParty] This is the trial version for evaluation purposes.  There is a 5 minute time limit for each Unity session.");
			}
			if (!NativeMousePlugin.Init())
			{
				Debug.LogError("Failed to initialise Mouse Party");
			}
			_instance = this;
		}

		public void Dispose()
		{
			NativeMousePlugin.Deinit();
			_instance = null;
		}

		//private bool _hasFocus = true;

		public void Update()
		{
			/*if (!Application.isFocused)
			{
				_hasFocus = false;
				return;
			}
			if (!_hasFocus)
			{
				// flush state
				_hasFocus = true;
			}*/

			// Reset per-frame state
			foreach (var mouse in _mice)
			{
				mouse.ResetFrameState();
			}

			// Get any changed state
			int stateCount = NativeMousePlugin.PollState(_states, MaxStateCount, Application.isFocused);
			if (stateCount != 0)
			{
				LastFrameUpdated = Time.frameCount;
			}

			// Process all incoming states
			for (int i = 0; i < stateCount; i++)
			{
				MouseState state = _states[i];
				var mouse = FindMouseById(state.deviceId);
				ProcessState(ref mouse, state);
			}
		}

		private void ProcessState(ref MouseDevice mouse, MouseState state)
		{
			MouseState oldState = default;
			bool hasOldState = false;
			if (mouse == null)
			{
				MouseSpecs specs = new MouseSpecs();
				specs.deviceId = state.deviceId;
				if (!NativeMousePlugin.GetDeviceSpecs(state.deviceId, out specs))
				{
					Debug.LogError("[MouseParty] Failed to get device specs");
				}
				mouse = new MouseDevice(specs, state);
				_mice.Add(mouse);
			}
			else
			{
				oldState = mouse.State;
				hasOldState = true;
				mouse.Update(state);
			}

			// If the connection state has changed, fire event.
			if (!hasOldState || oldState.connectionState != state.connectionState)
			{
				ChangedConnectionState?.Invoke(mouse);
			}
		}

		private MouseDevice FindMouseById(int deviceId)
		{
			MouseDevice result = null;
			for (int i = 0; i < _mice.Count; i++)
			{
				if (_mice[i].DeviceId == deviceId)
				{
					result = _mice[i];
					break;
				}
			}
			return result;
		}
	}
#else
	public class MouseDeviceManager : System.IDisposable
	{
		private const int MaxStateCount = 8;
		private List<MouseDevice> _mice = new List<MouseDevice>(MaxStateCount);

		public List<MouseDevice> All => _mice;
		public int LastFrameUpdated { get; private set; }
		public static event MouseEvent ChangedConnectionState;

		private static MouseDeviceManager _instance;
		public static MouseDeviceManager Instance
		{
			get
			{
				if (_instance == null)
				{
					_instance = new MouseDeviceManager();
				}
				return _instance;
			}
		}

		public MouseDeviceManager()
		{
		}

		public void Dispose()
		{
			_instance = null;
		}

		public void Update()
		{
		}
	}
	#endif
}