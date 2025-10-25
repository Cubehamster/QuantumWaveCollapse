using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Draws UI dots over the density RawImage for each "actual particle".
/// - Uses multi-actual buffer ActualParticlePositionElement when available.
/// - Falls back to legacy ActualParticlePosition singleton (single dot).
/// - Press 'C' to toggle visibility (New Input System).
/// - Positions are mapped from world coords via SimBounds2D to overlayRect space.
/// </summary>
[DisallowMultipleComponent]
public sealed class ActualParticlesDotsUI : MonoBehaviour
{
    [Header("Where to draw (same rect as your density RawImage)")]
    [Tooltip("RectTransform area where dots should be placed (usually the RawImage RectTransform).")]
    public RectTransform overlayRect;

    [Header("Dot look")]
    public Sprite dotSprite;
    public Color dotColor = new Color(1f, 0.2f, 0.2f, 0.95f);
    [Min(1f)] public float dotDiameter = 10f;
    [Tooltip("Optional per-dot outline (requires 'UI/Default' material support).")]
    public bool useOutline = false;
    [Range(0f, 8f)] public float outlineSize = 2f;
    public Color outlineColor = new Color(0f, 0f, 0f, 0.9f);

    [Header("Behavior")]
    [Tooltip("Toggle visibility at start.")]
    public bool visible = true;
    [Tooltip("Max number of dots to show (safeguard). 0 = unlimited.")]
    public int maxDots = 0;

    // ECS
    EntityManager _em;
    EntityQuery _boundsQ;
    EntityQuery _cfgQ;      // multi-actual config entity: ActualParticleSet + ActualParticlePositionElement buffer
    EntityQuery _legacyQ;   // legacy single: ActualParticlePosition

    // Pool of UI objects (children under overlayRect)
    readonly List<RectTransform> _dotRects = new List<RectTransform>();
    readonly List<Image> _dotImages = new List<Image>();
    readonly List<Outline> _outlines = new List<Outline>();

    void OnEnable()
    {
        if (overlayRect == null)
        {
            Debug.LogError("[ActualParticlesDotsUI] Assign 'overlayRect' (the RectTransform you want to draw onto). Disabling.");
            enabled = false;
            return;
        }

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[ActualParticlesDotsUI] No Default World. Disabling.");
            enabled = false;
            return;
        }

        _em = world.EntityManager;

        _boundsQ = _em.CreateEntityQuery(ComponentType.ReadOnly<SimBounds2D>());
        _cfgQ = _em.CreateEntityQuery(
            ComponentType.ReadOnly<ActualParticleSet>(),
            ComponentType.ReadOnly<ActualParticleRef>(),          // buffer exists on cfg
            ComponentType.ReadOnly<ActualParticlePositionElement>()); // positions buffer exists on cfg

        _legacyQ = _em.CreateEntityQuery(ComponentType.ReadOnly<ActualParticlePosition>());

        // Start with visibility state
        ApplyVisibility();
    }

    void OnDisable()
    {
        ClearPool();
    }

    void Update()
    {
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

        // Gather positions either from multi-actual buffer or legacy singleton
        int count = 0;
        float2[] posArray = null;

        if (!_cfgQ.IsEmptyIgnoreFilter)
        {
            // Use first config
            var cfgEnt = _cfgQ.ToEntityArray(Unity.Collections.Allocator.Temp)[0];
            var posBuf = _em.GetBuffer<ActualParticlePositionElement>(cfgEnt);
            count = posBuf.Length;

            if (maxDots > 0) count = Mathf.Min(count, maxDots);

            if (count > 0)
            {
                posArray = new float2[count];
                for (int i = 0; i < count; i++)
                    posArray[i] = posBuf[i].Value;
            }
        }
        else if (!_legacyQ.IsEmptyIgnoreFilter)
        {
            var ap = _legacyQ.GetSingleton<ActualParticlePosition>();
            count = 1;
            posArray = new[] { ap.Value };
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

            _dotRects[i].anchoredPosition = anchor;
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
            if (_dotRects[i] != null) Destroy(_dotRects[i].gameObject);

        _dotRects.Clear();
        _dotImages.Clear();
        _outlines.Clear();
    }

    void SetPoolSize(int needed)
    {
        if (needed < 0) needed = 0;

        // add if needed
        while (_dotRects.Count < needed)
            CreateDot();

        // disable extra
        for (int i = 0; i < _dotRects.Count; i++)
        {
            bool active = i < needed;
            if (_dotRects[i] != null && _dotRects[i].gameObject.activeSelf != active)
                _dotRects[i].gameObject.SetActive(active);

            // keep look up-to-date
            if (i < needed) StyleDot(i);
        }
    }

    void CreateDot()
    {
        GameObject go = new GameObject("ActualDot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(overlayRect, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(dotDiameter, dotDiameter);

        var img = go.GetComponent<Image>();
        img.raycastTarget = false;

        if (dotSprite != null)
        {
            img.sprite = dotSprite;
            img.type = Image.Type.Simple;
            img.preserveAspect = true;
        }

        Outline outline = null;
        if (useOutline)
        {
            outline = go.AddComponent<Outline>();
        }

        _dotRects.Add(rt);
        _dotImages.Add(img);
        _outlines.Add(outline);

        StyleDot(_dotRects.Count - 1);
    }

    void StyleDot(int i)
    {
        if (i < 0 || i >= _dotRects.Count) return;

        var rt = _dotRects[i];
        var img = _dotImages[i];
        var ol = _outlines[i];

        if (rt != null) rt.sizeDelta = new Vector2(dotDiameter, dotDiameter);
        if (img != null)
        {
            img.color = dotColor;
            if (dotSprite != null)
            {
                img.sprite = dotSprite;
                img.type = Image.Type.Simple;
                img.preserveAspect = true;
            }
        }

        if (useOutline)
        {
            if (ol == null && rt != null)
            {
                ol = rt.gameObject.AddComponent<Outline>();
                _outlines[i] = ol;
            }
            if (ol != null)
            {
                ol.effectColor = outlineColor;
                ol.effectDistance = new Vector2(outlineSize, -outlineSize);
            }
        }
        else
        {
            if (ol != null)
            {
                Destroy(ol);
                _outlines[i] = null;
            }
        }
    }
}
