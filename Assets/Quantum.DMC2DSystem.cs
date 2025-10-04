// DMC2DSystem.cs — drift-diffusion + velocity + decaying ClickForce acceleration (safe empty array)
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ParticleSpawnSystem))]
public partial struct DMC2DSystem : ISystem
{
    EntityQuery _forcesQ;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<QuantumParams2D>();
        state.RequireForUpdate<Potential2D>();
        state.RequireForUpdate<SimBounds2D>();
        state.RequireForUpdate<ParticleTag>();

        _forcesQ = state.GetEntityQuery(ComponentType.ReadOnly<ClickForce>());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var qp = SystemAPI.GetSingleton<QuantumParams2D>();
        var pot = SystemAPI.GetSingleton<Potential2D>();
        var b = SystemAPI.GetSingleton<SimBounds2D>();

        float dt = math.max(0f, qp.DeltaTau);
        float sqrtStep = math.sqrt(2f * math.max(0f, qp.Diffusion) * dt);

        // Per-second velocity damping (keeps motion from running away). Tune 0.5..5
        float velDamp = 2.0f;

        float2 minB = b.Center - b.Extents;
        float2 maxB = b.Center + b.Extents;

        // ALWAYS construct a forces array (even if empty) so the job gets a valid container
        NativeArray<ClickForce> forces = _forcesQ.IsEmpty
            ? new NativeArray<ClickForce>(0, Allocator.TempJob)
            : _forcesQ.ToComponentDataArray<ClickForce>(Allocator.TempJob);

        var job = new DMCStepJob
        {
            Dt = dt,
            SqrtStep = sqrtStep,
            ERef = qp.ERef,
            BoundaryMode = qp.BoundaryMode,
            MinB = minB,
            MaxB = maxB,
            Pot = pot,
            VelDamp = velDamp,
            TimeNow = SystemAPI.Time.ElapsedTime,
            Forces = forces
        };

        state.Dependency = job.ScheduleParallel(state.Dependency);
        state.Dependency.Complete(); // make updates visible downstream

        // Dispose the temp array now that the job completed
        if (forces.IsCreated) forces.Dispose();

        // Cleanup expired ClickForce entities (small set → cheap)
        if (!_forcesQ.IsEmpty)
        {
            var em = state.EntityManager;
            var fEnts = _forcesQ.ToEntityArray(Allocator.Temp);
            var fData = _forcesQ.ToComponentDataArray<ClickForce>(Allocator.Temp);
            double now = SystemAPI.Time.ElapsedTime;

            for (int i = 0; i < fEnts.Length; i++)
            {
                var f = fData[i];
                float age = (float)(now - f.StartTime);
                if (age > f.DecaySeconds * 4f) // ~98% decayed
                    em.DestroyEntity(fEnts[i]);
            }

            fEnts.Dispose();
            fData.Dispose();
        }
    }

    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct DMCStepJob : IJobEntity
    {
        public float Dt;
        public float SqrtStep;
        public float ERef;
        public int BoundaryMode; // 0 reflect, 1 clamp, 2 periodic
        public float2 MinB, MaxB;
        public Potential2D Pot;
        public float VelDamp;      // per-second linear damping for velocity
        public double TimeNow;

        [ReadOnly] public NativeArray<ClickForce> Forces;

        static float2 Gaussian2(ref Unity.Mathematics.Random rng)
        {
            float u1 = math.max(1e-7f, rng.NextFloat());
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
                        float a = p.Params.x, bb = p.Params.y;
                        float vx = a * math.pow(x.x * x.x - bb * bb, 2);
                        float vy = a * math.pow(x.y * x.y - bb * bb, 2);
                        return vx + vy;
                    }
                case PotentialType2D.BoxZero:
                default:
                    return 0f;
            }
        }

        static void ApplyBoundary(ref float2 p, ref float2 v, float2 minB, float2 maxB, int mode)
        {
            if (mode == 0) // reflect
            {
                if (p.x < minB.x) { p.x = minB.x + (minB.x - p.x); v.x = -v.x; }
                if (p.x > maxB.x) { p.x = maxB.x - (p.x - maxB.x); v.x = -v.x; }
                if (p.y < minB.y) { p.y = minB.y + (minB.y - p.y); v.y = -v.y; }
                if (p.y > maxB.y) { p.y = maxB.y - (p.y - maxB.y); v.y = -v.y; }
            }
            else if (mode == 1) // clamp
            {
                p = math.clamp(p, minB, maxB);
                v *= 0.5f;
            }
            else // periodic
            {
                float2 size = maxB - minB;
                float2 t = (p - minB) / size;
                t = t - math.floor(t);
                p = minB + t * size;
            }
        }

        float2 AccelFromForces(float2 x, double now)
        {
            if (!Forces.IsCreated || Forces.Length == 0) return 0f;

            float2 a = 0f;
            for (int i = 0; i < Forces.Length; i++)
            {
                var f = Forces[i];

                float age = (float)(now - f.StartTime);
                if (age < 0f) continue;

                float tau = math.max(1e-6f, f.DecaySeconds);
                float decay = math.exp(-age / tau);
                if (decay < 1e-3f) continue;

                float2 d = x - f.Center;
                float r = math.length(d);
                if (r <= 1e-8f) continue;

                float t = math.saturate((r - f.InnerRadius) / math.max(1e-6f, (f.OuterRadius - f.InnerRadius)));
                float fall = 1f - (t * t * (3f - 2f * t)); // smoothstep 1→0
                if (fall <= 0f) continue;

                float2 dir = d / r;
                a += dir * (f.Strength * decay * fall);
            }
            return a;
        }

        public void Execute(ref Position pos, ref Weight w, ref RandomState rnd, ref Velocity vel)
        {
            var rng = Unity.Mathematics.Random.CreateFromIndex(math.max(1u, rnd.Value));

            // External acceleration + semi-implicit Euler with damping
            float2 a = AccelFromForces(pos.Value, TimeNow);
            float lambda = math.max(0f, VelDamp);
            float damp = math.max(0f, 1f - lambda * Dt);
            vel.Value = vel.Value * damp + a * Dt;

            // Drift + diffusion
            float2 g = Gaussian2(ref rng);
            pos.Value += vel.Value * Dt + SqrtStep * g;

            // Boundaries
            ApplyBoundary(ref pos.Value, ref vel.Value, MinB, MaxB, BoundaryMode);

            // Weight update
            float V = PotentialValue(Pot, pos.Value);
            float exponent = math.clamp(-Dt * (V - ERef), -12f, 12f);
            w.Value *= math.exp(exponent);

            // persist RNG
            rnd.Value = rng.state;
        }
    }
}
