// Particles.PositionsWeightsExportSystem.cs (robust snapshot version)
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DMC2DSystem))] // or after your sim step
public sealed partial class PositionsWeightsExportSystem : SystemBase
{
    EntityQuery _q;
    NativeArray<float2> _positions;
    NativeArray<float> _weights;
    bool _valid;

    protected override void OnCreate()
    {
        _q = GetEntityQuery(
            ComponentType.ReadOnly<Position>(),
            ComponentType.ReadOnly<Weight>());
        RequireForUpdate(_q);
    }

    protected override void OnDestroy()
    {
        if (_positions.IsCreated) _positions.Dispose();
        if (_weights.IsCreated) _weights.Dispose();
    }

    protected override void OnUpdate()
    {
        // Take a snapshot that’s consistent even if structure changes later this frame.
        var posSnap = _q.ToComponentDataArrayAsync<Position>(Allocator.TempJob, out var hPos);
        var wSnap = _q.ToComponentDataArrayAsync<Weight>(Allocator.TempJob, out var hW);

        // Chain the two snapshot jobs
        var h = JobHandle.CombineDependencies(hPos, hW);
        h.Complete(); // Make data available now (no GC; TempJob arrays)

        int count = posSnap.Length; // pos and w have same length due to query
        _valid = count > 0;

        // Resize persistent arrays only when needed
        if ((_positions.IsCreated ? _positions.Length : 0) != count)
        {
            if (_positions.IsCreated) _positions.Dispose();
            if (_weights.IsCreated) _weights.Dispose();
            if (count > 0)
            {
                _positions = new NativeArray<float2>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _weights = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }

        if (_valid)
        {
            // Copy snapshot → persistent (blit, zero-GC)
            for (int i = 0; i < count; i++) _positions[i] = posSnap[i].Value;
            for (int i = 0; i < count; i++) _weights[i] = wSnap[i].Value;
        }

        posSnap.Dispose();
        wSnap.Dispose();
    }

    public bool HasData => _valid && _positions.IsCreated && _weights.IsCreated && _positions.Length > 0;
    public int Count => _positions.IsCreated ? _positions.Length : 0;
    public NativeArray<float2> GetPositionsRO() { return _positions; }
    public NativeArray<float> GetWeightsRO() { return _weights; }
}
