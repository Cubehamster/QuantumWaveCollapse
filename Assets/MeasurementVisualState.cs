using Unity.Entities;
using Unity.Mathematics;

public struct MeasurementVisualState : IComponentData
{
    // Is the click currently active this frame? (mouse held or pressed, your choice)
    public bool IsClickActive;

    // Was there at least one actual particle inside the *push radius* this frame?
    public bool HasActualInRadius;

    // True on the frame the click starts
    public bool EdgeDown;

    // True on the frame the click ends (release)
    public bool EdgeUp;

    // Where the measurement is happening (world-space, XY)
    public float2 Center;

    // Push radius used for this measurement
    public float Radius;
}
