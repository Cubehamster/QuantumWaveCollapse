using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public sealed class ActualParticlesAuthoring : MonoBehaviour
{
    [Header("How many actuals should be active at start")]
    [Min(1)] public int desiredActiveCount = 2;

    [Header("Total pool size (max number of actual slots)")]
    [Min(1)] public int poolSize = 50;

    [Header("Random seed for selecting walkers")]
    public uint seed = 12345;

    class Baker : Baker<ActualParticlesAuthoring>
    {
        public override void Bake(ActualParticlesAuthoring a)
        {
            var e = GetEntity(TransformUsageFlags.None);

            int desired = math.max(1, a.desiredActiveCount);
            int pool = math.max(desired, a.poolSize);

            // Singleton that stores counts and debug info
            AddComponent(e, new ActualParticleSet
            {
                DesiredCount = desired,
                PoolSize = pool,
                TargetActive = 0
            });

            // RNG state for deterministic selections
            uint s = (a.seed == 0u || a.seed == 0xFFFFFFFFu) ? 1u : a.seed;
            AddComponent(e, new ActualParticleRng { Value = s });

            // Pool buffers (length will be sized in ActualParticlePoolSystem)
            AddBuffer<ActualParticleRef>(e);
            AddBuffer<ActualParticlePositionElement>(e);
            AddBuffer<ActualParticleStatusElement>(e);
        }
    }
}

/// <summary>
/// Singleton component holding target counts for actual particles.
/// </summary>
public struct ActualParticleSet : IComponentData
{
    /// <summary>How many actuals should be active at startup.</summary>
    public int DesiredCount;

    /// <summary>Maximum number of slots in the pool.</summary>
    public int PoolSize;

    /// <summary>Debug / info: how many are currently active (Walker != Entity.Null).</summary>
    public int TargetActive;
}

public struct ActualParticleRng : IComponentData
{
    public uint Value;
}

public struct ActualParticleRef : IBufferElementData
{
    public Entity Walker;
}
