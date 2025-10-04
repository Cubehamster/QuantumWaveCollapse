using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(MeasurementSystem))]
public partial struct MeasurementResultLoggerSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MeasurementResult>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var ecb =
            SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                     .CreateCommandBuffer(state.WorldUnmanaged);

        foreach (var (res, e) in SystemAPI.Query<RefRO<MeasurementResult>>().WithEntityAccess())
        {
            var r = res.ValueRO;
            Debug.Log(
                $"[Measure][Result] success={(r.Success == 1)} " +
                $"p={r.Probability:0.#####} " +
                $"insideW={r.InsideWeight:0.###}/{r.TotalWeight:0.###} " +
                $"insideCount={r.InsideCount} destroyed={r.DestroyedCount} " +
                $"@ {r.Center} R={r.Radius}"
            );

            ecb.DestroyEntity(e); // one-shot
        }
    }
}
