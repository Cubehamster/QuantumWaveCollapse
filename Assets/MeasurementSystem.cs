// MeasurementSystem.cs — probability check + alias-safe in-place recycle + decaying ClickForce
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;   // NativeSetThreadIndex / DisableParallelForRestriction
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;         // JobsUtility.MaxJobThreadCount
using Unity.Mathematics;
using UnityEngine;

public struct MeasurementRequest : IComponentData
{
    public float2 Center;
    public float Radius;        // measurement radius
    public float PushRadius;    // outer influence radius for ClickForce
    public float PushStrength;  // ClickForce acceleration magnitude near inner radius (units/s^2)
    public uint Seed;          // RNG seed for Bernoulli trial
}

public struct MeasurementResult : IComponentData
{
    public float2 Center;
    public float Radius;
    public float InsideWeight;
    public float TotalWeight;
    public float Probability;
    public int InsideCount;
    public int DestroyedCount; // used as RecycledCount for compatibility
    public byte Success; // 1 = "particle was here", 0 = not
}

/// Decaying radial force spawned per request (read by DMC2DSystem to accelerate particles over time).
public struct ClickForce : IComponentData
{
    public float2 Center;
    public float InnerRadius;
    public float OuterRadius;
    public float Strength;      // accel near InnerRadius (units/s^2)
    public float DecaySeconds;  // 1/e decay time
    public double StartTime;     // Time.ElapsedTime at spawn
}

/// Marker added to walkers chosen for recycling, holds the donor entity.
public struct RecycleMarker : IComponentData
{
    public Entity Donor;
}

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DMC2DSystem))]          // after diffusion/weight update
[UpdateBefore(typeof(DensityBuildSystem))]  // so density sees recycled walkers immediately
public partial struct MeasurementSystem : ISystem
{
    EntityQuery _reqQ;
    EntityQuery _walkerQ;
    EntityQuery _markedQ;

    public void OnCreate(ref SystemState state)
    {
        _reqQ = state.GetEntityQuery(ComponentType.ReadOnly<MeasurementRequest>());

        // Walkers we operate on:
        _walkerQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadOnly<Weight>(),
            ComponentType.ReadOnly<Velocity>(),
            ComponentType.ReadOnly<RandomState>());

        // Marked walkers (after mark phase)
        _markedQ = state.GetEntityQuery(
            ComponentType.ReadOnly<RecycleMarker>(),
            ComponentType.ReadWrite<Position>(),
            ComponentType.ReadWrite<Weight>(),
            ComponentType.ReadWrite<Velocity>(),
            ComponentType.ReadWrite<RandomState>());

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

        // ---- Sum total weight (parallel reduction) ----
        var partialTotal = new NativeArray<double>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        state.Dependency = new SumWeightsJob { Partial = partialTotal }.ScheduleParallel(state.Dependency);
        state.Dependency.Complete();
        double totalW = 0.0;
        for (int i = 0; i < partialTotal.Length; i++) totalW += partialTotal[i];
        partialTotal.Dispose();

        // Donor pool (entities) for recycling; read-only in mark job & later on main thread
        var donors = _walkerQ.ToEntityArray(Allocator.TempJob);

