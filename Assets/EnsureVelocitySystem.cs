using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct EnsureVelocitySystem : ISystem
{
    EntityQuery _missingVelQ;

    public void OnCreate(ref SystemState state)
    {
        // Run once when we have any ParticleTag in the world.
        state.RequireForUpdate<ParticleTag>();

        // All particles that DON'T have Velocity yet
        _missingVelQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.Exclude<Velocity>());
    }

    public void OnUpdate(ref SystemState state)
    {
        // Nothing to do? Disable and exit.
        if (_missingVelQ.IsEmpty)
        {
            state.Enabled = false; // we assume new spawns add Velocity themselves
            return;
        }

        var ecb = new EntityCommandBuffer(Allocator.Temp);
        var entities = _missingVelQ.ToEntityArray(Allocator.Temp);

        for (int i = 0; i < entities.Length; i++)
            ecb.AddComponent(entities[i], new Velocity { Value = float2.zero });

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
        entities.Dispose();

        // We’ve patched all existing particles; turn this system off.
        state.Enabled = false;
    }
}
