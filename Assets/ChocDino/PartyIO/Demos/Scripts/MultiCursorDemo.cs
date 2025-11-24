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
	/// <summary>
	/// Simple demo that shows how to use the MouseManager to update multiple mice, handle device connection/disconnection.
	/// IMGUI is used to display the cursors.
	/// </summary>
	/// <remarks>
	/// Note everything is calculated in screen-coordinates.
	/// The bottom-left of the screen or window is at (0, 0). The top-right of the screen or window is at (Screen.width, Screen.height).
	/// Positions are converted to GUI coordinate during rendering.
	/// </remarks>
	public class MultiCursorDemo : MonoBehaviour
	{
		[SerializeField] MouseCursorManager _cursorManager = null;
		[SerializeField] Texture2D _particleTexture = null;

		private class Particle
		{
			public Color color;
			public Vector3 screenPosition;
		}

		private Color[] _cursorColors = { Color.white, Color.red, Color.green, Color.blue };
		private int _cursorSpawnCount;
		private List<Particle> _particles = new List<Particle>(64);

		void Awake()
		{
			Debug.Assert(_cursorManager != null);
			Debug.Assert(_particleTexture != null);
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

			UpdateParticles();
 
			// Update the cursors to spawn particles on mouse-down
			foreach (var cursor in _cursorManager.Cursors)
			{
				if (cursor.Enabled)
				{
					// Spawn particles on mouse-down
					if (_particleTexture)
					{
						if (cursor.Mouse.IsPressed(MouseButton.Left))
						{
							SpawnParticle(cursor.ScreenPosition, cursor.Color);
						}
					}
				}
			}
		}

		void SpawnParticle(Vector3 screenPosition, Color color)
		{
			var particle = new Particle();
			particle.screenPosition = screenPosition;
			particle.color = color;
			_particles.Add(particle);
		}

		void UpdateParticles()
		{
			// Update particles
			for (int i = 0; i < _particles.Count; i++)
			{
				_particles[i].screenPosition += Vector3.down * Time.deltaTime * 1000f;
			}

			// Remove dead particles that have fallen off the bottom of the screen
			for (int i = 0; i < _particles.Count; i++)
			{
				if (_particles[i].screenPosition.y < (-_particleTexture.height / 2f))
				{
					_particles.RemoveAt(i);
					i = 0;
				}
			}
		}

		void OnGUI()
		{
			// Draw particles
			foreach (var particle in _particles)
			{
				// Offset the rectangle so the particle is drawn centered on its position
				Vector2 offset = new Vector2(_particleTexture.width / 2f, -_particleTexture.height / 2f);
				var rect = new Rect(particle.screenPosition.x, particle.screenPosition.y, _particleTexture.width, _particleTexture.height);
				rect.position -= offset;
				
				// Convert from screen-space to GUI space
				rect.y = Screen.height - rect.y;
				
				GUI.depth = 0;
				GUI.color = particle.color;
				GUI.DrawTexture(rect, _particleTexture, ScaleMode.StretchToFill, true);
			}
		}
	}
}