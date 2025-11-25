using BoingKit;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws UI dots over the density RawImage for each *active* "actual particle".
/// Supports two players:
/// - Player 1 = ClickRequest + MeasurementResult
/// - Player 2 = ClickRequestP2 + MeasurementResultP2
///
/// Each player can highlight the closest actual independently.
/// Permanently marked Good/Bad particles retain their color.
/// 
/// Now pool-aware:
/// - Uses ActualParticleRef to filter active slots (Walker != Entity.Null).
/// - Positions taken from ActualParticlePositionElement for active slots only.
/// - highlightIndexP1/P2 are interpreted as *slot indices* into the pool.
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
    public int maxDots = 0;

    // ECS
    EntityManager _em;
    EntityQuery _boundsQ;
    EntityQuery _cfgQ;
    EntityQuery _legacyQ;

    EntityQuery _clickP1Q;
    EntityQuery _clickP2Q;

    EntityQuery _resultP1Q;
    EntityQuery _resultP2Q;

    // Dot pool
    readonly List<RectTransform> _dotRects = new();
    readonly List<Shapes.Disc> _dotDiscs = new();

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

    void OnDisable() => ClearPool();

    void Update()
    {
        // Toggle with "C"
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
        // 2) Collect ACTIVE positions and statuses from the pool
        //    - Active slot: ActualParticleRef[i].Walker != Entity.Null
        // ----------------------------------------------------------
        int activeCount = 0;
        float2[] posArray = null;
        ActualParticleStatus[] statusArray = null;
        int[] slotIndexArray = null; // maps dot index -> pool slot index

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

            int poolLen = math.min(refBuf.Length, posBuf.Length);

            // Gather active slots
            List<float2> posList = new();
            List<ActualParticleStatus> statusList = new();
            List<int> slotIndexList = new();

            for (int i = 0; i < poolLen; i++)
            {
                if (refBuf[i].Walker == Entity.Null)
                    continue; // inactive slot, skip

                posList.Add(posBuf[i].Value);
                slotIndexList.Add(i);

                ActualParticleStatus st = ActualParticleStatus.Unknown;
                if (statBuf.IsCreated && i < statBuf.Length)
                    st = statBuf[i].Value;

                statusList.Add(st);
            }

            activeCount = posList.Count;
            if (maxDots > 0)
                activeCount = Mathf.Min(activeCount, maxDots);

            if (activeCount > 0)
            {
                posArray = new float2[activeCount];
                statusArray = new ActualParticleStatus[activeCount];
                slotIndexArray = new int[activeCount];

                for (int i = 0; i < activeCount; i++)
                {
                    posArray[i] = posList[i];
                    statusArray[i] = statusList[i];
                    slotIndexArray[i] = slotIndexList[i]; // pool slot index
                }
            }
        }
        else if (!_legacyQ.IsEmptyIgnoreFilter)
        {
            // Legacy single-actual fallback
            var ap = _legacyQ.GetSingleton<ActualParticlePosition>();
            activeCount = 1;
            posArray = new[] { ap.Value };
            statusArray = new[] { ActualParticleStatus.Unknown };
            slotIndexArray = new[] { 0 };
        }
        else
        {
            SetPoolSize(0);
            return;
        }

        // ----------------------------------------------------------
        // 3) Draw dots (only for ACTIVE slots)
        // ----------------------------------------------------------
        SetPoolSize(activeCount);
        Vector2 canvasSize = overlayRect.rect.size;

        for (int i = 0; i < activeCount; i++)
        {
            float2 p = posArray[i];
            float2 uv = (p - minB) / sizeB;
            Vector2 anchor = new Vector2(uv.x * canvasSize.x, uv.y * canvasSize.y);

            var rt = _dotRects[i];
            var disc = _dotDiscs[i];

            rt.anchoredPosition = anchor;

            // permanent color
            Color baseColor = statusArray[i] switch
            {
                ActualParticleStatus.Good => goodColor,
                ActualParticleStatus.Bad => badColor,
                _ => unknownColor
            };

            int slotIndex = slotIndexArray[i];

            // highlight conditions (either P1 or P2), comparing against slot index
            bool highlightFromP1 = (slotIndex == highlightIndexP1 && scanningP1);
            bool highlightFromP2 = (slotIndex == highlightIndexP2 && scanningP2);

            bool finalHighlight = highlightFromP1 || highlightFromP2;

            Color finalColor = finalHighlight ? highlightColor : baseColor;
            float radiusMul = finalHighlight ? highlightSizeMultiplier : 1f;

            disc.Color = finalColor;
            disc.Radius = 0.5f * dotDiameter * radiusMul;
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
        while (_dotRects.Count < needed)
            CreateDot();

        for (int i = 0; i < _dotRects.Count; i++)
        {
            bool active = i < needed;
            if (_dotRects[i].gameObject.activeSelf != active)
                _dotRects[i].gameObject.SetActive(active);
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

        GameObject discGO = new GameObject("ActualDot",
            typeof(CanvasRenderer),
            typeof(RectTransform),
            typeof(Shapes.Disc),
            typeof(BoingBehavior));

        discGO.transform.SetParent(parentGO.transform, false);

        var disc = discGO.GetComponent<Shapes.Disc>();
        disc.Radius = dotDiameter * 0.5f;
        disc.Color = unknownColor;

        var boing = discGO.GetComponent<BoingBehavior>();
        boing.LockTranslationZ = true;

        _dotRects.Add(parentRT);
        _dotDiscs.Add(disc);
    }
}
