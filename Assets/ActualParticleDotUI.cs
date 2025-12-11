using BoingKit;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws UI dots over the density RawImage for each pool slot of "actual particles".
/// 
/// Key points:
/// - One dot per pool slot (refBuf index), no compression.
/// - A slot is "active" if ActualParticleRef[i].Walker != Entity.Null.
/// - MeasurementResult.ClosestActualIndex is assumed to be a slot index.
/// - Each slot has its own fade state:
///     * 0.5s fully invisible
///     * then 0.5s fade-in to full alpha.
/// - A fade restarts whenever a slot gets a *new* walker (including reuse after coop).
/// </summary>
[DisallowMultipleComponent]
public sealed class ActualParticleDotUI : MonoBehaviour
{
    [Header("Where to draw (same rect as your density RawImage)")]
    public RectTransform overlayRect;

    [Header("Dot look")]
    [Min(1f)] public float dotDiameter = 10f;

    [Header("Status colors")]
    public Color unknownColor = new Color(1f, 0.8f, 0.2f, 0.95f);
    public Color goodColor = new Color(0.2f, 1f, 0.2f, 0.95f);
    public Color badColor = new Color(1f, 0.1f, 0.1f, 0.95f);

    [Header("Highlight (applies to P1 and P2)")]
    public Color highlightColor = new Color(1f, 1f, 1f, 1f);
    public float highlightSizeMultiplier = 1.4f;

    [Header("Behavior")]
    public bool visible = true;

    [Tooltip("If > 0, clamp max number of pool slots shown (mostly for debugging). " +
             "Dots are kept 1:1 with slots [0..maxDots-1].")]
    public int maxDots = 0;

    [Header("Spawn fade")]
    [Tooltip("Duration a newly spawned/reused slot is completely invisible.")]
    public float invisibleDuration = 0.5f;
    [Tooltip("Duration after invisible phase during which alpha lerps to 1.")]
    public float fadeDuration = 0.5f;

    // ECS
    EntityManager _em;
    EntityQuery _boundsQ;
    EntityQuery _cfgQ;
    EntityQuery _legacyQ;

    EntityQuery _clickP1Q;
    EntityQuery _clickP2Q;

    EntityQuery _resultP1Q;
    EntityQuery _resultP2Q;

    // Dot pool (1 dot per *slot index*, no compression)
    readonly List<RectTransform> _dotRects = new();
    readonly List<Shapes.Disc> _dotDiscs = new();
    readonly List<Shapes.Disc> _dotDiscOutline = new();

    struct FadeState
    {
        public float Age; // seconds since this walker was assigned to this slot
    }

    // Per-slot state (indexed by pool slot index)
    readonly Dictionary<int, FadeState> _fadePerSlot = new();
    readonly Dictionary<int, Entity> _lastWalkerPerSlot = new();

    void OnEnable()
    {
        if (overlayRect == null)
        {
            Debug.LogError("Assign overlayRect.");
            enabled = false;
            return;
        }

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            enabled = false;
            return;
        }

        _em = world.EntityManager;

        _boundsQ = _em.CreateEntityQuery(ComponentType.ReadOnly<SimBounds2D>());

        _cfgQ = _em.CreateEntityQuery(
            ComponentType.ReadOnly<ActualParticleSet>(),
            ComponentType.ReadOnly<ActualParticleRef>(),
            ComponentType.ReadOnly<ActualParticlePositionElement>()
        );

        _legacyQ = _em.CreateEntityQuery(ComponentType.ReadOnly<ActualParticlePosition>());

