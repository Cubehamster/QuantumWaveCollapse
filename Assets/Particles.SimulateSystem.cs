using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct IntegrateAndBounce2DSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SimBounds2D>();
        state.RequireForUpdate<ParticleTag>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;
        var b = SystemAPI.GetSingleton<SimBounds2D>();
        float2 minB = b.Center - b.Extents;
        float2 maxB = b.Center + b.Extents;

        var job = new IntegrateJob
        {
            Dt = dt,
            MinBounds = minB,
            MaxBounds = maxB
        };

        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public partial struct IntegrateJob : IJobEntity
    {
        public float Dt;
        public float2 MinBounds;
        public float2 MaxBounds;

        // Weight is present on the archetype now; not used here yet
        public void Execute(ref Position pos, ref Velocity vel /*, in Weight weight*/)
        {
            pos.Value += vel.Value * Dt;

            bool2 hitMin = pos.Value < MinBounds;
            bool2 hitMax = pos.Value > MaxBounds;
            bool2 hit = hitMin | hitMax;

            // reflect per-axis
            vel.Value = math.select(vel.Value, -vel.Value, hit);

            // clamp inside bounds
            pos.Value = math.clamp(pos.Value, MinBounds, MaxBounds);
        }
    }
}
