using Unity.Entities;
using Unity.Mathematics;

// Tag stays the same
public struct ParticleTag : IComponentData { }

// 2D position/velocity (XY). Smaller & faster than float3 for 2D sims.
public struct Position : IComponentData { public float2 Value; }
public struct Velocity : IComponentData { public float2 Value; }

// Axis-aligned 2D bounds: [Center-Extents, Center+Extents] in XY
public struct SimBounds2D : IComponentData
{
    public float2 Center;
    public float2 Extents; // half-size (>= 0)
}

// Runtime spawn request (2D). Speed is scalar.
public struct SpawnRequest : IComponentData
{
    public int Count;
    public float MinSpeed;
    public float MaxSpeed;
    public float MinWeight;
    public float MaxWeight;
    public uint Seed;
}

// New: per-particle weight (arbitrary units)
public struct Weight : IComponentData
{
    public float Value;
}
