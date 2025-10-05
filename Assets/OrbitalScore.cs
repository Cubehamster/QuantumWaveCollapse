using Unity.Mathematics;
using static Unity.Mathematics.math;

public static class OrbitalScore
{
    // Pick kind with backward compatibility (Kind preferred, else Type)
    static OrbitalKind2D PickKind(in OrbitalParams2D op)
    {
        // Enum default is 0; if both are 0 we just return 0 (should be OneS in your enum).
        return (op.Kind != 0) ? op.Kind : op.Type;
    }

    // Pick clamp with backward compatibility (DriftClamp preferred, else ScoreClamp)
    static float PickClamp(in OrbitalParams2D op)
    {
        return (op.DriftClamp > 0f) ? op.DriftClamp : op.ScoreClamp;
    }

    static float2x2 Rot(float ang)
    {
        float c = cos(ang), s = sin(ang);
        return float2x2(c, -s, s, c);
    }

    // ---------- Public entry ----------
    // Returns ∇log ρ in WORLD space for current orbital params
    public static float2 ScoreWorld(float2 worldPos, in OrbitalParams2D op)
    {
        float2 d = worldPos - op.Center;
        float r = length(d);
        if (r < op.Epsilon) return float2(0f, 0f);

        // rotate to orbital local frame
        float2x2 Rinv = Rot(-op.Angle);
        float2 xL = mul(Rinv, d);

        float2 sL;
        switch (PickKind(op))
        {
            // s / p you already had
            case OrbitalKind2D.OneS: sL = Score_1s(xL, op.A0, op.Epsilon); break;           // (1,0,0)
            case OrbitalKind2D.TwoS: sL = Score_2s(xL, op.A0, op.Epsilon); break;           // (2,0,0)
            case OrbitalKind2D.TwoPX: sL = Score_2p_axis(xL, op.A0, op.Epsilon, 0); break;   // (2,1,±1) x
            case OrbitalKind2D.TwoPY: sL = Score_2p_axis(xL, op.A0, op.Epsilon, 1); break;   // (2,1,±1) y
            case OrbitalKind2D.ThreeS: sL = Score_3s(xL, op.A0, op.Epsilon); break;           // (3,0,0)
            case OrbitalKind2D.ThreePX: sL = Score_3p_axis(xL, op.A0, op.Epsilon, 0); break;   // (3,1,±1) x
            case OrbitalKind2D.FourPX: sL = Score_4p_axis(xL, op.A0, op.Epsilon, 0); break;   // (4,1,±1) x

            // ---- NEW d-like (l=2) 2D shapes ----
            case OrbitalKind2D.FourD_X2MinusY2: sL = Score_d_x2_y2(xL, op.A0, op.Epsilon, 4); break; // n≈4
            case OrbitalKind2D.FourD_XY: sL = Score_d_xy(xL, op.A0, op.Epsilon, 4); break;

            // ---- NEW f-like (l=3) 2D shapes ----
            case OrbitalKind2D.FourF_Cos3Phi: sL = Score_f_cos3phi(xL, op.A0, op.Epsilon, 4); break;
            case OrbitalKind2D.FourF_X_5X2_3R2: sL = Score_f_x_5x2_3r2(xL, op.A0, op.Epsilon, 4); break;
            case OrbitalKind2D.FourF_Y_5Y2_3R2: sL = Score_f_y_5y2_3r2(xL, op.A0, op.Epsilon, 4); break;

            // Fallback (safe): OneS
            default: sL = Score_1s(xL, op.A0, op.Epsilon); break;
        }

        // Clamp magnitude for stability (prefer DriftClamp, fallback ScoreClamp)
        float clampMag = PickClamp(op);
        if (clampMag > 0f)
        {
            float L = length(sL);
            if (L > clampMag) sL *= (clampMag / max(L, 1e-12f));
        }

        // back to world
        return mul(Rot(op.Angle), sL);
    }

    // ---------- s orbitals ----------
    // 1s: ρ ∝ exp(-2 r / a0) ⇒ ∇log ρ = -(2/a0) r̂
    static float2 Score_1s(float2 x, float a0, float eps)
    {
        float r = max(length(x), eps);
        return (-2f / max(a0, 1e-6f)) * (x / r);
    }

