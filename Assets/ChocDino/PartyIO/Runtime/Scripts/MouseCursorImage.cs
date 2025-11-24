//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using UnityEngine;

namespace ChocDino.PartyIO
{
	[CreateAssetMenuAttribute(fileName="MouseCursor", menuName="Mouse Party/Mouse Cursor Image")]
	public class MouseCursorImage : ScriptableObject
	{
		public Texture2D texture = null;
		public Vector2 hotspot = Vector2.zero;

		#if UNITY_EDITOR
		void OnValidate()
		{
			hotspot = Vector2.Max(Vector2.zero, hotspot);
			hotspot = Vector2.Min(Vector2.one, hotspot);
		}
		#endif
	}
}