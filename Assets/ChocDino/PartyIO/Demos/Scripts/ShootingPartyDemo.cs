//--------------------------------------------------------------------------//
// Copyright 2025 Chocolate Dinosaur Ltd. All rights reserved.              //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using ChocDino.PartyIO;

namespace ChocDino.PartyIO.Demos
{
	/// <remarks>
	/// Note everything is calcualted in screen-coordinates.
	/// The bottom-left of the screen or window is at (0, 0). The top-right of the screen or window is at (Screen.width, Screen.height).
	/// Positions are converted to GUI coordinate during rendering.
	/// </remarks>
	public class ShootingPartyDemo : MonoBehaviour
	{
		[SerializeField] MouseCursorManager _cursorManager = null;
		[SerializeField] Texture2D _bulletTexture = null;

		private class Bullet
		{
			public Color color;
			public Vector3 screenPosition;
			public Vector3 targetScreenPosition;
			public bool dead;
		}

		private Color[] _cursorColors = { Color.red, Color.green, Color.blue, Color.magenta, Color.cyan, Color.yellow };
		private int _cursorSpawnCount;
		private List<Bullet> _bullets = new List<Bullet>(64);
		private List<Bullet> _badBullets = new List<Bullet>(64);
		private float _badBulletSpawnTimer;

		void Awake()
		{
			Debug.Assert(_cursorManager != null);
			Debug.Assert(_bulletTexture != null);
			this.useGUILayout = false;
		}
		
		void OnEnable()
		{
			MouseCursorManager.OnCursorAdded += OnCursorAdded;
		}

		void OnDisable()
		{
			MouseCursorManager.OnCursorAdded -= OnCursorAdded;
		}

		void OnCursorAdded(BaseMouseCursor cursor)
		{
			cursor.Color = _cursorColors[_cursorSpawnCount % _cursorColors.Length];
			_cursorSpawnCount++;
		}

		void Update()
		{
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				Application.Quit();
				return;
			}
			
			UpdateBullets();

			// Update the cursors to spawn bullet on mouse-down
			foreach (var cursor in _cursorManager.Cursors)
			{
				if (cursor.Enabled)
				{
					// Spawn bullet on mouse-down
					if (_bulletTexture)
					{
						if (cursor.Mouse.IsPressed(MouseButton.Left))
						{
							SpawnPlayerBullet(cursor.ScreenPosition, cursor.Color);
						}
					}
				}
			}
		}

		void SpawnPlayerBullet(Vector3 screenPosition, Color color)
		{
			var bullet = new Bullet();
			bullet.targetScreenPosition = screenPosition;
			bullet.screenPosition = new Vector3(Camera.main.pixelWidth * 0.5f, 0f, 0f);
			bullet.color = color;
			_bullets.Add(bullet);
		}

		void SpawnBadBullet(Vector3 screenPosition, Color color)
		{
			var bullet = new Bullet();
			bullet.targetScreenPosition = new Vector3(Camera.main.pixelWidth * 0.5f, 0f, 0f);
			bullet.screenPosition = screenPosition;
			bullet.color = color;
			_badBullets.Add(bullet);
		}

		void UpdateBullets()
		{
			// Update bullets
			for (int i = 0; i < _bullets.Count; i++)
			{
				_bullets[i].screenPosition = Vector3.MoveTowards(_bullets[i].screenPosition, _bullets[i].targetScreenPosition, Time.deltaTime * 1200f);
			}
			for (int i = 0; i < _badBullets.Count; i++)
			{
				_badBullets[i].screenPosition = Vector3.MoveTowards(_badBullets[i].screenPosition, _badBullets[i].targetScreenPosition, Time.deltaTime * 300f);
			}

			// Detect collisions
			List<Bullet> _removeBullets = new List<Bullet>(16);
			for (int i = 0; i < _bullets.Count; i++)
			{
				var bullet = _bullets[i];
				for (int j = 0; j < _badBullets.Count; j++)
				{
					var badBullet = _badBullets[j];
					float d = (bullet.screenPosition - badBullet.screenPosition).magnitude;
					if (d < 40f)
					{
						badBullet.dead = true;
						bullet.dead = true;
					}
				}
			}
			_bullets.RemoveAll((x)=> x.dead);
			_badBullets.RemoveAll((x)=> x.dead);


			// Remove dead bullets
			for (int i = 0; i < _bullets.Count; i++)
			{
				float d = (_bullets[i].screenPosition - _bullets[i].targetScreenPosition).magnitude;

				if (d < 10f)
				{
					_bullets.RemoveAt(i);
					i = 0;
				}
			}
			for (int i = 0; i < _badBullets.Count; i++)
			{
				float d = (_badBullets[i].screenPosition - _badBullets[i].targetScreenPosition).magnitude;

				if (d < 10f)
				{
					_badBullets.RemoveAt(i);
					i = 0;
				}
			}

			// Spawn bad bullets
			_badBulletSpawnTimer += Time.deltaTime;
			const float SpawnDuration = 0.33f;
			if (_badBulletSpawnTimer > SpawnDuration)
			{
				SpawnBadBullet(new Vector3(Camera.main.pixelWidth * Random.value, Camera.main.pixelHeight, 0f), Color.white);
				_badBulletSpawnTimer -= SpawnDuration;
			}
		}

		void OnGUI()
		{
			// Draw bullet particles
			foreach (var bullet in _bullets)
			{
				Vector2 offset = new Vector2(_bulletTexture.width / 2f, -_bulletTexture.height / 2f);
				var rect = new Rect(bullet.screenPosition.x, bullet.screenPosition.y, _bulletTexture.width, _bulletTexture.height);
				rect.position -= offset;
				rect.y = Screen.height - rect.y;
				GUI.color = bullet.color;
				GUI.DrawTexture(rect, _bulletTexture, ScaleMode.StretchToFill, true);
			}
			foreach (var bullet in _badBullets)
			{
				Vector2 offset = new Vector2(_bulletTexture.width / 2f, -_bulletTexture.height / 2f);
				var rect = new Rect(bullet.screenPosition.x, bullet.screenPosition.y, _bulletTexture.width, _bulletTexture.height);
				rect.position -= offset;
				rect.y = Screen.height - rect.y;
				GUI.color = bullet.color;
				GUI.DrawTexture(rect, _bulletTexture, ScaleMode.StretchToFill, true);
			}		
		}
	}
}