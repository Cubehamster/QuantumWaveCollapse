//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using UnityEngine;
using UnityEngine.UI;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using ChocDino.PartyIO.Internal;

namespace ChocDino.PartyIO
{
	public class CanvasMouseCursor : BaseMouseCursor
	{
		internal RectTransform RectTransform { get; set; }
		internal RawImage RawImage { get; set; }

		public CanvasMouseCursor(MouseDevice mouse) : base(mouse)
		{
		}
	}

	[AddComponentMenu("Mouse Party/Mouse Cursor Manager - Canvas")]
	public class MouseCursorManager_Canvas : MouseCursorManager
	{
		[SerializeField] RectTransform _cursorsParent = null;

		protected override void Awake()
		{
			Debug.Assert(_cursorsParent != null);
			base.Awake();
		}

		protected override BaseMouseCursor CreateCursor(MouseDevice mouse)
		{
			var cursor = new CanvasMouseCursor(mouse);
			cursor.CursorImage = _defaultCursorImage;

			{
				var go = new GameObject("Cursor", typeof(RectTransform));
				go.transform.SetParent(_cursorsParent, true);
				cursor.RectTransform = go.GetComponent<RectTransform>();
				cursor.RawImage = go.AddComponent<RawImage>();

				UpdateCursor(cursor);
			}
			return cursor;
		}

		protected override void UpdateCursor(BaseMouseCursor cursorBase)
		{
			Debug.Assert(cursorBase != null);
			Debug.Assert(cursorBase is CanvasMouseCursor);
			var cursor = cursorBase as CanvasMouseCursor;

			if (cursor.Enabled)
			{
				cursor.RawImage.enabled = true;
				cursor.RawImage.color = cursor.Color;
				cursor.RawImage.texture = cursor.CursorImage.texture;
				cursor.RawImage.SetNativeSize();

				//Vector2 localPoint;
				//RectTransformUtility.ScreenPointToLocalPointInRectangle(_cursorsParent, cursor.screenPosition, cursor.cursorImage.canvas.worldCamera, out localPoint);

				var texture = cursor.CursorImage.texture;
				var hotspot = cursor.CursorImage.hotspot;

				Vector3 newPosition = cursor.ScreenPosition;
				Vector3 offset = new Vector3(texture.width * 0.5f, -texture.height * 0.5f, 0f);
				offset += new Vector3(texture.width * -hotspot.x, texture.height * hotspot.y, 0f);
				newPosition += offset;

				if (cursor.RectTransform.position != newPosition)
				{
					cursor.RectTransform.position = newPosition;
				}
			}
			else
			{
				cursor.RawImage.enabled = false;
			}
		}

		protected override void DestroyCursor(BaseMouseCursor cursorBase)
		{
			Debug.Assert(cursorBase != null);
			Debug.Assert(cursorBase is CanvasMouseCursor);
			var cursor = cursorBase as CanvasMouseCursor;

			// Check if the RectTransform still exists as it may have been destroyed elsewhere.
			if (cursor.RectTransform)
			{
				var go = cursor.RectTransform.gameObject;
				cursor.RectTransform = null;
				cursor.RawImage = null;
				GameObject.Destroy(go);
			}
		}
	}
}