using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DMC2DSystem))] // run after diffusion/weight update
public partial struct DMCResampleSystem : ISystem
{
    EntityQuery _walkersQ;
    EntityQuery _ctrlQ;

    public void OnCreate(ref SystemState state)
    {
        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadWrite<Position>(),
            ComponentType.ReadWrite<Weight>(),
            ComponentType.ReadOnly<ParticleTag>());

        _ctrlQ = state.GetEntityQuery(ComponentType.ReadWrite<DMCControl2D>());

        state.RequireForUpdate(_walkersQ);
        state.RequireForUpdate(_ctrlQ);
    }

    public void OnUpdate(ref SystemState state)
    {
        var ctrl = SystemAPI.GetSingleton<DMCControl2D>();
        ctrl.FrameCounter++;

        // Tick only every K frames
        if ((ctrl.FrameCounter % math.max(1, ctrl.BranchEveryFrames)) != 0)
        {
            SystemAPI.SetSingleton(ctrl);
            return;
        }

        int slices = math.max(1, ctrl.ResampleSlices);
        int tickIndex = ctrl.FrameCounter / math.max(1, ctrl.BranchEveryFrames);
        int sliceIdx = tickIndex % slices;

        // Snapshot weights & positions
        var wSnap = _walkersQ.ToComponentDataArrayAsync<Weight>(Allocator.TempJob, out var hW);
        var pSnap = _walkersQ.ToComponentDataArrayAsync<Position>(Allocator.TempJob, out var hP);
        JobHandle.CombineDependencies(hW, hP).Complete();

        int N = wSnap.Length;
        if (N == 0)
        {
            wSnap.Dispose(); pSnap.Dispose();
            SystemAPI.SetSingleton(ctrl);
            return;
        }

        // Compute slice range
        int start = (N * sliceIdx) / slices;
        int end = (N * (sliceIdx + 1)) / slices;
        int count = math.max(0, end - start);
        if (count == 0)
        {
            wSnap.Dispose(); pSnap.Dispose();
            SystemAPI.SetSingleton(ctrl);
            return;
        }

        // Sum weights (all walkers) and build CDF
        double sumW = 0.0;
        for (int i = 0; i < N; i++)
        {
            float w = math.max(0f, wSnap[i].Value);
            if (math.isfinite(w)) sumW += w;
        }

        if (sumW <= 0.0)
        {
            var reset = new ResetWeightsSliceJob { Start = start, End = end };
            state.Dependency = reset.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
            wSnap.Dispose(); pSnap.Dispose();
            SystemAPI.SetSingleton(ctrl);
            return;
        }

        var cdf = new NativeArray<float>(N, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        float acc = 0f;
        float invSum = (float)(1.0 / sumW);
        for (int i = 0; i < N; i++)
        {
            float wi = wSnap[i].Value;
            wi = math.select(0f, wi, math.isfinite(wi) & (wi > 0f));
            acc += wi * invSum;
            cdf[i] = acc;
        }
        cdf[N - 1] = 1f;

        // Systematic resampling points for THIS slice only
        var newPos = new NativeArray<float2>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        uint seed = (uint)(SystemAPI.Time.ElapsedTime * 1000.0) ^ (uint)(N + sliceIdx) | 1u;
        float u0 = Unity.Mathematics.Random.CreateFromIndex(seed).NextFloat(0f, 1f / math.max(1, N));

        var resample = new ResampleSliceJob
        {
            Cdf = cdf,
            Parents = pSnap,
            U0 = u0,
            CountGlobal = N,
            Start = start,
            End = end,
            JitterSigma = math.max(0f, ctrl.CloneJitterSigma),
            Seed = seed,
            NewPos = newPos
        };
        var h = resample.Schedule(count, 1024);
        h.Complete();

        // Write back only this slice; reset weights to 1 for updated ones
        var write = new WriteBackSliceJob
        {
            Start = start,
            End = end,
            NewPos = newPos
        };
        state.Dependency = write.ScheduleParallel(state.Dependency);
        state.Dependency.Complete();

        // Cleanup
        newPos.Dispose();
        cdf.Dispose();
        wSnap.Dispose();
        pSnap.Dispose();

        // Do NOT reset FrameCounter — it drives which slice runs next tick
        SystemAPI.SetSingleton(ctrl);
    }

    // ------- Jobs --------

    [BurstCompile]
    public partial struct ResetWeightsSliceJob : IJobEntity
    {
        public int Start, End; // [Start, End)
        public void Execute([EntityIndexInQuery] int i, ref Weight w)
        {
            if ((uint)i >= (uint)End || i < Start) return;
            w.Value = 1f;
        }
    }

    [BurstCompile]
    struct ResampleSliceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Cdf;         // size N, last=1
        [ReadOnly] public NativeArray<Position> Parents;     // size N
        public float U0;
        public int CountGlobal; // N
        public int Start, End;  // slice range
        public float JitterSigma;
        public uint Seed;

        [WriteOnly] public NativeArray<float2> NewPos;       // size = End-Start

        public void Execute(int local)
        {
            int i = Start + local; // global index
            float u = (i + U0) / CountGlobal;

            // Binary search in CDF
            int lo = 0, hi = CountGlobal - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Cdf[mid] >= u) hi = mid;
                else lo = mid + 1;
            }
            int idx = lo;

            float2 p = Parents[idx].Value;

            if (JitterSigma > 0f)
            {
                var rng = Unity.Mathematics.Random.CreateFromIndex(((uint)i) ^ Seed);
                float u1 = math.max(1e-7f, rng.NextFloat());
                float u2 = rng.NextFloat();
                float r = math.sqrt(-2f * math.log(u1)) * JitterSigma;
                float a = 2f * math.PI * u2;
                p += new float2(r * math.cos(a), r * math.sin(a));
            }

            NewPos[local] = p;
        }
    }

    [BurstCompile]
    public partial struct WriteBackSliceJob : IJobEntity
    {
        public int Start, End; // [Start, End)
        [ReadOnly] public NativeArray<float2> NewPos;

        public void Execute([EntityIndexInQuery] int i, ref Position pos, ref Weight w)
        {
            if ((uint)i >= (uint)End || i < Start) return;
            pos.Value = NewPos[i - Start];
            w.Value = 1f;
        }
    }
}
