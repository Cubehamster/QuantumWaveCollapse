//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using UnityEngine;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using ChocDino.PartyIO.Internal;

namespace ChocDino.PartyIO
{
	public enum SpawnCursorMode
	{
		OnAnyMovement,
		OnLeftClick,

		Script = 100,
	}

	public class BaseMouseCursor
	{
		public MouseDevice Mouse { get; internal set; }
		public Vector3 ScreenPosition { get; internal set; }
		public bool Enabled { get; set; }
		public MouseCursorImage CursorImage { get; set; }
		public Color Color { get; set; }

		private BaseMouseCursor() {}

		public BaseMouseCursor(MouseDevice mouse)
		{
			Mouse = mouse;
			Enabled = true;
			Color = Color.white;
		}
	}

	public delegate void MouseCursorEvent(BaseMouseCursor mouseCursor);

	[AddComponentMenu("Mouse Party/Mouse Cursor Manager - Base")]
	public class MouseCursorManager : MonoBehaviour
	{
		[SerializeField] protected MouseCursorImage _defaultCursorImage = null;
		[SerializeField] protected SpawnCursorMode _spawnCursorMode = SpawnCursorMode.OnAnyMovement;

		public List<BaseMouseCursor> States {get; protected set; }
		public IEnumerable<BaseMouseCursor> Cursors { get { return _cursors.Select(o => o); } }
		public int LastFrameUpdated {get => _mouseDeviceManager.LastFrameUpdated; }

		public static event MouseCursorEvent OnCursorAdded;
		public static event MouseCursorEvent OnCursorRemoving;

		protected MouseDeviceManager _mouseDeviceManager;
		protected bool _isPaused;
		protected List<BaseMouseCursor> _cursors = new List<BaseMouseCursor>(8);

		protected virtual void Awake()
		{
			Debug.Assert(_defaultCursorImage != null);
		}

		protected virtual void OnEnable()
		{
			Debug.Assert(_cursors.Count == 0);

			// Hide the system cursor
			Cursor.lockState = CursorLockMode.Locked;

			// Create the MouseDeviceManager
			MouseDeviceManager.ChangedConnectionState += OnChangedMouseConnectionState;
			_mouseDeviceManager = MouseDeviceManager.Instance;
		}

		protected virtual void OnDisable()
		{
			// Unhide the system cursor
			Cursor.lockState = CursorLockMode.None;

			// Destroy all cursors
			for (int i = 0; i < _cursors.Count; i++)
			{
				RemoveCursor(_cursors[i], removeFromList:false);
			}
			_cursors.Clear();

			// Destroy the MouseDeviceManager
			MouseDeviceManager.ChangedConnectionState -= OnChangedMouseConnectionState;
			if (_mouseDeviceManager != null)
			{
				_mouseDeviceManager.Dispose();
				_mouseDeviceManager = null;
			}
		}

		protected virtual void OnApplicationFocus(bool hasFocus)
		{
			_isPaused = !hasFocus;
		}

		protected virtual void OnApplicationPause(bool pauseStatus)
		{
			_isPaused = pauseStatus;
		}

		protected virtual void Update()
		{
			if (!_isPaused)
			{
				// Update the MouseDeviceManager
				_mouseDeviceManager.Update();

				if (_spawnCursorMode == SpawnCursorMode.OnLeftClick)
				{
					var mice = _mouseDeviceManager.All;
					foreach (var mouse in mice)
					{
						if (mouse.WasPressedThisFrame(MouseButton.Left))
						{
							AddCursor(mouse);
						}
					}
				}

				UpdateCursors();
			}
		}

		void OnChangedMouseConnectionState(MouseDevice mouse)
		{
			if (mouse.ConnectionState == MouseConnectionState.Connected)
			{
				if (_spawnCursorMode == SpawnCursorMode.OnAnyMovement)
				{
					AddCursor(mouse);
				}
			}
			else if (mouse.ConnectionState == MouseConnectionState.Disconnected)
			{
				RemoveCursor(mouse, removeFromList:true);
			}
		}

