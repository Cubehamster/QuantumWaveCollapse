// MeasurementClick.cs
// Reads mouse from the *New* Input System and publishes a ClickRequest singleton
// for MeasurementSystem. Works in orthographic or perspective; assumes XY plane (z=0).

using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using Unity.VisualScripting;

public struct ClickRequest : IComponentData
{
    public float2 WorldPos;
    public float Radius;
    public float PushStrength;
    public bool IsPressed;
    public bool EdgeDown;
    public bool EdgeUp;

    // NEW: how much larger the exclusion radius is relative to Radius.
    // MeasurementSystem clamps this to ≥ 1.
    public float ExclusionRadiusMultiplier;
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
    public float pushStrength = 25f;

    [Tooltip("Actual particles inside R * ExclusionRadiusMultiplier are protected from push.")]
    public float exclusionRadiusMultiplier = 1.5f;

    [Header("Debug")]
    public bool logEvents = false;

    [Header("Mode")]
    public ClickMode mode = ClickMode.HoldToPush;

    [Header("Cursor visuals")]
    public Transform AimCursorPlayer1;
    public Transform AimCursorPlayer2;
    public Shapes.Disc Cursor1;
    public Shapes.Disc Cursor2;
    public Shapes.Disc Progress1;
    public Shapes.Disc Progress2;
    public float RotationSpeed = 10f;
    public float progressSpeed = 1f;

    // ECS
    EntityManager _em;
    Entity _clickSingleton;
    bool _haveClickSingleton;

    // For reading MeasurementResult
    Entity _resultSingleton;
    bool _haveResultSingleton;
    MeasurementResult _lastResult;   // cached each frame

    // Cached button edge state (we derive from InputSystem each frame)
    bool _prevPressed;

    void OnEnable()
    {
        if (cam == null) cam = Camera.main;
        Cursor.visible = false;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[MeasurementClick] No Default World");
            enabled = false;
            return;
        }

        _em = world.EntityManager;

