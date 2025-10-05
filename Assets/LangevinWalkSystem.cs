using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct LangevinWalkSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<OrbitalParams2D>();
        state.RequireForUpdate<LangevinParams2D>();
        state.RequireForUpdate<SimBounds2D>();
        state.RequireForUpdate<ParticleTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var op = SystemAPI.GetSingleton<OrbitalParams2D>();
        var lp = SystemAPI.GetSingleton<LangevinParams2D>();
        var b = SystemAPI.GetSingleton<SimBounds2D>();

        float dt = state.WorldUnmanaged.Time.DeltaTime * math.max(0f, lp.DtScale);
        if (dt <= 0f) return;

        float D = math.max(0f, lp.Diffusion);
        float sigma = math.sqrt(2f * D * dt);

        float2 minB = b.Center - b.Extents;
        float2 maxB = b.Center + b.Extents;

        var job = new StepJob
        {
            Dt = dt,
            Sigma = sigma,
            D = D,
            MinB = minB,
            MaxB = maxB,
            MaxSpeed = lp.MaxSpeed,
            ForceAccel = lp.ForceAccel,
            VelDamp = math.saturate(lp.VelocityDamping),
            Orb = op
        };

        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public partial struct StepJob : IJobEntity
    {
        public float Dt;
        public float Sigma;
        public float D;
        public float2 MinB, MaxB;
        public float MaxSpeed;
        public float ForceAccel;
        public float VelDamp;
        public OrbitalParams2D Orb;

        static uint Sanitize(uint s) => (s == 0u || s == 0xFFFFFFFFu) ? 1u : s;

        static float2 Gaussian2(ref Unity.Mathematics.Random rng)
        {
            float u1 = math.max(1e-7f, rng.NextFloat());
            float u2 = rng.NextFloat();
            float r = math.sqrt(-2f * math.log(u1));
            float a = 2f * math.PI * u2;
            return new float2(r * math.cos(a), r * math.sin(a));
        }

        static float2 ReflectClamp(float2 p, float2 minB, float2 maxB)
        {
            if (p.x < minB.x) p.x = minB.x + (minB.x - p.x);
            if (p.x > maxB.x) p.x = maxB.x - (p.x - maxB.x);
            if (p.y < minB.y) p.y = minB.y + (minB.y - p.y);
            if (p.y > maxB.y) p.y = maxB.y - (p.y - maxB.y);
            return p;
        }

        void Execute(ref Position pos, ref Velocity vel, ref RandomState rnd, ref Force force)
        {
            uint seed = Sanitize(rnd.Value);
            var rng = Unity.Mathematics.Random.CreateFromIndex(seed);

            // Orbital-guided drift
            float2 score = OrbitalScore.ScoreWorld(pos.Value, Orb);
            float2 drift = ForceAccel * score;

            // Add user force, integrate damped velocity
            float2 acc = drift + force.Value;
            vel.Value += acc * Dt;
            vel.Value *= (1f - VelDamp);

            if (MaxSpeed > 0f)
            {
                float spd = math.length(vel.Value);
                if (spd > MaxSpeed) vel.Value *= MaxSpeed / spd;
            }

            // Brownian kick + drift + velocity
            float2 noise = (Sigma > 0f) ? Sigma * Gaussian2(ref rng) : float2.zero;
            pos.Value += D * score * Dt + noise + vel.Value * Dt;

            pos.Value = ReflectClamp(pos.Value, MinB, MaxB);

            // decay push so mouse forces fade naturally
            force.Value *= 0.85f;

            rnd.Value = Sanitize(rng.state);
        }
    }
}
