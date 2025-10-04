using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

public class DMCControl2DAuthoring : MonoBehaviour
{
    [Header("Resampling cadence")]
    [Tooltip("Do a resample tick every K simulation frames.")]
    public int BranchEveryFrames = 10;

    [Tooltip("Split population into this many slices; only 1 slice is resampled per tick.")]
    public int ResampleSlices = 8;

    [Header("Resample jitter")]
    [Tooltip("Small positional jitter (world units) when copying parents.")]
    public float CloneJitterSigma = 0.01f;

    [Header("Adaptive E_ref")]
    [Tooltip("Per-frame controller gain to keep average weight ~1 (0.2–0.5 typical).")]
    public float Kappa = 0.3f;

    class Baker : Baker<DMCControl2DAuthoring>
    {
        public override void Bake(DMCControl2DAuthoring src)
        {
            var e = GetEntity(TransformUsageFlags.None);
            AddComponent(e, new DMCControl2D
            {
                BranchEveryFrames = math.max(1, src.BranchEveryFrames),
                ResampleSlices = math.max(1, src.ResampleSlices),
                CloneJitterSigma = math.max(0f, src.CloneJitterSigma),
                Kappa = math.max(0f, src.Kappa),
                FrameCounter = 0
            });
        }
    }
}

public struct DMCControl2D : IComponentData
{
    public int BranchEveryFrames;
    public int ResampleSlices;
    public float CloneJitterSigma;
    public float Kappa;
    public int FrameCounter;
}
