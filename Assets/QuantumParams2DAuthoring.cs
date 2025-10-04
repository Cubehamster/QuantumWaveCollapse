using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

public class QuantumParams2DAuthoring : MonoBehaviour
{
    [Header("Imaginary-time step & diffusion")]
    public float DeltaTau = 0.005f;
    public float Diffusion = 0.5f; // ħ=1, m=1

    [Header("Reference energy (E_ref)")]
    public float ERef = 1.0f;

    [Header("Boundary: 0=Reflect, 1=Clamp, 2=Periodic")]
    public int BoundaryMode = 0;

    [Header("Potential")]
    public PotentialType2D Type = PotentialType2D.Harmonic;
    public float Omega = 1.0f;     // for Harmonic
    public float DoubleWellA = 1f; // for DoubleWell: V = a(x^2- b^2)^2 + same in y
    public float DoubleWellB = 2f;

    class Baker : Baker<QuantumParams2DAuthoring>
    {
        public override void Bake(QuantumParams2DAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.None);
            AddComponent(e, new QuantumParams2D
            {
                DeltaTau = math.max(1e-5f, src.DeltaTau),
                Diffusion = math.max(1e-6f, src.Diffusion),
                ERef = src.ERef,
                BoundaryMode = math.clamp(src.BoundaryMode, 0, 2)
            });

            var p = new float4(0);
            switch (src.Type)
            {
                case PotentialType2D.Harmonic:
                    p.x = math.max(1e-6f, src.Omega);
                    break;
                case PotentialType2D.DoubleWell:
                    p.x = src.DoubleWellA; p.y = src.DoubleWellB;
                    break;
                case PotentialType2D.BoxZero:
                    // no params needed
                    break;
            }

            AddComponent(e, new Potential2D { Type = src.Type, Params = p });
        }
    }
}
