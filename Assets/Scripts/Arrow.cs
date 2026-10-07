using UnityEngine;

// Снаряд лучника - та же идея реального физического попадания, что и в Kunai.cs, но проще:
// лучник никогда не целится рикошетом (в ArcherFighter вообще нет кода рикошетов), так что
// и здесь нет ничего, что оправдывало бы отскок. Промах просто летит прямо, пока не ударится
// о стену или не истечёт время жизни, вместо поведения Kunai с отражением от стен.
[RequireComponent(typeof(Rigidbody2D))]
public class Arrow : MonoBehaviour
{
    public float speed = 20f;
    public float maxLifetime = 4f;

    public LayerMask obstacleMask;

    [Tooltip("How long this arrow may continuously overlap an obstacle before it's treated as genuinely stuck and destroyed outright - same reasoning as Kunai.maxContinuousObstacleOverlap (this level's thin walls make a fixed penetration-depth threshold unreliable; duration isn't fooled by wall thickness).")]
    public float maxContinuousObstacleOverlap = 0.1f;

    // Счётчик реальных попаданий снаряда, та же конвенция, что и Kunai.TotalHits - сделан
    // отдельным, а не общим, так как это действительно другой тип снаряда.
    public static int TotalHits;

    private Rigidbody2D rb;
    private Collider2D col;
    private ContactFilter2D obstacleFilter;
    private readonly Collider2D[] overlapBuffer = new Collider2D[4];
    private float continuousObstacleOverlapTime;

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
        Destroy(gameObject, maxLifetime);
    }

    void FixedUpdate()
    {
        bool overlapping = col.Overlap(obstacleFilter, overlapBuffer) > 0;
        continuousObstacleOverlapTime = overlapping ? continuousObstacleOverlapTime + Time.fixedDeltaTime : 0f;
        if (continuousObstacleOverlapTime > maxContinuousObstacleOverlap)
        {
            Destroy(gameObject);
        }
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
        }
        // Любое другое попадание (стена или что угодно) тоже просто завершает выстрел - без
        // отскока и рикошета, в соответствии с прицеливанием ArcherFighter только по прямой.
        Destroy(gameObject);
    }
}
