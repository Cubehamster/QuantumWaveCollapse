// MeasurementClick.cs
// Uses Mouse Party's MouseCursorManager for multi-mouse support.
// P1 = first Mouse Party cursor: drives ClickRequest + measurement/push.
// P2 = second Mouse Party cursor: drives ClickRequestP2 + measurement/push.
//
// Works in orthographic or perspective; assumes XY plane (z=0).

using System.Linq;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using ChocDino.PartyIO;

// ---------------- ECS structs ----------------

public struct ClickRequest : IComponentData
{
    public float2 WorldPos;
    public float Radius;
    public float PushStrength;
    public bool IsPressed;
    public bool EdgeDown;
    public bool EdgeUp;

    // Multiplier used by MeasurementSystem for highlight/protection logic
    public float ExclusionRadiusMultiplier;

    // NEW: inner region around the click where a different force is used
    // (0 = no inner region)
    public float InnerRadius;
    public float InnerPushStrength;
}

// P2 version (same shape)
public struct ClickRequestP2 : IComponentData
{
    public float2 WorldPos;
    public float Radius;
    public float PushStrength;
    public bool IsPressed;
    public bool EdgeDown;
    public bool EdgeUp;
    public float ExclusionRadiusMultiplier;

    public float InnerRadius;
    public float InnerPushStrength;
}

// Result for Player 1
public struct MeasurementResult : IComponentData
{
    public float2 Center;
    public bool HasActualInRadius;
    public float ProbabilityLast;
    public int ClosestActualIndex;
    public float2 ClosestActualPos;
}

// Result for Player 2
public struct MeasurementResultP2 : IComponentData
{
    public float2 Center;
    public bool HasActualInRadius;
    public float ProbabilityLast;
    public int ClosestActualIndex;
    public float2 ClosestActualPos;
}

public enum ClickMode
{
    HoldToPush,
    PressOnceToPush
}

// ---------------- MonoBehaviour ----------------

[DisallowMultipleComponent]
public sealed class MeasurementClick : MonoBehaviour
{
    [Header("Camera & plane")]
    public Camera cam;
    [Tooltip("XY plane Z value used for screen->world. 0 is typical for 2D.")]
    public float planeZ = 0f;

    [Header("Mouse Party")]
    [Tooltip("Reference to MouseCursorManager in the scene (IMGUI or other).")]
    public MouseCursorManager cursorManager;

    [Header("Interaction (shared)")]
    [Min(0f)] public float radius = 0.25f;
    public float pushStrength = 25f;

    [Tooltip("Actual particles inside R * ExclusionRadiusMultiplier are protected from push.")]
    public float exclusionRadiusMultiplier = 1.5f;

    [Header("Inner region (shared)")]
    [Tooltip("Inner radius (world units) around the click where InnerPushStrength is used. 0 = no inner region.")]
    public float innerRadius = 0.0f;
    [Tooltip("Force used inside the inner radius. Example: small negative for gentle inward pull.")]
    public float innerPushStrength = -5.0f;

    [Header("Debug")]
    public bool logEvents = false;

    [Header("Mode (P1)")]
    public ClickMode mode = ClickMode.HoldToPush;

    [Header("Cursor visuals")]
    // Player 1
    public Transform AimCursorPlayer1;
    public Shapes.Disc Cursor1;
    public Shapes.Disc Progress1;

    // Player 2
    public Transform AimCursorPlayer2;
    public Shapes.Disc Cursor2;
    public Shapes.Disc Progress2;

    public float RotationSpeed = 10f;
    public float progressSpeed = 1f;

    // ---------------- ECS fields ----------------

    EntityManager _em;

    Entity _clickSingletonP1;
    Entity _clickSingletonP2;
    bool _haveClickP1;
    bool _haveClickP2;

    // Measurement results
    Entity _resultSingletonP1;
    Entity _resultSingletonP2;
    bool _haveResultP1;
    bool _haveResultP2;
    MeasurementResult _lastResultP1;
    MeasurementResultP2 _lastResultP2;

