using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Measurement interaction:
/// • While mouse is held: particles are attracted (suction) toward the center within a radius.
/// • On release: brief outward push within the same radius.
/// • We also compute a simple probability estimate (fraction of walkers inside the radius).
/// Optional collapse job (soft implosion) is included but left commented out.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(LangevinWalkSystem))]
public partial struct MeasurementSystem : ISystem
{
    private EntityQuery _walkersQ;
    private EntityQuery _reqQ;

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

        // Read current request
        var req = _reqQ.GetSingleton<ClickRequest>();

        int N = _walkersQ.CalculateEntityCount();
        if (N == 0) return;

        float2 c = req.WorldPos;
        float R = math.max(1e-6f, req.Radius);
        float R2 = R * R;

        // ------------------------------------------------------------------
        // While pressed -> suction (negative strength = inward)
        // ------------------------------------------------------------------
        if (req.IsPressed && req.PushStrength != 0f)
        {
            var job = new PushJob
            {
                Center = c,
                R2 = R2,
                Strength = math.abs(req.PushStrength), // inward
                EdgeSoft = 1.0f                          // softness near edge (1 = quadratic)
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        // ------------------------------------------------------------------
        // On release -> outward burst (positive strength = outward)
        // ------------------------------------------------------------------
        if (req.EdgeUp)
        {
            // Outward pulse
            var push = new PushJob
            {
                Center = c,
                R2 = R2,
                Strength = math.abs(req.PushStrength), // outward
                EdgeSoft = 1.0f
            };
            state.Dependency = push.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            // Count how many are inside (simple estimate)
            var insideCount = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var count = new CountInsideJob
            {
                Center = c,
                R2 = R2,
                Counter = insideCount
            };
            state.Dependency = count.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            int inside = insideCount[0];
            insideCount.Dispose();

            float p = (float)inside / math.max(1, N);

            // Optional: soft collapse (commented out; enable if you want that visual)
            /*
            float jitter = 0f;
            if (SystemAPI.TryGetSingleton<LangevinParams2D>(out var lp) &&
                SystemAPI.TryGetSingleton<OrbitalParams2D>(out var op))
            {
                jitter = lp.CollapseJitterFrac * math.max(1e-3f, op.A0);
            }

            var collapse = new CollapseJob
            {
                Center      = c,
                JitterSigma = jitter,
                LerpFactor  = 0.3f // 0..1 : higher = stronger instant collapse
            };
            state.Dependency = collapse.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
            */

            Debug.Log($"[Measure] p≈{Round3(p)} @ {c}");
        }
    }

    // Small helper to keep logs clean
    static float Round3(float v) => math.round(v * 1000f) * 0.001f;

    // ----------------------------------------------------------------------
    // JOBS
    // ----------------------------------------------------------------------

    /// <summary>
    /// Adds a radial force within a circle. Positive Strength pushes outward, negative pulls inward.
    /// Falloff is smooth (quadratic by default) toward the edge to avoid harsh impulses.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct PushJob : IJobEntity
    {
        public float2 Center;
        public float R2;        // radius^2
        public float Strength;  // +outward, -inward
        public float EdgeSoft;  // softness exponent for falloff (1=quadratic, 0.5=softer, 2=harder)

        public void Execute(ref Force f, in Position pos)
        {
            float2 d = pos.Value - Center;
            float d2 = math.lengthsq(d);
            if (d2 > R2) return;

            float r = math.sqrt(math.max(1e-12f, d2));
            float2 dir = d / r;

            // Smooth falloff t in [0..1] from center to edge; use (1 - r/R)^pow
            float t = 1f - (r / math.sqrt(R2));
            t = math.saturate(t);
            float shaped = t * t; // quadratic (EdgeSoft could remap if you want different curve)

            float2 forceVec = dir * (Strength * shaped);
            f.Value += forceVec;
        }
    }

    /// <summary>Counts walkers inside the radius (approx; benign race on a single int).</summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct CountInsideJob : IJobEntity
    {
        public float2 Center;
        public float R2;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> Counter;

        public void Execute(in Position pos)
        {
            if (math.lengthsq(pos.Value - Center) <= R2)
            {
                // Non-atomic increment is fine for a UI/debug estimate
                Counter[0] = Counter[0] + 1;
            }
        }
    }

    /// <summary>
    /// Soft “implosion” collapse toward the center with optional Gaussian jitter.
    /// Keep commented out unless you want the visual after a successful measurement.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct CollapseJob : IJobEntity
    {
        public float2 Center;
        public float JitterSigma; // world units
        public float LerpFactor;  // 0..1

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

            float2 jitter = (JitterSigma > 0f) ? (JitterSigma * Gaussian2(ref rng)) : float2.zero;

            pos.Value = math.lerp(pos.Value, Center + jitter, math.saturate(LerpFactor));
            vel.Value *= 0.2f; // heavily damp after collapse

            rnd.Value = Sanitize(rng.state);
        }
    }
}
