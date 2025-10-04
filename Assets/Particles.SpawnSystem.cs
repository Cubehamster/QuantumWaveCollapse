using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))] // keep polling until request shows
public partial struct ParticleSpawnSystem : ISystem
{
    EntityQuery _q;
    bool _done;

    public void OnCreate(ref SystemState state)
    {
        _q = state.GetEntityQuery(
            ComponentType.ReadOnly<SpawnRequest>(),
            ComponentType.ReadOnly<SimBounds2D>());
        // NOTE: don't RequireForUpdate; we want to tick while waiting.
    }

    public void OnUpdate(ref SystemState state)
    {
        if (_done) return;
        if (_q.IsEmpty) return; // keep waiting for SubScene to load/bake

        var ecb = new EntityCommandBuffer(Allocator.TempJob);
        var ecbP = ecb.AsParallelWriter();

        // Track which request entities we must consume AFTER the job finishes.
        var toConsume = new NativeList<Entity>(Allocator.Temp);

        foreach (var (req, bounds, entity) in
                 SystemAPI.Query<RefRO<SpawnRequest>, RefRO<SimBounds2D>>().WithEntityAccess())
        {
            int count = math.max(0, req.ValueRO.Count);

            if (count > 0)
            {
                var job = new SpawnJob
                {
                    Count = count,
                    Bounds = bounds.ValueRO,
                    MinSpeed = req.ValueRO.MinSpeed,
                    MaxSpeed = req.ValueRO.MaxSpeed,
                    MinWeight = req.ValueRO.MinWeight,
                    MaxWeight = req.ValueRO.MaxWeight,
                    SeedBase = math.max(1u, req.ValueRO.Seed),
                    ECB = ecbP
                };
                state.Dependency = job.ScheduleParallel(count, 1024, state.Dependency);
            }

            toConsume.Add(entity); // we'll remove SpawnRequest after Complete()
        }

        // ✔️ Ensure no job is still writing to the ECB
        state.Dependency.Complete();

        // Now it's safe to write to the same ECB on the main thread.
        for (int i = 0; i < toConsume.Length; i++)
            ecb.RemoveComponent<SpawnRequest>(toConsume[i]);

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
        toConsume.Dispose();

        _done = true; // spawned once; stop polling
    }

    [BurstCompile]
    struct SpawnJob : IJobFor
    {
        public int Count;
        public SimBounds2D Bounds;
        public float MinSpeed, MaxSpeed;
        public float MinWeight, MaxWeight;
        public uint SeedBase;
        public EntityCommandBuffer.ParallelWriter ECB;

        public void Execute(int i)
        {
            var rng = Unity.Mathematics.Random.CreateFromIndex((uint)i + SeedBase);

            float2 r = rng.NextFloat2(-1f, 1f);
            float2 pos = Bounds.Center + r * Bounds.Extents;

            float ang = rng.NextFloat(0f, 2f * math.PI);
            float2 dir = new float2(math.cos(ang), math.sin(ang));
            float spd = rng.NextFloat(MinSpeed, MaxSpeed);
            float2 vel = dir * spd;

            float w = rng.NextFloat(MinWeight, MaxWeight);

            int key = i;
            // ... inside SpawnJob.Execute(...)
            uint seed = (uint)i + math.max(1u, SeedBase);
            var e = ECB.CreateEntity(key);
            ECB.AddComponent(key, e, new ParticleTag());
            ECB.AddComponent(key, e, new Position { Value = pos });
            ECB.AddComponent(key, e, new Velocity { Value = vel });    // not used by DMC, ok
            ECB.AddComponent(key, e, new Weight { Value = w });
            ECB.AddComponent(key, e, new RandomState { Value = seed });
        }
    }
}
