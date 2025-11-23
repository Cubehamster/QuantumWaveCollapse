using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Redistributes all ParticleTag walkers when a RedistributeWalkersRequest exists.
/// Uses the same logic as SimpleWalkerSpawner (UniformInBounds or GaussianAroundCenter).
/// Triggered by RedistributeWalkersInput Mono (new Input System).
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct RedistributeWalkersSystem : ISystem
{
    private EntityQuery _walkersQ;
    private EntityQuery _boundsQ;
    private EntityQuery _spawnerQ;
    private EntityQuery _reqQ;

    public void OnCreate(ref SystemState state)
    {
        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadWrite<Position>(),
            ComponentType.ReadOnly<ParticleTag>());

        _boundsQ = state.GetEntityQuery(
            ComponentType.ReadOnly<SimBounds2D>());

        _spawnerQ = state.GetEntityQuery(
            ComponentType.ReadOnly<SimpleWalkerSpawner>());

        _reqQ = state.GetEntityQuery(
            ComponentType.ReadOnly<RedistributeWalkersRequest>());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // Only run on frames where a request exists
        if (_reqQ.IsEmptyIgnoreFilter)
            return;

        // Consume all requests immediately (one-shot behavior)
        state.EntityManager.DestroyEntity(_reqQ);

        if (_walkersQ.IsEmptyIgnoreFilter || _boundsQ.IsEmptyIgnoreFilter)
            return;

        var bounds = SystemAPI.GetSingleton<SimBounds2D>();
        float2 minB = bounds.Center - bounds.Extents;
        float2 maxB = bounds.Center + bounds.Extents;
        float2 center = bounds.Center;

        // Defaults in case no spawner is present
        float sigma = 0.5f;
        WalkerInitMode mode = WalkerInitMode.GaussianAroundCenter;
        uint seed = 12345;

        // Use the SimpleWalkerSpawner settings if present
        if (!_spawnerQ.IsEmptyIgnoreFilter)
        {
            var spawner = SystemAPI.GetSingleton<SimpleWalkerSpawner>();
            sigma = math.max(1e-4f, spawner.GaussianSigma);
            mode = spawner.Mode;
            seed = spawner.Seed;
        }

        var em = state.EntityManager;
        using var walkers = _walkersQ.ToEntityArray(Allocator.Temp);
        int count = walkers.Length;
        if (count <= 0) return;

        uint baseSeed = Sanitize(seed);

        for (int i = 0; i < count; i++)
        {
            uint s = Sanitize(baseSeed ^ ((uint)i * 747796405u + 2891336453u));
            var rng = Unity.Mathematics.Random.CreateFromIndex(s);

            float2 pos;
            if (mode == WalkerInitMode.UniformInBounds)
            {
                float2 u = rng.NextFloat2();
                pos = minB + u * (maxB - minB);
            }
            else
            {
                // Gaussian around bounds center
                pos = center + sigma * Gaussian2(ref rng);
                pos = math.clamp(pos, minB, maxB);
            }

            if (em.HasComponent<Position>(walkers[i]))
            {
                em.SetComponentData(walkers[i], new Position { Value = pos });
            }
        }

        UnityEngine.Debug.Log($"[Redistribute] Reinitialized {count} walkers (σ={sigma}, mode={mode})");
    }

    static uint Sanitize(uint s)
    {
        if (s == 0u) return 1u;
        if (s == 0xFFFFFFFFu) return 0xFFFFFFFEu;
        return s;
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

/// <summary>
/// Tag component; presence of this indicates that the walkers should be redistributed this frame.
/// </summary>
public struct RedistributeWalkersRequest : IComponentData { }
