using Unity.Entities;
using Unity.Mathematics;

public struct DensityField2D : IComponentData
{
    public int2 Size; // width,height (e.g., 512x512 or screen size)
    public float Eps; // epsilon for log mapping (e.g., 1e-9)
}
