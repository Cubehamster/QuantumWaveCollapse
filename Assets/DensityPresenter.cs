using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.UI;

/// <summary>
/// Maps a float density buffer to a Texture2D with robust dynamic range:
/// percentile-based normalization + log/Reinhard + OKLab gradient.
/// Hook this up to whatever produces `density` (float[] or NativeArray<float>).
/// </summary>
public class DensityPresenter : MonoBehaviour
{
    [Header("Input")]
    public RawImage target;
    public int width = 1024;
    public int height = 1024;

    [Tooltip("Stride>1 skips pixels during sampling (speed). 2–8 is fine.")]
    [Range(1, 16)] public int sampleStride = 4;

    [Header("Tone mapping")]
    public bool useLog = true;
    [Tooltip("Exposure multiplier before log/tone map.")]
    public float exposure = 1.0f;
    [Tooltip("Apply Reinhard v/(1+v) after exposure/log.")]
    public bool reinhard = false;
    [Tooltip("Gamma after normalization.")]
    [Range(0.25f, 3f)] public float gamma = 1.0f;

    [Header("Robust normalization")]
    [Tooltip("Lower percentile (0–0.2). E.g. 0.01 = 1%")]
    [Range(0f, 0.2f)] public float pctLow = 0.01f;
    [Tooltip("Upper percentile (0.8–1). E.g. 0.995 = 99.5%")]
    [Range(0.8f, 1.0f)] public float pctHigh = 0.995f;
    [Tooltip("EMA smoothing of percentiles (0=no smoothing, 0.9=very smooth).")]
    [Range(0f, 0.98f)] public float ema = 0.90f;

    [Header("OKLab gradient (4 stops)")]
    public Color oklabStop0 = new Color(0.04f, 0.07f, 0.20f); // deep blue
    public Color oklabStop1 = new Color(0.01f, 0.45f, 0.85f); // cyan
    public Color oklabStop2 = new Color(0.98f, 0.82f, 0.30f); // gold
    public Color oklabStop3 = new Color(1f, 0.98f, 0.97f);    // near-white

    // Internal
    Texture2D _tex;
    Color32[] _pixels;
    float _emaLo = 0.0f;
    float _emaHi = 1.0f;
    bool _haveEma;

    // Call this from your density builder with the latest buffer.
    public void Present(NativeArray<float> density)
    {
        if (!density.IsCreated || density.Length != width * height)
            return;

        if (_tex == null || _tex.width != width || _tex.height != height)
        {
            _tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            _tex.wrapMode = TextureWrapMode.Clamp;
            _tex.filterMode = FilterMode.Bilinear;
            target.texture = _tex;
            _pixels = new Color32[width * height];
            _haveEma = false;
        }

        // 1) Build a robust range from percentiles over a sample of pixels
        float lowP, highP;
        ComputePercentileRange(density, out lowP, out highP);

        // Smooth (EMA): prev*(ema) + cur*(1-ema)
        if (!_haveEma)
        {
            _emaLo = lowP; _emaHi = highP; _haveEma = true;
        }
        else
        {
            float a = 1f - Mathf.Clamp01(ema);
            _emaLo = Mathf.Lerp(_emaLo, lowP, a);
            _emaHi = Mathf.Lerp(_emaHi, highP, a);
        }

        // Avoid degenerate range
        if (_emaHi <= _emaLo + 1e-10f) _emaHi = _emaLo + 1e-10f;

        // 2) Map every pixel
        int n = density.Length;
        for (int i = 0; i < n; i++)
        {
            float d = density[i];
            float x = ApplyCurve(d, exposure, useLog, reinhard);
            float t = Mathf.Clamp01((x - _emaLo) / (_emaHi - _emaLo));
            if (gamma != 1f) t = Mathf.Pow(t, 1f / gamma);
            _pixels[i] = ToColor32(OKLabGradient(t));
        }

        _tex.SetPixels32(_pixels);
        _tex.Apply(false, false);
    }

    // -------- Mapping helpers --------

    float ApplyCurve(float v, float expMul, bool logMap, bool useReinhard)
    {
        float x = v * Mathf.Max(0f, expMul);
        if (logMap)
        {
            // log1p keeps low values visible and avoids log(0)
            x = Mathf.Log(1f + x);
        }
        if (useReinhard)
        {
            x = x / (1f + x);
        }
        return x;
    }

