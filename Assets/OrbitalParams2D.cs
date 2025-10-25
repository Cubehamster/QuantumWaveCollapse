using Unity.Entities;
using Unity.Mathematics;

public enum OrbitalType2D
{
    OneS, TwoS, ThreeS,
    TwoP_X, TwoP_Y,
    ThreeD_X2_Y2, ThreeD_XY
}


/// <summary>Simulation parameters for Langevin integration (singleton).</summary>
public struct LangevinParams2D : IComponentData
{
    /// <summary>Diffusion coefficient D (controls random jitter strength).</summary>
    public float Diffusion;

    /// <summary>Multiplier on Time.DeltaTime (leave at 1 for realtime).</summary>
    public float DtScale;

    /// <summary>Velocity damping in [0..1] per second (0 = no damping).</summary>
    public float VelocityDamping;

    /// <summary>Scales external Force -> acceleration (units per s^2 per unit force).</summary>
    public float ForceAccel;

    /// <summary>Clamp on speed (units per second). 0 = no clamp.</summary>
    public float MaxSpeed;

    /// <summary>Post-collapse jitter sigma as fraction of A0 (e.g. 0.01).</summary>
    public float CollapseJitterFrac;
}

/// <summary>Force accumulator (cleared/decayed every frame).</summary>
public struct Force : IComponentData
{
    public float2 Value;
}