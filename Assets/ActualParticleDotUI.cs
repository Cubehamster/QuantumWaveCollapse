// ActualParticleDotUI.cs
using UnityEngine;
using UnityEngine.UI;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.InputSystem; // New Input System

/// <summary>
/// Shows a small UI dot following the ActualParticlePosition (singleton).
/// Toggle visibility with 'C'.
/// </summary>
public sealed class ActualParticleDotUI : MonoBehaviour
{
    [Header("Hook up")]
    [Tooltip("RawImage that displays the density (same one used by GPUDensityRenderer).")]
    public RawImage densityImage;

    [Tooltip("A small UI Image (e.g., a 6x6 circle) that will be moved over the density image.")]
    public RectTransform dot;

    [Header("Appearance")]
    [Tooltip("Radius in pixels of the dot (if 'dot' has a Layout/Size, this is ignored).")]
    public float pixelSize = Screen.height/50;
    public Color dotColor = Color.white;

    [Header("Toggle")]
    [Tooltip("Start with the dot visible?")]
    public bool startVisible = true;

    // ECS access
    EntityManager _em;
    EntityQuery _boundsQ;
    EntityQuery _posQ;

    void OnEnable()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[ActualParticleDotUI] No Default World in scene.");
            enabled = false;
            return;
        }

        _em = world.EntityManager;

        _boundsQ = _em.CreateEntityQuery(ComponentType.ReadOnly<SimBounds2D>());
        _posQ = _em.CreateEntityQuery(ComponentType.ReadOnly<ActualParticlePosition>());

        if (densityImage == null)
        {
            Debug.LogError("[ActualParticleDotUI] Please assign 'densityImage' (the RawImage used for density).");
            enabled = false;
            return;
        }
        if (dot == null)
        {
            Debug.LogError("[ActualParticleDotUI] Please assign 'dot' (a small UI Image/RectTransform).");
            enabled = false;
            return;
        }

        // Initialize dot
        var img = dot.GetComponent<Image>();
        if (img != null) img.color = dotColor;

        // Ensure size if no layout
        dot.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pixelSize);
        dot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pixelSize);

        dot.gameObject.SetActive(startVisible);

        // Make sure the dot is a child of the density image so local coords line up
        if (dot.transform.parent != densityImage.rectTransform)
            dot.SetParent(densityImage.rectTransform, worldPositionStays: false);
    }

    void Update()
    {
        // Toggle with 'C' (new Input System)
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
        {
            dot.gameObject.SetActive(!dot.gameObject.activeSelf);
        }

        if (!dot.gameObject.activeSelf)
            return;

        if (_boundsQ.IsEmptyIgnoreFilter || _posQ.IsEmptyIgnoreFilter)
            return;

        // Read SimBounds2D + ActualParticlePosition
        float2 center, extents, ap;
        using (var bArr = _boundsQ.ToComponentDataArray<SimBounds2D>(Allocator.Temp))
        using (var pArr = _posQ.ToComponentDataArray<ActualParticlePosition>(Allocator.Temp))
        {
            var b = bArr[0];
            var p = pArr[0];
            center = b.Center;
            extents = b.Extents;
            ap = p.Value;
        }

        // World -> UV mapping (same as compute shader)
        float2 minB = center - extents;
        float2 sizeB = extents * 2f;
        float2 invSz = new float2(
            1f / Mathf.Max(1e-6f, sizeB.x),
            1f / Mathf.Max(1e-6f, sizeB.y)
        );

        float2 uv = (ap - minB) * invSz; // 0..1
        uv = math.saturate(uv);

        // UV -> local anchored position in densityImage rect
        RectTransform rt = densityImage.rectTransform;
        Rect r = rt.rect; // local rect
        // local origin for anchoredPosition is rect center (0,0)
        Vector2 local = new Vector2(
            (uv.x - 0.5f) * r.width,
            (uv.y - 0.5f) * r.height
        );

        dot.anchoredPosition = local;
    }
}
