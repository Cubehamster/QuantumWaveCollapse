// MeasurementClick.cs
// Uses Mouse Party's MouseCursorManager for multi-mouse support.
// P1 = first Mouse Party cursor: drives ClickRequest + measurement/push.
// P2 = second Mouse Party cursor: drives ClickRequestP2 + measurement/push.
//
// Works in orthographic or perspective; assumes XY plane (z=0).

using System.Linq;
using System.Collections.Generic;
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

    // Inner region where a different force is used (0 = no inner region)
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
    // Global click lock used during tutorial countdown:
    public static bool ClickLocked = false;

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

    [Tooltip("Actual particles inside R * ExclusionRadiusMultiplier are protected / highlighted.")]
    public float exclusionRadiusMultiplier = 1.5f;

    [Header("Inner region (shared)")]
    [Tooltip("Inner radius (world units) where InnerPushStrength is used. 0 = no inner region.")]
    public float innerRadius = 0.0f;
    [Tooltip("Force used inside the inner radius. Example: small negative for gentle inward pull.")]
    public float innerPushStrength = -5.0f;

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

    [Header("Radius scaling (holder/assister)")]
    [Tooltip("How fast cursor radii lerp toward their target sizes.")]
    public float radiusLerpSpeed = 8f;
    [Tooltip("Scale applied to holder cursor (and radii).")]
    public float holderRadiusMultiplier = 2f;
    [Tooltip("Scale applied to assisting / disrupted cursor (and radii).")]
    public float assistRadiusMultiplier = 2f / 3f;
    [Tooltip("Inner pull multiplier while holding an identified actual (e.g. 0.5 = half strength).")]
    public float holderInnerStrengthMultiplier = 0.5f;

    [Header("Post-scan repulsion")]
    [Tooltip("Duration after solo identification where inner force is flipped to push.")]
    public float postIdentifyRepelDuration = 0.2f;
    [Tooltip("Duration after full co-op scan where inner force is flipped to push.")]
    public float postCoopRepelDuration = 0.2f;

    [Header("Per-player drop window")]
    [Tooltip("Time window after a scan during which THAT player ignores that actual (used to drop released particles & avoid immediate re-grabs).")]
    public float spawnScanLockout = 1.0f;

    [Header("Debug")]
    public bool logEvents = false;

    [Header("Mode (P1)")]
    public ClickMode mode = ClickMode.HoldToPush;

    public bool p2mirrored = false;

    public bool playersFlipped = false;

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

    // Cached base radii for smooth scaling
    bool _baseRadiiInitialized;
    float _cursor1BaseRadius;
    float _progress1BaseRadius;
    float _cursor2BaseRadius;
    float _progress2BaseRadius;

    // Per-actual holder: actualIndex -> 1 (P1) or 2 (P2)
    readonly Dictionary<int, int> _actualHolderIndexToPlayer = new();

    // Post-scan repulsion timers
    float _p1RepelTimer;
    float _p2RepelTimer;

    // Per-player "drop this actual" window: ignore this index for some time
    int _p1DropIndex = -1;
    float _p1DropUntilTime;
    int _p2DropIndex = -1;
    float _p2DropUntilTime;

    // Track when a player was scanning an identified actual in the previous frame
    bool _p1WasScanningIdentPrev;
    bool _p2WasScanningIdentPrev;

    // For intro warmup detection: previous cursor positions
    float2 _prevWorldP1;
    float2 _prevWorldP2;
    bool _havePrevWorldP1;
    bool _havePrevWorldP2;

    // Per-player input gates (controlled by SquidGameController)
    bool _p1InputEnabled = true;
    bool _p2InputEnabled = true;


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

        InitBaseRadii();
    }

    void OnDisable()
    {
        ClearClickRequests();

        _haveClickP1 = _haveClickP2 = false;
        _haveResultP1 = _haveResultP2 = false;
        _actualHolderIndexToPlayer.Clear();
    }

    public void SetPlayer1InputEnabled(bool enabled)
    {
        _p1InputEnabled = enabled;

        if (!enabled)
        {
            // Neutralise P1 ClickRequest in ECS so no forces / scanning
            if (_em != null && _haveClickP1 && _em.Exists(_clickSingletonP1))
            {
                _em.SetComponentData(_clickSingletonP1, new ClickRequest
                {
                    WorldPos = float2.zero,
                    Radius = 0f,
                    PushStrength = 0f,
                    IsPressed = false,
                    EdgeDown = false,
                    EdgeUp = false,
                    ExclusionRadiusMultiplier = 0f,
                    InnerRadius = 0f,
                    InnerPushStrength = 0f
                });
            }

            // Drop any holder entries owned by player 1
            if (_actualHolderIndexToPlayer.Count > 0)
            {
                var toRemove = new List<int>();
                foreach (var kv in _actualHolderIndexToPlayer)
                {
                    if (kv.Value == 1)
                        toRemove.Add(kv.Key);
                }
                foreach (var key in toRemove)
                    _actualHolderIndexToPlayer.Remove(key);
            }

            // Reset P1-local state so no “ghost” interactions remain
            _prevPressedP1 = false;
            _p1RepelTimer = 0f;
            _p1DropIndex = -1;
            _p1DropUntilTime = 0f;
            _p1WasScanningIdentPrev = false;
            _havePrevWorldP1 = false;
        }
    }

    public void SetPlayer2InputEnabled(bool enabled)
    {
        _p2InputEnabled = enabled;

        if (!enabled)
        {
            // Neutralise P2 ClickRequest in ECS so no forces / scanning
            if (_em != null && _haveClickP2 && _em.Exists(_clickSingletonP2))
            {
                _em.SetComponentData(_clickSingletonP2, new ClickRequestP2
                {
                    WorldPos = float2.zero,
                    Radius = 0f,
                    PushStrength = 0f,
                    IsPressed = false,
                    EdgeDown = false,
                    EdgeUp = false,
                    ExclusionRadiusMultiplier = 0f,
                    InnerRadius = 0f,
                    InnerPushStrength = 0f
                });
            }

            // Drop any holder entries owned by player 2
            if (_actualHolderIndexToPlayer.Count > 0)
            {
                var toRemove = new List<int>();
                foreach (var kv in _actualHolderIndexToPlayer)
                {
                    if (kv.Value == 2)
                        toRemove.Add(kv.Key);
                }
                foreach (var key in toRemove)
                    _actualHolderIndexToPlayer.Remove(key);
            }

            // Reset P2-local state
            _prevPressedP2 = false;
            _p2RepelTimer = 0f;
            _p2DropIndex = -1;
            _p2DropUntilTime = 0f;
            _p2WasScanningIdentPrev = false;
            _havePrevWorldP2 = false;
        }
    }


    void ResetAllInteractionStateAndRequests()
    {
        if (_em == null)
            return;

        // --- Clear ECS click requests (P1 + P2) ---
        if (_haveClickP1 && _em.Exists(_clickSingletonP1))
        {
            _em.SetComponentData(_clickSingletonP1, new ClickRequest
            {
                WorldPos = float2.zero,
                Radius = 0f,
                PushStrength = 0f,
                IsPressed = false,
                EdgeDown = false,
                EdgeUp = false,
                ExclusionRadiusMultiplier = 0f,
                InnerRadius = 0f,
                InnerPushStrength = 0f
            });
        }

        if (_haveClickP2 && _em.Exists(_clickSingletonP2))
        {
            _em.SetComponentData(_clickSingletonP2, new ClickRequestP2
            {
                WorldPos = float2.zero,
                Radius = 0f,
                PushStrength = 0f,
                IsPressed = false,
                EdgeDown = false,
                EdgeUp = false,
                ExclusionRadiusMultiplier = 0f,
                InnerRadius = 0f,
                InnerPushStrength = 0f
            });
        }

        // --- Clear measurement results so nothing is "inside radius" anymore ---
        if (_haveResultP1 && _em.Exists(_resultSingletonP1))
        {
            _em.SetComponentData(_resultSingletonP1, new MeasurementResult
            {
                Center = float2.zero,
                HasActualInRadius = false,
                ProbabilityLast = 0f,
                ClosestActualIndex = -1,
                ClosestActualPos = float2.zero
            });
        }

        if (_haveResultP2 && _em.Exists(_resultSingletonP2))
        {
            _em.SetComponentData(_resultSingletonP2, new MeasurementResultP2
            {
                Center = float2.zero,
                HasActualInRadius = false,
                ProbabilityLast = 0f,
                ClosestActualIndex = -1,
                ClosestActualPos = float2.zero
            });
        }

        // --- Local state reset ---
        _prevPressedP1 = false;
        _prevPressedP2 = false;

        _p1RepelTimer = 0f;
        _p2RepelTimer = 0f;

        _p1DropIndex = -1;
        _p2DropIndex = -1;
        _p1DropUntilTime = 0f;
        _p2DropUntilTime = 0f;

        _p1WasScanningIdentPrev = false;
        _p2WasScanningIdentPrev = false;

        _actualHolderIndexToPlayer.Clear();

        _havePrevWorldP1 = false;
        _havePrevWorldP2 = false;

        // (Optionally you could also reset cursor visuals here if you want.)
    }


    public void ClearClickRequests()
    {
        if (_em == null)
            return;

        // Make sure we know about result singletons,
        // even if they were created after OnEnable.
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

        // -----------------------------
        // 1) Clear ClickRequest (P1/P2)
        // -----------------------------
        if (_haveClickP1 && _em.Exists(_clickSingletonP1))
        {
            _em.SetComponentData(_clickSingletonP1, new ClickRequest
            {
                WorldPos = float2.zero,
                Radius = 0f,
                PushStrength = 0f,
                IsPressed = false,
                EdgeDown = false,
                EdgeUp = false,
                ExclusionRadiusMultiplier = 0f,
                InnerRadius = 0f,
                InnerPushStrength = 0f
            });
        }

        if (_haveClickP2 && _em.Exists(_clickSingletonP2))
        {
            _em.SetComponentData(_clickSingletonP2, new ClickRequestP2
            {
                WorldPos = float2.zero,
                Radius = 0f,
                PushStrength = 0f,
                IsPressed = false,
                EdgeDown = false,
                EdgeUp = false,
                ExclusionRadiusMultiplier = 0f,
                InnerRadius = 0f,
                InnerPushStrength = 0f
            });
        }

        // --------------------------------------------
        // 2) Clear MeasurementResult (P1/P2) so that
        //    no Actual is treated as "inside radius".
        // --------------------------------------------
        if (_haveResultP1 && _em.Exists(_resultSingletonP1))
        {
            _em.SetComponentData(_resultSingletonP1, new MeasurementResult
            {
                Center = float2.zero,
                HasActualInRadius = false,
                ProbabilityLast = 0f,
                ClosestActualIndex = -1,
                ClosestActualPos = float2.zero
            });
        }

        if (_haveResultP2 && _em.Exists(_resultSingletonP2))
        {
            _em.SetComponentData(_resultSingletonP2, new MeasurementResultP2
            {
                Center = float2.zero,
                HasActualInRadius = false,
                ProbabilityLast = 0f,
                ClosestActualIndex = -1,
                ClosestActualPos = float2.zero
            });
        }

        // --------------------------------------------
        // 3) Local state reset so there are no holders,
        //    timers, drop windows, or "previous pressed".
        // --------------------------------------------
        _prevPressedP1 = false;
        _prevPressedP2 = false;

        _p1RepelTimer = 0f;
        _p2RepelTimer = 0f;

        _p1DropIndex = -1;
        _p2DropIndex = -1;
        _p1DropUntilTime = 0f;
        _p2DropUntilTime = 0f;

        _p1WasScanningIdentPrev = false;
        _p2WasScanningIdentPrev = false;

        _actualHolderIndexToPlayer.Clear();

        _havePrevWorldP1 = false;
        _havePrevWorldP2 = false;

        // (Optional) you can also reset cursor visuals here if you want them snapped back.
    }




    void InitBaseRadii()
    {
        if (_baseRadiiInitialized) return;

        if (Cursor1 != null)
            _cursor1BaseRadius = Cursor1.Radius > 0f ? Cursor1.Radius : radius;
        else
            _cursor1BaseRadius = radius;

        if (Progress1 != null)
            _progress1BaseRadius = Progress1.Radius > 0f ? Progress1.Radius : radius;
        else
            _progress1BaseRadius = radius;

        if (Cursor2 != null)
            _cursor2BaseRadius = Cursor2.Radius > 0f ? Cursor2.Radius : radius;
        else
            _cursor2BaseRadius = radius;

        if (Progress2 != null)
            _progress2BaseRadius = Progress2.Radius > 0f ? Progress2.Radius : radius;
        else
            _progress2BaseRadius = radius;

        _baseRadiiInitialized = true;
    }

    // ---------------- Identification complete (phase 1) ----------------

    // Called when P1’s progress bar completes a “scan” (identification)
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

        if (idx >= 0 && idx < statusBuf.Length)
        {
            bool isGood = false;

            // Intro/tutorial override: let the controller force Good/Bad when needed
            var ctrl = SquidGameController.Instance;
            bool forced = ctrl != null && ctrl.TryConsumeIntroForcedIdentify(out isGood);

            if (!forced)
            {
                // Normal game RNG
                isGood = UnityEngine.Random.value > 0.5f;
            }

            statusBuf[idx] = new ActualParticleStatusElement
            {
                Value = isGood ? ActualParticleStatus.Good : ActualParticleStatus.Bad
            };

            // Notify pool (spawns etc). Suppression for intro is handled INSIDE the pool.
            ActualParticlePoolSystem.NotifyIdentified(idx, isGood);

            // Tell the controller so the tutorial state machine can advance.
            if (ctrl != null)
            {
                ctrl.OnActualIdentified(idx, isGood);
            }

            // Clear any holder mapping for this index so future holds are clean
            _actualHolderIndexToPlayer.Remove(idx);

            // Start a short repulsion window for P1 to push walkers away
            _p1RepelTimer = Mathf.Max(_p1RepelTimer, postIdentifyRepelDuration);

            // Per-player drop: P1 ignores this index for a short time
            _p1DropIndex = idx;
            _p1DropUntilTime = Time.time + spawnScanLockout;
        }
    }

    // Called when P2’s progress bar completes a “scan” (identification)
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

        if (idx >= 0 && idx < statusBuf.Length)
        {
            bool isGood = false;

            // Intro/tutorial override
            var ctrl = SquidGameController.Instance;
            bool forced = ctrl != null && ctrl.TryConsumeIntroForcedIdentify(out isGood);

            if (!forced)
            {
                isGood = UnityEngine.Random.value > 0.5f;
            }

            statusBuf[idx] = new ActualParticleStatusElement
            {
                Value = isGood ? ActualParticleStatus.Good : ActualParticleStatus.Bad
            };

            ActualParticlePoolSystem.NotifyIdentified(idx, isGood);

            if (ctrl != null)
            {
                ctrl.OnActualIdentified(idx, isGood);
            }

            _actualHolderIndexToPlayer.Remove(idx);

            // Repulsion window for P2
            _p2RepelTimer = Mathf.Max(_p2RepelTimer, postIdentifyRepelDuration);

            // Per-player drop: P2 ignores this index for a short time
            _p2DropIndex = idx;
            _p2DropUntilTime = Time.time + spawnScanLockout;
        }
    }

    // ---------------- Coop full-scan complete (phase 2) ----------------

    void CoopCompleteP1()
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

        if (idx >= 0 && idx < statusBuf.Length)
        {
            var st = statusBuf[idx].Value;
            bool wasGood = (st == ActualParticleStatus.Good);

            // Coop full scan: send back to pool, spawn 1 (good) or 2 (bad)
            ActualParticlePoolSystem.NotifyFullyScanned(idx, wasGood);

            // Index no longer held
            _actualHolderIndexToPlayer.Remove(idx);

            // Start repulsion window for BOTH players (they were both involved in coop)
            _p1RepelTimer = Mathf.Max(_p1RepelTimer, postCoopRepelDuration);
            _p2RepelTimer = Mathf.Max(_p2RepelTimer, postCoopRepelDuration);

            // Both players drop this actual for a short time
            float dropUntil = Time.time + spawnScanLockout;
            _p1DropIndex = idx;
            _p2DropIndex = idx;
            _p1DropUntilTime = dropUntil;
            _p2DropUntilTime = dropUntil;

            // Notify the controller so tutorial can advance from "destroy bad" → countdown.
            var ctrl = SquidGameController.Instance;
            if (ctrl != null)
            {
                ctrl.OnActualFullyScanned(idx, wasGood);
            }
        }
    }

    void Update()
    {
        if (!_haveClickP1 || !_em.Exists(_clickSingletonP1) || !_haveClickP2 || !_em.Exists(_clickSingletonP2))
            return;


        // If the game is not in Active, forcefully zero all interaction & ECS click state
        var ctrl = SquidGameController.Instance;
        bool gameActive = ctrl != null && ctrl.CurrentStage == SquidStage.Active;

        if (!gameActive)
        {
            ResetAllInteractionStateAndRequests();
            return;
        }

        InitBaseRadii();

        // Tick repulsion timers
        float dt = Time.deltaTime;
        if (_p1RepelTimer > 0f) _p1RepelTimer = Mathf.Max(0f, _p1RepelTimer - dt);
        if (_p2RepelTimer > 0f) _p2RepelTimer = Mathf.Max(0f, _p2RepelTimer - dt);

        bool p1RepelActive = _p1RepelTimer > 0f;
        bool p2RepelActive = _p2RepelTimer > 0f;

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
        // 1) Read raw input + world positions for both players
        // =========================================================
        bool p1Available = cursorP1 != null && cursorP1.Enabled && _p1InputEnabled;
        bool p2Available = cursorP2 != null && cursorP2.Enabled && _p2InputEnabled;


        bool pressedP1 = false, edgeDownP1 = false, edgeUpP1 = false;
        bool pressedP2 = false, edgeDownP2 = false, edgeUpP2 = false;

        float2 worldP1 = float2.zero;
        float2 worldP2 = float2.zero;

        if (p1Available)
        {
            if (mode == ClickMode.HoldToPush)
            {
                pressedP1 = cursorP1.Mouse.IsPressed(MouseButton.Left);
                edgeDownP1 = pressedP1 && !_prevPressedP1;
                edgeUpP1 = !pressedP1 && _prevPressedP1;
            }
            else
            {
                pressedP1 = cursorP1.Mouse.IsPressed(MouseButton.Left) &&
                            cursorP1.Mouse.WasPressedThisFrame(MouseButton.Left);
                edgeDownP1 = pressedP1;
                edgeUpP1 = false;
            }

            Vector2 screenP1 = cursorP1.ScreenPosition;
           
            if (playersFlipped)
                screenP1.x = Screen.width - screenP1.x;

            worldP1 = ScreenToWorldXY(screenP1, planeZ);

            if (AimCursorPlayer1 != null)
                AimCursorPlayer1.position = new Vector2(worldP1.x, worldP1.y);
        }

        if (!p2mirrored)
        {
            if (p2Available)
            {
                pressedP2 = cursorP2.Mouse.IsPressed(MouseButton.Left);
                edgeDownP2 = pressedP2 && !_prevPressedP2;
                edgeUpP2 = !pressedP2 && _prevPressedP2;

                Vector2 screenP2 = cursorP2.ScreenPosition;
                worldP2 = ScreenToWorldXY(screenP2, planeZ);

                if (AimCursorPlayer2 != null)
                    AimCursorPlayer2.position = new Vector2(worldP2.x, worldP2.y);
            }
        }
        else
        {
            if (p2Available)
            {
                pressedP2 = cursorP2.Mouse.IsPressed(MouseButton.Left);
                edgeDownP2 = pressedP2 && !_prevPressedP2;
                edgeUpP2 = !pressedP2 && _prevPressedP2;

                // Get raw screen position from Mouse Party
                Vector2 screenP2 = cursorP2.ScreenPosition;

                // Mirror horizontally around the center of the screen
                if(!playersFlipped)
                    screenP2.x = Screen.width - screenP2.x;

                // Convert mirrored screen coords to world
                worldP2 = ScreenToWorldXY(screenP2, planeZ);

                // Move P2’s aim cursor to the mirrored world position
                if (AimCursorPlayer2 != null)
                    AimCursorPlayer2.position = new Vector2(worldP2.x, worldP2.y);
            }

        }


        // ---------------------------------------------------------
        // Intro warmup detection: movement OR click per player
        // ---------------------------------------------------------
        bool movedP1 = false;
        bool movedP2 = false;
        const float moveThreshold = 0.01f;

        if (p1Available && _havePrevWorldP1)
        {
            if (math.distance(worldP1, _prevWorldP1) > moveThreshold)
                movedP1 = true;
        }
        if (p2Available && _havePrevWorldP2)
        {
            if (math.distance(worldP2, _prevWorldP2) > moveThreshold)
                movedP2 = true;
        }

        var ctrlRef = SquidGameController.Instance;
        if (ctrlRef != null && ctrlRef.CurrentStage == SquidStage.Active)
        {
            if (edgeDownP1 || movedP1)
                ctrlRef.NotifyIntroClick(1);
            if (edgeDownP2 || movedP2)
                ctrlRef.NotifyIntroClick(2);
        }

        // During tutorial countdown we want movement but no clicks.
        if (ClickLocked)
        {
            pressedP1 = false;
            edgeDownP1 = false;
            edgeUpP1 = false;

            pressedP2 = false;
            edgeDownP2 = false;
            edgeUpP2 = false;
        }

        // =========================================================
        // 2) Read MeasurementResult for both players
        // =========================================================
        bool hasActualInsideP1 = false;
        bool hasActualInsideP2 = false;
        int idxP1 = -1, idxP2 = -1;

        if (_haveResultP1 && _em.Exists(_resultSingletonP1))
        {
            _lastResultP1 = _em.GetComponentData<MeasurementResult>(_resultSingletonP1);
            hasActualInsideP1 = _lastResultP1.HasActualInRadius;
            idxP1 = _lastResultP1.ClosestActualIndex;
        }

        if (_haveResultP2 && _em.Exists(_resultSingletonP2))
        {
            _lastResultP2 = _em.GetComponentData<MeasurementResultP2>(_resultSingletonP2);
            hasActualInsideP2 = _lastResultP2.HasActualInRadius;
            idxP2 = _lastResultP2.ClosestActualIndex;
        }

        // =========================================================
        // 3) Read actual particle statuses (Unknown / Good / Bad)
        // =========================================================
        ActualParticleStatus[] statusArray = null;
        {
            var qCfg = _em.CreateEntityQuery(
                typeof(ActualParticleSet),
                typeof(ActualParticleStatusElement)
            );
            if (!qCfg.IsEmptyIgnoreFilter)
            {
                var cfgEntity = qCfg.GetSingletonEntity();
                var statusBuf = _em.GetBuffer<ActualParticleStatusElement>(cfgEntity);
                int count = statusBuf.Length;
                statusArray = new ActualParticleStatus[count];
                for (int i = 0; i < count; i++)
                    statusArray[i] = statusBuf[i].Value;
            }
        }

        bool isIdentifiedP1 = false;
        bool isIdentifiedP2 = false;

        if (statusArray != null)
        {
            if (hasActualInsideP1 && idxP1 >= 0 && idxP1 < statusArray.Length)
            {
                var st = statusArray[idxP1];
                isIdentifiedP1 = (st == ActualParticleStatus.Good || st == ActualParticleStatus.Bad);
            }
            if (hasActualInsideP2 && idxP2 >= 0 && idxP2 < statusArray.Length)
            {
                var st = statusArray[idxP2];
                isIdentifiedP2 = (st == ActualParticleStatus.Good || st == ActualParticleStatus.Bad);
            }
        }

        // =========================================================
        // 3.5) Per-player drop windows
        // =========================================================
        float now = Time.time;

        bool p1Dropping = _p1DropIndex >= 0 && now < _p1DropUntilTime;
        bool p2Dropping = _p2DropIndex >= 0 && now < _p2DropUntilTime;

        if (p1Dropping && hasActualInsideP1 && idxP1 == _p1DropIndex)
        {
            hasActualInsideP1 = false;
            isIdentifiedP1 = false;
        }

        if (p2Dropping && hasActualInsideP2 && idxP2 == _p2DropIndex)
        {
            hasActualInsideP2 = false;
            isIdentifiedP2 = false;
        }

        if (_p1DropIndex >= 0 && now >= _p1DropUntilTime)
            _p1DropIndex = -1;
        if (_p2DropIndex >= 0 && now >= _p2DropUntilTime)
            _p2DropIndex = -1;

        // =========================================================
        // 4) Determine per-actual holders, near-holder shrink, assisting and disruption
        // =========================================================

        bool p1ScanningIdent = p1Available && pressedP1 && hasActualInsideP1 && isIdentifiedP1 && idxP1 >= 0;
        bool p2ScanningIdent = p2Available && pressedP2 && hasActualInsideP2 && isIdentifiedP2 && idxP2 >= 0;

        // --- Holder reset on release ---
        if (edgeUpP1 && _p1WasScanningIdentPrev && idxP1 >= 0)
        {
            if (_actualHolderIndexToPlayer.TryGetValue(idxP1, out int holderP1) && holderP1 == 1)
                _actualHolderIndexToPlayer.Remove(idxP1);
        }

        if (edgeUpP2 && _p2WasScanningIdentPrev && idxP2 >= 0)
        {
            if (_actualHolderIndexToPlayer.TryGetValue(idxP2, out int holderP2) && holderP2 == 2)
                _actualHolderIndexToPlayer.Remove(idxP2);
        }

        int holderForIdxP1 = 0;
        int holderForIdxP2 = 0;

        // Register holders per actual index (first come wins)
        if (p1ScanningIdent)
        {
            if (!_actualHolderIndexToPlayer.TryGetValue(idxP1, out holderForIdxP1))
            {
                _actualHolderIndexToPlayer[idxP1] = 1;
                holderForIdxP1 = 1;
            }
        }
        if (p2ScanningIdent)
        {
            if (!_actualHolderIndexToPlayer.TryGetValue(idxP2, out holderForIdxP2))
            {
                _actualHolderIndexToPlayer[idxP2] = 2;
                holderForIdxP2 = 2;
            }
        }

        bool p1IsHolder = p1ScanningIdent && holderForIdxP1 == 1;
        bool p2IsHolder = p2ScanningIdent && holderForIdxP2 == 2;

        float holderRadiusWorld = radius * holderRadiusMultiplier;
        float distP1P2 = 0f;
        if (p1Available && p2Available)
            distP1P2 = math.length(worldP1 - worldP2);

        // --- Disruption: two holders for different actuals collide ---
        bool holdersDifferentActual =
            p1IsHolder && p2IsHolder &&
            idxP1 >= 0 && idxP2 >= 0 &&
            idxP1 != idxP2;

        bool holdersCollide =
            holdersDifferentActual &&
            p1Available && p2Available &&
            distP1P2 <= holderRadiusWorld;

        bool p1Disrupted = false;
        bool p2Disrupted = false;

        if (holdersCollide)
        {
            p1Disrupted = true;
            p2Disrupted = true;

            _actualHolderIndexToPlayer.Remove(idxP1);
            _actualHolderIndexToPlayer.Remove(idxP2);

            p1IsHolder = false;
            p2IsHolder = false;
        }

        // --- Near-holder shrink ---
        bool p1NearHolder = false;
        bool p2NearHolder = false;

        if (p1Available && p2Available && !p1Disrupted && !p2Disrupted)
        {
            if (p1IsHolder && distP1P2 <= holderRadiusWorld * exclusionRadiusMultiplier)
                p2NearHolder = true;

            if (p2IsHolder && distP1P2 <= holderRadiusWorld * exclusionRadiusMultiplier)
                p1NearHolder = true;
        }

        // --- Assisting: near-holder + scanning the SAME identified actual ---
        bool sameIdentIdx =
            hasActualInsideP1 && hasActualInsideP2 &&
            isIdentifiedP1 && isIdentifiedP2 &&
            idxP1 >= 0 && idxP2 >= 0 &&
            idxP1 == idxP2;

        bool p1Assisting = p1NearHolder && pressedP1 && sameIdentIdx && !p1IsHolder && !p1Disrupted;
        bool p2Assisting = p2NearHolder && pressedP2 && sameIdentIdx && !p2IsHolder && !p2Disrupted;

        bool coopSameActual =
            sameIdentIdx &&
            ((p1IsHolder && p2Assisting) || (p2IsHolder && p1Assisting));

        // =========================================================
        // 5) Cursor visuals + progress logic
        // =========================================================

        float p1RadiusScale = 1f;
        float p2RadiusScale = 1f;

        if (!p1RepelActive)
        {
            if (p1IsHolder)
                p1RadiusScale = holderRadiusMultiplier;
            else if (p1NearHolder || p1Disrupted)
                p1RadiusScale = assistRadiusMultiplier;
        }

        if (!p2RepelActive)
        {
            if (p2IsHolder)
                p2RadiusScale = holderRadiusMultiplier;
            else if (p2NearHolder || p2Disrupted)
                p2RadiusScale = assistRadiusMultiplier;
        }

        float coopMinClamp = -1.5f * Mathf.PI;
        float halfClamp = -0.5f * Mathf.PI;

        // Just-started flags
        bool p1JustStartedIdent = p1ScanningIdent && !_p1WasScanningIdentPrev;
        bool p2JustStartedIdent = p2ScanningIdent && !_p2WasScanningIdentPrev;

        // --- Player 1 visuals ---
        if (p1Available && Cursor1 != null && Progress1 != null)
        {
            // Always lerp toward the appropriate radius scale
            Cursor1.Radius = Mathf.Lerp(
                Cursor1.Radius,
                _cursor1BaseRadius * p1RadiusScale,
                radiusLerpSpeed * Time.deltaTime
            );
            Progress1.Radius = Mathf.Lerp(
                Progress1.Radius,
                _progress1BaseRadius * p1RadiusScale,
                radiusLerpSpeed * Time.deltaTime
            );

            // Snap the progress arc to halfway on first grab of an identified actual
            if (p1JustStartedIdent && isIdentifiedP1)
            {
                Progress1.AngRadiansEnd = halfClamp;
                Cursor1.AngRadiansStart = halfClamp;
            }

            if (pressedP1)
            {
                Cursor1.DashType = Shapes.DashType.Angled;
                Cursor1.DashSize = 5f;
                Cursor1.DashSpacing = 2.5f;
                Cursor1.Thickness = 2 * Mathf.PI;

                Cursor1.DashOffset = (Cursor1.DashOffset + RotationSpeed * Time.deltaTime) % 1_000_000f;

                if (hasActualInsideP1)
                {
                    Cursor1.Thickness = 4 * Mathf.PI;
                    Cursor1.DashSize = 7f;

                    if (!isIdentifiedP1)
                    {
                        // Phase 1: identify (first half)
                        Progress1.AngRadiansEnd -= Time.deltaTime * progressSpeed;
                        Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, halfClamp, 0.5f * Mathf.PI);

                        Cursor1.AngRadiansStart -= Time.deltaTime * progressSpeed;
                        Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, halfClamp, 0.5f * Mathf.PI);

                        bool didComplete = Mathf.Approximately(Progress1.AngRadiansEnd, halfClamp);
                        if (didComplete)
                            OnProgressCompleteP1();
                    }
                    else
                    {
                        // Phase 2: already identified
                        if (coopSameActual && (p1IsHolder || p1Assisting))
                        {
                            // Both scanning same identified actual → progress toward full circle
                            Progress1.AngRadiansEnd -= Time.deltaTime * 2 * progressSpeed;
                            Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, coopMinClamp, halfClamp);

                            Cursor1.AngRadiansStart -= Time.deltaTime * 2 * progressSpeed;
                            Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, coopMinClamp, halfClamp);

                            if (Cursor1.AngRadiansStart == coopMinClamp)
                            {
                                // Coop full-scan reached: notify once via P1
                                CoopCompleteP1();

                                // Reset both cursors to neutral half-band
                                if (Cursor1 != null)
                                    Cursor1.AngRadiansStart = 0.5f * Mathf.PI;
                                if (Progress1 != null)
                                    Progress1.AngRadiansEnd = 0.5f * Mathf.PI;
                                if (Cursor2 != null)
                                    Cursor2.AngRadiansStart = 0.5f * Mathf.PI;
                                if (Progress2 != null)
                                    Progress2.AngRadiansEnd = 0.5f * Mathf.PI;

                                p1IsHolder = false;
                                p2IsHolder = false;
                                p1Assisting = false;
                                p2Assisting = false;
                                coopSameActual = false;
                            }

                        }
                        else
                        {
                            // Not in co-op: lerp back toward half circle
                            float target = halfClamp;
                            float step = Time.deltaTime * 4f * progressSpeed;
                            Progress1.AngRadiansEnd = Mathf.MoveTowards(Progress1.AngRadiansEnd, target, step);
                            Cursor1.AngRadiansStart = Mathf.MoveTowards(Cursor1.AngRadiansStart, target, step);
                        }
                    }
                }
                else
                {
                    // No actual under cursor: decay toward neutral half band
                    Progress1.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                    Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, halfClamp, 0.5f * Mathf.PI);

                    Cursor1.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                    Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, halfClamp, 0.5f * Mathf.PI);
                }
            }
            else
            {
                Cursor1.DashType = Shapes.DashType.Basic;
                Cursor1.DashSize = 2f;
                Cursor1.DashSpacing = 3f;
                Cursor1.Thickness = 5f;

                Cursor1.DashOffset = (Cursor1.DashOffset + 0.1f * RotationSpeed * Time.deltaTime) % 1_000_000f;

                // When not pressed, allow full decay over the whole circle
                Progress1.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                Progress1.AngRadiansEnd = Mathf.Clamp(Progress1.AngRadiansEnd, coopMinClamp, 0.5f * Mathf.PI);

                Cursor1.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                Cursor1.AngRadiansStart = Mathf.Clamp(Cursor1.AngRadiansStart, coopMinClamp, 0.5f * Mathf.PI);
            }
        }

        // --- Player 2 visuals ---
        if (p2Available && Cursor2 != null && Progress2 != null)
        {
            // Always lerp toward the appropriate radius scale
            Cursor2.Radius = Mathf.Lerp(
                Cursor2.Radius,
                _cursor2BaseRadius * p2RadiusScale,
                radiusLerpSpeed * Time.deltaTime
            );
            Progress2.Radius = Mathf.Lerp(
                Progress2.Radius,
                _progress2BaseRadius * p2RadiusScale,
                radiusLerpSpeed * Time.deltaTime
            );

            // Snap progress arc when first grabbing an already-identified actual
            if (p2JustStartedIdent && isIdentifiedP2)
            {
                Progress2.AngRadiansEnd = halfClamp;
                Cursor2.AngRadiansStart = halfClamp;
            }

            if (pressedP2)
            {
                Cursor2.DashType = Shapes.DashType.Angled;
                Cursor2.DashSize = 5f;
                Cursor2.DashSpacing = 2.5f;
                Cursor2.Thickness = 2 * Mathf.PI;

                Cursor2.DashOffset = (Cursor2.DashOffset + RotationSpeed * Time.deltaTime) % 1_000_000f;

                if (hasActualInsideP2)
                {
                    Cursor2.Thickness = 4 * Mathf.PI;
                    Cursor2.DashSize = 7f;

                    if (!isIdentifiedP2)
                    {
                        // Phase 1: identify (first half)
                        Progress2.AngRadiansEnd -= Time.deltaTime * progressSpeed;
                        Progress2.AngRadiansEnd = Mathf.Clamp(Progress2.AngRadiansEnd, halfClamp, 0.5f * Mathf.PI);

                        Cursor2.AngRadiansStart -= Time.deltaTime * progressSpeed;
                        Cursor2.AngRadiansStart = Mathf.Clamp(Cursor2.AngRadiansStart, halfClamp, 0.5f * Mathf.PI);

                        bool didComplete2 = Mathf.Approximately(Progress2.AngRadiansEnd, halfClamp);
                        if (didComplete2)
                            OnProgressCompleteP2();
                    }
                    else
                    {
                        // Phase 2: identified
                        if (coopSameActual && (p2IsHolder || p2Assisting))
                        {
                            Progress2.AngRadiansEnd -= Time.deltaTime * 2 * progressSpeed;
                            Progress2.AngRadiansEnd = Mathf.Clamp(Progress2.AngRadiansEnd, coopMinClamp, halfClamp);

                            Cursor2.AngRadiansStart -= Time.deltaTime * 2 * progressSpeed;
                            Cursor2.AngRadiansStart = Mathf.Clamp(Cursor2.AngRadiansStart, coopMinClamp, halfClamp);
                            // Coop completion driven by P1
                        }
                        else
                        {
                            float target = halfClamp;
                            float step = Time.deltaTime * 4f * progressSpeed;
                            Progress2.AngRadiansEnd = Mathf.MoveTowards(Progress2.AngRadiansEnd, target, step);
                            Cursor2.AngRadiansStart = Mathf.MoveTowards(Cursor2.AngRadiansStart, target, step);
                        }
                    }
                }
                else
                {
                    Progress2.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                    Progress2.AngRadiansEnd = Mathf.Clamp(Progress2.AngRadiansEnd, halfClamp, 0.5f * Mathf.PI);

                    Cursor2.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                    Cursor2.AngRadiansStart = Mathf.Clamp(Cursor2.AngRadiansStart, halfClamp, 0.5f * Mathf.PI);
                }
            }
            else
            {
                Cursor2.DashType = Shapes.DashType.Basic;
                Cursor2.DashSize = 2f;
                Cursor2.DashSpacing = 3f;
                Cursor2.Thickness = 5f;

                Cursor2.DashOffset = (Cursor2.DashOffset + 0.1f * RotationSpeed * Time.deltaTime) % 1_000_000f;

                Progress2.AngRadiansEnd += Time.deltaTime * 8f * progressSpeed;
                Progress2.AngRadiansEnd = Mathf.Clamp(Progress2.AngRadiansEnd, coopMinClamp, 0.5f * Mathf.PI);

                Cursor2.AngRadiansStart += Time.deltaTime * 8f * progressSpeed;
                Cursor2.AngRadiansStart = Mathf.Clamp(Cursor2.AngRadiansStart, coopMinClamp, 0.5f * Mathf.PI);
            }
        }

        // =========================================================
        // 6) Build & send ClickRequests with scaled radii / forces
        // =========================================================

        // --- Player 1 request ---
        if (p1Available)
        {
            float radiusP1 = radius;
            float exclMulP1 = exclusionRadiusMultiplier;
            float innerRadiusP1 = innerRadius;
            float pushP1 = pushStrength;
            float innerPushP1 = innerPushStrength;

            if (!p1RepelActive)
            {
                if (p1IsHolder)
                {
                    radiusP1 *= holderRadiusMultiplier;
                    innerRadiusP1 *= holderRadiusMultiplier;
                    exclMulP1 *= holderRadiusMultiplier;
                    innerPushP1 *= holderInnerStrengthMultiplier; // reduce inner pull while holding
                }
                else if (p1Assisting && pressedP1)
                {
                    // Assisting final scan: small radius, no force applied
                    radiusP1 *= assistRadiusMultiplier;
                    innerRadiusP1 *= assistRadiusMultiplier;
                    exclMulP1 *= assistRadiusMultiplier;
                    pushP1 = 0f;
                    innerPushP1 = 0f;
                }
                else if (p1NearHolder || p1Disrupted)
                {
                    radiusP1 *= assistRadiusMultiplier;
                    innerRadiusP1 *= assistRadiusMultiplier;
                    exclMulP1 *= assistRadiusMultiplier;
                }
            }

            if (_p1RepelTimer > 0f)
            {
                innerPushP1 = Mathf.Abs(innerPushStrength); // ensure outward
            }

            var reqP1 = new ClickRequest
            {
                WorldPos = worldP1,
                Radius = radiusP1,
                PushStrength = pushP1,
                IsPressed = pressedP1,
                EdgeDown = edgeDownP1,
                EdgeUp = edgeUpP1,
                ExclusionRadiusMultiplier = exclMulP1,
                InnerRadius = innerRadiusP1,
                InnerPushStrength = innerPushP1
            };
            _em.SetComponentData(_clickSingletonP1, reqP1);

            if (logEvents && (edgeDownP1 || edgeUpP1 || pressedP1 != _prevPressedP1))
            {
                Debug.Log($"[Click P1] pressed={pressedP1} down={edgeDownP1} up={edgeUpP1} " +
                          $"world={worldP1} R={radiusP1} outerPush={pushP1} innerR={innerRadiusP1} innerPush={innerPushP1} " +
                          $"holder={p1IsHolder} assisting={p1Assisting} nearHolder={p1NearHolder} disrupted={p1Disrupted} " +
                          $"repelTimer={_p1RepelTimer} dropping={(p1Dropping ? _p1DropIndex : -1)} " +
                          $"justStartedIdent={p1JustStartedIdent}");
            }

            _prevPressedP1 = pressedP1;
        }

        // --- Player 2 request ---
        if (p2Available)
        {
            float radiusP2 = radius;
            float exclMulP2 = exclusionRadiusMultiplier;
            float innerRadiusP2 = innerRadius;
            float pushP2 = pushStrength;
            float innerPushP2 = innerPushStrength;

            if (!p2RepelActive)
            {
                if (p2IsHolder)
                {
                    radiusP2 *= holderRadiusMultiplier;
                    innerRadiusP2 *= holderRadiusMultiplier;
                    exclMulP2 *= holderRadiusMultiplier;
                    innerPushP2 *= holderInnerStrengthMultiplier;
                }
                else if (p2Assisting && pressedP2)
                {
                    radiusP2 *= assistRadiusMultiplier;
                    innerRadiusP2 *= assistRadiusMultiplier;
                    exclMulP2 *= assistRadiusMultiplier;
                    pushP2 = 0f;
                    innerPushP2 = 0f;
                }
                else if (p2NearHolder || p2Disrupted)
                {
                    radiusP2 *= assistRadiusMultiplier;
                    innerRadiusP2 *= assistRadiusMultiplier;
                    exclMulP2 *= assistRadiusMultiplier;
                }
            }

            if (_p2RepelTimer > 0f)
            {
                innerPushP2 = Mathf.Abs(innerPushStrength);
            }

            var reqP2 = new ClickRequestP2
            {
                WorldPos = worldP2,
                Radius = radiusP2,
                PushStrength = pushP2,
                IsPressed = pressedP2,
                EdgeDown = edgeDownP2,
                EdgeUp = edgeUpP2,
                ExclusionRadiusMultiplier = exclMulP2,
                InnerRadius = innerRadiusP2,
                InnerPushStrength = innerPushP2
            };
            _em.SetComponentData(_clickSingletonP2, reqP2);

            if (logEvents && (edgeDownP2 || edgeUpP2 || pressedP2 != _prevPressedP2))
            {
                Debug.Log($"[Click P2] pressed={pressedP2} down={edgeDownP2} up={edgeUpP2} " +
                          $"world={worldP2} R={radiusP2} outerPush={pushP2} innerR={innerRadiusP2} innerPush={innerPushP2} " +
                          $"holder={p2IsHolder} assisting={p2Assisting} nearHolder={p2NearHolder} disrupted={p2Disrupted} " +
                          $"repelTimer={_p2RepelTimer} dropping={(p2Dropping ? _p2DropIndex : -1)} " +
                          $"justStartedIdent={p2JustStartedIdent}");
            }

            _prevPressedP2 = pressedP2;
        }

        // Store for next frame
        _p1WasScanningIdentPrev = p1ScanningIdent;
        _p2WasScanningIdentPrev = p2ScanningIdent;

        if (p1Available)
        {
            _prevWorldP1 = worldP1;
            _havePrevWorldP1 = true;
        }
        if (p2Available)
        {
            _prevWorldP2 = worldP2;
            _havePrevWorldP2 = true;
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

    public void FlipPlayers()
    {
        playersFlipped = !playersFlipped;
    }
}
