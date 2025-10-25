using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Handles measurement clicks: during press it attracts particles (suction),
/// while released it repels. On success, particles collapse smoothly inward.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(LangevinWalkSystem))]
public partial struct MeasurementSystem : ISystem
{
    EntityQuery _walkersQ;
    EntityQuery _reqQ;

    public void OnCreate(ref SystemState state)
    {
        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadWrite<Force>());

        _reqQ = state.GetEntityQuery(ComponentType.ReadOnly<ClickRequest>());
    }

    public void OnUpdate(ref SystemState state)
    {
        if (_reqQ.IsEmptyIgnoreFilter) return;
        var req = _reqQ.GetSingleton<ClickRequest>();

        int N = _walkersQ.CalculateEntityCount();
        if (N == 0) return;

        float2 c = req.WorldPos;
        float R = req.Radius;
        float R2 = R * R;

        // During press → suction (negative force)
        if (req.IsPressed && req.PushStrength != 0f)
        {
            var pushJob = new PushJob
            {
                Center = c,
                R2 = R2,
                Strength = math.abs(req.PushStrength) // pull inward
            };
            state.Dependency = pushJob.ScheduleParallel(state.Dependency);
        }

        // On release → outward push
        if (req.EdgeUp)
        {
            var pushJob = new PushJob
            {
                Center = c,
                R2 = R2,
                Strength = math.abs(req.PushStrength) // outward explosion
            };
            state.Dependency = pushJob.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            // Count inside radius
            var insideCount = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var countJob = new CountInsideJob
            {
                Center = c,
                R2 = R2,
                Counter = insideCount
            };
            state.Dependency = countJob.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            int inside = insideCount[0];
            insideCount.Dispose();
            float p = (float)inside / math.max(1, N);

            // check if actual particle is inside radius
            bool success = false;
            if (SystemAPI.HasSingleton<ActualParticle>())
            {
                var ap = SystemAPI.GetSingleton<ActualParticle>();
                if (state.EntityManager.Exists(ap.Walker) &&
                    state.EntityManager.HasComponent<Position>(ap.Walker))
                {
                    float2 apPos = state.EntityManager.GetComponentData<Position>(ap.Walker).Value;
                    success = math.lengthsq(apPos - c) <= R2;
                }
            }

            if (success)
            {
                //var lp = SystemAPI.GetSingleton<LangevinParams2D>();
                //var op = SystemAPI.GetSingleton<OrbitalParams2D>();
                //float jitter = lp.CollapseJitterFrac * math.max(1e-3f, op.A0);

                //var collapseJob = new CollapseJob
                //{
                //    Center = c,
                //    JitterSigma = jitter
                //};
                //state.Dependency = collapseJob.ScheduleParallel(state.Dependency);
                //state.Dependency.Complete();

                Debug.Log($"[Measure] SUCCESS  p≈{Round3(p)}  collapsed @ {c}");
            }
            else
            {
                Debug.Log($"[Measure] FAIL     p≈{Round3(p)}  pushed @ {c}");
            }
        }
    }

    static float Round3(float v) => math.round(v * 1000f) * 0.001f;

    // ----------------------------------------------------------------------
    //  JOBS
    // ----------------------------------------------------------------------

    /// <summary>
    /// Applies a circular smooth force (inward if Strength < 0, outward if > 0).
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct PushJob : IJobEntity
    {
        public float2 Center;
        public float R2;
        public float Strength;

        public void Execute(ref Force f, in Position pos)
        {
            float2 d = pos.Value - Center;
            float d2 = math.lengthsq(d);
            if (d2 > R2) return;

            float r = math.sqrt(math.max(1e-12f, d2));
            float2 dir = d / r;

            // Smooth circular falloff (quadratic)
            float t = 1f - math.saturate(r / math.sqrt(R2));
            float2 forceVec = dir * (Strength * t * t); // smooth attractor/repulsor

            f.Value += forceVec;
        }
    }

    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct CountInsideJob : IJobEntity
    {
        public float2 Center;
        public float R2;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counter;

        public void Execute(in Position pos)
        {
            if (math.lengthsq(pos.Value - Center) <= R2)
            {
                // approximate increment (benign race)
                Counter[0] = Counter[0] + 1;
            }
        }
    }

    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct CollapseJob : IJobEntity
    {
        public float2 Center;
        public float JitterSigma;

        static uint Sanitize(uint s) => (s == 0u || s == 0xFFFFFFFFu) ? 1u : s;

        static float2 Gaussian2(ref Unity.Mathematics.Random rng)
        {
            float u1 = math.max(1e-7f, rng.NextFloat());
            float u2 = rng.NextFloat();
            float r = math.sqrt(-2f * math.log(u1));
            float a = 2f * math.PI * u2;
            return new float2(r * math.cos(a), r * math.sin(a));
        }

        public void Execute(ref Position pos, ref Velocity vel, ref RandomState rnd)
        {
            uint seed = Sanitize(rnd.Value);
            var rng = Unity.Mathematics.Random.CreateFromIndex(seed);

            float2 jitter = (JitterSigma > 0f) ? JitterSigma * Gaussian2(ref rng) : float2.zero;

            // Implosion: move toward center with jitter
            pos.Value = math.lerp(pos.Value, Center + jitter, 0.3f); // 0.3 for soft collapse
            vel.Value *= 0.2f;

            rnd.Value = Sanitize(rng.state);
        }
    }
}
