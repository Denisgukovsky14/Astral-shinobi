using UnityEngine;

// Реальный физический метательный снаряд для дальнобойных врагов - отскакивает от стен через
// настоящую коллизию Rigidbody2D (Vector2.Reflect), тем же механизмом, что и Knife.cs игрока,
// вместо заранее посчитанной гарантированно попадающей траектории. Враг не рассчитывает
// идеальный выстрел - он один раз целится в текущую позицию игрока, бросает и дальше отдаёт
// дело реальной физике, так же как работают броски самого игрока.
[RequireComponent(typeof(Rigidbody2D))]
public class Kunai : MonoBehaviour
{
    public float speed = 10f;
    public int maxBounces = 3;
    public float maxLifetime = 4f;

    public LayerMask obstacleMask;

    [Tooltip("How long this kunai may continuously overlap an obstacle before it's treated as genuinely stuck (not just the one-step overlap every normal bounce briefly produces while its post-reflection nudge is still resolving) and destroyed outright, instead of left to keep re-colliding. This level's walls are thin partition slabs (as little as ~0.19 units) built from many overlapping/rotated CompositeCollider2D pieces - a fast-moving kunai can catch a seam between two of those pieces at a shallow angle and end up sliding along the surface. A fixed penetration-DEPTH threshold (matching EnemyChaseStop.emergencyDepenetrationThreshold) turned out unusable here: even the exact center of the thickest wall piece in this level only measured ~0.055 overlap, well under any depth threshold that also safely ignores a normal bounce's brief contact. DURATION doesn't have that problem - a healthy bounce clears in a step or two, while a stuck kunai keeps re-overlapping every single step for as long as it slides.")]
    public float maxContinuousObstacleOverlap = 0.1f;

    // Счётчик реальных попаданий снарядов (в отличие от измерения «could've hit you» для ближнего
    // боя из EnemyAttackZone) - реальное попадание куная это настоящий контакт, считается здесь
    // для HUD-индикации в левом верхнем углу (см. KunaiHitFeedbackUI).
    public static int TotalHits;

    private Rigidbody2D rb;
    private Collider2D col;
    private ContactFilter2D obstacleFilter;
    private readonly Collider2D[] overlapBuffer = new Collider2D[4];
    private Vector2 preCollisionVelocity;
    private int bounceCount;
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
        preCollisionVelocity = rb.linearVelocity;
        Destroy(gameObject, maxLifetime);
    }

    void FixedUpdate()
    {
        // Подтверждено вживую: кунай может оказаться полностью неподвижным, застрявшим у края
        // стены, спустя долгое время после того, как он должен был либо долететь до игрока, либо
        // отскочить. И Launch(), и логика отскока ниже всегда задают полную ненулевую скорость -
        // то есть это не скрипт оставляет её нулевой. Геометрия стен уровня - один большой
        // CompositeCollider2D из множества перекрывающихся боксов, и в месте, где два из них
        // стыкуются неровным швом или углом, собственный решатель ограничений Unity может заклинить
        // там быстрый Rigidbody2D и сам обнулить его скорость на одном из следующих физических
        // шагов, независимо от того, что было задано после отскока. Нож, мёртво застрявший в стене,
        // выглядит для игрока именно как такой баг, на каком бы слое он реально ни произошёл -
        // поэтому «остановился» приравнивается к «закончились отскоки», и снаряд просто
        // удаляется, вместо того чтобы оставлять замерший кунай висеть в геометрии.
        if (rb.linearVelocity.sqrMagnitude < 0.1f)
        {
            Destroy(gameObject);
            return;
        }

        // Любое реальное перекрытие форм (а не просто физический «контакт») означает, что
        // математика отражения в какой-то момент получила плохую/вырожденную нормаль, и этот кунай
        // теперь застрял внутри геометрии стены (или скользит по её внутренней стороне) вместо
        // того, чтобы отскочить. Обычный здоровый отскок тоже перекрывается на один шаг, пока
        // толчок после отражения разрешается, поэтому застрявшим считается только тот, кто
        // ПРОДОЛЖАЕТ перекрываться дольше, чем нужно одному отскоку.
        bool overlapping = col.Overlap(obstacleFilter, overlapBuffer) > 0;
        continuousObstacleOverlapTime = overlapping ? continuousObstacleOverlapTime + Time.fixedDeltaTime : 0f;
        if (continuousObstacleOverlapTime > maxContinuousObstacleOverlap)
        {
            Destroy(gameObject);
            return;
        }

        // Запоминается до физического шага, по той же причине, что и в Knife.cs: к моменту, когда
        // в этом же шаге срабатывает OnCollisionEnter2D, решатель уже мог тронуть rb.linearVelocity
        // (или выдать обманчивый collision.relativeVelocity).
        preCollisionVelocity = rb.linearVelocity;
    }

    void FaceDirection(Vector2 direction)
    {
        // Та же конвенция «+Y при повороте 0», что и во всём проекте.
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f;
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider.gameObject.layer == LayerMask.NameToLayer("Player"))
        {
            // Реального нанесения урона игроку в проекте пока нет нигде (подтверждено:
            // EnemyAttackZone - просто счётчик попаданий, а не настоящий урон) - просто считаем
            // попадание и исчезаем, следуя той же конвенции, пока не появится настоящая система урона.
            TotalHits++;
            Destroy(gameObject);
            return;
        }

        if (bounceCount >= maxBounces)
        {
            Destroy(gameObject);
            return;
        }

        Vector2 normal = collision.GetContact(0).normal;
        Vector2 reflected = Vector2.Reflect(preCollisionVelocity, normal);

        // Отражение должно сохранять скорость. Почти нулевой результат означает, что контакт был
        // вырожденным (попадание в угол, краевой случай полигонального коллайдера) - пересобираем
        // его вдоль нормали на полной скорости, вместо того чтобы дать кунаю застопориться о поверхность.
        reflected = reflected.sqrMagnitude < 0.01f ? normal * speed : reflected.normalized * speed;

        rb.linearVelocity = reflected;
        preCollisionVelocity = reflected;
        FaceDirection(reflected);
        transform.position += (Vector3)(normal * 0.05f);
        bounceCount++;
    }
}
