// MeasurementClick.cs
// Reads mouse from the *New* Input System and publishes a ClickRequest singleton
// for MeasurementSystem. Works in orthographic or perspective; assumes XY plane (z=0).

using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;   // <-- New Input System

public struct ClickRequest : IComponentData
{
    public float2 WorldPos;
    public float Radius;
    public float PushStrength;
    public bool IsPressed;
    public bool EdgeDown;
    public bool EdgeUp;
}

public enum ClickMode
{
    HoldToPush,
    PressOnceToPush
}

[DisallowMultipleComponent]
public sealed class MeasurementClick : MonoBehaviour
{
    [Header("Camera & plane")]
    public Camera cam;                // leave null to use Camera.main
    [Tooltip("XY plane Z value used for screen->world. 0 is typical for 2D.")]
    public float planeZ = 0f;

    [Header("Interaction")]
    [Min(0f)] public float radius = 0.25f;
    [Min(0f)] public float pushStrength = 25f;

    [Header("Debug")]
    public bool logEvents = false;

    // ECS
    EntityManager _em;
    Entity _singleton;
    bool _haveSingleton;

    // Cached button edge state (we derive from InputSystem each frame)
    bool _prevPressed;

    [Header("Mode")]
    public ClickMode mode = ClickMode.HoldToPush;

    void OnEnable()
    {
        if (cam == null) cam = Camera.main;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[MeasurementClick] No Default World");
            enabled = false; return;
        }

        _em = world.EntityManager;

        // Create or find the singleton entity holding ClickRequest
        var q = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequest>());
        if (q.CalculateEntityCount() == 0)
        {
            _singleton = _em.CreateEntity(typeof(ClickRequest));
            _em.SetName(_singleton, "ClickRequestSingleton");
            _haveSingleton = true;
        }
        else
        {
            _singleton = q.GetSingletonEntity();
            _haveSingleton = true;
        }
    }

    void OnDisable()
    {
        // Keep the singleton around; MeasurementSystem expects it.
        // If you prefer to remove it on disable, uncomment:
        // if (_haveSingleton && _em.Exists(_singleton)) _em.DestroyEntity(_singleton);
        _haveSingleton = false;
    }

    void Update()
    {
        if (!_haveSingleton || !_em.Exists(_singleton)) return;

        // Use New Input System mouse
        var mouse = Mouse.current;
        if (mouse == null) return; // no mouse device

        bool pressed, edgeDown, edgeUp;

        if (mode == ClickMode.HoldToPush)
        {
            pressed = mouse.leftButton.isPressed;
            edgeDown = mouse.leftButton.wasPressedThisFrame;
            edgeUp = mouse.leftButton.wasReleasedThisFrame;
        }
        else // PressOnceToPush: single pulse per click
        {
            pressed = mouse.leftButton.wasPressedThisFrame;
            edgeDown = pressed;
            edgeUp = false;
        }

        // Screen → world (XY plane at z = planeZ)
        Vector2 screen = mouse.position.ReadValue();
        float2 worldXY = ScreenToWorldXY(screen, planeZ);

        var req = new ClickRequest
        {
            WorldPos = worldXY,
            Radius = radius,
            PushStrength = pushStrength,
            IsPressed = pressed,
            EdgeDown = edgeDown,
            EdgeUp = edgeUp
        };

        _em.SetComponentData(_singleton, req);

        if (logEvents && (edgeDown || edgeUp || pressed != _prevPressed))
        {
            Debug.Log($"[Click] pressed={pressed} down={edgeDown} up={edgeUp}  world={worldXY}  R={radius} push={pushStrength}");
        }

        _prevPressed = pressed;
    }

    float2 ScreenToWorldXY(Vector2 screen, float zPlane)
    {
        if (cam == null) cam = Camera.main;
        if (cam == null)
        {
            // Fallback: identity mapping if no camera
            return screen;
        }

        if (cam.orthographic)
        {
            Vector3 wp = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, cam.nearClipPlane));
            // For ortho, Z is ignored; project to XY plane at zPlane
            return new float2(wp.x, wp.y);
        }
        else
        {
            // Perspective: ray-plane intersection with z = zPlane
            Ray r = cam.ScreenPointToRay(screen);
            // plane normal (0,0,1), point (0,0,zPlane): solve r.origin.z + t * r.dir.z = zPlane
            float denom = r.direction.z;
            float t = (Mathf.Abs(denom) < 1e-6f) ? 0f : (zPlane - r.origin.z) / denom;
            Vector3 p = r.origin + t * r.direction;
            return new float2(p.x, p.y);
        }
    }
}
