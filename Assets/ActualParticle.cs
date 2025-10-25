using Unity.Entities;
using Unity.Mathematics;

// Holds which walker entity is the "true electron"
public struct ActualParticle : IComponentData
{
    public Entity Walker;
}

// Always-updated world-space position of the true electron
public struct ActualParticlePosition : IComponentData
{
    public float2 Value;
}

// One-frame request to select a new true electron randomly
public struct SelectActualParticleRequest : IComponentData { }

// Persistent RNG for selections
public struct GlobalRng : IComponentData
{
    public uint State;
}