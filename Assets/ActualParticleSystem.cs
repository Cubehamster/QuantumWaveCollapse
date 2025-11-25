using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// -------- Status types --------
public enum ActualParticleStatus : byte
{
    Unknown = 0,
    Good = 1,
    Bad = 2
}

public struct ActualParticleStatusElement : IBufferElementData
{
    public ActualParticleStatus Value;
}

// Positions for multiple actual particles (one per pool slot)
public struct ActualParticlePositionElement : IBufferElementData
{
    public float2 Value;
}

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ActualParticlePoolSystem))] // <<< ensure we run AFTER the pool assigns slots
public partial struct ActualParticleSystem : ISystem
{
    EntityQuery _walkersQ;
    EntityQuery _cfgQ;
    EntityQuery _reqQ;
    EntityQuery _singleApQ;

    public void OnCreate(ref SystemState state)
    {
        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>());

        _cfgQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ActualParticleSet>(),
            ComponentType.ReadWrite<ActualParticleRef>());

        // We still acknowledge SelectActualParticleRequest, but we no longer do
        // any reselection here; we just clear the tag so it doesn't pile up.
        _reqQ = state.GetEntityQuery(ComponentType.ReadOnly<SelectActualParticleRequest>());

        _singleApQ = state.GetEntityQuery(
            ComponentType.ReadWrite<ActualParticle>(),
            ComponentType.ReadWrite<ActualParticlePosition>());

        state.RequireForUpdate(_walkersQ);
        state.RequireForUpdate(_cfgQ);
    }

    public void OnUpdate(ref SystemState state)
    {
        var em = state.EntityManager;
        if (_cfgQ.IsEmptyIgnoreFilter) return;

        using var cfgs = _cfgQ.ToEntityArray(Allocator.Temp);
        var cfgEnt = cfgs[0]; // use first, ignore duplicates

        // Ensure position & status buffers exist
        if (!em.HasBuffer<ActualParticlePositionElement>(cfgEnt))
            em.AddBuffer<ActualParticlePositionElement>(cfgEnt);

        if (!em.HasBuffer<ActualParticleStatusElement>(cfgEnt))
            em.AddBuffer<ActualParticleStatusElement>(cfgEnt);

        var refBuf = em.GetBuffer<ActualParticleRef>(cfgEnt);              // authoritative pool layout
        var posBuf = em.GetBuffer<ActualParticlePositionElement>(cfgEnt);  // positions per slot
        var statusBuf = em.GetBuffer<ActualParticleStatusElement>(cfgEnt);    // status per slot

        int slotCount = refBuf.Length;

        // ------------------------------------------------------------
        // 1) Make sure position + status buffers track pool slot count
        // ------------------------------------------------------------
        if (posBuf.Length != slotCount)
            posBuf.ResizeUninitialized(slotCount);

        if (statusBuf.Length < slotCount)
        {
            int oldLen = statusBuf.Length;
            for (int i = oldLen; i < slotCount; i++)
            {
                statusBuf.Add(new ActualParticleStatusElement
                {
                    Value = ActualParticleStatus.Unknown
                });
            }
        }
        else if (statusBuf.Length > slotCount)
        {
            statusBuf.RemoveRange(slotCount, statusBuf.Length - slotCount);
        }

        // ------------------------------------------------------------
        // 2) Sync positions for all pool slots
        //    - If Walker != Entity.Null and has Position -> copy
        //    - Else -> position = float2.zero (inactive / missing)
        // ------------------------------------------------------------
        for (int i = 0; i < slotCount; i++)
        {
            Entity w = refBuf[i].Walker;
            float2 p = float2.zero;

            if (w != Entity.Null && em.Exists(w) && em.HasComponent<Position>(w))
            {
                p = em.GetComponentData<Position>(w).Value;
            }

            posBuf[i] = new ActualParticlePositionElement { Value = p };
        }

        // ------------------------------------------------------------
        // 3) Handle legacy singletons: first ACTIVE actual
        // ------------------------------------------------------------
        Entity firstWalker = Entity.Null;
        float2 firstPos = float2.zero;

        for (int i = 0; i < slotCount; i++)
        {
            Entity w = refBuf[i].Walker;
            if (w == Entity.Null) continue;

            // first active slot wins
            firstWalker = w;
            firstPos = posBuf[i].Value;
            break;
        }

        EnsureLegacySingletons(ref state, firstWalker, firstWalker != Entity.Null, firstPos);

        // ------------------------------------------------------------
        // 4) Clear any SelectActualParticleRequest tags (no reselection here)
        // ------------------------------------------------------------
        if (!_reqQ.IsEmptyIgnoreFilter)
        {
            using var reqs = _reqQ.ToEntityArray(Allocator.Temp);
            foreach (var r in reqs)
            {
                em.RemoveComponent<SelectActualParticleRequest>(r);
            }
        }
    }

    void EnsureLegacySingletons(ref SystemState state, Entity firstWalker, bool hasFirst, float2 firstPos)
    {
        var em = state.EntityManager;

        if (!TryGetSingletonEntity<ActualParticle>(ref state, out var apEnt))
            apEnt = em.CreateEntity(typeof(ActualParticle));

        if (!TryGetSingletonEntity<ActualParticlePosition>(ref state, out var posEnt))
            posEnt = em.CreateEntity(typeof(ActualParticlePosition));

        em.SetComponentData(apEnt, new ActualParticle { Walker = hasFirst ? firstWalker : Entity.Null });
        em.SetComponentData(posEnt, new ActualParticlePosition { Value = hasFirst ? firstPos : float2.zero });
    }

    static bool TryGetSingletonEntity<T>(ref SystemState state, out Entity ent)
        where T : unmanaged, IComponentData
    {
        var q = state.GetEntityQuery(ComponentType.ReadOnly<T>());
        if (q.CalculateEntityCount() == 1)
        {
            ent = q.GetSingletonEntity();
            return true;
        }
        ent = Entity.Null;
        return false;
    }
}
