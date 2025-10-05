// DensityBuildSystem.cs
// Build a 2D density grid from particles (Position, Weight) using bilinear splats.
// Race-free: per-thread partial grids + reduction.

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe; // NativeSetThreadIndex, DisableParallelForRestriction
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class DensityBuildSystem : SystemBase
{
    // -------- Config --------
    public int TargetWidth = 512;
    public int TargetHeight = 512;

    // If true, drop contributions outside bounds. If false, clamp to edges.
    public bool DiscardOutOfBounds = true;

    // -------- Internals --------
    EntityQuery _qWalkers;

    NativeArray<float> _density;  // size = W*H
    NativeArray<float> _partials; // size = W*H * T (T = MaxJobThreadCount)
    int _W, _H, _T, _Stride;

    // Debug
    double _sumWLast;
    int _uniquePixLast;

    protected override void OnCreate()
    {
        _qWalkers = GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadOnly<Weight>());

        RequireForUpdate(_qWalkers);

        _T = math.max(1, JobsUtility.MaxJobThreadCount);
        Allocate(TargetWidth, TargetHeight);
    }

    protected override void OnDestroy()
    {
        DisposeAll();
    }

    void DisposeAll()
    {
        if (_density.IsCreated) _density.Dispose();
        if (_partials.IsCreated) _partials.Dispose();
        _W = _H = _Stride = 0;
    }

    void Allocate(int w, int h)
    {
        DisposeAll();
        _W = math.max(1, w);
        _H = math.max(1, h);
        _Stride = _W * _H;

        _density = new NativeArray<float>(_Stride, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _partials = new NativeArray<float>(_Stride * _T, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    public void SetResolution(int w, int h)
    {
        if (w == _W && h == _H) return;
        Allocate(w, h);
    }

    public void GetSize(out int w, out int h) { w = _W; h = _H; }
    public NativeArray<float> GetDensityRO() => _density;

    public void GetDebug(out int particles, out int uniquePixels, out float totalW)
    {
        particles = _qWalkers.CalculateEntityCount();
        uniquePixels = _uniquePixLast;
        totalW = (float)_sumWLast;
    }

    protected override void OnUpdate()
    {
        if (_W <= 0 || _H <= 0 || !_density.IsCreated || !_partials.IsCreated)
            return;

        var b = SystemAPI.GetSingleton<SimBounds2D>();
        float2 minB = b.Center - b.Extents;
        float2 sizeB = b.Extents * 2f;

        // 0) Clear partial buffers
        var zero = new ZeroJob { Arr = _partials };
        Dependency = zero.ScheduleParallel(_partials.Length, 4096, Dependency);

        // 1) Sum of all weights (for debugging/verification)
        var partSum = new NativeArray<double>(_T, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        Dependency = new SumWeightsJob
        {
            Partial = partSum
        }.ScheduleParallel(Dependency);
        Dependency.Complete(); // needed soon for debug

        double sumW = 0.0;
        for (int i = 0; i < _T; i++) sumW += partSum[i];
        partSum.Dispose();
        _sumWLast = sumW;

        // 2) Scatter each particle into per-thread partial grid (bilinear splat)
        var scatter = new ScatterJob
        {
            MinB = minB,
            SizeB = sizeB,
            Width = _W,
            Height = _H,
            Stride = _Stride,
            Partials = _partials,
            Discard = DiscardOutOfBounds ? 1 : 0
        };
        Dependency = scatter.ScheduleParallel(Dependency);

        // 3) Reduce partials → final density
        var reduce = new ReduceJob
        {
            Partials = _partials,
            Density = _density,
            Stride = _Stride,
            Threads = _T
        };
        Dependency = reduce.ScheduleParallel(_Stride, 8192, Dependency);

        // 4) Compute unique pixel count (how many > 0). Optional; cheap enough.
        var unique = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        var count = new CountNonZeroJob
        {
            Data = _density,
            OutCount = unique
        };
        Dependency = count.ScheduleParallel(_Stride, 8192, Dependency);
        Dependency.Complete();
        _uniquePixLast = unique[0];
        unique.Dispose();
    }

    // -------- Jobs --------

    [BurstCompile]
    public struct ZeroJob : IJobFor
    {
        public NativeArray<float> Arr;
        public void Execute(int i) => Arr[i] = 0f;
    }

    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct SumWeightsJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public NativeArray<double> Partial;
        [NativeSetThreadIndex] int ti;

        public void Execute(in Weight w)
        {
            int idx = math.clamp(ti - 1, 0, Partial.Length - 1);
            float val = w.Value;
            if (math.isfinite(val) && val > 0f) Partial[idx] += val;
        }
    }

    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct ScatterJob : IJobEntity
    {
        public float2 MinB;
        public float2 SizeB;
        public int Width;
        public int Height;
        public int Stride;
        public int Discard; // 1 => drop OOB; 0 => clamp

        [NativeDisableParallelForRestriction] public NativeArray<float> Partials;
        [NativeSetThreadIndex] int ti;

        public void Execute(in Position pos, in Weight w)
        {
            float2 rel = (pos.Value - MinB) / SizeB; // 0..1 across sim bounds

            if (Discard == 1)
            {
                if (rel.x <= 0f || rel.x >= 1f || rel.y <= 0f || rel.y >= 1f) return;
            }
            else
            {
                rel = math.saturate(rel);
            }

            // Pixel space
            float px = rel.x * (Width - 1);
            float py = rel.y * (Height - 1);

            int x0 = (int)math.floor(px);
            int y0 = (int)math.floor(py);
            int x1 = math.min(x0 + 1, Width - 1);
            int y1 = math.min(y0 + 1, Height - 1);

            float fx = px - x0;
            float fy = py - y0;

            float w00 = (1f - fx) * (1f - fy);
            float w10 = fx * (1f - fy);
            float w01 = (1f - fx) * fy;
            float w11 = fx * fy;

            float val = w.Value;
            if (!math.isfinite(val) || val <= 0f) return;

            int baseIx = math.clamp(ti - 1, 0, JobsUtility.MaxJobThreadCount - 1) * Stride;

            int i00 = baseIx + y0 * Width + x0;
            int i10 = baseIx + y0 * Width + x1;
            int i01 = baseIx + y1 * Width + x0;
            int i11 = baseIx + y1 * Width + x1;

            Partials[i00] += val * w00;
            Partials[i10] += val * w10;
            Partials[i01] += val * w01;
            Partials[i11] += val * w11;
        }
    }

    [BurstCompile]
    public struct ReduceJob : IJobFor
    {
        [ReadOnly] public NativeArray<float> Partials;
        public NativeArray<float> Density;
        public int Stride;
        public int Threads;

        public void Execute(int i)
        {
            float s = 0f;
            int idx = i;
            for (int t = 0; t < Threads; t++)
            {
                s += Partials[idx];
                idx += Stride;
            }
            Density[i] = s;
        }
    }

    [BurstCompile]
    public struct CountNonZeroJob : IJobFor
    {
        [ReadOnly] public NativeArray<float> Data;
        [NativeDisableParallelForRestriction] public NativeArray<int> OutCount;

        public void Execute(int i)
        {
            if (Data[i] > 0f)
            {
                // benign contention; tiny integer increments
                int v = OutCount[0];
                OutCount[0] = v + 1;
            }
        }
    }
}
