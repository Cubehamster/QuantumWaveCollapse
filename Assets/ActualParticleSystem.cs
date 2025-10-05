using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct ActualParticleSystem : ISystem
{
    EntityQuery _q;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _q = state.GetEntityQuery(ComponentType.ReadOnly<ParticleTag>());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // If there is no ActualParticle singleton, pick one deterministically.
        if (!SystemAPI.HasSingleton<ActualParticle>())
        {
            var ents = _q.ToEntityArray(Unity.Collections.Allocator.Temp);
            if (ents.Length > 0)
            {
                var ap = new ActualParticle { Walker = ents[0] };
                var ent = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(ent, ap);
            }
            ents.Dispose();
        }
        else
        {
            // If stored entity no longer exists (rare), re-pick.
            var ap = SystemAPI.GetSingleton<ActualParticle>();
            if (!state.EntityManager.Exists(ap.Walker))
            {
                var ents = _q.ToEntityArray(Unity.Collections.Allocator.Temp);
                ap.Walker = ents.Length > 0 ? ents[0] : Entity.Null;
                SystemAPI.SetSingleton(ap);
                ents.Dispose();
            }
        }
    }
}
