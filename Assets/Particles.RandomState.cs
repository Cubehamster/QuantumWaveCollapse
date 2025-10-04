using Unity.Entities;

public struct RandomState : IComponentData
{
    public uint Value; // Unity.Mathematics.Random state
}