        for (int r = 0; r < requests.Length; r++)
        {
            var req = requests[r];
            float2 C = req.Center;
            float R = math.max(1e-6f, req.Radius);
            float Rpush = math.max(R, req.PushRadius);

            // Inside sums and counts (parallel)
            var partialInside = new NativeArray<double>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var partialCountI = new NativeArray<int>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            state.Dependency = new SumInsideJob
            {
                Center = C,
                Radius2 = R * R,
                Partial = partialInside,
                Count = partialCountI
            }.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            double insideW = 0.0; int insideCount = 0;
            for (int i = 0; i < partialInside.Length; i++) insideW += partialInside[i];
            for (int i = 0; i < partialCountI.Length; i++) insideCount += partialCountI[i];
            partialInside.Dispose(); partialCountI.Dispose();

            // Probability & Bernoulli
            float p = (float)(insideW / math.max(1e-12, totalW));
            p = math.saturate(p);
            var rng = Unity.Mathematics.Random.CreateFromIndex(req.Seed != 0 ? req.Seed : 1u);
            bool success = rng.NextFloat() < p;

            int recycled = 0;

            if (!success && insideCount > 0 && donors.IsCreated && donors.Length > 0)
            {
                // --- Phase 1: mark all inside with a RecycleMarker (donor chosen per entity) ---
                var ecb = new EntityCommandBuffer(Allocator.TempJob);
                state.Dependency = new MarkRecycleInsideJob
                {
                    Center = C,
                    Radius2 = R * R,
                    Donors = donors,
                    ECB = ecb.AsParallelWriter()
                }.ScheduleParallel(state.Dependency);
                state.Dependency.Complete();

                ecb.Playback(state.EntityManager);
                ecb.Dispose();

                // --- Phase 2: apply recycling on main thread (copy donor data, then remove marker) ---
                var em = state.EntityManager;
                var marked = _markedQ.ToEntityArray(Allocator.TempJob);

                // Use direct component access on main thread (safe & simple)
                for (int i = 0; i < marked.Length; i++)
                {
                    var e = marked[i];
                    var m = em.GetComponentData<RecycleMarker>(e);
                    if (!em.Exists(m.Donor)) continue; // donor might have been removed, rare

                    // Read donor state
                    var dPos = em.GetComponentData<Position>(m.Donor);
                    var dW = em.GetComponentData<Weight>(m.Donor);
                    var dVel = em.GetComponentData<Velocity>(m.Donor);
                    var dRnd = em.GetComponentData<RandomState>(m.Donor);

                    // Tiny jitter to avoid exact duplicates
                    var rloc = Unity.Mathematics.Random.CreateFromIndex((uint)(dRnd.Value ^ (uint)i + 1u));
                    float2 jitter = 0.001f * new float2(rloc.NextFloat(-1f, 1f), rloc.NextFloat(-1f, 1f));

                    // Write into target
                    var tPos = em.GetComponentData<Position>(e);
                    tPos.Value = dPos.Value + jitter;
                    em.SetComponentData(e, tPos);

                    var tW = em.GetComponentData<Weight>(e);
                    tW.Value = math.max(1e-8f, dW.Value);
                    em.SetComponentData(e, tW);

                    var tVel = em.GetComponentData<Velocity>(e);
                    tVel.Value = dVel.Value;
                    em.SetComponentData(e, tVel);

                    var tRnd = em.GetComponentData<RandomState>(e);
                    uint newSeed = dRnd.Value ^ rloc.NextUInt();
                    if (newSeed == 0u) newSeed = 1u;
                    tRnd.Value = newSeed;
                    em.SetComponentData(e, tRnd);

                    // Remove marker
                    em.RemoveComponent<RecycleMarker>(e);

                    recycled++;
                }

                marked.Dispose();
            }

            // Spawn a decaying radial ClickForce (handled by DMC2DSystem)
            {
                var ecbForce = new EntityCommandBuffer(Allocator.Temp);
                var fe = ecbForce.CreateEntity();
                ecbForce.AddComponent(fe, new ClickForce
                {
                    Center = C,
                    InnerRadius = R,
                    OuterRadius = Rpush,
                    Strength = req.PushStrength,
                    DecaySeconds = 0.40f,
                    StartTime = SystemAPI.Time.ElapsedTime
                });
                ecbForce.Playback(state.EntityManager);
                ecbForce.Dispose();
            }

            // Emit result (optional)
            {
                var ecbRes = new EntityCommandBuffer(Allocator.Temp);
                var resE = ecbRes.CreateEntity();
                ecbRes.AddComponent(resE, new MeasurementResult
                {
                    Center = C,
                    Radius = R,
                    InsideWeight = (float)insideW,
                    TotalWeight = (float)totalW,
                    Probability = p,
                    InsideCount = insideCount,
                    DestroyedCount = recycled, // recycled count
                    Success = (byte)(success ? 1 : 0)
                });
                ecbRes.Playback(state.EntityManager);
                ecbRes.Dispose();
            }

            // Consume request
            var ecbReq = new EntityCommandBuffer(Allocator.Temp);
            ecbReq.DestroyEntity(reqEntities[r]);
            ecbReq.Playback(state.EntityManager);
            ecbReq.Dispose();

            Debug.Log($"[Measure] center={C} R={R} totalW={totalW:0.###} insideW={insideW:0.###} p={p:0.#####} success={success} recycled={recycled}");
        }

        donors.Dispose();
        requests.Dispose();
        reqEntities.Dispose();
    }

    // ---------------- Jobs ----------------

    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
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
    [WithAll(typeof(ParticleTag))]
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

    /// Phase 1 (job): mark all walkers inside the radius with a RecycleMarker that stores the chosen donor.
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct MarkRecycleInsideJob : IJobEntity
    {
        public float2 Center;
        public float Radius2;

        [ReadOnly] public NativeArray<Entity> Donors;
        public EntityCommandBuffer.ParallelWriter ECB;

        public void Execute([EntityIndexInQuery] int sortKey, Entity e, in Position pos, ref RandomState rnd)
        {
            float2 d = pos.Value - Center;
            if (math.lengthsq(d) > Radius2) return;

            // Use each entity's RNG to pick a donor
            var r = Unity.Mathematics.Random.CreateFromIndex(math.max(1u, rnd.Value));
            int idx = (Donors.Length > 0) ? r.NextInt(Donors.Length) : 0;
            if (idx < 0) idx = 0;
            if (idx >= Donors.Length) return;

            Entity donor = Donors[idx];

            // Optionally decorrelate RNG here (we also reseed in apply phase)
            uint newSeed = r.NextUInt(); if (newSeed == 0u) newSeed = 1u;
            rnd.Value = newSeed;

            ECB.AddComponent(sortKey, e, new RecycleMarker { Donor = donor });
        }
    }
}
