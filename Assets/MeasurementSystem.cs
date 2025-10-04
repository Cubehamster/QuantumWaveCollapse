// MeasurementSystem.cs
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine; // <<< needed for Debug.Log

public struct MeasurementRequest : IComponentData
{
    public float2 Center;
    public float Radius;        // destroy threshold (on failure)
    public float PushRadius;    // effect radius (on failure)
    public float PushStrength;  // displacement scale at center
    public uint Seed;          // RNG for Bernoulli
}

public struct MeasurementResult : IComponentData
{
    public float2 Center;
    public float Radius;
    public float InsideWeight;
    public float TotalWeight;
    public float Probability;
    public int InsideCount;
    public int PushCount;
    public byte Success; // 1 = particle "was here", 0 = not
}

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DMC2DSystem))]
[UpdateBefore(typeof(DensityBuildSystem))]
public partial struct MeasurementSystem : ISystem
{
    EntityQuery _reqQ;
    EntityQuery _walkerQ;

    public void OnCreate(ref SystemState state)
    {
        _reqQ = state.GetEntityQuery(ComponentType.ReadOnly<MeasurementRequest>());
        _walkerQ = state.GetEntityQuery(
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadOnly<Weight>(),
            ComponentType.ReadOnly<ParticleTag>());

        state.RequireForUpdate(_walkerQ);
    }

    public void OnUpdate(ref SystemState state)
    {
        if (_reqQ.IsEmpty) return;

        var requests = _reqQ.ToComponentDataArray<MeasurementRequest>(Allocator.Temp);
        var reqEntities = _reqQ.ToEntityArray(Allocator.Temp);
        int walkerCount = _walkerQ.CalculateEntityCount();

        if (walkerCount == 0)
        {
            var ecbNow = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < reqEntities.Length; i++) ecbNow.DestroyEntity(reqEntities[i]);
            ecbNow.Playback(state.EntityManager);
            ecbNow.Dispose();
            requests.Dispose(); reqEntities.Dispose();
            Debug.LogWarning("[Measure] No walkers present.");
            return;
        }

        // Sum total weight (parallel)
        var partialTotal = new NativeArray<double>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        state.Dependency = new SumWeightsJob { Partial = partialTotal }.ScheduleParallel(state.Dependency);
        state.Dependency.Complete();
        double total = 0.0;
        for (int i = 0; i < partialTotal.Length; i++) total += partialTotal[i];
        partialTotal.Dispose();

        var ecbSys = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSys.CreateCommandBuffer(state.WorldUnmanaged);
        var ecbPar = ecb.AsParallelWriter();

        for (int r = 0; r < requests.Length; r++)
        {
            var req = requests[r];
            float2 C = req.Center;
            float R = math.max(1e-6f, req.Radius);
            float Rpush = math.max(R, req.PushRadius);

            // Inside sums and counts
            var partialInside = new NativeArray<double>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var partialCountI = new NativeArray<int>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var insideJob = new SumInsideJob
            {
                Center = C,
                Radius2 = R * R,
                Partial = partialInside,
                Count = partialCountI
            };
            state.Dependency = insideJob.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            double inside = 0.0; int insideCount = 0;
            for (int i = 0; i < partialInside.Length; i++) inside += partialInside[i];
            for (int i = 0; i < partialCountI.Length; i++) insideCount += partialCountI[i];
            partialInside.Dispose(); partialCountI.Dispose();

            // Push annulus counts (R..Rpush]
            var partialCountP = new NativeArray<int>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var pushCountJob = new CountAnnulusJob
            {
                Center = C,
                R2 = R * R,
                Rpush2 = Rpush * Rpush,
                Count = partialCountP
            };
            state.Dependency = pushCountJob.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
            int pushCount = 0;
            for (int i = 0; i < partialCountP.Length; i++) pushCount += partialCountP[i];
            partialCountP.Dispose();

            float p = (float)(inside / math.max(1e-12, total));
            p = math.saturate(p);

            var rng = Unity.Mathematics.Random.CreateFromIndex(req.Seed != 0 ? req.Seed : 1u);
            bool success = rng.NextFloat() < p;

            Debug.Log($"[Measure] req center={C} R={R} totalW={total:0.###} insideW={inside:0.###} p={p:0.#####} success={success} insideCount={insideCount} pushCount={pushCount}");

            // Emit result entity
            var resE = ecb.CreateEntity();
            ecb.AddComponent(resE, new MeasurementResult
            {
                Center = C,
                Radius = R,
                InsideWeight = (float)inside,
                TotalWeight = (float)total,
                Probability = p,
                InsideCount = insideCount,
                PushCount = pushCount,
                Success = (byte)(success ? 1 : 0)
            });

            if (!success)
            {
                var job = new DestroyAndPushJob
                {
                    Center = C,
                    R2 = R * R,
                    Rpush2 = Rpush * Rpush,
                    PushStrength = req.PushStrength,
                    ECB = ecbPar
                };
                state.Dependency = job.ScheduleParallel(state.Dependency);
                state.Dependency.Complete();
            }

            // consume request
            ecb.DestroyEntity(reqEntities[r]);
        }

