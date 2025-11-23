using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// -------- NEW: status types --------
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

// Positions for multiple actual particles
public struct ActualParticlePositionElement : IBufferElementData
{
    public float2 Value;
}

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
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
            ComponentType.ReadWrite<ActualParticleRng>(),
            ComponentType.ReadWrite<ActualParticleRef>());

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

        // Ensure position buffer exists
        if (!em.HasBuffer<ActualParticlePositionElement>(cfgEnt))
            em.AddBuffer<ActualParticlePositionElement>(cfgEnt);

        // NEW: ensure status buffer exists
        if (!em.HasBuffer<ActualParticleStatusElement>(cfgEnt))
            em.AddBuffer<ActualParticleStatusElement>(cfgEnt);

        var set = em.GetComponentData<ActualParticleSet>(cfgEnt);
        var rngState = em.GetComponentData<ActualParticleRng>(cfgEnt);
        var selBuf = em.GetBuffer<ActualParticleRef>(cfgEnt);
        var posBuf = em.GetBuffer<ActualParticlePositionElement>(cfgEnt);
        var statusBuf = em.GetBuffer<ActualParticleStatusElement>(cfgEnt);

        int desired = math.max(1, set.DesiredCount);
        int walkerCount = _walkersQ.CalculateEntityCount();
        if (walkerCount == 0) return;

        using var walkers = _walkersQ.ToEntityArray(Allocator.Temp);

        // Build set of currently chosen walkers (only valid ones)
        var chosen = new NativeHashSet<Entity>(math.max(desired, selBuf.Length) * 2, Allocator.Temp);
        for (int i = 0; i < selBuf.Length; i++)
        {
            var w = selBuf[i].Walker;
            if (em.Exists(w) && em.HasComponent<Position>(w))
                chosen.Add(w);
        }

        uint seed = Sanitize(rngState.Value);
        var rnd = Unity.Mathematics.Random.CreateFromIndex(seed);

        // Fill up to desired count
        while (chosen.Count < desired && chosen.Count < walkerCount)
        {
            var pick = walkers[(int)(rnd.NextUInt() % (uint)walkerCount)];
            chosen.Add(pick);
        }

        // Trim down if too many (randomly drops)
        while (chosen.Count > desired)
        {
            var pick = walkers[(int)(rnd.NextUInt() % (uint)walkerCount)];
            if (chosen.Contains(pick))
                chosen.Remove(pick);
        }

        // Handle re-roll request: replace the first slot with a fresh random walker
        if (!_reqQ.IsEmptyIgnoreFilter && chosen.Count > 0)
        {
            using var reqs = _reqQ.ToEntityArray(Allocator.Temp);
            foreach (var r in reqs)
                em.RemoveComponent<SelectActualParticleRequest>(r);

            var walkersArr = walkers;
            var old = selBuf.Length > 0 ? selBuf[0].Walker : Entity.Null;
            if (old != Entity.Null)
                chosen.Remove(old);

            for (int tries = 0; tries < 512 && chosen.Count < desired; tries++)
            {
                var cand = walkersArr[(int)(rnd.NextUInt() % (uint)walkerCount)];
                if (!chosen.Contains(cand))
                {
                    chosen.Add(cand);
                    break;
                }
            }
        }

        // Store RNG back
        rngState.Value = Sanitize(rnd.state);
        em.SetComponentData(cfgEnt, rngState);

        // --------------------------------------------------------------------
        // Rebuild selBuf from 'chosen' set (keeping it dense, deterministic-ish).
        // DO NOT clear statusBuf here; it tracks per-slot state (Unknown/Good/Bad).
        // --------------------------------------------------------------------
        int iBuf = 0;
        for (int i = 0; i < walkers.Length; i++)
        {
            var w = walkers[i];
            if (!chosen.Contains(w)) continue;

            if (iBuf >= selBuf.Length)
                selBuf.Add(new ActualParticleRef { Walker = w });
            else
                selBuf[iBuf] = new ActualParticleRef { Walker = w };

            iBuf++;
            if (iBuf >= desired) break;
        }

        if (iBuf < selBuf.Length)
            selBuf.RemoveRange(iBuf, selBuf.Length - iBuf);

        // --------------------------------------------------------------------
        // Keep position buffer in sync with selBuf (one-to-one).
        // --------------------------------------------------------------------
        posBuf.ResizeUninitialized(selBuf.Length);
        for (int i = 0; i < selBuf.Length; i++)
        {
            var w = selBuf[i].Walker;
            posBuf[i] = new ActualParticlePositionElement
            {
                Value = (em.Exists(w) && em.HasComponent<Position>(w))
                    ? em.GetComponentData<Position>(w).Value
                    : float2.zero
            };
        }

        // --------------------------------------------------------------------
        // Keep status buffer length in sync with selBuf, but preserve existing
        // statuses for the leading slots. New slots default to Unknown.
        // --------------------------------------------------------------------
        if (statusBuf.Length < selBuf.Length)
        {
            int oldLen = statusBuf.Length;
            for (int i = oldLen; i < selBuf.Length; i++)
            {
                statusBuf.Add(new ActualParticleStatusElement
                {
                    Value = ActualParticleStatus.Unknown
                });
            }
        }
        else if (statusBuf.Length > selBuf.Length)
        {
            statusBuf.RemoveRange(selBuf.Length, statusBuf.Length - selBuf.Length);
        }

        // --------------------------------------------------------------------
        // Maintain legacy singletons: first actual is the "classic" ActualParticle
        // --------------------------------------------------------------------
        Entity first = selBuf.Length > 0 ? selBuf[0].Walker : Entity.Null;
        float2 firstPos = float2.zero;

        if (first != Entity.Null && em.Exists(first) && em.HasComponent<Position>(first))
            firstPos = em.GetComponentData<Position>(first).Value;

        EnsureLegacySingletons(ref state, first, first != Entity.Null, firstPos);
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

    static uint Sanitize(uint s) => (s == 0u || s == 0xFFFFFFFFu) ? 1u : s;
}
