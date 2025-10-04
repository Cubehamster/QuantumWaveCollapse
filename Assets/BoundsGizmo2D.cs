using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

public class BoundsGizmo2D : MonoBehaviour
{
    [Header("Appearance")]
    public Color color = new Color(1f, 0.6f, 0f, 0.9f);
    public bool fill = false;
    public bool onlyWhenPlaying = true;

    World _world;
    EntityManager _em;
    EntityQuery _q;
    bool _inited;

    void TryInit()
    {
        if (_inited) return;
        _world = World.DefaultGameObjectInjectionWorld;
        if (_world == null) return;
        _em = _world.EntityManager;
        _q = _em.CreateEntityQuery(ComponentType.ReadOnly<SimBounds2D>());
        _inited = true;
    }

    void OnDrawGizmos()
    {
        if (onlyWhenPlaying && !Application.isPlaying) return;

        TryInit();
        if (!_inited || _q == default || _q.IsEmpty) return;

        // Prefer singleton; fall back to first match if multiple exist.
        SimBounds2D b;
        try
        {
            b = _em.GetComponentData<SimBounds2D>(_q.GetSingletonEntity());
        }
        catch
        {
            using var ents = _q.ToEntityArray(Allocator.Temp);
            if (ents.Length == 0) return;
            b = _em.GetComponentData<SimBounds2D>(ents[0]);
        }

        float2 c = b.Center;
        float2 e = b.Extents;
        var c3 = new Vector3(c.x, c.y, 0f);

        // Corners (XY plane)
        var a = c3 + new Vector3(-e.x, -e.y, 0);
        var b1 = c3 + new Vector3(-e.x, e.y, 0);
        var c1 = c3 + new Vector3(e.x, e.y, 0);
        var d = c3 + new Vector3(e.x, -e.y, 0);

        Gizmos.color = color;

        if (fill)
        {
            // Tiny Z to ensure visibility if size.z==0
            Gizmos.DrawCube(c3, new Vector3(e.x * 2f, e.y * 2f, 0.001f));
            Gizmos.color = new Color(color.r, color.g, color.b, 1f); // outline opaque
        }

        // Outline
        Gizmos.DrawLine(a, b1);
        Gizmos.DrawLine(b1, c1);
        Gizmos.DrawLine(c1, d);
        Gizmos.DrawLine(d, a);

#if UNITY_EDITOR
        // Optional label (Editor only)
        if (!Application.isPlaying || (Time.frameCount % 30) == 0)
        {
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.Label(c3, $"Bounds2D\nCenter: {c}\nExtents: {e}");
        }
#endif
    }
}
