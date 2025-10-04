using Unity.Entities;
using UnityEngine;

// Runs before the spawner so it can see requests *before* they’re consumed.
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[UpdateBefore(typeof(ParticleSpawnSystem))]
public partial struct SpawnRequestDetector2D : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var q = state.GetEntityQuery(ComponentType.ReadOnly<SpawnRequest>(),
                                     ComponentType.ReadOnly<SimBounds2D>());
        Debug.Log($"[2D] SpawnRequests present: {q.CalculateEntityCount()}");
        state.Enabled = false; // one-shot
    }
}
