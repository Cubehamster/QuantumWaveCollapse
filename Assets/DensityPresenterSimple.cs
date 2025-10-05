// DensityPresenterSimple.cs
// Minimal viewer: log1p → normalize by frame max → gamma → gradient.

using UnityEngine;
using Unity.Entities;
using Unity.Collections;
using UnityEngine.UI;

public class DensityPresenterSimple : MonoBehaviour
{
    public RawImage target;
    [Range(0.1f, 10f)] public float exposure = 1.0f; // multiplies density before log1p
    [Range(0.25f, 3f)] public float gamma = 0.9f;    // <1 lifts shadows

    [Header("Gradient (sRGB)")]
    public Color low = new Color(0.05f, 0.08f, 0.20f); // deep blue
    public Color mid = new Color(0.00f, 0.70f, 1.00f); // cyan
    public Color high = new Color(1.00f, 0.65f, 0.20f); // orange
    public Color white = Color.white;

    Texture2D _tex;
    Color32[] _pixels;
    DensityBuildSystem _sys;

    void Awake()
    {
        if (!target) target = GetComponent<RawImage>();
        _sys = World.DefaultGameObjectInjectionWorld?
               .GetExistingSystemManaged<DensityBuildSystem>();
    }

    void LateUpdate()
    {
        if (_sys == null) return;

        _sys.GetSize(out int W, out int H);
        if (W <= 0 || H <= 0) return;

        EnsureTex(W, H);

        var buf = _sys.GetDensityRO();
        if (!buf.IsCreated || buf.Length != W * H) return;

        // Find frame max (simple & robust)
        float maxV = 0f;
        for (int i = 0; i < buf.Length; i++) if (buf[i] > maxV) maxV = buf[i];
        if (maxV <= 0f)
        {
            // clear to blackish if empty
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = new Color32(5, 8, 20, 255);
            _tex.SetPixels32(_pixels); _tex.Apply(false, false);
            return;
        }

        float invMaxLog = 1f / Mathf.Log(1f + exposure * maxV);
        float invGamma = 1f / Mathf.Max(0.001f, gamma);

        for (int i = 0; i < buf.Length; i++)
        {
            // Map: log1p -> normalize -> gamma
            float t = Mathf.Log(1f + exposure * buf[i]) * invMaxLog;
            t = Mathf.Pow(Mathf.Clamp01(t), invGamma);

            // 3-stop gradient (low→mid→high→white)
            Color c;
            if (t < 0.33f) c = Color.Lerp(low, mid, t / 0.33f);
            else if (t < 0.66f) c = Color.Lerp(mid, high, (t - 0.33f) / 0.33f);
            else c = Color.Lerp(high, white, (t - 0.66f) / 0.34f);

            _pixels[i] = (Color32)c;
        }

        _tex.SetPixels32(_pixels);
        _tex.Apply(false, false);
    }

    void EnsureTex(int w, int h)
    {
        if (_tex != null && _tex.width == w && _tex.height == h) return;
        _tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        _pixels = new Color32[w * h];
        if (target) target.texture = _tex;
    }
}
