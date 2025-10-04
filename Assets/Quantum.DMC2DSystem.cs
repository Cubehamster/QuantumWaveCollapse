using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ParticleSpawnSystem))]
[UpdateBefore(typeof(DMCResampleSystem))] // ensure DMC runs before the resampler
public partial struct DMC2DSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<QuantumParams2D>();
        state.RequireForUpdate<Potential2D>();
        state.RequireForUpdate<SimBounds2D>();
        state.RequireForUpdate<ParticleTag>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var qp = SystemAPI.GetSingleton<QuantumParams2D>();
        var pot = SystemAPI.GetSingleton<Potential2D>();
        var b = SystemAPI.GetSingleton<SimBounds2D>();

        float dt = qp.DeltaTau;
        float sqrtStep = math.sqrt(2f * qp.Diffusion * dt);

        float2 minB = b.Center - b.Extents;
        float2 maxB = b.Center + b.Extents;

        var job = new DMCStepJob
        {
            Dt = dt,
            SqrtStep = sqrtStep,
            ERef = qp.ERef,
            BoundaryMode = qp.BoundaryMode,
            MinB = minB,
            MaxB = maxB,
            Pot = pot
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public partial struct DMCStepJob : IJobEntity
    {
        public float Dt;
        public float SqrtStep;
        public float ERef;
        public int BoundaryMode; // 0 reflect, 1 clamp, 2 periodic
        public float2 MinB, MaxB;
        public Potential2D Pot;

        // Box-Muller: two standard normals
        static float2 Gaussian2(ref Unity.Mathematics.Random rng)
        {
            float u1 = math.max(1e-7f, rng.NextFloat()); // avoid log(0)
            float u2 = rng.NextFloat();
            float r = math.sqrt(-2f * math.log(u1));
            float a = 2f * math.PI * u2;
            return new float2(r * math.cos(a), r * math.sin(a));
        }

        static float PotentialValue(Potential2D p, float2 x)
        {
            switch (p.Type)
            {
                case PotentialType2D.Harmonic:
                    {
                        float omega = math.max(1e-6f, p.Params.x);
                        float r2 = math.lengthsq(x);
                        return 0.5f * omega * omega * r2;
                    }
                case PotentialType2D.DoubleWell:
                    {
                        float a = p.Params.x, b = p.Params.y;
                        float vx = a * math.pow(x.x * x.x - b * b, 2);
                        float vy = a * math.pow(x.y * x.y - b * b, 2);
                        return vx + vy;
                    }
                case PotentialType2D.BoxZero:
                default:
                    return 0f;
            }
        }

        static float2 ApplyBoundary(float2 p, float2 minB, float2 maxB, int mode)
        {
            if (mode == 0) // reflect
            {
                if (p.x < minB.x) p.x = minB.x + (minB.x - p.x);
                if (p.x > maxB.x) p.x = maxB.x - (p.x - maxB.x);
                if (p.y < minB.y) p.y = minB.y + (minB.y - p.y);
                if (p.y > maxB.y) p.y = maxB.y - (p.y - maxB.y);
            }
            else if (mode == 1) // clamp
            {
                p = math.clamp(p, minB, maxB);
            }
            else // periodic
            {
                float2 size = maxB - minB;
                float2 t = (p - minB) / size;
                t = t - math.floor(t);
                p = minB + t * size;
            }
            return p;
        }

        public void Execute(ref Position pos, ref Weight w, ref RandomState rnd /* optional: ref Velocity vel */)
        {
            // Rebuild RNG from stored state (uint)
            var rng = Unity.Mathematics.Random.CreateFromIndex(math.max(1u, rnd.Value));

            // Diffusion
            float2 g = Gaussian2(ref rng);
            pos.Value += SqrtStep * g;

            // Bounds
            pos.Value = ApplyBoundary(pos.Value, MinB, MaxB, BoundaryMode);

            // Weight update with clamped exponent to keep numbers stable
            float V = PotentialValue(Pot, pos.Value);
            float exponent = math.clamp(-Dt * (V - ERef), -12f, 12f); // was -20..20
            float factor = math.exp(exponent);

            float newW = w.Value * factor;
            if (!math.isfinite(newW)) newW = 0f;
            newW = math.clamp(newW, 1e-30f, 1e+30f);
            w.Value = newW;

            // Store RNG state back (never 0)
            if (rng.state == 0) rng = Unity.Mathematics.Random.CreateFromIndex(1u);
            rnd.Value = rng.state;
        }
    }
}
