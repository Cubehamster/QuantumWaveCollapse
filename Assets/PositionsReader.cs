// PositionsReader.cs
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

public class PositionsReader : MonoBehaviour
{
    [Header("Logging")]
    public bool enableLogging = false;
    public int logEveryNFrames = 30;
    public int logSampleCount = 5;

    [Header("Gizmos (Scene View)")]
    public bool drawGizmos = true;
    public int gizmoMaxDots = 500;
    public float gizmoSize = 0.5f;
    public Color coldColor = new Color(0.2f, 0.6f, 1f, 0.9f); // low weight
    public Color hotColor = new Color(1f, 0.35f, 0.2f, 0.9f); // high weight

    PositionsWeightsExportSystem _export;
    int _frameCounter;

    void Update()
    {
        _export ??= World.DefaultGameObjectInjectionWorld?
                    .GetExistingSystemManaged<PositionsWeightsExportSystem>();
        if (_export == null || !_export.HasData) return;

        if (!enableLogging) return;
        if ((++_frameCounter % math.max(1, logEveryNFrames)) != 0) return;

        var pos = _export.GetPositionsRO();
        var w = _export.GetWeightsRO();
        int total = math.min(pos.Length, w.Length);
        if (total == 0) return;

        int samples = math.clamp(logSampleCount, 1, 64);
        int step = math.max(1, total / samples);

        int shown = 0;
        for (int i = 0; i < total && shown < samples; i += step, shown++)
            Debug.Log($"[Reader2D] p[{i}]={pos[i]}  w={w[i]:0.###}  (total={total})");
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos || !Application.isPlaying) return;

        _export ??= World.DefaultGameObjectInjectionWorld?
                    .GetExistingSystemManaged<PositionsWeightsExportSystem>();
        if (_export == null || !_export.HasData) return;

        var pos = _export.GetPositionsRO();
        var w = _export.GetWeightsRO();
        int total = math.min(pos.Length, w.Length);
        if (total == 0) return;

        int maxDots = math.max(1, gizmoMaxDots);
        int step = math.max(1, total / maxDots);

        // First pass: find min/max on the sampled set
        float wMin = float.PositiveInfinity, wMax = float.NegativeInfinity;
        int counted = 0;
        for (int i = 0; i < total && counted < maxDots; i += step, counted++)
        {
            float wi = w[i];
            if (wi < wMin) wMin = wi;
            if (wi > wMax) wMax = wi;
        }
        float denom = math.max(1e-6f, wMax - wMin);

        // Second pass: draw with gradient color
        counted = 0;
        for (int i = 0; i < total && counted < maxDots; i += step, counted++)
        {
            float2 p = pos[i];
            float t = math.saturate((w[i] - wMin) / denom);
            Gizmos.color = Color.Lerp(coldColor, hotColor, t);
            Gizmos.DrawSphere(new Vector3(p.x, p.y, 0f), gizmoSize);
        }
    }
}