        _clickP1Q = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequest>());
        _clickP2Q = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequestP2>());
        _resultP1Q = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResult>());
        _resultP2Q = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResultP2>());

        ApplyVisibility();
    }

    void OnDisable()
    {
        ClearPool();
        _fadePerSlot.Clear();
        _lastWalkerPerSlot.Clear();
    }

    void Update()
    {
        // Toggle with "C" for debug if desired
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
        {
            visible = !visible;
            ApplyVisibility();
        }

        if (!visible) return;
        if (_boundsQ.IsEmptyIgnoreFilter) return;

        var bounds = _boundsQ.GetSingleton<SimBounds2D>();
        float2 minB = bounds.Center - bounds.Extents;
        float2 sizeB = math.max(new float2(1e-6f), bounds.Extents * 2f);

        // ----------------------------------------------------------
        // 1) Read scanning states from BOTH players
        // ----------------------------------------------------------
        bool scanningP1 = false;
        bool scanningP2 = false;
        int highlightIndexP1 = -1;
        int highlightIndexP2 = -1;

        if (!_clickP1Q.IsEmptyIgnoreFilter)
        {
            var c1 = _clickP1Q.GetSingleton<ClickRequest>();
            scanningP1 = c1.IsPressed;
        }
        if (!_resultP1Q.IsEmptyIgnoreFilter)
        {
            var r1 = _resultP1Q.GetSingleton<MeasurementResult>();
            if (r1.HasActualInRadius && scanningP1)
                highlightIndexP1 = r1.ClosestActualIndex; // slot index
        }

        if (!_clickP2Q.IsEmptyIgnoreFilter)
        {
            var c2 = _clickP2Q.GetSingleton<ClickRequestP2>();
            scanningP2 = c2.IsPressed;
        }
        if (!_resultP2Q.IsEmptyIgnoreFilter)
        {
            var r2 = _resultP2Q.GetSingleton<MeasurementResultP2>();
            if (r2.HasActualInRadius && scanningP2)
                highlightIndexP2 = r2.ClosestActualIndex; // slot index
        }

        // ----------------------------------------------------------
        // 2) Collect pool slot count + positions + statuses
        //    We keep one dot per *slot*, not per active actual.
        // ----------------------------------------------------------
        float2[] posArray = null;
        ActualParticleStatus[] statusArray = null;
        int poolLen = 0;

        if (!_cfgQ.IsEmptyIgnoreFilter)
        {
            using var cfgEnts = _cfgQ.ToEntityArray(Allocator.Temp);
            var cfgEnt = cfgEnts[0];

            var refBuf = _em.GetBuffer<ActualParticleRef>(cfgEnt);
            var posBuf = _em.GetBuffer<ActualParticlePositionElement>(cfgEnt);
            DynamicBuffer<ActualParticleStatusElement> statBuf =
                _em.HasBuffer<ActualParticleStatusElement>(cfgEnt)
                    ? _em.GetBuffer<ActualParticleStatusElement>(cfgEnt)
                    : default;

            poolLen = math.min(refBuf.Length, posBuf.Length);
            if (poolLen <= 0)
            {
                SetPoolSize(0);
                return;
            }

            posArray = new float2[poolLen];
            statusArray = new ActualParticleStatus[poolLen];

            for (int i = 0; i < poolLen; i++)
            {
                posArray[i] = posBuf[i].Value;
                ActualParticleStatus st = ActualParticleStatus.Unknown;
                if (statBuf.IsCreated && i < statBuf.Length)
                    st = statBuf[i].Value;
                statusArray[i] = st;
            }

            // Update fade states based on current Walker assignment
            UpdateFadeStates(refBuf, poolLen);
        }
        else if (!_legacyQ.IsEmptyIgnoreFilter)
        {
            // Legacy single-actual fallback
            var ap = _legacyQ.GetSingleton<ActualParticlePosition>();
            poolLen = 1;
            posArray = new[] { ap.Value };
            statusArray = new[] { ActualParticleStatus.Unknown };
        }
        else
        {
            SetPoolSize(0);
            return;
        }

        // ----------------------------------------------------------
        // 3) Draw dots (one per slot index, up to maxDots)
        // ----------------------------------------------------------
        int drawCount = poolLen;
        if (maxDots > 0)
            drawCount = Mathf.Min(drawCount, maxDots);

        SetPoolSize(drawCount);
        Vector2 canvasSize = overlayRect.rect.size;

        for (int slot = 0; slot < drawCount; slot++)
        {
            var rt = _dotRects[slot];
            var disc = _dotDiscs[slot];
            var outline = _dotDiscOutline[slot];

            bool hasPosition = (posArray != null && slot < posArray.Length);
            Entity walker = Entity.Null;

            // Get walker for active check
            if (!_cfgQ.IsEmptyIgnoreFilter)
            {
                var cfgEnt = _cfgQ.GetSingletonEntity();
                var refBuf = _em.GetBuffer<ActualParticleRef>(cfgEnt);
                if (slot < refBuf.Length)
                    walker = refBuf[slot].Walker;
            }

            // If inactive slot → hide dot completely
            if (walker == Entity.Null)
            {
                disc.Color = Color.clear;
                outline.Color = Color.clear;
                continue;
            }

            float2 p = posArray[slot];
            float2 uv = (p - minB) / sizeB;
            Vector2 anchor = new Vector2(uv.x * canvasSize.x, uv.y * canvasSize.y);
            rt.anchoredPosition = anchor;

            ActualParticleStatus st = statusArray?[slot] ?? ActualParticleStatus.Unknown;

            // Base color for status
            Color baseColor = st switch
            {
                ActualParticleStatus.Good => goodColor,
                ActualParticleStatus.Bad => badColor,
                _ => unknownColor
            };

            // Fade calculation
            float alphaFactor = 1f;

            if (_fadePerSlot.TryGetValue(slot, out var fs))
            {
                // sentinel for inactive
                if (fs.Age < 0f)
                {
                    alphaFactor = 0f;
                }
                else if (fs.Age < invisibleDuration)
                {
                    alphaFactor = 0f;
                }
                else if (fs.Age < invisibleDuration + fadeDuration)
                {
                    float t = (fs.Age - invisibleDuration) / fadeDuration;
                    alphaFactor = Mathf.Clamp01(t);
                }
                else
                {
                    alphaFactor = 1f;
                }
            }

            baseColor.a *= alphaFactor;

            // Highlight for scanning
            bool highlight =
                (slot == highlightIndexP1 && scanningP1) ||
                (slot == highlightIndexP2 && scanningP2);

            Color finalColor = highlight ? highlightColor : baseColor;
            float radiusMul = highlight ? highlightSizeMultiplier : 1f;

            disc.Color = finalColor;

            outline.Color = new Color(1,1,1, alphaFactor);
            disc.Radius = 0.5f * dotDiameter * radiusMul;

        }
    }

    // ----------------- Fade state update -----------------

    void UpdateFadeStates(DynamicBuffer<ActualParticleRef> refBuf, int poolLen)
    {
        float dt = Time.deltaTime;

        for (int i = 0; i < poolLen; i++)
        {
            Entity walker = refBuf[i].Walker;

            if (walker == Entity.Null)
            {
                // Slot is inactive → fully reset fade & last walker
                _lastWalkerPerSlot.Remove(i);
                _fadePerSlot[i] = new FadeState { Age = -999f }; // sentinel = invisible
                continue;
            }

            // New walker assigned?
            if (!_lastWalkerPerSlot.TryGetValue(i, out var oldW) || oldW != walker)
            {
                _lastWalkerPerSlot[i] = walker;
                _fadePerSlot[i] = new FadeState { Age = 0f }; // restart fade
            }
            else
            {
                // Same walker → age increases
                var fs = _fadePerSlot[i];
                if (fs.Age >= 0f) // valid fade
                    fs.Age += dt;
                _fadePerSlot[i] = fs;
            }
        }

        // cleanup any indices > poolLen (rare case)
        List<int> cleanup = new();
        foreach (int key in _fadePerSlot.Keys)
            if (key >= poolLen) cleanup.Add(key);

        foreach (int k in cleanup)
        {
            _fadePerSlot.Remove(k);
            _lastWalkerPerSlot.Remove(k);
        }
    }

    // ----------------- Helpers -----------------

    void ApplyVisibility()
    {
        if (overlayRect != null)
            overlayRect.gameObject.SetActive(visible);
    }

    void ClearPool()
    {
        foreach (var rt in _dotRects)
            if (rt != null)
                Destroy(rt.gameObject);

        _dotRects.Clear();
        _dotDiscs.Clear();
    }

    void SetPoolSize(int needed)
    {
        // Make sure we have one dot object per *slot index* [0..needed-1]
        while (_dotRects.Count < needed)
            CreateDot();

        for (int i = 0; i < _dotRects.Count; i++)
        {
            bool active = i < needed;
            var go = _dotRects[i].gameObject;
            if (go.activeSelf != active)
                go.SetActive(active);
        }
    }

    void CreateDot()
    {
        GameObject parentGO = new GameObject("ActualDotParent", typeof(RectTransform));
        parentGO.transform.SetParent(overlayRect, false);
        parentGO.transform.position = new Vector3(parentGO.transform.position.x, parentGO.transform.position.y, -1);

        var parentRT = parentGO.GetComponent<RectTransform>();
        parentRT.anchorMin = Vector2.zero;
        parentRT.anchorMax = Vector2.zero;

        GameObject discGO = new GameObject(
            "ActualDot",
            typeof(CanvasRenderer),
            typeof(RectTransform),
            typeof(Shapes.Disc),
            typeof(BoingBehavior));

        GameObject outlineGO = new GameObject(
            "outline",
            typeof(CanvasRenderer),
            typeof(RectTransform),
            typeof(Shapes.Disc),
            typeof(BoingBehavior));

        outlineGO.transform.SetParent(parentGO.transform, false);
        discGO.transform.SetParent(parentGO.transform, false);

        var disc = discGO.GetComponent<Shapes.Disc>();
        disc.Radius = dotDiameter * 0.5f;
        disc.Color = unknownColor;

        var outline = outlineGO.GetComponent<Shapes.Disc>();
        outline.Color = Color.black;
        outline.Type = Shapes.DiscType.Ring;
        outline.Thickness = 5;
        outline.Radius = dotDiameter * 0.5f;

        var boing = discGO.GetComponent<BoingBehavior>();
        boing.LockTranslationZ = true;

        var boing2 = outline.GetComponent<BoingBehavior>();
        boing2.LockTranslationZ = true;

        _dotRects.Add(parentRT);
        _dotDiscs.Add(disc);
        _dotDiscOutline.Add(outline);
    }
}
