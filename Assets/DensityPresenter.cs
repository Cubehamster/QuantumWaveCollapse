using UnityEngine;
using Unity.Entities;
using Unity.Collections;
using UnityEngine.UI;

public class DensityPresenter : MonoBehaviour
{
    [Header("Target")]
    public RawImage target;                 // assign a RawImage (full-screen UI)

    [Header("Visuals")]
    [Range(0, 8)] public int blurRadius = 2;  // 0 = off
    [Min(0f)] public float exposure = 1f;     // multiplies density before log

    [Header("OKLab Gradient (edit keys in sRGB)")]
    public OKLabGradient okGradient = OKLabGradient.Default(); // OKLab-interpolated

    [Header("Tone Mapping")]
    [Tooltip("Clip a bit of the darkest log-range so blacks ease in.")]
    [Range(0f, 0.2f)] public float blackClip = 0.02f;
    [Tooltip("Clip a bit of the brightest log-range so highlights aren’t dominated by speckle.")]
    [Range(0f, 0.2f)] public float whiteClip = 0.00f;

    [Tooltip("Extra smoothing on the low end (0 = none, 0.05–0.1 typical).")]
    [Range(0f, 0.2f)] public float lowBlendRange = 0.06f;

    [Tooltip("Gamma after log/clip/toe. <1 lifts shadows, >1 darkens them.")]
    [Range(0.3f, 3f)] public float gamma = 0.9f;

    [Tooltip("EMA smoothing for lmin/lmax so colors don’t pulse.")]
    [Range(0f, 0.99f)] public float logSmoothing = 0.90f; // 0 = off, 0.9 = strong

    // EMA state
    float _emaLmin, _emaLmax;
    bool _emaInit;

    Texture2D _tex;
    Color32[] _colors;
    float[] _bufA;   // working buffer (density)
    float[] _bufB;   // working buffer (temp)

    // Gaussian kernel cache
    float[] _kernel; // length = 2r+1
    int _kRadius = -1;

    // OKLab LUT (re-built each frame; cheap)
    const int LUT_SIZE = 256;
    Color32[] _oklabLUT;

    DensityBuildSystem _sys;

    // Debug overlay
    GUIStyle _overlayStyle;
    string _lastStats = "";

    // Background hue used for low-end blend (matches WriteSolidBackground)
    static readonly Color32 kBg = new Color32(5, 8, 20, 255);

    void OnEnable()
    {
        _sys = World.DefaultGameObjectInjectionWorld?
               .GetExistingSystemManaged<DensityBuildSystem>();

        if (okGradient == null || okGradient.keys == null || okGradient.keys.Length == 0)
            okGradient = OKLabGradient.Default();

        _overlayStyle = new GUIStyle
        {
            fontSize = 14,
            normal = new GUIStyleState { textColor = Color.white }
        };
    }

