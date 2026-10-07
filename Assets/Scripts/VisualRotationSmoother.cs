using UnityEngine;

// Чисто косметический демпфирующий слой: что бы ни делал реальный поворот владельца под
// капотом (метания математики путей AIPath у препятствия, гистерезис в логике «остановись и
// повернись», что угодно) - это никогда не попадает на экран напрямую, вместо этого
// визуал всегда плавно подтягивается к нему, так что быстрые туда-сюда изменения физически не
// могут выглядеть как дрожание/резонанс, что бы ни управляло реальным поворотом.
public class VisualRotationSmoother : MonoBehaviour
{
    public float smoothTime = 0.12f;

    private Transform owner;
    private float currentAngle;
    private float angleVelocity;

    void Awake()
    {
        owner = transform.parent;
        currentAngle = owner.eulerAngles.z;
    }

    void LateUpdate()
    {
        float targetAngle = owner.eulerAngles.z;
        currentAngle = Mathf.SmoothDampAngle(currentAngle, targetAngle, ref angleVelocity, smoothTime);
        transform.rotation = Quaternion.Euler(0f, 0f, currentAngle);
    }
}
