using UnityEngine;

// Снаряд лучника - та же идея реального физического попадания, что и в Kunai.cs, но проще:
// лучник целится только по прямой (в ArcherFighter нет расчёта рикошетов), а сам рикошет у стрелы
// «обычный», физический: ударившись о препятствие под достаточно косым углом, стрела один раз
// отскакивает (отражается), а при более прямом попадании просто исчезает. Угол считается между
// направлением полёта и нормалью поверхности (0° - прямо в лоб, 90° - скользит вдоль стены).
[RequireComponent(typeof(Rigidbody2D))]
public class Arrow : MonoBehaviour
{
    public float speed = 20f;
    public float maxLifetime = 4f;

    public LayerMask obstacleMask;

    [Tooltip("How long this arrow may continuously overlap an obstacle before it's treated as genuinely stuck and destroyed outright - same reasoning as Kunai.maxContinuousObstacleOverlap (this level's thin walls make a fixed penetration-depth threshold unreliable; duration isn't fooled by wall thickness).")]
    public float maxContinuousObstacleOverlap = 0.1f;

    [Tooltip("Сколько раз стрела может отскочить от препятствия.")]
    public int maxRicochets = 1;

    [Tooltip("Минимальный угол попадания, при котором стрела отскакивает, в градусах. Угол между направлением полёта и нормалью поверхности: 0 - прямо в лоб, 90 - скользящий удар. Косее этого угла - рикошет, прямее - стрела просто исчезает.")]
    public float ricochetMinAngle = 45f;

    // Счётчик реальных попаданий снаряда, та же конвенция, что и Kunai.TotalHits - сделан
    // отдельным, а не общим, так как это действительно другой тип снаряда.
    public static int TotalHits;

    [Tooltip("Урон по игроку в долях от полного хп (1 = всё хп).")]
    public float damage = 0.6f;

    private Rigidbody2D rb;
    private Collider2D col;
    private ContactFilter2D obstacleFilter;
    private readonly Collider2D[] overlapBuffer = new Collider2D[4];
    private float continuousObstacleOverlapTime;
    private Vector2 preCollisionVelocity;
    private int ricochetCount;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<Collider2D>();
        obstacleFilter = new ContactFilter2D();
        obstacleFilter.SetLayerMask(obstacleMask);
        obstacleFilter.useTriggers = false;
    }

    public void Launch(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.0001f) direction = Vector2.up;
        FaceDirection(direction);
        rb.linearVelocity = direction.normalized * speed;
        preCollisionVelocity = rb.linearVelocity;
        Destroy(gameObject, maxLifetime);
    }

    void FixedUpdate()
    {
        bool overlapping = col.Overlap(obstacleFilter, overlapBuffer) > 0;
        continuousObstacleOverlapTime = overlapping ? continuousObstacleOverlapTime + Time.fixedDeltaTime : 0f;
        if (continuousObstacleOverlapTime > maxContinuousObstacleOverlap)
        {
            Destroy(gameObject);
            return;
        }

        // Скорость до физического шага: к моменту OnCollisionEnter2D решатель уже мог её изменить (как в Kunai.cs).
        preCollisionVelocity = rb.linearVelocity;
    }

    void FaceDirection(Vector2 direction)
    {
        // Та же конвенция «+Y при повороте 0», что и в Kunai.cs/Knife.cs - спрайт стрелы
        // по умолчанию смотрит вверх, как и у куная.
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f;
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider.gameObject.layer == LayerMask.NameToLayer("Player"))
        {
            TotalHits++;
            var health = collision.collider.GetComponentInParent<PlayerHealth>();
            if (health != null) health.TakeDamage(damage);
        }
        // Рикошет только от препятствий и только пока есть отскоки: достаточно косое попадание отражает
        // стрелу, прямое - просто завершает выстрел.
        bool isObstacle = ((1 << collision.collider.gameObject.layer) & obstacleMask.value) != 0;
        if (isObstacle && ricochetCount < maxRicochets && TryRicochet(collision)) return;

        Destroy(gameObject);
    }

    bool TryRicochet(Collision2D collision)
    {
        Vector2 incoming = preCollisionVelocity;
        if (incoming.sqrMagnitude < 0.01f) return false;

        Vector2 normal = collision.GetContact(0).normal;
        float angle = Vector2.Angle(-incoming, normal);
        if (angle <= ricochetMinAngle) return false;

        Vector2 reflected = Vector2.Reflect(incoming, normal);
        reflected = reflected.sqrMagnitude < 0.01f ? normal * speed : reflected.normalized * speed;

        rb.linearVelocity = reflected;
        preCollisionVelocity = reflected;
        FaceDirection(reflected);
        transform.position += (Vector3)(normal * 0.05f);
        ricochetCount++;
        return true;
    }
}
