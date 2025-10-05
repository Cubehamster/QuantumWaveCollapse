using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[ExecuteAlways]
public class SimDebugGizmos : MonoBehaviour
{
    [Header("Appearance")]
    public Color boundsColor = new(0.2f, 1f, 0.3f, 0.5f);
    public Color centerColor = new(1f, 0.3f, 0.3f, 1f);
    public float centerCrossSize = 2f;

    [Header("Draw Options")]
    public bool drawSimBounds = true;
    public bool drawCenter = true;

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying)
            return;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var em = world.EntityManager;

        // --- Draw simulation bounds ---
        if (drawSimBounds && em.CreateEntityQuery(typeof(SimBounds2D)).TryGetSingleton(out SimBounds2D bounds))
        {
            float2 c = bounds.Center;
            float2 e = bounds.Extents;
            Vector3 min = new(c.x - e.x, c.y - e.y, 0);
            Vector3 max = new(c.x + e.x, c.y + e.y, 0);

            Gizmos.color = boundsColor;
            Gizmos.DrawWireCube(new Vector3(c.x, c.y, 0), new Vector3(e.x * 2f, e.y * 2f, 0));
        }

        // --- Draw orbital center ---
        if (drawCenter && em.CreateEntityQuery(typeof(OrbitalParams2D)).TryGetSingleton(out OrbitalParams2D orb))
        {
            float2 cc = orb.Center;
            Gizmos.color = centerColor;
            float s = centerCrossSize;

            Gizmos.DrawLine(new Vector3(cc.x - s, cc.y, 0), new Vector3(cc.x + s, cc.y, 0));
            Gizmos.DrawLine(new Vector3(cc.x, cc.y - s, 0), new Vector3(cc.x, cc.y + s, 0));

            // Draw small circle around center
            Gizmos.DrawWireSphere(new Vector3(cc.x, cc.y, 0), s * 0.5f);
        }
    }
}
