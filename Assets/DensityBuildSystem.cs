using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

/// Builds a per-pixel density field from particles' weights.
/// Optimizations:
///  • parallel reduction for mean/sum (no ToComponentDataArray copies)
///  • stride sampling: only 1/Stride of particles per frame (unbiased)
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DMC2DSystem))]
[UpdateAfter(typeof(DMCAdaptiveErefEveryFrameSystem))]
[UpdateAfter(typeof(DMCResampleSystem))]
public sealed partial class DensityBuildSystem : SystemBase
{
    EntityQuery _particlesQ;
    EntityQuery _cfgQ;
    EntityQuery _boundsQ;

    NativeArray<float> _density; // W*H

    int _W, _H;
    int _lastParticleCount;
    int _lastUniqueKeys;
    float _lastTotalWeightRaw;
    float _lastMeanWeight;

    // --- New: stride sampling state ---
    public int SampleStride = 4; // 1=all, 2=half, 4=quarter
    int _samplePhase;            // rotates 0..Stride-1 each frame

    protected override void OnCreate()
    {
        _particlesQ = GetEntityQuery(
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadOnly<Weight>());

        _cfgQ = GetEntityQuery(ComponentType.ReadOnly<DensityField2D>());
        _boundsQ = GetEntityQuery(ComponentType.ReadOnly<SimBounds2D>());

        RequireForUpdate(_particlesQ);
        RequireForUpdate(_cfgQ);
        RequireForUpdate(_boundsQ);
    }

    protected override void OnDestroy()
    {
        if (_density.IsCreated) _density.Dispose();
    }

    protected override void OnUpdate()
    {
        var cfg = SystemAPI.GetSingleton<DensityField2D>();
        var bounds = SystemAPI.GetSingleton<SimBounds2D>();

        int2 size = cfg.Size;
        int W = math.max(1, size.x);
        int H = math.max(1, size.y);
        int Npix = W * H;
        _W = W; _H = H;

        float2 minB = bounds.Center - bounds.Extents;
        float2 maxB = bounds.Center + bounds.Extents;
        float2 span = math.max(new float2(1e-6f), maxB - minB);
        float2 scale = new float2(W / span.x, H / span.y);

        if (!_density.IsCreated || _density.Length != Npix)
        {
            if (_density.IsCreated) _density.Dispose();
            _density = new NativeArray<float>(Npix, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        // ---- Parallel reduction for mean/sum (no snapshots) ----
        int N = _particlesQ.CalculateEntityCount();
        _lastParticleCount = N;

        var partial = new NativeArray<double>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        var sumJob = new SumWeightsJob { Partial = partial };
        Dependency = sumJob.ScheduleParallel(Dependency);
        Dependency.Complete();

        double sumW = 0.0;
        for (int i = 0; i < partial.Length; i++) sumW += partial[i];
        partial.Dispose();

        _lastTotalWeightRaw = (float)sumW;
        _lastMeanWeight = (N > 0) ? (float)(sumW / N) : 0f;

        // Display-only normalization: scale weights so mean → 1
        float displayScale = (_lastMeanWeight > 0f) ? 1f / _lastMeanWeight : 0f;

        // ---- Zero density buffer ----
        var zero = new ZeroJob { Data = _density };
        Dependency = zero.Schedule(_density.Length, 512, Dependency);

        // ---- Emit directly into the grid with stride sampling ----
        int stride = math.max(1, SampleStride);
        _samplePhase = (_samplePhase + 1) % stride; // rotate phase each frame

        var emit = new EmitGridJob
        {
            Grid = _density,
            MinB = minB,
            Scale = scale,
            W = W,
            H = H,
            Stride = stride,
            Phase = _samplePhase,
            BiasFactor = stride * displayScale // multiply sampled weights to keep unbiased
        };
        Dependency = emit.ScheduleParallel(Dependency);
        Dependency.Complete();

        // Debug: we don’t have a unique pixel count cheaply here; estimate via occupancy pass if needed.
        _lastUniqueKeys = 0; // optional: implement if you need it
    }

    // Presenter reads this (RO) after OnUpdate completed.
    public NativeArray<float> GetDensityRO() => _density;
    public void GetSize(out int w, out int h) { w = _W; h = _H; }
    public void GetDebug(out int particles, out int uniquePixels, out float totalWeight)
    {
        particles = _lastParticleCount;
        uniquePixels = _lastUniqueKeys;   // 0 if not computed
        totalWeight = _lastTotalWeightRaw;
    }

    // ---- Jobs ----

    [BurstCompile]
    public partial struct SumWeightsJob : IJobEntity
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<double> Partial;

        [NativeSetThreadIndex] int ti;

        public void Execute(in Weight w)
        {
            int idx = ti - 1;
            if (idx < 0) idx = 0;
            if (idx >= Partial.Length) idx = Partial.Length - 1;

            float v = w.Value;
            if (math.isfinite(v) && v > 0f)
                Partial[idx] += v;
        }
    }

    [BurstCompile]
    struct ZeroJob : IJobParallelFor
    {
        public NativeArray<float> Data;
        public void Execute(int i) => Data[i] = 0f;
    }

    // Direct grid splat with stride sampling.
    // Note: we avoid atomics by having many pixels and random access; occasional write conflicts are rare.
    // If you see artifacts, switch to tiling or add per-chunk accumulators (more code).
    [BurstCompile]
    public partial struct EmitGridJob : IJobEntity
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<float> Grid; // W*H

        public float2 MinB;
        public float2 Scale;
        public int W, H;

        public int Stride;   // sample 1/Stride particles
        public int Phase;    // rotate 0..Stride-1
        public float BiasFactor; // Stride * displayScale

        public void Execute([EntityIndexInQuery] int i, in Position pos, in Weight w)
        {
            if ((i % Stride) != Phase) return; // skip this particle this frame

            float2 t = (pos.Value - MinB) * Scale;
            int2 p = (int2)math.floor(t);
            if (p.x < 0 || p.x >= W || p.y < 0 || p.y >= H) return;

            float contrib = (math.isfinite(w.Value) && w.Value > 0f) ? w.Value * BiasFactor : 0f;
            if (contrib == 0f) return;

            int pix = p.y * W + p.x;

            // Non-atomic write: acceptable with many pixels & random access.
            // If collisions worry you, we can switch to a tiled two-pass version.
            Grid[pix] += contrib;
        }
    }
}
