using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Measurement interaction:
/// • Uses ClickRequest (from MeasurementClick) to apply forces to walkers.
/// • While pressed: suction (inward force).
/// • On release: outward pulse.
/// • Also finds the *closest* actual particle inside the measurement radius
///   every frame and writes it to MeasurementResult.
/// • Good/Bad status is NOT used here; highlight selection is purely geometric.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(LangevinWalkSystem))]

public struct MeasurementResult : IComponentData
{
    public float2 Center;            // world position of current measurement center
    public bool HasActualInRadius;   // true if at least one "actual" walker inside exclusion radius this frame
    public float ProbabilityLast;    // fraction of walkers inside last released radius
    public int ClosestActualIndex;  // -1 = none
    public float2 ClosestActualPos;
}

public partial struct MeasurementSystem : ISystem
{
    EntityQuery _walkersQ;
    EntityQuery _reqQ;
    EntityQuery _cfgQ;
    EntityQuery _resultQ;

    public void OnCreate(ref SystemState state)
    {
        var em = state.EntityManager;

        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadWrite<Force>());

        _reqQ = state.GetEntityQuery(ComponentType.ReadOnly<ClickRequest>());

        // Config that holds actual particles + positions
        _cfgQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ActualParticleSet>(),
            ComponentType.ReadOnly<ActualParticleRef>(),
            ComponentType.ReadOnly<ActualParticlePositionElement>());

        _resultQ = state.GetEntityQuery(ComponentType.ReadWrite<MeasurementResult>());

        // Ensure MeasurementResult singleton exists
        if (_resultQ.CalculateEntityCount() == 0)
        {
            var e = em.CreateEntity(typeof(MeasurementResult));
            em.SetName(e, "MeasurementResultSingleton");
            em.SetComponentData(e, new MeasurementResult
            {
                HasActualInRadius = false,
                ClosestActualIndex = -1,
                ClosestActualPos = float2.zero
            });
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        var em = state.EntityManager;

        if (_resultQ.IsEmptyIgnoreFilter)
            return;

        var resultEnt = _resultQ.GetSingletonEntity();
        var result = em.GetComponentData<MeasurementResult>(resultEnt);

        // No click input? Clear highlight & bail.
        if (_reqQ.IsEmptyIgnoreFilter)
        {
            result.HasActualInRadius = false;
            result.ClosestActualIndex = -1;
            em.SetComponentData(resultEnt, result);
            return;
        }

        var req = _reqQ.GetSingleton<ClickRequest>();

        int N = _walkersQ.CalculateEntityCount();
        if (N == 0)
        {
            result.HasActualInRadius = false;
            result.ClosestActualIndex = -1;
            em.SetComponentData(resultEnt, result);
            return;
        }

        float2 c = req.WorldPos;
        float R = math.max(1e-6f, req.Radius);
        float R2 = R * R;

        //---------------------------------------------------------------
        // Highlight radius = R * exclusionMultiplier
        //---------------------------------------------------------------
        float exclMul = math.max(0f, req.ExclusionRadiusMultiplier);
        float highlightR = R * exclMul;
        float highlightR2 = highlightR * highlightR;

        int closestIdx = -1;
        float closestD2 = float.MaxValue;
        float2 closestPos = float2.zero;

        // If multiplier = 0 → highlightR = 0 → skip highlight entirely
        if (highlightR > 0f && !_cfgQ.IsEmptyIgnoreFilter)
        {
            using var cfgEnts = _cfgQ.ToEntityArray(Allocator.Temp);
            var cfgEnt = cfgEnts[0];

            var posBuf = em.GetBuffer<ActualParticlePositionElement>(cfgEnt);

            for (int i = 0; i < posBuf.Length; i++)
            {
                float2 p = posBuf[i].Value;
                float2 d = p - c;
                float d2 = math.lengthsq(d);

                // Only highlight if inside highlight radius
                if (d2 <= highlightR2 && d2 < closestD2)
                {
                    closestD2 = d2;
                    closestIdx = i;
                    closestPos = p;
                }
            }
        }

        result.HasActualInRadius = (closestIdx >= 0);
        result.ClosestActualIndex = closestIdx;
        result.ClosestActualPos = closestPos;
        em.SetComponentData(resultEnt, result);
        // ------------------------------------------------------------------
        // 2) Apply forces to walkers (same shape as your old system).
        // ------------------------------------------------------------------

        float R2Push = R2;

        // While pressed -> suction (inward)
        if (req.IsPressed && req.PushStrength != 0f)
        {
            var job = new PushJob
            {
                Center = c,
                R2 = R2Push,
                Strength = math.abs(req.PushStrength), // inward; flip sign in job if you want outward
                EdgeSoft = 1.0f
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        // On release -> outward pulse
        if (req.EdgeUp)
        {
            var push = new PushJob
            {
                Center = c,
                R2 = R2Push,
                Strength = math.abs(req.PushStrength), // outward (same magnitude)
                EdgeSoft = 1.0f
            };
            state.Dependency = push.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            // Count how many walkers ended inside radius (for debug p≈...)
            var insideCount = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var count = new CountInsideJob
            {
                Center = c,
                R2 = R2Push,
                Counter = insideCount
            };
            state.Dependency = count.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

            int inside = insideCount[0];
            insideCount.Dispose();

            float p = (float)inside / math.max(1, N);
            Debug.Log($"[Measure] p≈{Round3(p)} @ {c}");
        }

        // ------------------------------------------------------------------
        // 3) (Optional) Protect the closest actual from this frame's push:
        //    We simply zero its Force so the measurement push doesn't move it.
        //    This matches the behaviour "one actual is not influenced by push".
        // ------------------------------------------------------------------
        if (result.HasActualInRadius &&
            result.ClosestActualIndex >= 0 &&
            !_cfgQ.IsEmptyIgnoreFilter)
        {
            using var cfgEnts2 = _cfgQ.ToEntityArray(Allocator.Temp);
            var cfgEnt2 = cfgEnts2[0];
            var refBuf = em.GetBuffer<ActualParticleRef>(cfgEnt2);

            int idx = result.ClosestActualIndex;
            if (idx >= 0 && idx < refBuf.Length)
            {
                Entity protectedWalker = refBuf[idx].Walker;

                var zeroJob = new ZeroForceActualJob
                {
                    ActualEntity = protectedWalker
                };
                state.Dependency = zeroJob.ScheduleParallel(state.Dependency);
            }
        }

        // (Optional: you still have your commented-out CollapseJob if you want
        //  that visual after a successful measurement.)
    }

    static float Round3(float v) => math.round(v * 1000f) * 0.001f;

    // ----------------------------------------------------------------------
    // JOBS
    // ----------------------------------------------------------------------

    /// <summary>
    /// Adds a radial force within a circle. Positive or negative Strength is
    /// interpreted by you (e.g. use sign inside Execute if you want suction vs blow).
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct PushJob : IJobEntity
    {
        public float2 Center;
        public float R2;        // radius^2
        public float Strength;  // magnitude (sign is up to your convention)
        public float EdgeSoft;  // softness exponent for falloff

        public void Execute(ref Force f, in Position pos)
        {
            float2 d = pos.Value - Center;
            float d2 = math.lengthsq(d);
            if (d2 > R2)
                return;

            float r = math.sqrt(math.max(1e-12f, d2));
            float2 dir = d / r;

            // Smooth falloff t in [0..1]
            float t = 1f - (r / math.sqrt(R2));
            t = math.saturate(t);
            float shaped = t * t; // quadratic

            float2 forceVec = dir * (Strength * shaped);
            f.Value += forceVec;
        }
    }

    /// <summary>
    /// Counts walkers inside the radius (approx; benign race on a single int).
    /// </summary>
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
                Counter[0] = Counter[0] + 1;
            }
        }
    }

    /// <summary>
    /// Zeroes Force on the chosen "protected" actual walker so that the measurement
    /// push does not affect it this frame.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct ZeroForceActualJob : IJobEntity
    {
        public Entity ActualEntity;

        public void Execute(Entity entity, ref Force f)
        {
            if (entity == ActualEntity)
            {
                f.Value = float2.zero;
            }
        }
    }
}