    void ComputePercentileRange(NativeArray<float> buf, out float lo, out float hi)
    {
        // Sample sparsely to avoid heavy CPU. Use a small reservoir we sort.
        int stride = Mathf.Max(1, sampleStride);
        int w = width, h = height;

        // Cap sample size for speed
        int maxSamples = 1 << 15; // ~32k
        var samples = new List<float>(Mathf.Min(maxSamples, (w / stride) * (h / stride)));

        // Build transformed samples (apply the same curve we’ll use to display)
        int count = 0;
        for (int y = 0; y < h; y += stride)
        {
            int row = y * w;
            for (int x = 0; x < w; x += stride)
            {
                float v = buf[row + x];
                if (v <= 0f || float.IsNaN(v)) continue;

                float t = ApplyCurve(v, exposure, useLog, reinhard);
                samples.Add(t);
                count++;
                if (count >= maxSamples) break;
            }
            if (count >= maxSamples) break;
        }

        if (samples.Count == 0)
        {
            lo = 0f; hi = 1f; return;
        }

        samples.Sort();

        float lp = Mathf.Clamp01(pctLow);
        float hp = Mathf.Clamp01(pctHigh);
        if (hp <= lp) hp = Mathf.Min(0.999f, lp + 0.001f);

        int iLo = Mathf.Clamp(Mathf.RoundToInt(lp * (samples.Count - 1)), 0, samples.Count - 1);
        int iHi = Mathf.Clamp(Mathf.RoundToInt(hp * (samples.Count - 1)), 0, samples.Count - 1);
        lo = samples[iLo];
        hi = samples[iHi];

        // Safety: if the range is too tiny, widen a hair to avoid banding
        if (hi - lo < 1e-6f) { hi = lo + 1e-6f; }
    }

    // -------- OKLab gradient (4-stop) --------

    Color OKLabGradient(float t)
    {
        t = Mathf.Clamp01(t);
        // piecewise between 4 stops
        if (t < 1f / 3f) return OklabLerp(oklabStop0, oklabStop1, t * 3f);
        else if (t < 2f / 3f) return OklabLerp(oklabStop1, oklabStop2, (t - 1f / 3f) * 3f);
        else return OklabLerp(oklabStop2, oklabStop3, (t - 2f / 3f) * 3f);
    }

    static Color OklabLerp(Color sRGBa, Color sRGBb, float t)
    {
        // Convert sRGB -> OKLab, lerp in OKLab, back to sRGB
        var la = RGB2Oklab(sRGBa.linear);
        var lb = RGB2Oklab(sRGBb.linear);
        var l = new Vector3(Mathf.Lerp(la.x, la.x + (lb.x - la.x), t),
                             Mathf.Lerp(la.y, la.y + (lb.y - la.y), t),
                             Mathf.Lerp(la.z, la.z + (lb.z - la.z), t));
        return Oklab2RGB(l);
    }

    static Vector3 RGB2Oklab(Color lin)
    {
        float l = 0.4122214708f * lin.r + 0.5363325363f * lin.g + 0.0514459929f * lin.b;
        float m = 0.2119034982f * lin.r + 0.6806995451f * lin.g + 0.1073969566f * lin.b;
        float s = 0.0883024619f * lin.r + 0.2817188376f * lin.g + 0.6299787005f * lin.b;

        float l_ = Mathf.Pow(l, 1f / 3f);
        float m_ = Mathf.Pow(m, 1f / 3f);
        float s_ = Mathf.Pow(s, 1f / 3f);

        return new Vector3(
            0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
            1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
            0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_
        );
    }

    static Color Oklab2RGB(Vector3 lab)
    {
        float l_ = lab.x + 0.3963377774f * lab.y + 0.2158037573f * lab.z;
        float m_ = lab.x - 0.1055613458f * lab.y - 0.0638541728f * lab.z;
        float s_ = lab.x - 0.0894841775f * lab.y - 1.2914855480f * lab.z;

        float l = l_ * l_ * l_;
        float m = m_ * m_ * m_;
        float s = s_ * s_ * s_;

        float r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

        // Clamp to [0,1] and convert back to gamma sRGB
        r = Mathf.Clamp01(r); g = Mathf.Clamp01(g); b = Mathf.Clamp01(b);
        return new Color(r, g, b, 1f);
    }

    static Color32 ToColor32(Color c)
    {
        return new Color32(
            (byte)Mathf.RoundToInt(c.r * 255f),
            (byte)Mathf.RoundToInt(c.g * 255f),
            (byte)Mathf.RoundToInt(c.b * 255f),
            255);
    }
}
