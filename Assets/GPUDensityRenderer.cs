using UnityEngine;
using UnityEngine.UI;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

public class GPUDensityRenderer : MonoBehaviour
{
    [Header("Hook up")]
    public RawImage target;                 // assign your RawImage
    public ComputeShader densityCS;         // assign GPUDensity.compute

    [Tooltip("Optional: provide a pre-made material that uses the DensityToColor shader.")]
    public Material densityMaterial;

    [Tooltip("Optional fallback if you don’t assign a material. Must reference the same shader used by DensityToColor.mat.")]
    public Shader densityShader;            // e.g. the “Hidden/DensityToColor” shader asset

    [Header("Texture")]
    public int texWidth = 1024;
    public int texHeight = 1024;

    [Header("World mapping")]
    public Color background = new Color(0.02f, 0.03f, 0.08f, 1f);
    public float exposureK = 0.01f;         // exp mapping: 1-exp(-k*density)
    public float gamma = 0.9f;              // final gamma
    public int blurRadius = 2;              // 0 = off (uses copy)
    [Range(0f, 64f)] public float blurSigma = 0f; // 0 = auto (radius * 0.5)

    [Header("Gradient (sRGB LUT baked here)")]
    public Gradient srgbGradient = DefaultGradient();

    [Header("Debug")]
    public bool debugTestPattern = false;

    // runtime
    Material _mat;
    RenderTexture _countsU32;   // R32_UInt
    RenderTexture _tempF;       // R16 or RFloat
    RenderTexture _densityF;    // R16 or RFloat (final float density)
    Texture2D _lutTex;          // 256x1 LUT
    ComputeBuffer _posBuffer;

    int kClear, kScatter, kBlurH, kBlurV, kCopy;

    PositionsUploadSystem _posSys;

    // ECS access
    EntityManager _em;
    EntityQuery _boundsQ;

    void OnEnable()
    {
        if (!densityCS)
        {
            Debug.LogError("[GPUDensityRenderer] Assign GPUDensity.compute to 'densityCS'. Disabling.");
            enabled = false;
            return;
        }

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null)
        {
            Debug.LogError("[GPUDensityRenderer] No Default World. Disabling.");
            enabled = false;
            return;
        }

        _em = world.EntityManager;
        _boundsQ = _em.CreateEntityQuery(ComponentType.ReadOnly<SimBounds2D>());

        _posSys = world.GetExistingSystemManaged<PositionsUploadSystem>();
        if (_posSys == null)
        {
            Debug.LogError("[GPUDensityRenderer] PositionsUploadSystem not found. Disabling.");
            enabled = false;
            return;
        }

        // kernels
        try
        {
            kClear = densityCS.FindKernel("ClearCounts");
            kScatter = densityCS.FindKernel("ScatterPoints");
            kBlurH = densityCS.FindKernel("BlurH");
            kBlurV = densityCS.FindKernel("BlurV");
            kCopy = densityCS.FindKernel("CopyCountsToFloat");
        }
        catch
        {
            Debug.LogError("[GPUDensityRenderer] Kernel not found in compute shader. Double-check kernel names.");
            enabled = false;
            return;
        }

        AllocateRTs();

        // Material & shader setup (robust for Player builds)
        if (densityMaterial != null)
        {
            _mat = Instantiate(densityMaterial);
        }
        else
        {
            Shader sh = densityShader;
            if (sh == null)
            {
                // Last-resort lookup (can be stripped in Player if not referenced elsewhere)
                sh = Shader.Find("Hidden/DensityToColor");
            }

            if (sh == null)
            {
                Debug.LogError("[GPUDensityRenderer] No material assigned and shader not found. " +
                               "Create a Material that uses your DensityToColor shader and assign it in the Inspector, " +
                               "or drag the Shader asset into 'densityShader'. Disabling.");
                enabled = false;
                return;
            }

            _mat = new Material(sh);
        }

        _mat.SetTexture("_Density", _densityF);
        _mat.SetColor("_Bg", background);
        _mat.SetFloat("_K", exposureK);
        _mat.SetFloat("_Gamma", Mathf.Max(0.001f, gamma));

        _lutTex = new Texture2D(256, 1, TextureFormat.RGBA32, false, false);
        _lutTex.wrapMode = TextureWrapMode.Clamp;
        BakeLUT(_lutTex, srgbGradient);
        _mat.SetTexture("_LUT", _lutTex);

