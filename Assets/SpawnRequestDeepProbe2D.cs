// SpawnRequestDeepProbe2D.cs
using Unity.Entities;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct SpawnRequestDeepProbe2D : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var normal = state.GetEntityQuery(
            ComponentType.ReadOnly<SpawnRequest>(),
            ComponentType.ReadOnly<SimBounds2D>());

        var incDisabled = state.GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<SpawnRequest>(), ComponentType.ReadOnly<SimBounds2D>() },
            Options = EntityQueryOptions.IncludeDisabledEntities
        });

        var incDisabledPrefab = state.GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<SpawnRequest>(), ComponentType.ReadOnly<SimBounds2D>() },
            Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
        });

        Debug.Log($"[2D] SpawnRequest counts → normal:{normal.CalculateEntityCount()}  " +
                  $"inclDisabled:{incDisabled.CalculateEntityCount()}  " +
                  $"inclDisabled+Prefab:{incDisabledPrefab.CalculateEntityCount()}");

        state.Enabled = false;
    }
}
