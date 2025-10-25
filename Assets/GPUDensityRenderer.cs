using UnityEngine;
using UnityEngine.UI;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

public class GPUDensityRenderer : MonoBehaviour
{
    [Header("Hook up")]
    public RawImage target;
    public ComputeShader densityCS;

    [Tooltip("Optional material using the DensityToColor shader.")]
    public Material densityMaterial;

    [Tooltip("Fallback if no material is assigned—drag the DensityToColor Shader asset here.")]
    public Shader densityShader;

    [Header("Texture")]
    public int texWidth = 1024;
    public int texHeight = 1024;

    [Header("World mapping")]
    public Color background = new Color(0.02f, 0.03f, 0.08f, 1f);
    public float exposureK = 0.01f;
    public float gamma = 0.9f;
    public int blurRadius = 2;
    [Range(0f, 64f)] public float blurSigma = 0f;

    [Header("Gradient (sRGB LUT baked here)")]
    public Gradient srgbGradient = DefaultGradient();

    [Header("Debug")]
    public bool debugTestPattern = false;

    // runtime
    Material _mat;
    RenderTexture _countsU32;   // RInt
    RenderTexture _tempF;       // RFloat
    RenderTexture _densityF;    // RFloat
    Texture2D _lutTex;
    ComputeBuffer _posBuffer;

    int kClear = -1, kScatter = -1, kBlurH = -1, kBlurV = -1, kCopy = -1;

    PositionsUploadSystem _posSys;

    // ECS
    EntityManager _em;
    EntityQuery _boundsQ;

    bool _kernelsValid = false;

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

            _kernelsValid = (kClear >= 0 && kScatter >= 0 && kBlurH >= 0 && kBlurV >= 0 && kCopy >= 0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[GPUDensityRenderer] Kernel not found / shader compile failed: " + e.Message);
            _kernelsValid = false;
        }

        if (!_kernelsValid)
        {
            Debug.LogError("[GPUDensityRenderer] Compute shader kernels are invalid. Disabling.");
            enabled = false;
            return;
        }

        AllocateRTs();
        SetupMaterial();
    }

    void SetupMaterial()
    {
        if (densityMaterial != null)
        {
            _mat = Instantiate(densityMaterial);
        }
        else
        {
            Shader sh = densityShader ? densityShader : Shader.Find("Hidden/DensityToColor");
            if (sh == null)
            {
                Debug.LogError("[GPUDensityRenderer] No material or shader found for density display. Disabling.");
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

        // Integer scatter buffer for atomic adds
        _countsU32 = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.RInt)
        { enableRandomWrite = true };
        _countsU32.Create();

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
        if (!_kernelsValid) return;
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

        _posBuffer.SetData(positions);

        // Clear
        densityCS.SetTexture(kClear, "_Counts", _countsU32);
        Dispatch2D(kClear, texWidth, texHeight, 8, 8);

        // Scatter
        densityCS.SetTexture(kScatter, "_Counts", _countsU32);
        densityCS.SetBuffer(kScatter, "_Positions", _posBuffer);
        densityCS.SetInts("_TexSize", texWidth, texHeight);
        densityCS.SetFloats("_MinB", minB.x, minB.y);
        densityCS.SetFloats("_InvSize", invSz.x, invSz.y);
        densityCS.SetInt("_Num", count);
        Dispatch1D(kScatter, count, 256);

        // Blur or copy
        if (blurRadius > 0)
        {
            float sigma = (blurSigma > 0f) ? blurSigma : Mathf.Max(0.5f, blurRadius * 0.5f);

            densityCS.SetTexture(kBlurH, "_CountsIn", _countsU32);
            densityCS.SetTexture(kBlurH, "_Out", _tempF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            densityCS.SetInt("_Radius", blurRadius);
            densityCS.SetFloat("_Sigma", sigma);
            Dispatch2D(kBlurH, texWidth, texHeight, 8, 8);

            densityCS.SetTexture(kBlurV, "_InFloat", _tempF);
            densityCS.SetTexture(kBlurV, "_Out", _densityF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            densityCS.SetInt("_Radius", blurRadius);
            densityCS.SetFloat("_Sigma", sigma);
            Dispatch2D(kBlurV, texWidth, texHeight, 8, 8);
        }
        else
        {
            if (_countsU32 == null || !_countsU32.IsCreated()) return;
            if (_densityF == null || !_densityF.IsCreated()) return;

            densityCS.SetTexture(kCopy, "_CountsIn", _countsU32);
            densityCS.SetTexture(kCopy, "_Out", _densityF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            Dispatch2D(kCopy, texWidth, texHeight, 8, 8);
        }

        // Debug pattern (optional)
        if (debugTestPattern && densityCS.HasKernel("TestPattern"))
        {
            int kt = densityCS.FindKernel("TestPattern");
            densityCS.SetTexture(kt, "_Out", _densityF);
            densityCS.SetInts("_TexSize", texWidth, texHeight);
            Dispatch2D(kt, texWidth, texHeight, 8, 8);
        }

        // Material params
        if (_mat)
        {
            _mat.SetTexture("_Density", _densityF);
            _mat.SetColor("_Bg", background);
            _mat.SetFloat("_K", Mathf.Max(1e-6f, exposureK));
            _mat.SetFloat("_Gamma", Mathf.Max(0.001f, gamma));
        }
    }

    void Dispatch1D(int kernel, int count, int threadGroupSize)
    {
        if (kernel < 0) return;
        int groups = Mathf.Max(1, (count + threadGroupSize - 1) / threadGroupSize);
        densityCS.Dispatch(kernel, groups, 1, 1);
    }

    void Dispatch2D(int kernel, int w, int h, int tx, int ty)
    {
        if (kernel < 0) return;
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
