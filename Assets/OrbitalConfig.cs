// OrbitalConfig.cs
using Unity.Entities;
using Unity.Mathematics;

public enum OrbitalKind2D
{
    // s & p (you already have some of these)
    OneS,
    TwoS,
    ThreeS,
    TwoPX, 
    TwoPY,
    ThreePX, /* add others as you like */ 
    FourPX,

    // NEW: d-like (l=2) 2D shapes
    FourD_X2MinusY2,   // ∝ x^2 - y^2  (4 lobes at 45°)
    FourD_XY,          // ∝ 2xy        (4 lobes aligned with axes)

    // NEW: f-like (l=3) 2D shapes
    FourF_X_5X2_3R2,   // ∝ x(5x^2 - 3r^2)
    FourF_Y_5Y2_3R2,   // ∝ y(5y^2 - 3r^2)
    FourF_Cos3Phi      // ∝ (x^3 - 3xy^2)   (3 lobes)
}


public struct OrbitalParams2D : IComponentData
{
    // --- Primary fields (new) ---
    public OrbitalKind2D Kind;   // preferred in new code
    public float A0;             // orbital length scale (world units)
    public float2 Center;        // world center
    public float Angle;          // radians
    public float DriftClamp;     // clamp |∇logρ| (0 = off)
    public float Exposure;

    // --- Legacy aliases (kept for existing authoring/scripts) ---
    // Authoring sets these names; systems can read either.
    public OrbitalKind2D Type;   // alias of Kind
    public float ScoreClamp;     // alias of DriftClamp
    public float Epsilon;        // small floor to stabilize near nodes (optional use)
}
