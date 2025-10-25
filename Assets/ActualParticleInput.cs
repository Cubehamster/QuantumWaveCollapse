using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Entities;

public class ActualParticleInput : MonoBehaviour
{
    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.sKey.wasPressedThisFrame)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;

            var em = world.EntityManager;
            var q = em.CreateEntityQuery(ComponentType.ReadWrite<SelectActualParticleRequest>());
            if (q.IsEmptyIgnoreFilter)
            {
                em.CreateEntity(typeof(SelectActualParticleRequest));
            }
            // If it already exists this frame, the system will consume it.
        }
    }
}
