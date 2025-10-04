using Unity.Entities;
using Unity.Mathematics;

public struct QuantumParams2D : IComponentData
{
    // Imaginary-time step Δτ (seconds). Start small, e.g. 0.002–0.01
    public float DeltaTau;
    // Diffusion coefficient D = ħ/(2m). With ħ=1,m=1 → D=0.5
    public float Diffusion;
    // Reference energy (population/weight control). Start near typical V scale, e.g. 1.0
    public float ERef;
    // 0=Reflect, 1=Clamp, 2=Periodic (XY)
    public int BoundaryMode;
}

// Choose a potential type and its params
public enum PotentialType2D : int { Harmonic = 0, DoubleWell = 1, BoxZero = 2 }

public struct Potential2D : IComponentData
{
    public PotentialType2D Type;
    // Generic params (interpret per-type). For Harmonic: p.x = omega
    public float4 Params;
}
