using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public enum WalkerInitMode
{
    UniformInBounds = 0,
    GaussianAroundCenter = 1
}

public sealed class SimpleWalkerSpawnerAuthoring : MonoBehaviour
{
    [Min(1)] public int count = 1_000_000;
    public uint seed = 12345;
    public WalkerInitMode initMode = WalkerInitMode.UniformInBounds;
    [Tooltip("World-units sigma for Gaussian init around SimBounds2D.Center.")]
    public float gaussianSigma = 0.5f;

    class Baker : Baker<SimpleWalkerSpawnerAuthoring>
    {
        public override void Bake(SimpleWalkerSpawnerAuthoring a)
        {
            var e = GetEntity(TransformUsageFlags.None);
            AddComponent(e, new SimpleWalkerSpawner
            {
                Count = math.max(1, a.count),
                Seed = (a.seed == 0u) ? 1u : a.seed,
                Mode = a.initMode,
                GaussianSigma = math.max(1e-4f, a.gaussianSigma)
            });
        }
    }
}

// IComponentData that the system reads
public struct SimpleWalkerSpawner : IComponentData
{
    public int Count;
    public uint Seed;
    public WalkerInitMode Mode;
    public float GaussianSigma;
}
