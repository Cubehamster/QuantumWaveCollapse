using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(LangevinWalkSystem))]  // update the field first
public partial struct CollapseAttractorSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<CollapseAttractor>()) return;

        float dt = state.WorldUnmanaged.Time.DeltaTime;
        var e = SystemAPI.GetSingletonEntity<CollapseAttractor>();
        var ca = SystemAPI.GetComponent<CollapseAttractor>(e);

        ca.TimeLeft -= dt;
        // simple exponential decay
        ca.Strength *= math.exp(-math.max(0f, ca.DecayRate) * dt);

        if (ca.TimeLeft <= 0f || ca.Strength <= 1e-4f)
        {
            state.EntityManager.DestroyEntity(e);
        }
        else
        {
            SystemAPI.SetComponent(e, ca);
        }
    }
}
