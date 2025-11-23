using BoingKit;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws UI dots over the density RawImage for each "actual particle".
/// - Uses multi-actual buffers on the ActualParticleSet singleton:
///     • ActualParticlePositionElement (positions)
///     • ActualParticleStatusElement   (statuses: Unknown / Good / Bad)
/// - Falls back to legacy ActualParticlePosition singleton (single dot).
/// - Press 'C' (New Input System) to toggle visibility.
/// - Positions are mapped from world coords via SimBounds2D to overlayRect space.
/// - While scanning and an actual is "hit", that dot is highlighted.
/// </summary>
[DisallowMultipleComponent]
public sealed class ActualParticleDotUI : MonoBehaviour
{
    [Header("Where to draw (same rect as your density RawImage)")]
    [Tooltip("RectTransform area where dots should be placed (usually the RawImage RectTransform).")]
    public RectTransform overlayRect;

    [Header("Dot look")]
    [Min(1f)] public float dotDiameter = 10f;

    [Header("Status colors")]
    [Tooltip("Color for actuals with Unknown status.")]
    [ColorUsage (true, true)]
    public Color unknownColor = new Color(1f, 0.8f, 0.2f, 0.95f);
    [Tooltip("Color for 'Good' actuals.")]
    [ColorUsage(true, true)]
    public Color goodColor = new Color(0.2f, 1f, 0.2f, 0.95f);
    [Tooltip("Color for 'Bad' actuals.")]
    [ColorUsage(true, true)]
    public Color badColor = new Color(1f, 0.1f, 0.1f, 0.95f);

    [Header("Highlight")]
    [Tooltip("Color used while scanning over the closest actual inside the measurement radius.")]
    [ColorUsage(true, true)]
    public Color highlightColor = new Color(1f, 1f, 1f, 1f);
    [Tooltip("Multiply radius while highlighted (for a slightly bigger dot).")]
    public float highlightSizeMultiplier = 1.4f;

    [Header("Behavior")]
    [Tooltip("Toggle visibility at start.")]
    public bool visible = true;
    [Tooltip("Max number of dots to show (safeguard). 0 = unlimited.")]
    public int maxDots = 0;

    // ECS
    EntityManager _em;
    EntityQuery _boundsQ;
    EntityQuery _cfgQ;      // multi-actual config entity: ActualParticleSet + buffers
    EntityQuery _legacyQ;   // legacy single: ActualParticlePosition
    EntityQuery _clickQ;    // ClickRequest singleton
    EntityQuery _resultQ;   // MeasurementResult singleton

    // Pool of UI objects (children under overlayRect)
    readonly List<RectTransform> _dotRects = new List<RectTransform>();
    readonly List<Shapes.Disc> _dotDiscs = new List<Shapes.Disc>();

    void OnEnable()
    {
        if (overlayRect == null)
        {
            Debug.LogError("[ActualParticleDotUI] Assign 'overlayRect' (the RectTransform you want to draw onto). Disabling.");
            enabled = false;
            return;
        }

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[ActualParticleDotUI] No Default World. Disabling.");
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

        _clickQ = _em.CreateEntityQuery(ComponentType.ReadOnly<ClickRequest>());
        _resultQ = _em.CreateEntityQuery(ComponentType.ReadOnly<MeasurementResult>());

        ApplyVisibility();
    }

    void OnDisable()
    {
        ClearPool();
    }

