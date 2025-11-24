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
	/// <remarks>
	/// Note everything is calculated in screen-coordinates.
	/// The bottom-left of the screen or window is at (0, 0). The top-right of the screen or window is at (Screen.width, Screen.height).
	/// Positions are converted to GUI coordinate during rendering.
	/// </remarks>
	[AddComponentMenu("Mouse Party/Mouse Cursor Manager - IMGUI")]
	public class MouseCursorManager_IMGUI : MouseCursorManager
	{
		protected override void Awake()
		{
			this.useGUILayout = false;
			base.Awake();
		}

		void OnGUI()
		{	
			// Draw cursors
			foreach (var cursor in Cursors)
			{
				if (cursor.Enabled)
				{
					var texture = cursor.CursorImage.texture;
					var hotspot = cursor.CursorImage.hotspot;

					// Offset the rectangle so the cursor hotspot is drawn at the correct position
					Vector2 cursorOffset = new Vector2(texture.width * hotspot.x, -texture.height * hotspot.y);
					var rect = new Rect(cursor.ScreenPosition.x, cursor.ScreenPosition.y, texture.width, texture.height);
					rect.position -= cursorOffset;

					// Convert from screen-space to GUI space
					rect.y = Screen.height - rect.y;

					GUI.depth = -100;
					GUI.color = cursor.Color;
					GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true);
				}
			}
		}
	}
}