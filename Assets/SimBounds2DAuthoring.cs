// SimBounds2DAuthoring.cs
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct SimBounds2D : IComponentData
{
    public float2 Center;
    public float2 Extents; // half-size
}

public sealed class SimBounds2DAuthoring : MonoBehaviour
{
    public Vector2 center = Vector2.zero;
    public Vector2 extents = new Vector2(10, 10);

    class Baker : Baker<SimBounds2DAuthoring>
    {
        public override void Bake(SimBounds2DAuthoring a)
        {
            var e = GetEntity(TransformUsageFlags.None);
            AddComponent(e, new SimBounds2D
            {
                Center = a.center,
                Extents = math.max(new float2(0.001f, 0.001f), (float2)a.extents)
            });
        }
    }
}
