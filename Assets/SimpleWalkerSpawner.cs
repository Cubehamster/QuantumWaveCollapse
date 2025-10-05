using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct SimpleWalkerSpawnerSystem : ISystem
{
    EntityQuery _spawnerQ;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _spawnerQ = state.GetEntityQuery(ComponentType.ReadOnly<SimpleWalkerSpawner>());
        state.RequireForUpdate(_spawnerQ);
        state.RequireForUpdate<SimBounds2D>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // Use the ECB that matches our group
        var ecbSingleton = SystemAPI.GetSingleton<EndInitializationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged);
        var ecbPar = ecb.AsParallelWriter();

        var bounds = SystemAPI.GetSingleton<SimBounds2D>();
        float2 minB = bounds.Center - bounds.Extents;
        float2 maxB = bounds.Center + bounds.Extents;

        var ents = _spawnerQ.ToEntityArray(Allocator.Temp);
        var datas = _spawnerQ.ToComponentDataArray<SimpleWalkerSpawner>(Allocator.Temp);

        for (int i = 0; i < ents.Length; i++)
        {
            var sd = datas[i];
            int count = math.max(0, sd.Count);
            if (count > 0)
            {
                var job = new SpawnJob
                {
                    ECB = ecbPar,
                    Count = count,
                    Seed = SanitizeSeed(sd.Seed),
                    MinB = minB,
                    MaxB = maxB,
                    Center = bounds.Center,
                    Sigma = math.max(1e-4f, sd.GaussianSigma),
                    Mode = sd.Mode
                };
                state.Dependency = job.Schedule(count, 256, state.Dependency);
            }

            // Remove the spawner tag immediately so this runs once.
            // (We avoid writing to the same ECB on main thread.)
            state.EntityManager.RemoveComponent<SimpleWalkerSpawner>(ents[i]);
        }

        // IMPORTANT: Complete now so ECB playback happens after job finished.
        // This avoids the need for AddJobHandleForProducer and the ownership error.
        state.Dependency.Complete();
    }

    static uint SanitizeSeed(uint s)
    {
        if (s == 0u) return 1u;
        if (s == 0xFFFFFFFFu) return 0xFFFFFFFEu;
        return s;
    }

    [BurstCompile]
    struct SpawnJob : IJobParallelFor
    {
        public EntityCommandBuffer.ParallelWriter ECB;

        public int Count;
        public uint Seed;
        public float2 MinB;
        public float2 MaxB;
        public float2 Center;
        public float Sigma;
        public WalkerInitMode Mode;

        public void Execute(int index)
        {
            uint si = Seed ^ ((uint)index * 747796405u + 2891336453u);
            if (si == 0u || si == 0xFFFFFFFFu) si = 1u;
            var rng = Unity.Mathematics.Random.CreateFromIndex(si);

            float2 pos;
            if (Mode == WalkerInitMode.UniformInBounds)
            {
                float2 u = rng.NextFloat2();
                pos = MinB + u * (MaxB - MinB);
            }
            else
            {
                pos = Center + Sigma * Gaussian2(ref rng);
                pos = math.clamp(pos, MinB, MaxB);
            }

            var e = ECB.CreateEntity(index);
            ECB.AddComponent(index, e, new WalkerTag());
            ECB.AddComponent(index, e, new ParticleTag());
            ECB.AddComponent(index, e, new Position { Value = pos });
            ECB.AddComponent(index, e, new Velocity { Value = float2.zero });
            ECB.AddComponent(index, e, new Force { Value = float2.zero });

            uint st = rng.state;
            if (st == 0u || st == 0xFFFFFFFFu) st = 1u;
            ECB.AddComponent(index, e, new RandomState { Value = st });
        }

        static float2 Gaussian2(ref Unity.Mathematics.Random rng)
        {
            float u1 = math.max(1e-7f, rng.NextFloat());
            float u2 = rng.NextFloat();
            float r = math.sqrt(-2f * math.log(u1));
            float a = 2f * math.PI * u2;
            return new float2(r * math.cos(a), r * math.sin(a));
        }
    }
}