    // 2s: ψ ∝ (1 - r/(2a0)) e^{-r/(2a0)}
    // ln ρ = 2 ln(1 - r/2a0) - r/a0
    // d/dr ln ρ = -1/a0 - (1/a0)/(1 - r/2a0)
    static float2 Score_2s(float2 x, float a0, float eps)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);
        float u = 1f - r / (2f * a);
        u = sign(u) * max(abs(u), 1e-4f); // avoid exact node
        float dlogdr = -(1f / a) - (1f / a) * (1f / u);
        return dlogdr * (x / r);
    }

    // 3s (compact, visually faithful approx):
    // ψ ∝ (1 - 2r/(3a0) + 2/27 (r/a0)^2) e^{-r/(3a0)}
    // ln ρ = 2 ln P(r) - 2r/(3a0), P=1-αr+βr², α=2/(3a0), β=2/(27a0²)
    static float2 Score_3s(float2 x, float a0, float eps)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);
        float α = 2f / (3f * a);
        float β = 2f / (27f * a * a);
        float P = 1f - α * r + β * r * r;
        P = sign(P) * max(abs(P), 1e-4f);
        float dP = -α + 2f * β * r;
        float dlogdr = 2f * (dP / P) - 2f / (3f * a);
        return dlogdr * (x / r);
    }

    // ---------- p orbitals ----------
    // 2p axis-aligned (x or y):
    // ρ ∝ axis^2 * e^{-r/a0}  → ln ρ = 2 ln|axis| - r/a0
    // ∇ ln|axis| = ê_axis / axis
    static float2 Score_2p_axis(float2 x, float a0, float eps, int axis /*0=x,1=y*/)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);
        float axisVal = (axis == 0) ? x.x : x.y;
        axisVal = sign(axisVal) * max(abs(axisVal), 1e-4f);
        float2 eAxis = (axis == 0) ? float2(1f, 0f) : float2(0f, 1f);
        return 2f * (eAxis / axisVal) - (1f / a) * (x / r);
    }

    // 3p:
    // ψ ∝ (1 - r/(6a0)) * axis * e^{-r/(3a0)}  ⇒
    // ln ρ = 2 ln|axis| + 2 ln|1 - r/(6a0)| - 2r/(3a0)
    static float2 Score_3p_axis(float2 x, float a0, float eps, int axis)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);
        float axisVal = (axis == 0) ? x.x : x.y;
        axisVal = sign(axisVal) * max(abs(axisVal), 1e-4f);
        float2 eAxis = (axis == 0) ? float2(1f, 0f) : float2(0f, 1f);
        float u = 1f - r / (6f * a);
        u = sign(u) * max(abs(u), 1e-4f);
        return
            2f * (eAxis / axisVal) +
            2f * (-(1f / (6f * a)) / u) * (x / r) -
            (2f / (3f * a)) * (x / r);
    }

    // 4p (simple, stable approximation):
    // ψ ∝ (1 - r/(8a0) + 0.2 (r/(8a0))^2) * axis * e^{-r/(4a0)}
    static float2 Score_4p_axis(float2 x, float a0, float eps, int axis)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);
        float axisVal = (axis == 0) ? x.x : x.y;
        axisVal = sign(axisVal) * max(abs(axisVal), 1e-4f);
        float2 eAxis = (axis == 0) ? float2(1f, 0f) : float2(0f, 1f);

        float u = 1f - r / (8f * a) + 0.2f * (r * r) / (64f * a * a);
        u = sign(u) * max(abs(u), 1e-4f);
        float du = -(1f / (8f * a)) + 0.2f * (2f * r) / (64f * a * a);

        return
            2f * (eAxis / axisVal) +
            2f * (du / u) * (x / r) -
            (1f / (2f * a)) * (x / r);
    }

    // ---------- d-like (l=2)  ρ ∝ P^2 * e^{-r/(n a0)} ----------
    // ln ρ = 2 ln|P| - r/(n a0)  ⇒ ∇lnρ = 2 ∇P/P - (1/(n a0)) r̂
    static float2 Score_d_x2_y2(float2 x, float a0, float eps, int n /*~4*/)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);

        float Px = x.x * x.x - x.y * x.y;
        Px = sign(Px) * max(abs(Px), 1e-4f); // avoid div by zero at nodal lines
        float2 dP = float2(2f * x.x, -2f * x.y);

        return 2f * (dP / Px) - (1f / (n * a)) * (x / r);
    }

    static float2 Score_d_xy(float2 x, float a0, float eps, int n /*~4*/)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);

        float Px = 2f * x.x * x.y;
        Px = sign(Px) * max(abs(Px), 1e-4f);
        float2 dP = float2(2f * x.y, 2f * x.x);

        return 2f * (dP / Px) - (1f / (n * a)) * (x / r);
    }

    // ---------- f-like (l=3)  ρ ∝ P^2 * e^{-r/(n a0)} ----------
    // cos(3φ) ∝ x^3 - 3 x y^2
    static float2 Score_f_cos3phi(float2 x, float a0, float eps, int n /*~4*/)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);

        float Px = x.x * (x.x * x.x - 3f * x.y * x.y);                 // x^3 - 3xy^2
        Px = sign(Px) * max(abs(Px), 1e-4f);
        float2 dP = float2(3f * x.x * x.x - 3f * x.y * x.y, -6f * x.x * x.y);

        return 2f * (dP / Px) - (1f / (n * a)) * (x / r);
    }

    // x(5x^2 - 3r^2) = x(2x^2 - 3y^2) in 2D
    static float2 Score_f_x_5x2_3r2(float2 x, float a0, float eps, int n /*~4*/)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);

        float x2 = x.x * x.x, y2 = x.y * x.y;
        float poly = 2f * x2 - 3f * y2;
        float Px = x.x * poly;
        Px = sign(Px) * max(abs(Px), 1e-4f);

        // d/dx: 10x^2 - 3y^2 ; d/dy: -6xy
        float2 dP = float2(10f * x2 - 3f * y2, -6f * x.x * x.y);

        return 2f * (dP / Px) - (1f / (n * a)) * (x / r);
    }

    // y(5y^2 - 3r^2) = y(2y^2 - 3x^2) in 2D
    static float2 Score_f_y_5y2_3r2(float2 x, float a0, float eps, int n /*~4*/)
    {
        float r = max(length(x), eps);
        float a = max(a0, 1e-6f);

        float x2 = x.x * x.x, y2 = x.y * x.y;
        float poly = 2f * y2 - 3f * x2;
        float Px = x.y * poly;
        Px = sign(Px) * max(abs(Px), 1e-4f);

        // d/dx: -6xy ; d/dy: 10y^2 - 3x^2
        float2 dP = float2(-6f * x.x * x.y, 10f * y2 - 3f * x2);

        return 2f * (dP / Px) - (1f / (n * a)) * (x / r);
    }
}
