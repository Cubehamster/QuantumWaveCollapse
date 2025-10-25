using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ActualParticleSystem : ISystem
{
    EntityQuery _walkersQ;
    EntityQuery _reqQ;
    EntityQuery _apQ;
    EntityQuery _posQ;
    EntityQuery _rngQ;

    public void OnCreate(ref SystemState state)
    {
        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>());

        _reqQ = state.GetEntityQuery(ComponentType.ReadOnly<SelectActualParticleRequest>());
        _apQ = state.GetEntityQuery(ComponentType.ReadWrite<ActualParticle>());
        _posQ = state.GetEntityQuery(ComponentType.ReadWrite<ActualParticlePosition>());
        _rngQ = state.GetEntityQuery(ComponentType.ReadWrite<GlobalRng>());

        var em = state.EntityManager;

        // Ensure RNG singleton
        if (!_rngQ.TryGetSingletonEntity<GlobalRng>(out var _rngEnt))
        {
            var e = em.CreateEntity(typeof(GlobalRng));
            uint seed = (uint)System.DateTime.UtcNow.Ticks;
            if (seed == 0u || seed == 0xFFFFFFFFu) seed = 1u;
            em.SetComponentData(e, new GlobalRng { State = seed });
        }

        // Ensure ActualParticle singleton
        if (!_apQ.TryGetSingletonEntity<ActualParticle>(out var _apEnt))
        {
            var e = em.CreateEntity(typeof(ActualParticle));
            em.SetComponentData(e, new ActualParticle { Walker = Entity.Null });
        }

        // Ensure position singleton
        if (!_posQ.TryGetSingletonEntity<ActualParticlePosition>(out var _posEnt))
        {
            var e = em.CreateEntity(typeof(ActualParticlePosition));
            em.SetComponentData(e, new ActualParticlePosition { Value = float2.zero });
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        var em = state.EntityManager;

        var apEnt = _apQ.GetSingletonEntity();
        var ap = em.GetComponentData<ActualParticle>(apEnt);

        bool needSelect = !_walkersQ.IsEmptyIgnoreFilter && (
            !_reqQ.IsEmptyIgnoreFilter ||
            ap.Walker == Entity.Null ||
            !em.Exists(ap.Walker) ||
            !em.HasComponent<Position>(ap.Walker)
        );

        if (needSelect)
        {
            TryPickRandomWalker(ref state, ref ap);
            em.SetComponentData(apEnt, ap);

            // consume request if present
            if (!_reqQ.IsEmptyIgnoreFilter)
            {
                var reqEnt = _reqQ.GetSingletonEntity();
                em.RemoveComponent<SelectActualParticleRequest>(reqEnt);
            }
        }

        // Update ActualParticlePosition
        var posEnt = _posQ.GetSingletonEntity();
        var apPos = em.GetComponentData<ActualParticlePosition>(posEnt);

        if (ap.Walker != Entity.Null && em.Exists(ap.Walker) && em.HasComponent<Position>(ap.Walker))
        {
            apPos.Value = em.GetComponentData<Position>(ap.Walker).Value;
            em.SetComponentData(posEnt, apPos);
        }
    }

    void TryPickRandomWalker(ref SystemState state, ref ActualParticle ap)
    {
        var em = state.EntityManager;
        int count = _walkersQ.CalculateEntityCount();
        if (count <= 0) { ap.Walker = Entity.Null; return; }

        using var walkers = _walkersQ.ToEntityArray(Allocator.Temp);

        // RNG
        var rngEnt = _rngQ.GetSingletonEntity();
        var g = em.GetComponentData<GlobalRng>(rngEnt);
        if (g.State == 0u || g.State == 0xFFFFFFFFu) g.State = 1u;

        var rng = Unity.Mathematics.Random.CreateFromIndex(g.State);
        int idx = (int)(rng.NextUInt() % (uint)count);
        ap.Walker = walkers[idx];

        g.State = (rng.state == 0u || rng.state == 0xFFFFFFFFu) ? 1u : rng.state;
        em.SetComponentData(rngEnt, g);
    }
}
