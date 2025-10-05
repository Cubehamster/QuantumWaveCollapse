// PositionsUploadSystem.cs
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[UpdateInGroup(typeof(SimulationSystemGroup))]
public sealed partial class PositionsUploadSystem : SystemBase
{
    EntityQuery _q;
    NativeArray<float2> _pos;
    bool _valid;

    public bool HasData => _valid && _pos.IsCreated && _pos.Length > 0;
    public int Count => _pos.IsCreated ? _pos.Length : 0;

    protected override void OnCreate()
    {
        // Match your tag here; add/replace WalkerTag <-> ParticleTag as needed.
        _q = GetEntityQuery(
            ComponentType.ReadOnly<WalkerTag>(),
            ComponentType.ReadOnly<Position>());

        RequireForUpdate(_q);
    }

    protected override void OnDestroy()
    {
        if (_pos.IsCreated) _pos.Dispose();
    }

    protected override void OnUpdate()
    {
        int n = _q.CalculateEntityCount();

        if (n != (_pos.IsCreated ? _pos.Length : 0))
        {
            if (_pos.IsCreated) _pos.Dispose();
            if (n > 0) _pos = new NativeArray<float2>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        _valid = n > 0;
        if (!_valid) return;

        var outArr = _pos;
        var job = new CopyJob { Out = outArr };
        Dependency = job.ScheduleParallel(_q, Dependency);
        Dependency.Complete();
    }

    public NativeArray<float2> GetReadOnly()
    {
        Dependency.Complete();
        return _pos;
    }

    [BurstCompile]
    partial struct CopyJob : IJobEntity
    {
        public NativeArray<float2> Out;
        void Execute([EntityIndexInQuery] int i, in Position pos)
        {
            Out[i] = pos.Value;
        }
    }
}