        requests.Dispose();
        reqEntities.Dispose();
    }

    // ------- Jobs -------

    [BurstCompile]
    public partial struct SumWeightsJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public NativeArray<double> Partial;
        [NativeSetThreadIndex] int ti;

        public void Execute(in Weight w)
        {
            int idx = ti - 1; if (idx < 0) idx = 0; if (idx >= Partial.Length) idx = Partial.Length - 1;
            float v = w.Value;
            if (math.isfinite(v) && v > 0f) Partial[idx] += v;
        }
    }

    [BurstCompile]
    public partial struct SumInsideJob : IJobEntity
    {
        public float2 Center;
        public float Radius2;

        [NativeDisableParallelForRestriction] public NativeArray<double> Partial;
        [NativeDisableParallelForRestriction] public NativeArray<int> Count;
        [NativeSetThreadIndex] int ti;

        public void Execute(in Position pos, in Weight w)
        {
            int idx = ti - 1; if (idx < 0) idx = 0; if (idx >= Partial.Length) idx = Partial.Length - 1;
            float2 d = pos.Value - Center;
            if (math.lengthsq(d) <= Radius2)
            {
                float v = w.Value;
                if (math.isfinite(v) && v > 0f) Partial[idx] += v;
                Count[idx] += 1;
            }
        }
    }

    [BurstCompile]
    public partial struct CountAnnulusJob : IJobEntity
    {
        public float2 Center;
        public float R2;     // inner
        public float Rpush2; // outer

        [NativeDisableParallelForRestriction] public NativeArray<int> Count;
        [NativeSetThreadIndex] int ti;

        public void Execute(in Position pos)
        {
            int idx = ti - 1; if (idx < 0) idx = 0; if (idx >= Count.Length) idx = Count.Length - 1;
            float2 d = pos.Value - Center;
            float d2 = math.lengthsq(d);
            if (d2 > R2 && d2 <= Rpush2) Count[idx] += 1;
        }
    }

    [BurstCompile]
    public partial struct DestroyAndPushJob : IJobEntity
    {
        public float2 Center;
        public float R2;        // destroy <= R
        public float Rpush2;    // push <= Rpush
        public float PushStrength;

        public EntityCommandBuffer.ParallelWriter ECB;

        public void Execute([EntityIndexInQuery] int sortKey, Entity e, ref Position pos, ref Weight w)
        {
            float2 d = pos.Value - Center;
            float d2 = math.lengthsq(d);

            if (d2 <= R2)
            {
                ECB.DestroyEntity(sortKey, e);
                return;
            }

            if (d2 <= Rpush2 && PushStrength > 0f)
            {
                float dist = math.sqrt(math.max(1e-12f, d2));
                float2 dir = d / dist;

                float R = math.sqrt(R2);
                float Rpush = math.sqrt(Rpush2);

                // Falloff: 1 at R, 0 at Rpush (smooth)
                float t = math.saturate((dist - R) / math.max(1e-6f, (Rpush - R)));
                float falloff = 1f - t;
                falloff = falloff * falloff * (3 - 2 * falloff);

                pos.Value += dir * (PushStrength * falloff);
                // Optional: slight weight damp near click
                // w.Value *= math.saturate(1f - 0.05f * falloff);
            }
        }
    }
}
