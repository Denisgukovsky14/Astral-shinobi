using UnityEngine;
using Pathfinding;

// Заменяет AIDestinationSetter, который каждый кадр подаёт AIPath «сырую» позицию цели.
// Когда цель быстро виляет, такая сырая подача заставляет путь (и получающееся из него
// поведение разгона/торможения) дёргаться вместе с ней. Сглаживание подаваемой позиции даёт
// AIPath более стабильную цель для преследования, не затрагивая его манёвренность на
// поворотах (rotationSpeed).
public class SmoothedDestinationSetter : MonoBehaviour
{
    public Transform target;
    public float smoothTime = 0.15f;

    private IAstarAI ai;
    private Vector3 smoothedPosition;
    private Vector3 velocity;

    void Awake()
    {
        ai = GetComponent<IAstarAI>();
        if (target != null) smoothedPosition = target.position;
    }

    void Update()
    {
        if (target == null) return;
        smoothedPosition = Vector3.SmoothDamp(smoothedPosition, target.position, ref velocity, smoothTime);
        ai.destination = smoothedPosition;
    }
}