        if (target)
        {
            target.texture = _densityF;
            target.material = _mat;
            // Ensure RawImage shows unmodified color
            target.color = Color.white;
        }
    }

    void OnDisable()
    {
        ReleaseRT(ref _countsU32);
        ReleaseRT(ref _tempF);
        ReleaseRT(ref _densityF);
        if (_posBuffer != null) { _posBuffer.Release(); _posBuffer = null; }
        if (_mat) { Destroy(_mat); _mat = null; }
        if (_lutTex) { Destroy(_lutTex); _lutTex = null; }
    }

    void AllocateRTs()
    {
        ReleaseRT(ref _countsU32);
        ReleaseRT(ref _tempF);
        ReleaseRT(ref _densityF);

        _countsU32 = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = true
        };
        _countsU32.Create();

        // Use RFloat instead of RHalf – more widely supported for UAV read/write in Player builds
        _tempF = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.RFloat)
        { enableRandomWrite = true };
        _tempF.Create();

        _densityF = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.RFloat)
        { enableRandomWrite = true };
        _densityF.Create();
    }

    void ReleaseRT(ref RenderTexture rt)
    {
        if (rt != null) { rt.Release(); Destroy(rt); rt = null; }
    }

    void LateUpdate()
    {
        if (!enabled) return;
        if (_posSys == null || !_posSys.HasData) return;
        if (_boundsQ.IsEmptyIgnoreFilter) return;
        if (_countsU32 == null || _densityF == null) return;

        // Read bounds
        float2 minB;
        float2 invSz;
        using (var arr = _boundsQ.ToComponentDataArray<SimBounds2D>(Allocator.Temp))
        {
            var b = arr[0];
            minB = b.Center - b.Extents;
            float2 sizeB = b.Extents * 2f;
            invSz = new float2(
                1f / Mathf.Max(1e-6f, sizeB.x),
                1f / Mathf.Max(1e-6f, sizeB.y)
            );
        }

        var positions = _posSys.GetReadOnly();
        int count = positions.Length;
        if (count <= 0) return;

        // ensure buffer capacity
        if (_posBuffer == null || _posBuffer.count < count)
        {
            if (_posBuffer != null) _posBuffer.Release();
            _posBuffer = new ComputeBuffer(count, sizeof(float) * 2, ComputeBufferType.Structured);
        }

        // upload positions
        _posBuffer.SetData(positions);

        // clear counts
        densityCS.SetTexture(kClear, "_Counts", _countsU32);
        Dispatch2D(kClear, texWidth, texHeight, 8, 8);

        // scatter
        densityCS.SetTexture(kScatter, "_Counts", _countsU32);
        densityCS.SetBuffer(kScatter, "_Positions", _posBuffer);
        densityCS.SetInts("_TexSize", texWidth, texHeight);
        densityCS.SetFloats("_MinB", minB.x, minB.y);
        densityCS.SetFloats("_InvSize", invSz.x, invSz.y);
        densityCS.SetInt("_Num", count);
        Dispatch1D(kScatter, count, 256);

        if (!densityCS.HasKernel("CopyCountsToFloat"))
            Debug.LogError("Kernel CopyCountsToFloat not found!");

        int texID = Shader.PropertyToID("_CountsIn");
        if (texID == -1)
            Debug.LogError("Property _CountsIn not found on shader!");

        // Gaussian blur (separable) OR copy
        if (blurRadius > 0)
        {
            // auto sigma if not provided
            float sigma = (blurSigma > 0f) ? blurSigma : Mathf.Max(0.5f, blurRadius * 0.5f);

            // Horizontal pass
            densityCS.SetTexture(kBlurH, "_CountsIn", _countsU32);
            densityCS.SetTexture(kBlurH, "_Out", _tempF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            densityCS.SetInt("_Radius", blurRadius);
            densityCS.SetFloat("_Sigma", sigma);
            Dispatch2D(kBlurH, texWidth, texHeight, 8, 8);

            // Vertical pass
            densityCS.SetTexture(kBlurV, "_InFloat", _tempF);
            densityCS.SetTexture(kBlurV, "_Out", _densityF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            densityCS.SetInt("_Radius", blurRadius);
            densityCS.SetFloat("_Sigma", sigma);
            Dispatch2D(kBlurV, texWidth, texHeight, 8, 8);
        }
        else
        {
            // your existing copy path (unchanged)
            if (_countsU32 == null || !_countsU32.IsCreated()) return;
            if (_densityF == null || !_densityF.IsCreated()) return;

            densityCS.SetTexture(kCopy, "_CountsIn", _countsU32);
            densityCS.SetTexture(kCopy, "_Out", _densityF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            Dispatch2D(kCopy, texWidth, texHeight, 8, 8);
        }



        if (debugTestPattern)
        {
            // Optional: if you added a TestPattern kernel in your compute, draw it here.
            int kt;
            if (TryKernel("TestPattern", out kt))
            {
                densityCS.SetTexture(kt, "_Out", _densityF);
                densityCS.SetInts("_TexSize", texWidth, texHeight);
                Dispatch2D(kt, texWidth, texHeight, 8, 8);
            }
        }

        // material params
        if (_mat)
        {
            _mat.SetTexture("_Density", _densityF);
            _mat.SetColor("_Bg", background);
            _mat.SetFloat("_K", Mathf.Max(1e-6f, exposureK));
            _mat.SetFloat("_Gamma", Mathf.Max(0.001f, gamma));
        }
    }

    bool TryKernel(string name, out int id)
    {
        id = -1;
        try { id = densityCS.FindKernel(name); return true; }
        catch { return false; }
    }

    void Dispatch1D(int kernel, int count, int threadGroupSize)
    {
        int groups = Mathf.Max(1, (count + threadGroupSize - 1) / threadGroupSize);
        densityCS.Dispatch(kernel, groups, 1, 1);
    }

    void Dispatch2D(int kernel, int w, int h, int tx, int ty)
    {
        int gx = (w + tx - 1) / tx;
        int gy = (h + ty - 1) / ty;
        densityCS.Dispatch(kernel, gx, gy, 1);
    }

    public static Gradient DefaultGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[] {
                new GradientColorKey(new Color(0.05f, 0.08f, 0.20f), 0f),
                new GradientColorKey(new Color(0.00f, 0.70f, 1.00f), 0.50f),
                new GradientColorKey(new Color(1.00f, 0.65f, 0.20f), 0.85f),
                new GradientColorKey(Color.white,                     1f),
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
        );
        return g;
    }

    static void BakeLUT(Texture2D tex, Gradient g)
    {
        var cols = new Color[tex.width];
        for (int i = 0; i < tex.width; i++)
        {
            float t = (tex.width == 1) ? 0f : (float)i / (tex.width - 1);
            cols[i] = g.Evaluate(t);
        }
        tex.SetPixels(cols);
        tex.Apply(false, true);
    }
}
