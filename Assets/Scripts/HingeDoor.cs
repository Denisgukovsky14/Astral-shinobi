using UnityEngine;

// Межкомнатная дверь на петле. Корень двери = ось петли, сама дверь идёт вдоль локального +X.
// Физика: Rigidbody2D с замороженной позицией и центром масс в петле, поэтому её толкают и она
// поворачивается вокруг петли, как в Hotline Miami (игрок толкает, она остаётся там, где её оставили).
// Поворот ограничен диапазоном ±maxSwing вокруг закрытого положения (по умолчанию 180° всего).
// Логики замедления врагов пока нет - дверь только физическая.
[RequireComponent(typeof(Rigidbody2D))]
public class HingeDoor : MonoBehaviour
{
    [Tooltip("Угол корня (Z, градусы), при котором дверь закрыта и стоит в проёме. Диапазон качания считается от него.")]
    public float closedAngle;

    [Tooltip("На сколько градусов дверь может отойти от закрытого положения в каждую сторону (90 + 90 = 180°).")]
    public float maxSwing = 90f;

    [Tooltip("Масса двери: чем больше, тем тяжелее её открыть.")]
    public float mass = 2f;

    [Tooltip("Торможение вращения: чем больше, тем быстрее дверь останавливается после толчка.")]
    public float angularDamping = 3f;

    [Tooltip("Положение двери при старте: угол от закрытого (−90..+90). Двигай ползунок в инспекторе - дверь поворачивается вокруг петли прямо в редакторе.")]
    [Range(-90f, 90f)] public float startSwing;

    private Rigidbody2D rb;

#if UNITY_EDITOR
    // Ось двери - корень объекта, поэтому поворот идёт вокруг петли (Rotate tool - в режиме Pivot, клавиша Z).
    void OnValidate()
    {
        if (Application.isPlaying) return;
        transform.rotation = Quaternion.Euler(0f, 0f, closedAngle + startSwing);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, 0.12f);
    }
#endif

    // Угол двери относительно закрытого положения, -maxSwing..+maxSwing.
    public float Swing => Mathf.DeltaAngle(closedAngle, rb != null ? rb.rotation : transform.eulerAngles.z);

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.constraints = RigidbodyConstraints2D.FreezePosition;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.angularDamping = angularDamping;
        rb.mass = mass;

        // Вращение вокруг петли: центр масс - в начале координат корня, момент инерции стержня вокруг конца.
        float length = 2.28f;
        var box = GetComponent<BoxCollider2D>();
        if (box != null) length = box.offset.x + box.size.x * 0.5f;
        rb.centerOfMass = Vector2.zero;
        rb.inertia = mass * length * length / 3f;

        // Петля сидит в торце стены, дверь стоит между её кусками: стены дверь не толкают (иначе она
        // упирается в них и дрожит). С колоннами, игроком и врагами дверь сталкивается как обычно.
        var mine = GetComponents<Collider2D>();
        foreach (var wall in FindObjectsByType<CompositeCollider2D>(FindObjectsSortMode.None))
            foreach (var m in mine) Physics2D.IgnoreCollision(m, wall);
    }

    void FixedUpdate()
    {
        float swing = Swing;
        if (swing > maxSwing)
        {
            rb.rotation = closedAngle + maxSwing;
            if (rb.angularVelocity > 0f) rb.angularVelocity = 0f;
        }
        else if (swing < -maxSwing)
        {
            rb.rotation = closedAngle - maxSwing;
            if (rb.angularVelocity < 0f) rb.angularVelocity = 0f;
        }
    }
}
