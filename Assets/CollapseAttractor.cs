using Unity.Entities;
using Unity.Mathematics;

public struct CollapseAttractor : IComponentData
{
    public float2 Center;
    public float Strength;   // acceleration scale applied in LangevinWalkSystem
    public float DecayRate;  // per-second exponential decay
    public float TimeLeft;   // seconds
}