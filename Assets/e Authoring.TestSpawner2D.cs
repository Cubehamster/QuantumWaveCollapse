using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

public class TestSpawner2DAuthoring : MonoBehaviour
{
    public int Count = 10_000;
    public Vector2 Center = Vector2.zero;
    public Vector2 Extents = new(20, 20);
    public float MinSpeed = 0.5f, MaxSpeed = 2f;
    public float MinWeight = 0.8f, MaxWeight = 1.2f;
    public uint Seed = 1;

    class Baker : Baker<TestSpawner2DAuthoring>
    {
        public override void Bake(TestSpawner2DAuthoring src)
        {
            // This log appears at BAKE time (when the SubScene converts). 
            // If you don't see it when opening/playing the SubScene, the Baker isn't running.
            UnityEngine.Debug.Log($"[Baker2D] Baking TestSpawner2D Count={src.Count}");

            var e = GetEntity(TransformUsageFlags.None);

            AddComponent(e, new SpawnRequest
            {
                Count = math.max(0, src.Count),
                MinSpeed = math.min(src.MinSpeed, src.MaxSpeed),
                MaxSpeed = math.max(src.MinSpeed, src.MaxSpeed),
                MinWeight = math.min(src.MinWeight, src.MaxWeight),
                MaxWeight = math.max(src.MinWeight, src.MaxWeight),
                Seed = src.Seed == 0 ? 1u : src.Seed
            });

            AddComponent(e, new SimBounds2D
            {
                Center = new float2(src.Center.x, src.Center.y),
                Extents = math.max(new float2(0.001f), new float2(src.Extents.x, src.Extents.y))
            });
        }
    }
}
