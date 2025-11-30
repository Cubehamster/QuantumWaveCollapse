using System;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine.InputSystem;
using TMPro;

public sealed class OrbitalPresetCycler : MonoBehaviour
{
    [Header("Bindings (New Input System)")]
    [Tooltip("Press to go to the previous orbital")]
    public string prevBinding = "<Keyboard>/leftArrow, <Keyboard>/a, <Gamepad>/dpad/left, <Gamepad>/leftShoulder";

    [Tooltip("Press to go to the next orbital")]
    public string nextBinding = "<Keyboard>/rightArrow, <Keyboard>/d, <Gamepad>/dpad/right, <Gamepad>/rightShoulder";

    [Header("Optional")]
    [Tooltip("If true, auto-set Angle for some orbitals (e.g. pX=0°, pY=90°).")]
    public bool autoAlignAngle = true;

    [Header("Idle mode")]
    [Tooltip("Fallback interval used if EnterIdleMode() is called with <= 0.")]
    public float defaultIdleInterval = 10f;

    public TextMeshProUGUI Orbitalname;
    public TextMeshProUGUI OrbitalnameFlipped;

    // Cycle order
    static readonly OrbitalKind2D[] kOrder = new[]
    {
        OrbitalKind2D.OneS,              // (1,0,0)
        OrbitalKind2D.TwoS,              // (2,0,0)
        OrbitalKind2D.TwoPX,             // (2,1,±1) X
        OrbitalKind2D.TwoPY,             // (2,1,±1) Y
        OrbitalKind2D.ThreeS,            // (3,0,0)
        OrbitalKind2D.ThreePX,           // (3,1,±1) X
        OrbitalKind2D.FourPX,            // (4,1,±1) X
        OrbitalKind2D.FourD_X2MinusY2,   // (4,2)
        OrbitalKind2D.FourD_XY,          // (4,2)
        OrbitalKind2D.FourF_Cos3Phi,     // (4,3)
        OrbitalKind2D.FourF_X_5X2_3R2,   // (4,3)
        OrbitalKind2D.FourF_Y_5Y2_3R2,   // (4,3)
    };

    // Hand-tuned A0 per kind (world units)
    static float A0For(OrbitalKind2D kind)
    {
        switch (kind)
        {
            case OrbitalKind2D.OneS: return 40f;
            case OrbitalKind2D.TwoS: return 40f;
            case OrbitalKind2D.TwoPX: return 40f;
            case OrbitalKind2D.TwoPY: return 40f;
            case OrbitalKind2D.ThreeS: return 30f;
            case OrbitalKind2D.ThreePX: return 25f;
            case OrbitalKind2D.FourPX: return 15f;
            case OrbitalKind2D.FourD_X2MinusY2: return 12f;
            case OrbitalKind2D.FourD_XY: return 12f;
            case OrbitalKind2D.FourF_Cos3Phi: return 9f;
            case OrbitalKind2D.FourF_X_5X2_3R2: return 10f;
            case OrbitalKind2D.FourF_Y_5Y2_3R2: return 6f;
            default: return 40f;
        }
    }

    static float ExposureFor(OrbitalKind2D kind)
    {
        switch (kind)
        {
            case OrbitalKind2D.OneS: return 40f;
            case OrbitalKind2D.TwoS: return 40f;
            case OrbitalKind2D.TwoPX: return 40f;
            case OrbitalKind2D.TwoPY: return 40f;
            case OrbitalKind2D.ThreeS: return 30f;
            case OrbitalKind2D.ThreePX: return 25f;
            case OrbitalKind2D.FourPX: return 15f;
            case OrbitalKind2D.FourD_X2MinusY2: return 12f;
            case OrbitalKind2D.FourD_XY: return 12f;
            case OrbitalKind2D.FourF_Cos3Phi: return 9f;
            case OrbitalKind2D.FourF_X_5X2_3R2: return 10f;
            case OrbitalKind2D.FourF_Y_5Y2_3R2: return 6f;
            default: return 40f;
        }
    }

