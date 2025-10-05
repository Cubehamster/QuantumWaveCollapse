// OrbitalPresetCycler.cs
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine.InputSystem;

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

    // runtime
    EntityManager _em;
    EntityQuery _orbQ;
    bool _ready;
    int _idx;

    InputAction _prevAction;
    InputAction _nextAction;

    void OnEnable()
    {
        // Build input first (so keys work the moment we’re ready)
        _prevAction = new InputAction("PrevPreset", InputActionType.Button);
        _nextAction = new InputAction("NextPreset", InputActionType.Button);
        foreach (var path in prevBinding.Split(',')) _prevAction.AddBinding(path.Trim());
        foreach (var path in nextBinding.Split(',')) _nextAction.AddBinding(path.Trim());
        _prevAction.performed += _ => { if (_ready) Step(-1); };
        _nextAction.performed += _ => { if (_ready) Step(+1); };
        _prevAction.Enable();
        _nextAction.Enable();

        TryInit();
    }

    void TryInit()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        _em = world.EntityManager;
        _orbQ = _em.CreateEntityQuery(ComponentType.ReadWrite<OrbitalParams2D>());

        if (_orbQ.IsEmptyIgnoreFilter) return; // not ready yet (SubScene may still be streaming)

        // Seed index from current Kind/Type
        var e = GetOrbitalEntity();
        var op = _em.GetComponentData<OrbitalParams2D>(e);

        _idx = 0;
        for (int i = 0; i < kOrder.Length; i++)
        {
            if (op.Kind == kOrder[i] || op.Type == kOrder[i]) { _idx = i; break; }
        }

        _ready = true;
        Debug.Log("[OrbitalPresetCycler] Ready. Use Left/Right (or A/D) to switch orbitals.");
    }

    void Update()
    {
        // If we weren’t ready on enable, keep checking until the orbital entity exists.
        if (!_ready) TryInit();
        // No per-frame work needed once ready; input is event-driven.
    }

    void OnDisable()
    {
        _prevAction?.Disable();
        _nextAction?.Disable();
        _prevAction?.Dispose();
        _nextAction?.Dispose();
        _prevAction = null;
        _nextAction = null;
        _ready = false;
    }

    Entity GetOrbitalEntity()
    {
        if (_orbQ.CalculateEntityCount() == 1)
            return _orbQ.GetSingletonEntity();

        using var arr = _orbQ.ToEntityArray(Allocator.Temp);
        return arr[0];
    }

    void Step(int dir)
    {
        if (_orbQ.IsEmptyIgnoreFilter) return;

        var e = GetOrbitalEntity();
        var op = _em.GetComponentData<OrbitalParams2D>(e);

        _idx = (_idx + dir + kOrder.Length) % kOrder.Length;
        var kind = kOrder[_idx];

        op.Kind = kind;
        op.Type = kind;       // keep alias in sync
        op.A0 = A0For(kind);

        if (autoAlignAngle)
        {
            // simple hints
            if (kind == OrbitalKind2D.TwoPY || kind == OrbitalKind2D.FourF_Y_5Y2_3R2)
                op.Angle = math.radians(90f);
            else
                op.Angle = 0f;
        }

        _em.SetComponentData(e, op);
        Debug.Log($"[OrbitalPresetCycler] Kind={kind}  A0={op.A0:0.##}  Angle={math.degrees(op.Angle):0.#}°");
    }
}
