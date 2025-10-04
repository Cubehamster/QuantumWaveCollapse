using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

public class DensityField2DAuthoring : MonoBehaviour
{
    [Min(1)] public int Width = 512;
    [Min(1)] public int Height = 512;
    [Tooltip("Small epsilon to avoid log(0).")]
    public float Eps = 1e-9f;

    class Baker : Baker<DensityField2DAuthoring>
    {
        public override void Bake(DensityField2DAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.None);
            AddComponent(e, new DensityField2D
            {
                Size = new int2(math.max(1, src.Width), math.max(1, src.Height)),
                Eps = math.max(1e-12f, src.Eps)
            });
        }
    }
}