    static string GetOrbitalLabel(OrbitalKind2D kind)
    {
        switch (kind)
        {
            case OrbitalKind2D.OneS: return "1s";
            case OrbitalKind2D.TwoS: return "2s";
            case OrbitalKind2D.TwoPX: return "2p(x)";
            case OrbitalKind2D.TwoPY: return "2p(y)";
            case OrbitalKind2D.ThreeS: return "3s";
            case OrbitalKind2D.ThreePX: return "3p(x)";
            case OrbitalKind2D.FourPX: return "4p(x)";
            case OrbitalKind2D.FourD_X2MinusY2: return "4d(x2-y2)";
            case OrbitalKind2D.FourD_XY: return "4d(xy)";
            case OrbitalKind2D.FourF_Cos3Phi: return "4f(z3)";
            case OrbitalKind2D.FourF_X_5X2_3R2: return "4f(x3)";
            case OrbitalKind2D.FourF_Y_5Y2_3R2: return "4f(y3)";
            default: return kind.ToString();
        }
    }

    // runtime
    EntityManager _em;
    EntityQuery _orbQ;
    bool _ready;
    int _idx;

    InputAction _prevAction;
    InputAction _nextAction;

    // Random-cycle tracking: which orbitals have already been visited
    bool[] _seenRandom;

    // Idle mode runtime
    bool _idleMode;
    float _idleInterval;
    float _idleNextTime;

    // Pending forced preset (for calls before ECS is ready)
    bool _hasPendingForce;
    OrbitalKind2D _pendingForcedKind;

    // Global instance so ECS systems / controller can talk to us
    public static OrbitalPresetCycler Instance { get; private set; }

    void OnEnable()
    {
        Instance = this;

        // Build input (so keys work once we’re ready)
        _prevAction = new InputAction("PrevPreset", InputActionType.Button);
        _nextAction = new InputAction("NextPreset", InputActionType.Button);
        foreach (var path in prevBinding.Split(','))
            _prevAction.AddBinding(path.Trim());
        foreach (var path in nextBinding.Split(','))
            _nextAction.AddBinding(path.Trim());

        _prevAction.performed += _ => { if (_ready) Step(-1); };
        _nextAction.performed += _ => { if (_ready) Step(+1); };
        _prevAction.Enable();
        _nextAction.Enable();

        TryInit();
    }

    void Update()
    {
        // Late-initialise once the orbital SubScene is loaded.
        if (!_ready)
            TryInit();

        // Idle: time-based random stepping, independent of spawns.
        if (_ready && _idleMode && Time.time >= _idleNextTime)
        {
            StepRandomFromSpawn();  // reuses existing random-cycle logic
            _idleNextTime = Time.time + _idleInterval;
        }
    }

    void OnDisable()
    {
        if (Instance == this)
            Instance = null;

        _prevAction?.Disable();
        _nextAction?.Disable();
        _prevAction?.Dispose();
        _nextAction?.Dispose();
        _prevAction = null;
        _nextAction = null;
        _ready = false;
    }

    void TryInit()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        _em = world.EntityManager;
        _orbQ = _em.CreateEntityQuery(ComponentType.ReadWrite<OrbitalParams2D>());

        if (_orbQ.IsEmptyIgnoreFilter)
            return; // SubScene might still be streaming

        // Seed index from current Kind/Type
        var e = GetOrbitalEntity();
        var op = _em.GetComponentData<OrbitalParams2D>(e);

        _idx = 0;
        for (int i = 0; i < kOrder.Length; i++)
        {
            if (op.Kind == kOrder[i] || op.Type == kOrder[i])
            {
                _idx = i;
                break;
            }
        }

        // Init seen array and mark the current one as seen.
        if (_seenRandom == null || _seenRandom.Length != kOrder.Length)
            _seenRandom = new bool[kOrder.Length];

        for (int i = 0; i < _seenRandom.Length; i++)
            _seenRandom[i] = false;

        if (_idx >= 0 && _idx < _seenRandom.Length)
            _seenRandom[_idx] = true;

