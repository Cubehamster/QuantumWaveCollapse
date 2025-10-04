using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;

public class ParticleSpawnerAuthoring : MonoBehaviour
{
    [Header("How many particles")]
    public int Count = 1_000_000;

    [Header("Bounds (center & half-size) — 2D")]
    public Vector2 Center = Vector2.zero;
    public Vector2 Extents = new Vector2(500, 500);

    [Header("Speed range (scalar)")]
    public float MinSpeed = 0.5f;
    public float MaxSpeed = 3.0f;

    [Header("Weight range")]
    public float MinWeight = 0.8f;
    public float MaxWeight = 1.2f;

    [Header("Random seed (0 = random)")]
    public uint Seed = 0;

    class Baker : Baker<ParticleSpawnerAuthoring>
    {
        public override void Bake(ParticleSpawnerAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.None);

            uint seed = src.Seed != 0 ? src.Seed : (uint)UnityEngine.Random.Range(1, int.MaxValue);

            AddComponent(e, new SpawnRequest
            {
                Count = math.max(0, src.Count),
                MinSpeed = math.min(src.MinSpeed, src.MaxSpeed),
                MaxSpeed = math.max(src.MinSpeed, src.MaxSpeed),
                MinWeight = math.min(src.MinWeight, src.MaxWeight),
                MaxWeight = math.max(src.MinWeight, src.MaxWeight),
                Seed = seed
            });

            AddComponent(e, new SimBounds2D
            {
                Center = new float2(src.Center.x, src.Center.y),
                Extents = math.max(new float2(0.001f, 0.001f),
                                   new float2(src.Extents.x, src.Extents.y))
            });
        }
    }
}
