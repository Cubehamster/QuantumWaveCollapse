using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Measurement interaction (two players):
/// • P1: ClickRequest + MeasurementResult
/// • P2: ClickRequestP2 + MeasurementResultP2
///
/// For each player:
///   - Finds closest actual particle within a highlight radius.
///   - Applies radial force to walkers:
///       * R_outer = req.Radius, StrengthOuter = req.PushStrength
///       * R_inner = req.InnerRadius, StrengthInner = req.InnerPushStrength
///     Inside R_inner: uses StrengthInner (e.g. small negative pull).
///     Between R_inner and R_outer: uses StrengthOuter.
///   - Only the closest actual can be pulled; other actuals are always pushed.
///
/// NEW:
///   - Uses ActualParticleMetaElement.Age and ActualParticlePoolSystem.ScanLockDuration
///   - If Age < ScanLockDuration, that actual is ignored for highlighting/scanning.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(LangevinWalkSystem))]
public partial struct MeasurementSystem : ISystem
{
    EntityQuery _walkersQ;
    EntityQuery _cfgQ;

    EntityQuery _reqP1Q;
    EntityQuery _reqP2Q;

    EntityQuery _resP1Q;
    EntityQuery _resP2Q;

    public void OnCreate(ref SystemState state)
    {
        var em = state.EntityManager;

        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadWrite<Force>());