        // Set initial UI label based on current orbital
        if (Orbitalname != null && _idx >= 0 && _idx < kOrder.Length)
            Orbitalname.text = GetOrbitalLabel(kOrder[_idx]);

        // Set initial UI label based on current orbital
        if (OrbitalnameFlipped != null && _idx >= 0 && _idx < kOrder.Length)
            OrbitalnameFlipped.text = GetOrbitalLabel(kOrder[_idx]);

        _ready = true;
        Debug.Log("[OrbitalPresetCycler] Ready. Use Left/Right (or A/D) to switch orbitals.");

        // If someone called ForcePreset() before we were ready, honour it now.
        if (_hasPendingForce)
        {
            int forcedIndex = Array.IndexOf(kOrder, _pendingForcedKind);
            if (forcedIndex < 0) forcedIndex = 0;
            ApplyIndex(forcedIndex, markSeenRandom: false);
            _hasPendingForce = false;
        }
    }

    Entity GetOrbitalEntity()
    {
        if (_orbQ.CalculateEntityCount() == 1)
            return _orbQ.GetSingletonEntity();

        using var arr = _orbQ.ToEntityArray(Allocator.Temp);
        return arr[0];
    }

    // --------------------------------------------------------------------
    // Core apply function used by both keyboard stepping and random stepping
    // --------------------------------------------------------------------
    void ApplyIndex(int newIndex, bool markSeenRandom)
    {
        if (!_ready || _orbQ.IsEmptyIgnoreFilter)
            return;

        _idx = (newIndex % kOrder.Length + kOrder.Length) % kOrder.Length;
        var kind = kOrder[_idx];

        var e = GetOrbitalEntity();
        var op = _em.GetComponentData<OrbitalParams2D>(e);

        op.Kind = kind;
        op.Type = kind;        // keep alias in sync
        op.A0 = A0For(kind);
        op.Exposure = ExposureFor(kind);

        if (autoAlignAngle)
        {
            //// simple hints
            //if (kind == OrbitalKind2D.TwoPY || kind == OrbitalKind2D.FourF_Y_5Y2_3R2)
            //    op.Angle = math.radians(90f);
            //else
            //    op.Angle = 0f;
        }

        _em.SetComponentData(e, op);

        // Update UI label
        if (Orbitalname != null)
            Orbitalname.text = GetOrbitalLabel(kind);
        if (OrbitalnameFlipped != null)
            OrbitalnameFlipped.text = GetOrbitalLabel(kind);

        if (markSeenRandom)
        {
            if (_seenRandom == null || _seenRandom.Length != kOrder.Length)
                _seenRandom = new bool[kOrder.Length];
            if (_idx >= 0 && _idx < _seenRandom.Length)
                _seenRandom[_idx] = true;
        }

        Debug.Log($"[OrbitalPresetCycler] Kind={kind}  A0={op.A0:0.##}  Angle={math.degrees(op.Angle):0.#}°");
    }

    void Step(int dir)
    {
        if (!_ready) return;

        int newIndex = _idx + dir;
        ApplyIndex(newIndex, markSeenRandom: true);
    }

    // --------------------------------------------------------------------
    //  Spawn-driven random stepping  (as you had before)
    // --------------------------------------------------------------------
    public void StepRandomFromSpawn()
    {
        if (!_ready) return;

        if (_seenRandom == null || _seenRandom.Length != kOrder.Length)
            _seenRandom = new bool[kOrder.Length];

        int total = kOrder.Length;

        // Build candidate list of unseen orbitals
        int[] candidates = new int[total];
        int candidateCount = 0;

        for (int i = 0; i < total; i++)
        {
            if (!_seenRandom[i])
            {
                candidates[candidateCount++] = i;
            }
        }

        // If we've seen them all, reset the cycle but keep current as seen
        if (candidateCount == 0)
        {
            for (int i = 0; i < total; i++)
                _seenRandom[i] = false;

            if (_idx >= 0 && _idx < total)
                _seenRandom[_idx] = true;

            // Rebuild candidate list
            candidateCount = 0;
            for (int i = 0; i < total; i++)
            {
                if (!_seenRandom[i])
                    candidates[candidateCount++] = i;
            }
        }

        if (candidateCount == 0)
            return; // safety

        int pick = UnityEngine.Random.Range(0, candidateCount);
        int newIndex = candidates[pick];

        ApplyIndex(newIndex, markSeenRandom: true);
    }

    // ====================================================================
    //  STATIC API  (used by SquidGameController & ActualParticlePoolSystem)
    // ====================================================================

    /// <summary>
    /// Called when entering Idle:
    /// - enables time-based random orbital stepping;
    /// - one random step every intervalSeconds (or default if <= 0).
    /// </summary>
    public static void EnterIdleMode(float intervalSeconds)
    {
        var inst = Instance;
        if (inst == null)
        {
            Debug.LogWarning("[OrbitalPresetCycler] EnterIdleMode() called but no Instance in scene.");
            return;
        }

        inst._idleMode = true;
        inst._idleInterval = (intervalSeconds > 0f)
            ? intervalSeconds
            : Mathf.Max(0.1f, inst.defaultIdleInterval);
        inst._idleNextTime = Time.time + inst._idleInterval;

        Debug.Log($"[OrbitalPresetCycler] Idle mode ON, interval={inst._idleInterval:0.00}s");
    }

    /// <summary>
    /// Called when leaving Idle (Intro/Playing):
    /// - disables time-based stepping;
    /// - spawn-driven stepping resumes (via RequestRandomStepFromSpawns).
    /// </summary>
    public static void ExitIdleMode()
    {
        var inst = Instance;
        if (inst == null)
        {
            Debug.LogWarning("[OrbitalPresetCycler] ExitIdleMode() called but no Instance in scene.");
            return;
        }

        inst._idleMode = false;
        Debug.Log("[OrbitalPresetCycler] Idle mode OFF");
    }

    /// <summary>
    /// Called when we need to force a specific orbital kind by name,
    /// e.g. "OneS", "TwoPX", "FourD_XY".
    /// This is safe to call BEFORE ECS is ready; it will be applied later.
    /// </summary>
    public static void ForcePreset(string presetId)
    {
        var inst = Instance;
        if (inst == null)
        {
            Debug.LogWarning("[OrbitalPresetCycler] ForcePreset() called but no Instance in scene.");
            return;
        }

        if (string.IsNullOrEmpty(presetId))
        {
            Debug.LogWarning("[OrbitalPresetCycler] ForcePreset() with empty presetId.");
            return;
        }

        if (!Enum.TryParse<OrbitalKind2D>(presetId, ignoreCase: true, out var kind))
        {
            Debug.LogWarning($"[OrbitalPresetCycler] ForcePreset: '{presetId}' is not a valid OrbitalKind2D.");
            return;
        }

        // If we aren't ready yet, just remember this choice and apply it in TryInit()
        if (!inst._ready)
        {
            inst._pendingForcedKind = kind;
            inst._hasPendingForce = true;
            Debug.Log($"[OrbitalPresetCycler] Queued ForcePreset({kind}) until ECS is ready.");
            return;
        }

        int idx = Array.IndexOf(kOrder, kind);
        if (idx < 0)
        {
            Debug.LogWarning($"[OrbitalPresetCycler] ForcePreset: kind '{kind}' not in kOrder, using first entry.");
            idx = 0;
        }

        inst.ApplyIndex(idx, markSeenRandom: false);
    }

    /// <summary>
    /// Called from gameplay (e.g. ActualParticlePoolSystem) when spawns
    /// want to trigger a new orbital. No-op while Idle mode is active.
    /// </summary>
    public static void RequestRandomStepFromSpawns()
    {
        var inst = Instance;
        if (inst == null)
        {
            Debug.LogWarning("[OrbitalPresetCycler] RequestRandomStepFromSpawns() but no Instance.");
            return;
        }

        if (inst._idleMode)
            return; // idle mode uses time-based stepping instead

        inst.StepRandomFromSpawn();
    }
}
