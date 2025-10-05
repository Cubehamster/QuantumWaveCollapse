using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

public class MouseToClickRequest : MonoBehaviour
{
    public Camera worldCam;
    public float radius = 20f;
    public float pushStrength = 1f;

    EntityManager _em;
    Entity _reqEnt;
    bool _prevPressed;

    void Awake()
    {
        _em = World.DefaultGameObjectInjectionWorld.EntityManager;
        _reqEnt = _em.CreateEntity(typeof(ClickRequest));
    }

    void Update()
    {
        if (worldCam == null) worldCam = Camera.main;
        if (worldCam == null) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        bool pressed = mouse.leftButton.isPressed;
        bool down = mouse.leftButton.wasPressedThisFrame;
        bool up = mouse.leftButton.wasReleasedThisFrame;

        Vector2 mp = mouse.position.ReadValue();
        Ray ray = worldCam.ScreenPointToRay(mp);
        // 2D world: assume z=0 plane
        float t = (0f - ray.origin.z) / (ray.direction.z == 0 ? 1e-6f : ray.direction.z);
        Vector3 w = ray.origin + t * ray.direction;
        float2 w2 = new float2(w.x, w.y);

        _em.SetComponentData(_reqEnt, new ClickRequest
        {
            WorldPos = w2,
            Radius = radius,
            PushStrength = pushStrength,
            IsPressed = pressed,
            EdgeDown = down,
            EdgeUp = up
        });

        _prevPressed = pressed;
    }

    void OnDestroy()
    {
        if (_em.Exists(_reqEnt)) _em.DestroyEntity(_reqEnt);
    }
}