        _cfgQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ActualParticleSet>(),
            ComponentType.ReadOnly<ActualParticleRef>(),
            ComponentType.ReadOnly<ActualParticlePositionElement>(),
            ComponentType.ReadOnly<ActualParticleMetaElement>()); // NEW

        _reqP1Q = state.GetEntityQuery(ComponentType.ReadOnly<ClickRequest>());
        _reqP2Q = state.GetEntityQuery(ComponentType.ReadOnly<ClickRequestP2>());

        _resP1Q = state.GetEntityQuery(ComponentType.ReadWrite<MeasurementResult>());
        _resP2Q = state.GetEntityQuery(ComponentType.ReadWrite<MeasurementResultP2>());

        // Ensure MeasurementResult singletons exist
        if (_resP1Q.CalculateEntityCount() == 0)
        {
            var e1 = em.CreateEntity(typeof(MeasurementResult));
            em.SetName(e1, "MeasurementResult_P1");
            em.SetComponentData(e1, new MeasurementResult
            {
                HasActualInRadius = false,
                ClosestActualIndex = -1,
                ClosestActualPos = float2.zero
            });
        }

        if (_resP2Q.CalculateEntityCount() == 0)
        {
            var e2 = em.CreateEntity(typeof(MeasurementResultP2));
            em.SetName(e2, "MeasurementResult_P2");
            em.SetComponentData(e2, new MeasurementResultP2
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

        int N = _walkersQ.CalculateEntityCount();
        bool haveCfg = !_cfgQ.IsEmptyIgnoreFilter;

        // Early out: no walkers at all or no config
        if (N == 0 || !haveCfg)
        {
            ClearResultIfPresent<MeasurementResult>(ref state, _resP1Q);
            ClearResultIfPresent<MeasurementResultP2>(ref state, _resP2Q);
            return;
        }

        // We have at least one cfg entity
        using var cfgEnts = _cfgQ.ToEntityArray(Allocator.Temp);
        var cfgEnt = cfgEnts[0];

        var posBuf = em.GetBuffer<ActualParticlePositionElement>(cfgEnt);
        var refBuf = em.GetBuffer<ActualParticleRef>(cfgEnt);
        var metaBuf = em.GetBuffer<ActualParticleMetaElement>(cfgEnt); // NEW

        // Process Player 1
        if (!_resP1Q.IsEmptyIgnoreFilter)
        {
            var resultEnt1 = _resP1Q.GetSingletonEntity();
            var result1 = em.GetComponentData<MeasurementResult>(resultEnt1);

            if (_reqP1Q.IsEmptyIgnoreFilter)
            {
                result1.HasActualInRadius = false;
                result1.ClosestActualIndex = -1;
                em.SetComponentData(resultEnt1, result1);
            }
            else
            {
                var req1 = _reqP1Q.GetSingleton<ClickRequest>();
                ProcessPlayer(
                    ref state,
                    ref result1,
                    resultEnt1,
                    req1,
                    N,
                    posBuf,
                    refBuf,
                    metaBuf,
                    isP2: false);
            }
        }

        // Process Player 2
        if (!_resP2Q.IsEmptyIgnoreFilter)
        {
            var resultEnt2 = _resP2Q.GetSingletonEntity();
            var result2 = em.GetComponentData<MeasurementResultP2>(resultEnt2);

            if (_reqP2Q.IsEmptyIgnoreFilter)
            {
                result2.HasActualInRadius = false;
                result2.ClosestActualIndex = -1;
                em.SetComponentData(resultEnt2, result2);
            }
            else
            {
                var req2 = _reqP2Q.GetSingleton<ClickRequestP2>();
                ProcessPlayer(
                    ref state,
                    ref result2,
                    resultEnt2,
                    req2,
                    N,
                    posBuf,
                    refBuf,
                    metaBuf,
                    isP2: true);
            }
        }
    }

    // ---------------- Player 1 version ----------------
    void ProcessPlayer(
        ref SystemState state,
        ref MeasurementResult result,
        Entity resultEnt,
        ClickRequest req,
        int N,
        DynamicBuffer<ActualParticlePositionElement> posBuf,
        DynamicBuffer<ActualParticleRef> refBuf,
        DynamicBuffer<ActualParticleMetaElement> metaBuf, // NEW
        bool isP2)
    {
        var em = state.EntityManager;

        float2 c = req.WorldPos;
        float R = math.max(1e-6f, req.Radius);
        float R2 = R * R;

        // Highlight radius (for selecting closest actual)
        float exclMul = math.max(0f, req.ExclusionRadiusMultiplier);
        float highlightR = (exclMul > 0f) ? R * exclMul : R;
        float highlightR2 = highlightR * highlightR;

        int closestIdx = -1;
        float closestD2 = float.MaxValue;
        float2 closestPos = float2.zero;

        float scanLock = ActualParticlePoolSystem.ScanLockDuration;

        if (highlightR > 0f && posBuf.Length > 0)
        {
            int len = math.min(posBuf.Length, math.min(refBuf.Length, metaBuf.Length));
            for (int i = 0; i < len; i++)
            {
                // ignore inactive slots
                if (refBuf[i].Walker == Entity.Null)
                    continue;

                // NEW: ignore very young actuals (still invisible or fading in)
                if (metaBuf[i].Age < scanLock)
                    continue;

                float2 p = posBuf[i].Value;
                float2 d = p - c;
                float d2 = math.lengthsq(d);

                if (d2 <= highlightR2 && d2 < closestD2)
                {
                    closestD2 = d2;
                    closestIdx = i;
                    closestPos = p;
                }
            }
        }

        result.Center = c;
        result.HasActualInRadius = (closestIdx >= 0);
        result.ClosestActualIndex = closestIdx;
        result.ClosestActualPos = closestPos;
        em.SetComponentData(resultEnt, result);

        // ---------------- Inner region force parameters ----------------
        float innerR = math.max(0f, req.InnerRadius);
        float innerR2 = innerR * innerR;
        float strengthOuter = req.PushStrength;
        float strengthInner = req.InnerPushStrength;

        // Convert actual refs buffer to NativeArray<Entity> for the job
        NativeArray<Entity> actualRefs = refBuf.Reinterpret<Entity>().AsNativeArray();

        // Which walker is the closest actual for this player?
        Entity closestActualEntity = Entity.Null;
        bool hasClosestActual = false;
        if (result.HasActualInRadius &&
            result.ClosestActualIndex >= 0 &&
            result.ClosestActualIndex < refBuf.Length)
        {
            closestActualEntity = refBuf[result.ClosestActualIndex].Walker;
            hasClosestActual = true;
        }

        // While pressed: suction/blow (two-zone)
        if (req.IsPressed && strengthOuter != 0f)
        {
            var job = new PushJob
            {
                Center = c,
                OuterR2 = R2,
                InnerR2 = innerR2,
                StrengthOuter = strengthOuter,
                StrengthInner = strengthInner,
                EdgeSoft = 1.0f,
                ClosestActualEntity = closestActualEntity,
                HasClosestActual = hasClosestActual,
                ActualRefs = actualRefs
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        // On release: outward pulse, also compute probability
        if (req.EdgeUp)
        {
            var push = new PushJob
            {
                Center = c,
                OuterR2 = R2,
                InnerR2 = innerR2,
                StrengthOuter = math.abs(strengthOuter),
                StrengthInner = strengthInner,
                EdgeSoft = 1.0f,
                ClosestActualEntity = closestActualEntity,
                HasClosestActual = hasClosestActual,
                ActualRefs = actualRefs
            };
            state.Dependency = push.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

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
            result.ProbabilityLast = p;
            em.SetComponentData(resultEnt, result);

            //Debug.Log($"[Measure {(isP2 ? "P2" : "P1")}] p≈{Round3(p)} @ {c}");
        }
    }

    // ---------------- Player 2 version ----------------
    void ProcessPlayer(
        ref SystemState state,
        ref MeasurementResultP2 result,
        Entity resultEnt,
        ClickRequestP2 req,
        int N,
        DynamicBuffer<ActualParticlePositionElement> posBuf,
        DynamicBuffer<ActualParticleRef> refBuf,
        DynamicBuffer<ActualParticleMetaElement> metaBuf, // NEW
        bool isP2)
    {
        var em = state.EntityManager;

        float2 c = req.WorldPos;
        float R = math.max(1e-6f, req.Radius);
        float R2 = R * R;

        float exclMul = math.max(0f, req.ExclusionRadiusMultiplier);
        float highlightR = (exclMul > 0f) ? R * exclMul : R;
        float highlightR2 = highlightR * highlightR;

        int closestIdx = -1;
        float closestD2 = float.MaxValue;
        float2 closestPos = float2.zero;

        float scanLock = ActualParticlePoolSystem.ScanLockDuration;

        if (highlightR > 0f && posBuf.Length > 0)
        {
            int len = math.min(posBuf.Length, math.min(refBuf.Length, metaBuf.Length));
            for (int i = 0; i < len; i++)
            {
                if (refBuf[i].Walker == Entity.Null)
                    continue;

                if (metaBuf[i].Age < scanLock)
                    continue;

                float2 p = posBuf[i].Value;
                float2 d = p - c;
                float d2 = math.lengthsq(d);

                if (d2 <= highlightR2 && d2 < closestD2)
                {
                    closestD2 = d2;
                    closestIdx = i;
                    closestPos = p;
                }
            }
        }

        result.Center = c;
        result.HasActualInRadius = (closestIdx >= 0);
        result.ClosestActualIndex = closestIdx;
        result.ClosestActualPos = closestPos;
        em.SetComponentData(resultEnt, result);

        float innerR = math.max(0f, req.InnerRadius);
        float innerR2 = innerR * innerR;
        float strengthOuter = req.PushStrength;
        float strengthInner = req.InnerPushStrength;

        NativeArray<Entity> actualRefs = refBuf.Reinterpret<Entity>().AsNativeArray();

        Entity closestActualEntity = Entity.Null;
        bool hasClosestActual = false;
        if (result.HasActualInRadius &&
            result.ClosestActualIndex >= 0 &&
            result.ClosestActualIndex < refBuf.Length)
        {
            closestActualEntity = refBuf[result.ClosestActualIndex].Walker;
            hasClosestActual = true;
        }

        if (req.IsPressed && strengthOuter != 0f)
        {
            var job = new PushJob
            {
                Center = c,
                OuterR2 = R2,
                InnerR2 = innerR2,
                StrengthOuter = strengthOuter,
                StrengthInner = strengthInner,
                EdgeSoft = 1.0f,
                ClosestActualEntity = closestActualEntity,
                HasClosestActual = hasClosestActual,
                ActualRefs = actualRefs
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        if (req.EdgeUp)
        {
            var push = new PushJob
            {
                Center = c,
                OuterR2 = R2,
                InnerR2 = innerR2,
                StrengthOuter = math.abs(strengthOuter),
                StrengthInner = strengthInner,
                EdgeSoft = 1.0f,
                ClosestActualEntity = closestActualEntity,
                HasClosestActual = hasClosestActual,
                ActualRefs = actualRefs
            };
            state.Dependency = push.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();

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
            result.ProbabilityLast = p;
            em.SetComponentData(resultEnt, result);

            //Debug.Log($"[Measure {(isP2 ? "P2" : "P1")}] p≈{Round3(p)} @ {c}");
        }
    }

    static float Round3(float v) => math.round(v * 1000f) * 0.001f;

    void ClearResultIfPresent<T>(ref SystemState state, EntityQuery q) where T : unmanaged, IComponentData
    {
        var em = state.EntityManager;
        if (!q.IsEmptyIgnoreFilter)
        {
            var e = q.GetSingletonEntity();
            var r = em.GetComponentData<T>(e);
            // no-op for now
        }
    }

    // ----------------------------------------------------------------------
    // JOBS
    // ----------------------------------------------------------------------

    /// <summary>
    /// Two-zone radial push job:
    /// - OuterR2: full measurement radius^2, StrengthOuter used outside inner region.
    /// - InnerR2: inner radius^2, StrengthInner used inside (if > 0).
    /// - Only ClosestActualEntity is allowed to feel inner pull; other actuals are always pushed.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ParticleTag))]
    public partial struct PushJob : IJobEntity
    {
        public float2 Center;
        public float OuterR2;
        public float InnerR2;      // 0 → no inner region
        public float StrengthOuter;
        public float StrengthInner;
        public float EdgeSoft;     // currently unused, kept for curve tuning if needed

        public Entity ClosestActualEntity;
        public bool HasClosestActual;

        [ReadOnly]
        public NativeArray<Entity> ActualRefs;

        public void Execute(Entity entity, ref Force f, in Position pos)
        {
            float2 d = pos.Value - Center;
            float d2 = math.lengthsq(d);
            if (d2 > OuterR2)
                return;

            float r = math.sqrt(math.max(1e-12f, d2));
            float2 dir = d / r;

            float t = 1f - (r / math.sqrt(OuterR2));
            t = math.saturate(t);
            float shaped = t * t;

            // Is this entity one of the actuals?
            bool isActual = false;
            for (int i = 0; i < ActualRefs.Length; i++)
            {
                if (entity == ActualRefs[i])
                {
                    isActual = true;
                    break;
                }
            }

            float strength;

            if (HasClosestActual && entity == ClosestActualEntity)
            {
                // Closest actual: can get inner pull
                if (InnerR2 > 0f && d2 <= InnerR2)
                    strength = StrengthInner;  // e.g. negative pull
                else
                    strength = StrengthOuter;
            }
            else if (isActual)
            {
                // Other actuals: *never* pulled, always pushed
                strength = StrengthOuter;
            }
            else
            {
                // Normal walkers: inner pull if inside inner radius, otherwise push
                if (InnerR2 > 0f && d2 <= InnerR2)
                    strength = StrengthInner;
                else
                    strength = StrengthOuter;
            }

            f.Value += dir * (strength * shaped);
        }
    }

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
}
