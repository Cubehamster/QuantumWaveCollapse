using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// MonoBehaviour bridge: listens for a key press via the *new* Input System
/// and posts a RedistributeWalkersRequest into the ECS world.
/// Attach this to any active GameObject in your main scene.
/// </summary>
public class RedistributeWalkersInput : MonoBehaviour
{
    [Tooltip("Key that triggers redistribution of all walkers.")]
    public Key key = Key.R;

    private EntityManager _em;
    private bool _initialized;

    void Awake()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world != null)
        {
            _em = world.EntityManager;
            _initialized = true;
        }
        else
        {
            Debug.LogError("[RedistributeWalkersInput] No DefaultWorld found.");
        }
    }

    void Update()
    {
        if (!_initialized) return;
        if (Keyboard.current == null) return;

        var k = Keyboard.current[key];
        if (k == null) return;

        if (k.wasPressedThisFrame)
        {
            // Fire a one-frame request; ECS system will handle & then destroy it
            var e = _em.CreateEntity(typeof(RedistributeWalkersRequest));
            Debug.Log("[RedistributeWalkersInput] Posted RedistributeWalkersRequest.");
        }
    }
}