    void LateUpdate()
    {
        if (_sys == null) return;

        _sys.GetSize(out int W, out int H);
        if (W <= 0 || H <= 0) return;

        EnsureTexture(W, H);

        NativeArray<float> density = _sys.GetDensityRO();
        int N = W * H;

        // If density not ready yet, just keep background bound
        if (!density.IsCreated || density.Length != N)
        {
            WriteSolidBackground(W, H);
            return;
        }

        // Make sure working buffers exist
        if (_bufA == null || _bufA.Length != N) { _bufA = new float[N]; _bufB = new float[N]; }

        // Copy density → bufA and apply exposure
        for (int i = 0; i < N; i++) _bufA[i] = exposure * density[i];

        // Optional Gaussian blur (separable, artifact-free)
        if (blurRadius > 0)
        {
            BlurHorizontalGaussian(_bufA, _bufB, W, H, blurRadius);
            BlurVerticalGaussian(_bufB, _bufA, W, H, blurRadius); // back into _bufA
        }

        // If all zeros (e.g., early frames), keep previous texture (avoid flashes)
        bool allZero = true;
        for (int i = 0; i < N; i++) { if (_bufA[i] > 0f) { allZero = false; break; } }
        if (allZero) return;

        // Log tone-map with sampled min/max
        const float eps = 1e-9f;
        float lmin = float.PositiveInfinity, lmax = float.NegativeInfinity;
        int step = Mathf.Max(1, N / 4096); // sample up to ~4k pixels
        for (int i = 0; i < N; i += step)
        {
            float lv = Mathf.Log(Mathf.Max(eps, _bufA[i]));
            if (lv < lmin) lmin = lv;
            if (lv > lmax) lmax = lv;
        }

        // EMA smooth the dynamic range
        if (!_emaInit)
        {
            _emaLmin = lmin;
            _emaLmax = lmax;
            _emaInit = true;
        }
        else
        {
            _emaLmin = Mathf.Lerp(lmin, _emaLmin, logSmoothing);
            _emaLmax = Mathf.Lerp(lmax, _emaLmax, logSmoothing);
        }
        lmin = _emaLmin;
        lmax = _emaLmax;

        // Apply black/white clip inside the log range
        float l0 = Mathf.Lerp(lmin, lmax, Mathf.Clamp01(blackClip));
        float l1 = Mathf.Lerp(lmin, lmax, 1f - Mathf.Clamp01(whiteClip));
        if (l1 <= l0) l1 = l0 + 1e-6f;
        float inv = 1f / (l1 - l0);

        // Build OKLab LUT for this frame (cheap; ensures inspector edits apply instantly)
        if (_oklabLUT == null || _oklabLUT.Length != LUT_SIZE)
            _oklabLUT = new Color32[LUT_SIZE];
        okGradient.BakeLUT(_oklabLUT);

        // Allocate color buffer
        if (_colors == null || _colors.Length != N) _colors = new Color32[N];

        // Map each pixel:
        //   log → normalize w/ clips → soft toe → gamma → OKLab gradient (via LUT)
        float invGamma = 1f / Mathf.Max(0.001f, gamma);
        float lowBlend = Mathf.Clamp01(lowBlendRange);

        for (int i = 0; i < N; i++)
        {
            float lv = Mathf.Log(Mathf.Max(eps, _bufA[i]));
            float t = Mathf.Clamp01((lv - l0) * inv);

            // Soft toe/shoulder
            t = t * t * (3f - 2f * t); // smoothstep

            // Gamma tweak (gamma<1 lifts shadows, gamma>1 darkens)
            t = Mathf.Pow(t, invGamma);

            // OKLab gradient sample
            int idx = Mathf.Clamp(Mathf.RoundToInt(t * (LUT_SIZE - 1)), 0, LUT_SIZE - 1);
            Color c = _oklabLUT[idx];

            // Optional low-end blend to background so the very darkest fade smoothly
            if (lowBlend > 0f)
            {
                float f = Mathf.Clamp01(t / lowBlend); // 0..lowBlend → blend 0..1
                c = Color.Lerp(kBg, c, f);
            }

            _colors[i] = c;
        }

        _tex.SetPixelData(_colors, 0);
        _tex.Apply(false, false);

        // Update overlay
        _sys.GetDebug(out int particles, out int uniquePix, out float totalW);
        _lastStats = $"Particles: {particles:N0}  Pixels hit: {uniquePix:N0}  TotalW(raw): {totalW:0.###}";
    }

    void EnsureTexture(int W, int H)
    {
        if (_tex != null && _tex.width == W && _tex.height == H) return;

        _tex = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
        _tex.filterMode = FilterMode.Bilinear;

        if (target) target.texture = _tex;

        // Initialize with dark background
        WriteSolidBackground(W, H);
    }

    void WriteSolidBackground(int W, int H)
    {
        int N = W * H;
        if (_colors == null || _colors.Length != N) _colors = new Color32[N];
        for (int i = 0; i < N; i++) _colors[i] = kBg;
        _tex.SetPixelData(_colors, 0);
        _tex.Apply(false, false);
    }

    // ---------- Gaussian blur (separable) ----------

    void BuildGaussianKernel(int r)
    {
        if (r == _kRadius && _kernel != null) return;
        _kRadius = r;
        int size = r * 2 + 1;
        if (_kernel == null || _kernel.Length != size) _kernel = new float[size];

        // sigma ~ r/2 keeps kernel tight; tweak if you want wider blur
        float sigma = Mathf.Max(0.5f, r * 0.5f);
        float twoSigma2 = 2f * sigma * sigma;
        float sum = 0f;
        for (int i = -r, j = 0; i <= r; i++, j++)
        {
            float w = Mathf.Exp(-(i * i) / twoSigma2);
            _kernel[j] = w;
            sum += w;
        }
        float inv = 1f / Mathf.Max(1e-6f, sum);
        for (int j = 0; j < size; j++) _kernel[j] *= inv;
    }

    void BlurHorizontalGaussian(float[] src, float[] dst, int W, int H, int r)
    {
        BuildGaussianKernel(r);
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                float acc = 0f;
                for (int k = -r, j = 0; k <= r; k++, j++)
                    acc += src[row + Mathf.Clamp(x + k, 0, W - 1)] * _kernel[j];
                dst[row + x] = acc;
            }
        }
    }

    void BlurVerticalGaussian(float[] src, float[] dst, int W, int H, int r)
    {
        BuildGaussianKernel(r);
        for (int x = 0; x < W; x++)
        {
            for (int y = 0; y < H; y++)
            {
                float acc = 0f;
                for (int k = -r, j = 0; k <= r; k++, j++)
                {
                    int yy = Mathf.Clamp(y + k, 0, H - 1);
                    acc += src[yy * W + x] * _kernel[j];
                }
                dst[y * W + x] = acc;
            }
        }
    }

    // ---------- Debug overlay ----------

    void OnGUI()
    {
        if (string.IsNullOrEmpty(_lastStats)) return;
        GUI.Label(new Rect(8, 8, 800, 40), _lastStats, _overlayStyle);
    }
}