        // Create or find the singleton entity holding ClickRequest
        {
            var q = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequest>());
            if (q.CalculateEntityCount() == 0)
            {
                _clickSingleton = _em.CreateEntity(typeof(ClickRequest));
                _em.SetName(_clickSingleton, "ClickRequestSingleton");
                _haveClickSingleton = true;
            }
            else
            {
                _clickSingleton = q.GetSingletonEntity();
                _haveClickSingleton = true;
            }
        }

        // Try to find MeasurementResult singleton (MeasurementSystem creates it)
        {
            var qRes = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResult>());
            if (qRes.CalculateEntityCount() > 0)
            {
                _resultSingleton = qRes.GetSingletonEntity();
                _haveResultSingleton = true;
            }
        }
    }

    void OnDisable()
    {
        _haveClickSingleton = false;
        _haveResultSingleton = false;
    }

    void OnProgressComplete()
    {
        if (!_haveResultSingleton) return;
        if (!_em.Exists(_resultSingleton)) return;

        var result = _em.GetComponentData<MeasurementResult>(_resultSingleton);
        int idx = result.ClosestActualIndex;
        if (idx < 0) return; // no actual inside

        // Fetch the config entity that holds the buffer
        var qCfg = _em.CreateEntityQuery(
            typeof(ActualParticleSet),
            typeof(ActualParticleStatusElement)
        );
        if (qCfg.IsEmpty) return;

        var cfgEntity = qCfg.GetSingletonEntity();
        var statusBuf = _em.GetBuffer<ActualParticleStatusElement>(cfgEntity);

        // ************ DECISION LOGIC ***************
        bool isGood = UnityEngine.Random.value > 0.5f;  // <-- replace with your real logic
        statusBuf[idx] = new ActualParticleStatusElement
        {
            Value = isGood ? ActualParticleStatus.Good : ActualParticleStatus.Bad
        };
    }

    void Update()
    {
        if (!_haveClickSingleton || !_em.Exists(_clickSingleton)) return;

        // Ensure we have a result singleton reference (in case systems came up later).
        if (!_haveResultSingleton)
        {
            var qRes = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResult>());
            if (qRes.CalculateEntityCount() > 0)
            {
                _resultSingleton = qRes.GetSingletonEntity();
                _haveResultSingleton = true;
            }
        }

        var mouse = Mouse.current;
        if (mouse == null) return;

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

        if (AimCursorPlayer1 != null)
            AimCursorPlayer1.position = new Vector2(worldXY.x, worldXY.y);

        // ---- Read MeasurementResult so we know if an actual is inside *exclusion* radius ----
        bool hasActualInside = false;
        if (_haveResultSingleton && _em.Exists(_resultSingleton))
        {
            _lastResult = _em.GetComponentData<MeasurementResult>(_resultSingleton);
            hasActualInside = _lastResult.HasActualInRadius;
        }

        // ---- UI / cursor visuals ----
        if (Cursor1 != null && AimCursorPlayer1 != null)
        {
            if (pressed)
            {
                //// Spin cursor while pressed
                //Vector3 eulers = AimCursorPlayer1.rotation.eulerAngles;
                //AimCursorPlayer1.eulerAngles = new Vector3(
                //    eulers.x, eulers.y,
                //    (eulers.z + RotationSpeed * Time.deltaTime) % 360f
                //);



                // Base style while pressed
                Cursor1.DashType = Shapes.DashType.Angled;
                Cursor1.DashSize = 5f;
                Cursor1.DashSpacing = 2.5f;
                Cursor1.Thickness = 2 * Mathf.PI;

                Cursor1.DashOffset = Cursor1.DashOffset + RotationSpeed * Time.deltaTime;
                Cursor1.DashOffset = Cursor1.DashOffset % 1000000;

                // EXTRA: highlight when an actual is inside the radius
                if (hasActualInside)
                {
                    // Example tweak: make it thicker and denser when hitting an actual
                    Cursor1.Thickness = 4 * Mathf.PI;
                    Cursor1.DashSize = 7f;
                    Progress1.AngRadiansEnd = Progress1.AngRadiansEnd - Time.deltaTime * progressSpeed;
                    Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, -0.5f * Mathf.PI, 0.5f * Mathf.PI);

                    Cursor1.AngRadiansStart = Cursor1.AngRadiansStart - Time.deltaTime * progressSpeed;
                    Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, -0.5f * Mathf.PI, 0.5f * Mathf.PI);
                    // you can also modify color via Cursor1.Color if desired

                    bool didComplete = Mathf.Approximately(Progress1.AngRadiansEnd, -0.5f * Mathf.PI);

                    if (didComplete && hasActualInside)
                    {
                        OnProgressComplete();
                    }
                }
                else
                {
                    Progress1.AngRadiansEnd = Progress1.AngRadiansEnd + Time.deltaTime * 8 * progressSpeed;
                    Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, -0.5f * Mathf.PI, 0.5f * Mathf.PI);

                    Cursor1.AngRadiansStart = Cursor1.AngRadiansStart + Time.deltaTime * 8 * progressSpeed;
                    Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, -0.5f * Mathf.PI, 0.5f * Mathf.PI);
                }
            }
            else
            {
                Cursor1.DashType = Shapes.DashType.Basic;
                Cursor1.DashSize = 2f;
                Cursor1.DashSpacing = 3f;
                Cursor1.Thickness = 5f;

                Cursor1.DashOffset = Cursor1.DashOffset + 0.1f * RotationSpeed * Time.deltaTime;
                Cursor1.DashOffset = Cursor1.DashOffset % 1000000;

                Progress1.AngRadiansEnd = Progress1.AngRadiansEnd + Time.deltaTime * 8 * progressSpeed;
                Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, -1.5f * Mathf.PI, 0.5f * Mathf.PI);

                Cursor1.AngRadiansStart = Cursor1.AngRadiansStart + Time.deltaTime * 8 * progressSpeed;
                Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, -1.5f * Mathf.PI, 0.5f * Mathf.PI);
            }
        }

        // Build and publish ClickRequest
        var req = new ClickRequest
        {
            WorldPos = worldXY,
            Radius = radius,
            PushStrength = pushStrength,
            IsPressed = pressed,
            EdgeDown = edgeDown,
            EdgeUp = edgeUp,
            ExclusionRadiusMultiplier = exclusionRadiusMultiplier
        };

        _em.SetComponentData(_clickSingleton, req);

        if (logEvents && (edgeDown || edgeUp || pressed != _prevPressed))
        {
            Debug.Log($"[Click] pressed={pressed} down={edgeDown} up={edgeUp} " +
                      $"world={worldXY}  R={radius} push={pushStrength} " +
                      $"exclMul={exclusionRadiusMultiplier} hasActualInside={hasActualInside}");
        }

        _prevPressed = pressed;
    }

    float2 ScreenToWorldXY(Vector2 screen, float zPlane)
    {
        if (cam == null) cam = Camera.main;
        if (cam == null)
        {
            return screen;
        }

        if (cam.orthographic)
        {
            Vector3 wp = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, cam.nearClipPlane));
            return new float2(wp.x, wp.y);
        }
        else
        {
            Ray r = cam.ScreenPointToRay(screen);
            float denom = r.direction.z;
            float t = (Mathf.Abs(denom) < 1e-6f) ? 0f : (zPlane - r.origin.z) / denom;
            Vector3 p = r.origin + t * r.direction;
            return new float2(p.x, p.y);
        }
    }
}
