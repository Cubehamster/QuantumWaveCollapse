using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

/// Keeps the average particle weight ~ 1 by nudging E_ref every frame.
/// Uses a parallel reduction (no ToComponentDataArray copies).
[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DMC2DSystem))]
[UpdateBefore(typeof(DMCResampleSystem))]
[UpdateBefore(typeof(DensityBuildSystem))]
public partial struct DMCAdaptiveErefEveryFrameSystem : ISystem
{
    EntityQuery _qWeights;


    public void OnCreate(ref SystemState state)
    {
        _qWeights = state.GetEntityQuery(ComponentType.ReadOnly<Weight>());
        state.RequireForUpdate<QuantumParams2D>();
        state.RequireForUpdate<DMCControl2D>();
        state.RequireForUpdate(_qWeights);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var qp = SystemAPI.GetSingleton<QuantumParams2D>();
        var ctrl = SystemAPI.GetSingleton<DMCControl2D>();
        if (qp.DeltaTau <= 0f || ctrl.Kappa <= 0f) return;

        int N = _qWeights.CalculateEntityCount();
        if (N == 0) return;

        var partial = new NativeArray<double>(JobsUtility.MaxJobThreadCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);

        var job = new MeanWeightJob { Partial = partial };
        state.Dependency = job.ScheduleParallel(state.Dependency);
        state.Dependency.Complete();

        double sum = 0.0;
        for (int i = 0; i < partial.Length; i++) sum += partial[i];
        partial.Dispose();

        if (sum <= 0.0)
        {
            // If collapsed, push E_ref UP so weights grow next frame.
            qp.ERef += 0.5f / math.max(1e-6f, qp.DeltaTau);
            SystemAPI.SetSingleton(qp);
            return;
        }

        float mean = (float)(sum / math.max(1, N));
        float gain = math.clamp(ctrl.Kappa, 0.01f, 1.0f);
        float deltaE = -gain * math.log(math.max(1e-6f, mean)) / math.max(1e-6f, qp.DeltaTau);

        // Safety clamp
        float maxStep = 2f / math.max(1e-6f, qp.DeltaTau);
        qp.ERef += math.clamp(deltaE, -maxStep, maxStep);

        SystemAPI.SetSingleton(qp);
    }

    [BurstCompile]
    public partial struct MeanWeightJob : IJobEntity
    {
        // allow writing at an index different from [EntityIndexInQuery]
        [NativeDisableParallelForRestriction]
        public NativeArray<double> Partial;

        [NativeSetThreadIndex] int ti;

        public void Execute(in Weight w)
        {
            // thread index is usually 1..MaxJobThreadCount (sometimes 0 on main)
            int idx = ti - 1;
            if (idx < 0) idx = 0;
            if (idx >= Partial.Length) idx = Partial.Length - 1;

            float v = w.Value;
            if (math.isfinite(v) && v > 0f)
                Partial[idx] += v;
        }
    }
}
