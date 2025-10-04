// MeasurementClick.cs — hold-to-scan version (Input System)
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;

public class MeasurementClick : MonoBehaviour
{
    public enum FireMode { OnPress, WhileHeld }

    [Header("Mode")]
    [Tooltip("OnPress = one measurement on click.\nWhileHeld = continuous measurements while the button is down.")]
    public FireMode fireMode = FireMode.WhileHeld;

    [Tooltip("How many measurements per second when holding the button (WhileHeld mode).")]
    [Range(1, 120)] public int rateHz = 30;

    [Tooltip("Optional logs to Console.")]
    public bool verboseLogging = false;

    [Header("Measurement")]
    [Tooltip("Primary measurement radius (world units).")]
    public float radius = 0.8f;

    [Tooltip("Push effect radius (world units). Particles within (radius, pushRadius] are pushed outward on failure.")]
    public float pushRadius = 2.0f;

    [Tooltip("Outward displacement applied to near particles on failure (world units at center, falls off to 0 at pushRadius).")]
    public float pushStrength = 0.75f;

    [Header("Mapping")]
    [Tooltip("Camera used to map screen -> viewport; defaults to Camera.main.")]
    public Camera viewCamera;

    // internal
    float _interval;
    float _accum;
    Vector2 _lastWorldPos;
    bool _haveLast;

    void Awake()
    {
        if (viewCamera == null) viewCamera = Camera.main;
        _interval = 1f / Mathf.Max(1, rateHz);
    }

    void OnValidate()
    {
        _interval = 1f / Mathf.Max(1, rateHz);
        pushRadius = Mathf.Max(pushRadius, radius);
    }

    void Update()
    {
        // Active input?
        var mouse = Mouse.current;
        var touch = Touchscreen.current;

        bool pressedThisFrame =
            (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
            (touch != null && touch.primaryTouch.press.wasPressedThisFrame);

        bool isHeld =
            (mouse != null && mouse.leftButton.isPressed) ||
            (touch != null && touch.primaryTouch.press.isPressed);

        // Fire on press (immediate)
        if (fireMode == FireMode.OnPress && pressedThisFrame)
        {
            if (TryGetScreenPos(out Vector2 sp) && TryMapToWorld(sp, out float2 worldPos))
            {
                EnqueueRequest(worldPos);
                if (verboseLogging) Debug.Log($"[Measure/Hold] OnPress @ {worldPos}");
            }
        }

        // Fire continuously while held
        if (fireMode == FireMode.WhileHeld && isHeld)
        {
            _accum += Time.deltaTime;
            while (_accum >= _interval)
            {
                _accum -= _interval;
                if (TryGetScreenPos(out Vector2 sp) && TryMapToWorld(sp, out float2 worldPos))
                {
                    EnqueueRequest(worldPos);
                    if (verboseLogging) Debug.Log($"[Measure/Hold] WhileHeld tick @ {worldPos}");
                }
                else break; // mapping failed; don't spin
            }
        }

        // Reset the accumulator on release so the next hold fires immediately
        bool releasedThisFrame =
            (mouse != null && mouse.leftButton.wasReleasedThisFrame) ||
            (touch != null && touch.primaryTouch.press.wasReleasedThisFrame);

        if (releasedThisFrame) _accum = 0f;
    }

    // ----- Input helpers -----

    bool TryGetScreenPos(out Vector2 screenPos)
    {
        if (Mouse.current != null && Mouse.current.leftButton.isPressed)
        {
            screenPos = Mouse.current.position.ReadValue();
            return true;
        }
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            screenPos = Touchscreen.current.primaryTouch.position.ReadValue();
            return true;
        }
        screenPos = default;
        return false;
    }

    bool TryMapToWorld(Vector2 screenPos, out float2 worldPos)
    {
        worldPos = default;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return false;

        var em = world.EntityManager;
        var qBounds = em.CreateEntityQuery(ComponentType.ReadOnly<SimBounds2D>());
        if (qBounds.IsEmpty) return false;

        var sim = qBounds.GetSingleton<SimBounds2D>();
        float2 minB = sim.Center - sim.Extents;
        float2 maxB = sim.Center + sim.Extents;

        Vector3 vp = (viewCamera != null)
            ? viewCamera.ScreenToViewportPoint(new Vector3(screenPos.x, screenPos.y, 0f))
            : new Vector3(screenPos.x / Screen.width, screenPos.y / Screen.height, 0f);

        vp.x = Mathf.Clamp01(vp.x);
        vp.y = Mathf.Clamp01(vp.y);

        worldPos = minB + new float2(vp.x, vp.y) * (maxB - minB);
        _lastWorldPos = worldPos; _haveLast = true;
        return true;
    }

    void EnqueueRequest(float2 worldPos)
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var em = world.EntityManager;

        var e = em.CreateEntity(typeof(MeasurementRequest));
        em.SetComponentData(e, new MeasurementRequest
        {
            Center = worldPos,
            Radius = Mathf.Max(1e-4f, radius),
            PushRadius = Mathf.Max(Mathf.Max(1e-4f, radius), pushRadius),
            PushStrength = Mathf.Max(0f, pushStrength),
            Seed = (uint)UnityEngine.Random.Range(1, int.MaxValue)
        });
    }

    // ----- Debug rings -----
    void OnDrawGizmos()
    {
        if (!_haveLast) return;
        Gizmos.color = Color.yellow;
        DrawCircle(_lastWorldPos, radius, 64);
        Gizmos.color = new Color(0.3f, 1f, 0.3f);
        DrawCircle(_lastWorldPos, pushRadius, 64);
    }

    void DrawCircle(Vector2 c, float r, int seg)
    {
        if (seg < 3) seg = 3;
        Vector3 prev = new Vector3(c.x + r, c.y, 0f);
        for (int i = 1; i <= seg; i++)
        {
            float a = (i / (float)seg) * Mathf.PI * 2f;
            Vector3 cur = new Vector3(c.x + r * Mathf.Cos(a), c.y + r * Mathf.Sin(a), 0f);
            Gizmos.DrawLine(prev, cur);
            prev = cur;
        }
    }
}