    bool _prevPressedP1;
    bool _prevPressedP2;

    void OnEnable()
    {
        if (cam == null) cam = Camera.main;
        Cursor.visible = false;

        if (cursorManager == null)
        {
            cursorManager = FindObjectOfType<MouseCursorManager>();
            if (cursorManager == null)
            {
                Debug.LogError("[MeasurementClick] No MouseCursorManager found in scene.");
                enabled = false;
                return;
            }
        }

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[MeasurementClick] No Default World");
            enabled = false;
            return;
        }

        _em = world.EntityManager;

        // P1 click singleton
        {
            var q = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequest>());
            if (q.CalculateEntityCount() == 0)
            {
                _clickSingletonP1 = _em.CreateEntity(typeof(ClickRequest));
                _em.SetName(_clickSingletonP1, "ClickRequest_P1");
            }
            else
            {
                _clickSingletonP1 = q.GetSingletonEntity();
            }
            _haveClickP1 = true;
        }

        // P2 click singleton
        {
            var q2 = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequestP2>());
            if (q2.CalculateEntityCount() == 0)
            {
                _clickSingletonP2 = _em.CreateEntity(typeof(ClickRequestP2));
                _em.SetName(_clickSingletonP2, "ClickRequest_P2");
            }
            else
            {
                _clickSingletonP2 = q2.GetSingletonEntity();
            }
            _haveClickP2 = true;
        }

        // Measurement results (P1)
        {
            var qRes1 = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResult>());
            if (qRes1.CalculateEntityCount() > 0)
            {
                _resultSingletonP1 = qRes1.GetSingletonEntity();
                _haveResultP1 = true;
            }
        }

        // Measurement results (P2)
        {
            var qRes2 = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResultP2>());
            if (qRes2.CalculateEntityCount() > 0)
            {
                _resultSingletonP2 = qRes2.GetSingletonEntity();
                _haveResultP2 = true;
            }
        }
    }

    void OnDisable()
    {
        _haveClickP1 = _haveClickP2 = false;
        _haveResultP1 = _haveResultP2 = false;
    }

    // Called when P1’s progress bar completes a “scan”
    void OnProgressCompleteP1()
    {
        if (!_haveResultP1 || !_em.Exists(_resultSingletonP1)) return;

        var result = _em.GetComponentData<MeasurementResult>(_resultSingletonP1);
        int idx = result.ClosestActualIndex;
        if (idx < 0) return;

        var qCfg = _em.CreateEntityQuery(
            typeof(ActualParticleSet),
            typeof(ActualParticleStatusElement)
        );
        if (qCfg.IsEmpty) return;

        var cfgEntity = qCfg.GetSingletonEntity();
        var statusBuf = _em.GetBuffer<ActualParticleStatusElement>(cfgEntity);

        bool isGood = UnityEngine.Random.value > 0.5f;  // your real logic goes here
        statusBuf[idx] = new ActualParticleStatusElement
        {
            Value = isGood ? ActualParticleStatus.Good : ActualParticleStatus.Bad
        };
    }

    // Called when P2’s progress bar completes a “scan”
    void OnProgressCompleteP2()
    {
        if (!_haveResultP2 || !_em.Exists(_resultSingletonP2)) return;

        var result = _em.GetComponentData<MeasurementResultP2>(_resultSingletonP2);
        int idx = result.ClosestActualIndex;
        if (idx < 0) return;

        var qCfg = _em.CreateEntityQuery(
            typeof(ActualParticleSet),
            typeof(ActualParticleStatusElement)
        );
        if (qCfg.IsEmpty) return;

        var cfgEntity = qCfg.GetSingletonEntity();
        var statusBuf = _em.GetBuffer<ActualParticleStatusElement>(cfgEntity);

        bool isGood = UnityEngine.Random.value > 0.5f;  // your real logic goes here
        statusBuf[idx] = new ActualParticleStatusElement
        {
            Value = isGood ? ActualParticleStatus.Good : ActualParticleStatus.Bad
        };
    }

    bool IsActualAlreadyIdentified(int index)
    {
        if (index < 0) return false;

        // Query actual particle config
        var qCfg = _em.CreateEntityQuery(
            typeof(ActualParticleSet),
            typeof(ActualParticleStatusElement)
        );
        if (qCfg.IsEmpty) return false;

        var cfgEntity = qCfg.GetSingletonEntity();
        var statusBuf = _em.GetBuffer<ActualParticleStatusElement>(cfgEntity);

        if (index >= statusBuf.Length) return false;

        var status = statusBuf[index].Value;
        return status == ActualParticleStatus.Good ||
               status == ActualParticleStatus.Bad;
    }

    void Update()
    {
        if (!_haveClickP1 || !_em.Exists(_clickSingletonP1)) return;

        // Ensure result singletons exist (in case systems were initialized later)
        if (!_haveResultP1)
        {
            var qRes1 = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResult>());
            if (qRes1.CalculateEntityCount() > 0)
            {
                _resultSingletonP1 = qRes1.GetSingletonEntity();
                _haveResultP1 = true;
            }
        }
        if (!_haveResultP2)
        {
            var qRes2 = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResultP2>());
            if (qRes2.CalculateEntityCount() > 0)
            {
                _resultSingletonP2 = qRes2.GetSingletonEntity();
                _haveResultP2 = true;
            }
        }

        if (cursorManager == null || cursorManager.Cursors == null)
            return;

        var cursorList = cursorManager.Cursors.ToList();
        if (cursorList.Count == 0)
            return;

        BaseMouseCursor cursorP1 = cursorList.Count > 0 ? cursorList[0] : null;
        BaseMouseCursor cursorP2 = cursorList.Count > 1 ? cursorList[1] : null;

        // =========================================================
        // PLAYER 1
        // =========================================================
        if (cursorP1 != null && cursorP1.Enabled)
        {
            bool pressedP1 = cursorP1.Mouse.IsPressed(ChocDino.PartyIO.MouseButton.Left);
            bool edgeDownP1 = pressedP1 && !_prevPressedP1;
            bool edgeUpP1 = !pressedP1 && _prevPressedP1;

            Vector2 screenP1 = cursorP1.ScreenPosition;
            float2 worldP1 = ScreenToWorldXY(screenP1, planeZ);

            if (AimCursorPlayer1 != null)
                AimCursorPlayer1.position = new Vector2(worldP1.x, worldP1.y);

            bool hasActualInsideP1 = false;
            if (_haveResultP1 && _em.Exists(_resultSingletonP1))
            {
                _lastResultP1 = _em.GetComponentData<MeasurementResult>(_resultSingletonP1);
                hasActualInsideP1 = _lastResultP1.HasActualInRadius;
            }

            // Cursor visuals
            if (Cursor1 != null && Progress1 != null)
            {
                if (pressedP1)
                {
                    Cursor1.DashType = Shapes.DashType.Angled;
                    Cursor1.DashSize = 5f;
                    Cursor1.DashSpacing = 2.5f;
                    Cursor1.Thickness = 2 * Mathf.PI;

                    Cursor1.DashOffset = (Cursor1.DashOffset + RotationSpeed * Time.deltaTime) % 1000000f;

                    if (hasActualInsideP1)
                    {
                        int idx = _lastResultP1.ClosestActualIndex;
                        bool alreadyIdentified = IsActualAlreadyIdentified(idx);

                        Cursor1.Thickness = 4 * Mathf.PI;
                        Cursor1.DashSize = 7f;

                        if (alreadyIdentified)
                        {
                            // Instantly filled / locked
                            Progress1.AngRadiansEnd = -0.5f * Mathf.PI;
                            Cursor1.AngRadiansStart = -0.5f * Mathf.PI;
                        }
                        else
                        {
                            // Normal scanning countdown
                            Progress1.AngRadiansEnd -= Time.deltaTime * progressSpeed;
                            Progress1.AngRadiansEnd = Mathf.Clamp(
                                Progress1.AngRadiansEnd, -0.5f * Mathf.PI, 0.5f * Mathf.PI
                            );

                            Cursor1.AngRadiansStart -= Time.deltaTime * progressSpeed;
                            Cursor1.AngRadiansStart = Mathf.Clamp(
                                Cursor1.AngRadiansStart, -0.5f * Mathf.PI, 0.5f * Mathf.PI
                            );

                            bool didComplete = Mathf.Approximately(Progress1.AngRadiansEnd, -0.5f * Mathf.PI);
                            if (didComplete)
                                OnProgressCompleteP1();
                        }
                    }
                    else
                    {
                        Progress1.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                        Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, -0.5f * Mathf.PI, 0.5f * Mathf.PI);

                        Cursor1.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                        Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, -0.5f * Mathf.PI, 0.5f * Mathf.PI);
                    }
                }
                else
                {
                    Cursor1.DashType = Shapes.DashType.Basic;
                    Cursor1.DashSize = 2f;
                    Cursor1.DashSpacing = 3f;
                    Cursor1.Thickness = 5f;

                    Cursor1.DashOffset = (Cursor1.DashOffset + 0.1f * RotationSpeed * Time.deltaTime) % 1000000f;

                    Progress1.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                    Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, -1.5f * Mathf.PI, 0.5f * Mathf.PI);

                    Cursor1.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                    Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, -1.5f * Mathf.PI, 0.5f * Mathf.PI);
                }
            }

            // Send ECS ClickRequest (P1)
            var reqP1 = new ClickRequest
            {
                WorldPos = worldP1,
                Radius = radius,
                PushStrength = pushStrength,
                IsPressed = pressedP1,
                EdgeDown = edgeDownP1,
                EdgeUp = edgeUpP1,
                ExclusionRadiusMultiplier = exclusionRadiusMultiplier,
                InnerRadius = innerRadius,
                InnerPushStrength = innerPushStrength
            };
            _em.SetComponentData(_clickSingletonP1, reqP1);

            if (logEvents && (edgeDownP1 || edgeUpP1 || pressedP1 != _prevPressedP1))
            {
                Debug.Log($"[Click P1] pressed={pressedP1} down={edgeDownP1} up={edgeUpP1} " +
                          $"world={worldP1} R={radius} outerPush={pushStrength} innerR={innerRadius} innerPush={innerPushStrength}");
            }

            _prevPressedP1 = pressedP1;
        }

        // =========================================================
        // PLAYER 2
        // =========================================================
        if (cursorP2 != null && cursorP2.Enabled && AimCursorPlayer2 != null)
        {
            bool pressedP2 = cursorP2.Mouse.IsPressed(ChocDino.PartyIO.MouseButton.Left);
            bool edgeDownP2 = pressedP2 && !_prevPressedP2;
            bool edgeUpP2 = !pressedP2 && _prevPressedP2;

            Vector2 screenP2 = cursorP2.ScreenPosition;
            float2 worldP2 = ScreenToWorldXY(screenP2, planeZ);

            AimCursorPlayer2.position = new Vector2(worldP2.x, worldP2.y);

            bool hasActualInsideP2 = false;
            if (_haveResultP2 && _em.Exists(_resultSingletonP2))
            {
                _lastResultP2 = _em.GetComponentData<MeasurementResultP2>(_resultSingletonP2);
                hasActualInsideP2 = _lastResultP2.HasActualInRadius;
            }

            if (Cursor2 != null && Progress2 != null)
            {
                if (pressedP2)
                {
                    Cursor2.DashType = Shapes.DashType.Angled;
                    Cursor2.DashSize = 5f;
                    Cursor2.DashSpacing = 2.5f;
                    Cursor2.Thickness = 2 * Mathf.PI;

                    Cursor2.DashOffset = (Cursor2.DashOffset + RotationSpeed * Time.deltaTime) % 1000000f;

                    if (hasActualInsideP2)
                    {
                        int idx = _lastResultP2.ClosestActualIndex;
                        bool alreadyIdentified = IsActualAlreadyIdentified(idx);

                        Cursor2.Thickness = 4 * Mathf.PI;
                        Cursor2.DashSize = 7f;

                        if (alreadyIdentified)
                        {
                            // Lock filled
                            Progress2.AngRadiansEnd = -0.5f * Mathf.PI;
                            Cursor2.AngRadiansStart = -0.5f * Mathf.PI;
                        }
                        else
                        {
                            Progress2.AngRadiansEnd -= Time.deltaTime * progressSpeed;
                            Progress2.AngRadiansEnd = Mathf.Clamp(
                                Progress2.AngRadiansEnd, -0.5f * Mathf.PI, 0.5f * Mathf.PI
                            );

                            Cursor2.AngRadiansStart -= Time.deltaTime * progressSpeed;
                            Cursor2.AngRadiansStart = Mathf.Clamp(
                                Cursor2.AngRadiansStart, -0.5f * Mathf.PI, 0.5f * Mathf.PI
                            );

                            bool didComplete2 = Mathf.Approximately(Progress2.AngRadiansEnd, -0.5f * Mathf.PI);
                            if (didComplete2)
                                OnProgressCompleteP2();
                        }
                    }

                    else
                    {
                        Progress2.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                        Progress2.AngRadiansEnd = Mathf.Clamp(Progress2.AngRadiansEnd, -0.5f * Mathf.PI, 0.5f * Mathf.PI);

                        Cursor2.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                        Cursor2.AngRadiansStart = Mathf.Clamp(Cursor2.AngRadiansStart, -0.5f * Mathf.PI, 0.5f * Mathf.PI);
                    }
                }
                else
                {
                    Cursor2.DashType = Shapes.DashType.Basic;
                    Cursor2.DashSize = 2f;
                    Cursor2.DashSpacing = 3f;
                    Cursor2.Thickness = 5f;

                    Cursor2.DashOffset = (Cursor2.DashOffset + 0.1f * RotationSpeed * Time.deltaTime) % 1000000f;

                    Progress2.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                    Progress2.AngRadiansEnd = Mathf.Clamp(Progress2.AngRadiansEnd, -1.5f * Mathf.PI, 0.5f * Mathf.PI);

                    Cursor2.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                    Cursor2.AngRadiansStart = Mathf.Clamp(Cursor2.AngRadiansStart, -1.5f * Mathf.PI, 0.5f * Mathf.PI);
                }
            }

            var reqP2 = new ClickRequestP2
            {
                WorldPos = worldP2,
                Radius = radius,
                PushStrength = pushStrength,
                IsPressed = pressedP2,
                EdgeDown = edgeDownP2,
                EdgeUp = edgeUpP2,
                ExclusionRadiusMultiplier = exclusionRadiusMultiplier,
                InnerRadius = innerRadius,
                InnerPushStrength = innerPushStrength
            };
            _em.SetComponentData(_clickSingletonP2, reqP2);

            if (logEvents && (edgeDownP2 || edgeUpP2 || pressedP2 != _prevPressedP2))
            {
                Debug.Log($"[Click P2] pressed={pressedP2} down={edgeDownP2} up={edgeUpP2} " +
                          $"world={worldP2} R={radius} outerPush={pushStrength} innerR={innerRadius} innerPush={innerPushStrength}");
            }

            _prevPressedP2 = pressedP2;
        }
    }

    // ------------------------------------------------------------
    // Screen → world helper
    // ------------------------------------------------------------
    float2 ScreenToWorldXY(Vector2 screen, float zPlane)
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) return screen;

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
