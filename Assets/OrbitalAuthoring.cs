using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// Choose presets in the inspector; we map them to OrbitalKind2D.
public enum OrbitalPreset
{
    H_1s, H_2s, H_2pX, H_2pY, H_3s, H_3pX, H_4pX,
    H_4d_x2_y2, H_4d_xy,
    H_4f_cos3phi, H_4f_x_5x2_3r2, H_4f_y_5y2_3r2
}

public class OrbitalAuthoring : MonoBehaviour
{
    [Header("Orbital")]
    public OrbitalPreset preset = OrbitalPreset.H_1s;
    public float a0 = 20f;
    public Vector2 center = Vector2.zero;
    [Range(-180, 180)] public float angleDeg = 0f;

    [Header("Score Safety")]
    [Range(0.5f, 20f)] public float scoreClamp = 8f; // caps |∇logρ|
    public float epsilon = 1e-4f;                    // small floor near nodes

    [Header("Langevin")]
    public float diffusion = 60f;
    public float dtScale = 1f;
    [Range(0f, 1f)] public float velocityDamping = 0.08f;
    public float forceAccel = 25f;
    public float maxSpeed = 0f;           // 0 = no clamp
    [Range(0f, 0.1f)] public float collapseJitterFrac = 0.01f;

    static OrbitalKind2D MapPresetToKind(OrbitalPreset p)
    {
        switch (p)
        {
            // --- s / p you already had ---
            case OrbitalPreset.H_1s: return OrbitalKind2D.OneS;      // (1,0,0)
            case OrbitalPreset.H_2s: return OrbitalKind2D.TwoS;      // (2,0,0)
            case OrbitalPreset.H_2pX: return OrbitalKind2D.TwoPX;     // (2,1,±1) along +X
            case OrbitalPreset.H_2pY: return OrbitalKind2D.TwoPY;     // (2,1,±1) along +Y
            case OrbitalPreset.H_3s: return OrbitalKind2D.ThreeS;    // (3,0,0)
            case OrbitalPreset.H_3pX: return OrbitalKind2D.ThreePX;   // (3,1,±1)
            case OrbitalPreset.H_4pX: return OrbitalKind2D.FourPX;    // (4,1,±1)

            // --- new d-like (n≈4, l=2) ---
            case OrbitalPreset.H_4d_x2_y2: return OrbitalKind2D.FourD_X2MinusY2; // ∝ x^2 - y^2
            case OrbitalPreset.H_4d_xy: return OrbitalKind2D.FourD_XY;        // ∝ xy (rotate Angle for orientation)

            // --- new f-like (n≈4, l=3) ---
            case OrbitalPreset.H_4f_cos3phi: return OrbitalKind2D.FourF_Cos3Phi;      // ∝ cos(3φ)
            case OrbitalPreset.H_4f_x_5x2_3r2: return OrbitalKind2D.FourF_X_5X2_3R2;    // ∝ x(5x^2-3r^2)
            case OrbitalPreset.H_4f_y_5y2_3r2: return OrbitalKind2D.FourF_Y_5Y2_3R2;    // ∝ y(5y^2-3r^2)

            default: return OrbitalKind2D.OneS;
        }
    }


    class Baker : Baker<OrbitalAuthoring>
    {
        public override void Bake(OrbitalAuthoring a)
        {
            var e = GetEntity(TransformUsageFlags.None);

            var kind = MapPresetToKind(a.preset);

            AddComponent(e, new OrbitalParams2D
            {
                // Preferred fields
                Kind = kind,
                A0 = math.max(1e-3f, a.a0),
                Center = new float2(a.center.x, a.center.y),
                Angle = math.radians(a.angleDeg),
                DriftClamp = a.scoreClamp,                    // preferred name
                Epsilon = math.max(1e-8f, a.epsilon),

                // Legacy aliases (keep both filled for compatibility with older scripts)
                Type = kind,
                ScoreClamp = a.scoreClamp
            });

            AddComponent(e, new LangevinParams2D
            {
                Diffusion = math.max(0f, a.diffusion),
                DtScale = math.max(0f, a.dtScale),
                VelocityDamping = math.saturate(a.velocityDamping),
                ForceAccel = a.forceAccel,
                MaxSpeed = a.maxSpeed,
                CollapseJitterFrac = a.collapseJitterFrac,
            });
        }
    }
}