    void Update()
    {
        // Toggle with 'C' (New Input System)
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
        {
            visible = !visible;
            ApplyVisibility();
        }

        if (!visible) return;

        if (_boundsQ.IsEmptyIgnoreFilter) return; // need bounds for mapping
        var bounds = _boundsQ.GetSingleton<SimBounds2D>();
        float2 minB = bounds.Center - bounds.Extents;
        float2 sizeB = math.max(new float2(1e-6f, 1e-6f), bounds.Extents * 2f);

        // --------------------------------------------------------------------
        // Read scanning + highlight info from ECS:
        //   - ClickRequest.IsPressed → scanning
        //   - MeasurementResult.HasActualInRadius + ClosestActualIndex → which actual is "hit"
        // --------------------------------------------------------------------
        bool scanning = false;
        int highlightIndex = -1;

        if (!_clickQ.IsEmptyIgnoreFilter)
        {
            var click = _clickQ.GetSingleton<ClickRequest>();
            scanning = click.IsPressed;
        }

        if (!_resultQ.IsEmptyIgnoreFilter)
        {
            var result = _resultQ.GetSingleton<MeasurementResult>();
            if (result.HasActualInRadius && result.ClosestActualIndex >= 0)
            {
                if (scanning)
                    highlightIndex = result.ClosestActualIndex;
            }
        }

        // --------------------------------------------------------------------
        // Collect positions + statuses
        // --------------------------------------------------------------------
        int count = 0;
        float2[] posArray = null;
        ActualParticleStatus[] statusArray = null;

        if (!_cfgQ.IsEmptyIgnoreFilter)
        {
            using var cfgEnts = _cfgQ.ToEntityArray(Allocator.Temp);
            var cfgEnt = cfgEnts[0];

            var posBuf = _em.GetBuffer<ActualParticlePositionElement>(cfgEnt);
            count = posBuf.Length;
            if (maxDots > 0) count = Mathf.Min(count, maxDots);

            if (count > 0)
            {
                posArray = new float2[count];
                for (int i = 0; i < count; i++)
                    posArray[i] = posBuf[i].Value;

                // Optional: statuses buffer
                if (_em.HasBuffer<ActualParticleStatusElement>(cfgEnt))
                {
                    var statusBuf = _em.GetBuffer<ActualParticleStatusElement>(cfgEnt);
                    int statCount = math.min(statusBuf.Length, count);
                    statusArray = new ActualParticleStatus[count];

                    for (int i = 0; i < statCount; i++)
                        statusArray[i] = statusBuf[i].Value;

                    for (int i = statCount; i < count; i++)
                        statusArray[i] = ActualParticleStatus.Unknown;
                }
            }
        }
        else if (!_legacyQ.IsEmptyIgnoreFilter)
        {
            // Legacy single position only
            var ap = _legacyQ.GetSingleton<ActualParticlePosition>();
            count = 1;
            posArray = new[] { ap.Value };
            statusArray = new[] { ActualParticleStatus.Unknown };
        }
        else
        {
            // Nothing to draw
            SetPoolSize(0);
            return;
        }

        SetPoolSize(count);
        Vector2 canvasSize = overlayRect.rect.size;

        for (int i = 0; i < count; i++)
        {
            float2 p = posArray[i];
            float2 uv = (p - minB) / sizeB;          // 0..1
            Vector2 anchor = new Vector2(uv.x * canvasSize.x, uv.y * canvasSize.y);

            var rt = _dotRects[i];
            var disc = _dotDiscs[i];
            if (rt == null || disc == null) continue;

            rt.anchoredPosition = anchor;

            // Base color from status
            Color baseColor = unknownColor;
            if (statusArray != null && i < statusArray.Length)
            {
                switch (statusArray[i])
                {
                    case ActualParticleStatus.Good: baseColor = goodColor; break;
                    case ActualParticleStatus.Bad: baseColor = badColor; break;
                    case ActualParticleStatus.Unknown:
                    default: baseColor = unknownColor; break;
                }
            }

            // Highlight override while scanning the closest actual
            bool isHighlighted = (i == highlightIndex && scanning);
            Color finalColor = isHighlighted ? highlightColor : baseColor;
            float radiusMul = isHighlighted ? highlightSizeMultiplier : 1f;

            disc.Color = finalColor;
            disc.Radius = 0.5f * dotDiameter * radiusMul;
        }
    }

    // ----------------- Pool management -----------------

    void ApplyVisibility()
    {
        if (overlayRect == null) return;
        overlayRect.gameObject.SetActive(visible);
    }

    void ClearPool()
    {
        for (int i = 0; i < _dotRects.Count; i++)
            if (_dotRects[i] != null)
                Destroy(_dotRects[i].gameObject);

        _dotRects.Clear();
        _dotDiscs.Clear();
    }

    void SetPoolSize(int needed)
    {
        if (needed < 0) needed = 0;

        while (_dotRects.Count < needed)
            CreateDot();

        for (int i = 0; i < _dotRects.Count; i++)
        {
            bool active = i < needed;
            if (_dotRects[i] != null && _dotRects[i].gameObject.activeSelf != active)
                _dotRects[i].gameObject.SetActive(active);
        }
    }

    void CreateDot()
    {
        // Parent: handles positioning in overlayRect space
        GameObject parentGO = new GameObject("QuantumParticle", typeof(RectTransform));
        parentGO.transform.SetParent(overlayRect, false);
        var parentRT = parentGO.GetComponent<RectTransform>();
        parentRT.anchorMin = Vector2.zero;
        parentRT.anchorMax = Vector2.zero;

        // Child: the actual disc with Boing behavior
        GameObject discGO = new GameObject("ActualDot",
            typeof(CanvasRenderer),
            typeof(RectTransform),
            typeof(Shapes.Disc),
            typeof(BoingBehavior));

        discGO.transform.SetParent(parentGO.transform, false);

        var discRT = discGO.GetComponent<RectTransform>();
        discRT.position = new Vector3(discRT.position.x, discRT.position.y, -1f);
        discRT.pivot = new Vector2(0.5f, 0.5f);
        discRT.sizeDelta = new Vector2(dotDiameter, dotDiameter);

        var disc = discGO.GetComponent<Shapes.Disc>();
        disc.Radius = dotDiameter * 0.5f;
        disc.Color = unknownColor;

        var boing = discGO.GetComponent<BoingBehavior>();
        boing.LockTranslationZ = true;

        _dotRects.Add(parentRT);
        _dotDiscs.Add(disc);
    }
}