		public BaseMouseCursor AddCursor(MouseDevice mouse)
		{
			// If there is already a cursor assocated with this mouse then don't spawn a new one
			BaseMouseCursor state = GetState(mouse);
			if (state == null)
			{
				state = CreateCursor(mouse);

				if (state != null)
				{
					// Set initial screen position
					if (mouse.IsPositionAbsolute())
					{
						state.ScreenPosition = mouse.PositionDelta;
					}
					else
					{
						state.ScreenPosition = GetScreenSize() * 0.5f;
					}

					_cursors.Add(state);

					UpdateCursors();

					OnCursorAdded?.Invoke(state);
				}
				else
				{
					// SpawnMouseState() did not spawn a cursor
				}
			}
			return state;
		}

		public void RemoveCursor(MouseDevice mouse, bool removeFromList)
		{
			BaseMouseCursor cursor = GetState(mouse);
			if (cursor != null)
			{
				RemoveCursor(cursor, removeFromList);
			}
		}

		public void RemoveCursor(BaseMouseCursor cursor, bool removeFromList)
		{
			if (cursor != null)
			{
				int index = GetIndex(cursor);
				if (index >= 0)
				{
					OnCursorRemoving?.Invoke(_cursors[index]);
					DestroyCursor(_cursors[index]);
					if (removeFromList)
					{
						_cursors.RemoveAt(index);
					}
				}
			}
		}

		protected virtual BaseMouseCursor CreateCursor(MouseDevice mouse)
		{
			var result = new BaseMouseCursor(mouse);
			result.CursorImage = _defaultCursorImage;
			return result;
		}

		protected virtual void UpdateCursor(BaseMouseCursor cursorBase)
		{
		}

		protected virtual void DestroyCursor(BaseMouseCursor cursorBase)
		{
		}

		private void UpdateCursors(bool force = false)
		{
			Vector3 screenMinimum = Vector3.zero; // bottom-left of the screen
			Vector3 screenMaximum = GetScreenSize(); // top-right of the screen
			foreach (var state in _cursors)
			{
				if (state.Enabled)
				{
					// Update cursor position clamping to game view area
					{
						Vector3 newScreenPosition = state.ScreenPosition;
						if (state.Mouse.IsPositionAbsolute())
						{
							newScreenPosition = state.Mouse.PositionDelta;
						}
						else if (state.Mouse.PositionDelta != Vector3.zero)
						{
							// NOTE: mouse delta Y is negated to match Unity's screen-space convention
							newScreenPosition += new Vector3(state.Mouse.PositionDelta.x, -state.Mouse.PositionDelta.y, 0f);
						}
						if (newScreenPosition != state.ScreenPosition || force)
						{
							Vector3 newPosition = newScreenPosition;
							newPosition = Vector3.Max(newPosition, screenMinimum);
							newPosition = Vector3.Min(newPosition, screenMaximum);
							state.ScreenPosition = newPosition;
						}
					}
				}
				UpdateCursor(state);
			}
		}
		
		private static Vector3 GetScreenSize()
		{
			Vector3 result = Vector3.zero;
			if (Camera.main != null)
			{
				result = new Vector3(Camera.main.pixelWidth, Camera.main.pixelHeight, 0f);
			}
			else
			{
				result = new Vector3(Screen.width, Screen.height, 0f);
			}
			return result;
		}

		public BaseMouseCursor GetState(MouseDevice mouse)
		{
			BaseMouseCursor result = null;
			for (int i = 0; i < _cursors.Count; i++)
			{
				if (_cursors[i].Mouse == mouse)
				{
					result = _cursors[i];
					break;
				}
			}
			return result;
		}

		protected int GetIndex(MouseDevice mouse)
		{
			int result = -1;
			for (int i = 0; i < _cursors.Count; i++)
			{
				if (_cursors[i].Mouse == mouse)
				{
					result = i;
					break;
				}
			}
			return result;
		}

		protected int GetIndex(BaseMouseCursor cursor)
		{
			int result = -1;
			for (int i = 0; i < _cursors.Count; i++)
			{
				if (_cursors[i] == cursor)
				{
					result = i;
					break;
				}
			}
			return result;
		}
	}
}