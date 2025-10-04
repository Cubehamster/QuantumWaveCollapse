using Unity.Entities;
using Unity.Mathematics;

public struct Rng : IComponentData
{
    public Unity.Mathematics.Random Value;
}