/* ===========================
   OKLab gradient utilities
   =========================== */

[System.Serializable]
public class OKLabGradient
{
    [System.Serializable]
    public struct Key
    {
        public Color color;      // sRGB in inspector
        [Range(0f, 1f)] public float t;
    }

    public Key[] keys;

    // Default palette roughly matching your old gradient
    public static OKLabGradient Default()
    {
        return new OKLabGradient
        {
            keys = new[]
            {
                new Key{ color = new Color(0.05f,0.08f,0.20f), t = 0.00f }, // deep blue
                new Key{ color = new Color(0.00f,0.70f,1.00f), t = 0.50f }, // cyan
                new Key{ color = new Color(1.00f,0.65f,0.20f), t = 0.85f }, // orange
                new Key{ color = Color.white,                  t = 1.00f }  // white
            }
        };
    }

    public void BakeLUT(Color32[] lut)
    {
        if (keys == null || keys.Length == 0)
        {
            var def = Default();
            keys = def.keys;
        }
        EnsureSorted();

        // Preconvert keys to OKLab
        var kL = new float[keys.Length];
        var kA = new float[keys.Length];
        var kB = new float[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            RGBToOKLab(keys[i].color, out kL[i], out kA[i], out kB[i]);

        int size = lut.Length;
        for (int i = 0; i < size; i++)
        {
            float t = (size == 1) ? 0f : (float)i / (size - 1);
            // Find segment
            int j = 0;
            while (j < keys.Length - 2 && t > keys[j + 1].t) j++;
            var k0 = keys[j];
            var k1 = keys[Mathf.Min(j + 1, keys.Length - 1)];

            float span = Mathf.Max(1e-6f, (k1.t - k0.t));
            float u = Mathf.Clamp01((t - k0.t) / span);

            // Interp in OKLab
            float L = Mathf.Lerp(kL[j], kL[j + 1], u);
            float A = Mathf.Lerp(kA[j], kA[j + 1], u);
            float B = Mathf.Lerp(kB[j], kB[j + 1], u);

            lut[i] = OKLabToRGB32(L, A, B);
        }
    }

    void EnsureSorted()
    {
        // Simple insertion sort (few keys)
        for (int i = 1; i < keys.Length; i++)
        {
            var k = keys[i];
            int j = i - 1;
            while (j >= 0 && keys[j].t > k.t)
            {
                keys[j + 1] = keys[j];
                j--;
            }
            keys[j + 1] = k;
        }
        // Ensure endpoints
        if (keys[0].t > 0f) keys[0].t = 0f;
        if (keys[keys.Length - 1].t < 1f) keys[keys.Length - 1].t = 1f;
    }

    // ---- Color space math ----
    static void RGBToOKLab(Color srgb, out float L, out float A, out float B)
    {
        // sRGB -> linear
        float r = Mathf.GammaToLinearSpace(Mathf.Clamp01(srgb.r));
        float g = Mathf.GammaToLinearSpace(Mathf.Clamp01(srgb.g));
        float b = Mathf.GammaToLinearSpace(Mathf.Clamp01(srgb.b));

        // Linear RGB -> LMS
        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

        l = Mathf.Max(0f, l); m = Mathf.Max(0f, m); s = Mathf.Max(0f, s);
        float l_ = Cbrt(l);
        float m_ = Cbrt(m);
        float s_ = Cbrt(s);

        L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
        A = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
        B = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;
    }

    static Color32 OKLabToRGB32(float L, float A, float B)
    {
        float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
        float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
        float s_ = L - 0.0894841775f * A - 1.2914855480f * B;

        float l = l_ * l_ * l_;
        float m = m_ * m_ * m_;
        float s = s_ * s_ * s_;

        float rLin = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float gLin = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float bLin = 0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

        float r = Mathf.LinearToGammaSpace(Mathf.Clamp01(rLin));
        float g = Mathf.LinearToGammaSpace(Mathf.Clamp01(gLin));
        float b = Mathf.LinearToGammaSpace(Mathf.Clamp01(bLin));

        return new Color32(
            (byte)Mathf.RoundToInt(r * 255f),
            (byte)Mathf.RoundToInt(g * 255f),
            (byte)Mathf.RoundToInt(b * 255f),
            255);
    }

    static float Cbrt(float x) => Mathf.Sign(x) * Mathf.Pow(Mathf.Abs(x), 1f / 3f);
}
