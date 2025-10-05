// MeasurementRequest.cs
using Unity.Entities;
using Unity.Mathematics;

public struct MeasurementRequest : IComponentData
{
    public float2 Center;
    public float Radius;

    public float Push;          // alias
    public float PushStrength;  // alias
    public float PushRadius;    // used by your click script
    public uint Seed;          // used by your click script

    public bool IsDown;        // mouse held
    public bool Continuous;    // continuous scan while held
}
