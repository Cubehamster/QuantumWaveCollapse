using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public sealed class ActualParticlesAuthoring : MonoBehaviour
{
    [Min(1)] public int desiredCount = 3;
    public uint seed = 12345;

    class Baker : Baker<ActualParticlesAuthoring>
    {
        public override void Bake(ActualParticlesAuthoring a)
        {
            var e = GetEntity(TransformUsageFlags.None);

            // Singleton that stores how many we want
            AddComponent(e, new ActualParticleSet { DesiredCount = math.max(1, a.desiredCount) });

            // RNG state for deterministic selections
            uint s = (a.seed == 0u || a.seed == 0xFFFFFFFFu) ? 1u : a.seed;
            AddComponent(e, new ActualParticleRng { Value = s });

            // Dynamic buffer of chosen walkers (filled at runtime)
            AddBuffer<ActualParticleRef>(e);
        }
    }
}

/// <summary>Singleton component holding target count for actual particles.</summary>
public struct ActualParticleSet : IComponentData
{
    public int DesiredCount;
}

/// <summary>RNG state component (on the same singleton entity).</summary>
public struct ActualParticleRng : IComponentData
{
    public uint Value;
}

/// <summary>Buffer of references to the chosen "actual" walker entities.</summary>
public struct ActualParticleRef : IBufferElementData
{
    public Entity Walker;
}
