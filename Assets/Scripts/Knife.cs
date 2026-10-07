using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class Knife : MonoBehaviour
{
    public float speed = 12f;
    public float baseDamage = 20f;
    public float previewMaxDistance = 20f;
    public LayerMask obstacleMask = ~0;
    public LayerMask enemyMask = ~0;
    public int maxBounces = 3;
    public float maxLifetime = 6f;
    public float minTrajectoryDistance = 0.8f; // один тайл при 100 пикс/юнит (~80 пикс)
    public Color normalTrajectoryColor = new Color(1f, 1f, 0f, 0.5f);
    public Color blockedTrajectoryColor = new Color(1f, 0f, 0f, 0.5f);

    [Tooltip("If the reflected leg would hit a second wall closer than cornerRiskLengthFraction of the knife's own physical length, the throw is blocked (same as being too close to throw at all) instead of just previewed - a corner, where the real thrown knife's own body (not a zero-width preview ray) doesn't have room to physically clear the first surface before the second one is already there. Measured from the sprite (Hero_Knife, ~0.67 units long), not the CircleCollider2D - that collider is a tiny ~0.033-diameter hit-detection point, nowhere near representative of the blade's real visual size.")]
    public float cornerRiskLengthFraction = 0.6f;
    public float missCooldown = 1.2f;
    public float hitEnemyCooldown = 0.5f;

    [Tooltip("How long this knife may continuously overlap an obstacle before it's treated as genuinely stuck (not just the one-step overlap every normal bounce briefly produces while its post-reflection nudge is still resolving) and destroyed outright, instead of left to keep re-colliding. This level's walls are thin partition slabs (as little as ~0.19 units) built from many overlapping/rotated CompositeCollider2D pieces - a fast-moving knife can catch a seam between two of those pieces at a shallow angle and end up sliding along the surface. A fixed penetration-DEPTH threshold (matching EnemyChaseStop.emergencyDepenetrationThreshold) turned out unusable here: even the exact center of the thickest wall piece in this level only measured ~0.055 overlap, well under any depth threshold that also safely ignores a normal bounce's brief contact. DURATION doesn't have that problem - a healthy bounce clears in a step or two, while a stuck knife keeps re-overlapping every single step for as long as it slides.")]
    public float maxContinuousObstacleOverlap = 0.1f;

    private Rigidbody2D rb;
    private Collider2D col;
    private ContactFilter2D obstacleFilter;
    private readonly Collider2D[] overlapBuffer = new Collider2D[4];
    private float continuousObstacleOverlapTime;
    private LineRenderer trajectoryLine;
    private int bounceCount = 0;
    private Vector2 preCollisionVelocity;
    private bool throwBlocked = false;
    // Собственная длина спрайта без поворота (а не AABB при текущем повороте, который меняется,
    // пока нож поворачивается в сторону прицела) - кэшируется один раз, так как ни спрайт, ни
    // масштаб в рантайме не меняются.
    private float knifeLength = 1f;

    public bool IsThrowBlocked => throwBlocked;
    public bool HasBounced => bounceCount > 0;
    public Vector2 Position => rb.position;
    public bool HitEnemy { get; private set; }
    // Тот, кто бросает этот нож, читает это, когда нож исчез - впустую потраченный бросок
    // (отскакал, истекло время, был пойман) стоит больше кулдауна, чем чистое попадание,
    // чтобы не поощрять спам.
    public float ResultingCooldown => HitEnemy ? hitEnemyCooldown : missCooldown;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        trajectoryLine = GetComponent<LineRenderer>();
        col = GetComponent<Collider2D>();
        obstacleFilter = new ContactFilter2D();
        obstacleFilter.SetLayerMask(obstacleMask);
        obstacleFilter.useTriggers = false;

        var spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (spriteRenderer != null && spriteRenderer.sprite != null)
            knifeLength = spriteRenderer.sprite.bounds.size.y * transform.lossyScale.y;
    }

    public void SetHoldPosition(Vector2 playerPos, Vector2 desiredOffset)
    {
        // Мгновенный «снап» вместо плавного скольжения - плавное перемещение ножа у стены/колонны
        // заставляло его заметно «уползать» в точку, откуда его ещё нельзя бросить, и это выглядело
        // как будто нож дрейфует сам по себе. Мгновенный снап делает переключение между доступными
        // и заблокированными точками чётким «монтажным склеем», а не медленным скольжением.
        rb.position = ResolveHoldPosition(playerPos, desiredOffset);
    }

    private Vector2 ResolveHoldPosition(Vector2 playerPos, Vector2 desiredOffset)
    {
        Vector2 desiredPos = playerPos + desiredOffset;
        RaycastHit2D straightHit = Physics2D.Linecast(playerPos, desiredPos, obstacleMask);
        if (straightHit.collider == null)
            return desiredPos;

        // Прямо вперёд заблокировано. Фиксированный поворот на 45 градусов всё равно уводит часть
        // длины в стену (cos(45) от исходной дистанции), так что при достаточной близости нож всё
        // равно оказывается в стене. Вместо этого строим касательную по реальной нормали стены -
        // направление, идущее строго параллельно поверхности, - у которого нет составляющей
        // в сторону стены, как бы близко мы ни стояли.
        Vector2 normal = straightHit.normal;
        Vector2 tangent = new Vector2(-normal.y, normal.x);
        float distance = desiredOffset.magnitude;
        if (Vector2.Dot(tangent, desiredOffset) < 0f) tangent = -tangent;

        Vector2 posA = playerPos + tangent * distance;
        if (Physics2D.Linecast(playerPos, posA, obstacleMask).collider == null)
            return posA;

        Vector2 posB = playerPos - tangent * distance;
        if (Physics2D.Linecast(playerPos, posB, obstacleMask).collider == null)
            return posB;

        // Оба направления вдоль стены тоже заблокированы (угол) - прижимаемся к ближайшей поверхности.
        return straightHit.point - desiredOffset.normalized * 0.1f;
    }

    public void SetAimDirection(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.0001f) return;
        FaceDirection(direction);
        DrawTrajectoryPreview(direction.normalized);
    }

    public void Throw(Vector2 direction)
    {
        transform.SetParent(null);
        // Удерживаемые ножи Kinematic, чтобы не конфликтовать с коллайдерами стен/колонн, пока их
        // позиция каждый кадр выставляется вручную - физика начинает на них действовать только
        // после броска.
        rb.bodyType = RigidbodyType2D.Dynamic;
        FaceDirection(direction);
        rb.linearVelocity = direction.normalized * speed;
        preCollisionVelocity = rb.linearVelocity;
        if (trajectoryLine != null) trajectoryLine.enabled = false;
        // Страховка: если отскок когда-нибудь даст вырожденный результат и нож заклинит у
        // поверхности, он всё равно будет убран, а не останется мусором навсегда.
        Destroy(gameObject, maxLifetime);
    }

    void FixedUpdate()
    {
        // Удерживаемые ножи Kinematic и могут законно находиться у самой поверхности стены
        // (даже касаться её) при позиционировании для прицеливания (см. запасной вариант с
        // прижатием к углу в ResolveHoldPosition) - от бага «нож вонзился в стену» эта проверка
        // защищает только УЖЕ БРОШЕННЫЙ нож в полёте, поэтому полностью пропускается, пока
        // Throw() не переключит тело в Dynamic.
        if (rb.bodyType == RigidbodyType2D.Dynamic)
        {
            // Любое реальное перекрытие форм (а не просто физический «контакт») означает, что
            // математика отражения в какой-то момент получила плохую/вырожденную нормаль, и этот
            // нож теперь застрял внутри геометрии стены (или скользит по её внутренней стороне)
            // вместо того, чтобы отскочить. Обычный здоровый отскок тоже перекрывается на один шаг,
            // пока толчок после отражения разрешается, поэтому застрявшим считается только тот,
            // кто ПРОДОЛЖАЕТ перекрываться дольше, чем нужно одному отскоку.
            bool overlapping = col.Overlap(obstacleFilter, overlapBuffer) > 0;
            continuousObstacleOverlapTime = overlapping ? continuousObstacleOverlapTime + Time.fixedDeltaTime : 0f;
            if (continuousObstacleOverlapTime > maxContinuousObstacleOverlap)
            {
                Destroy(gameObject);
                return;
            }
        }
        else
        {
            continuousObstacleOverlapTime = 0f;
        }

        // Запоминается до физического шага, так как к моменту, когда в этом же шаге срабатывает
        // OnCollisionEnter2D, решатель уже мог тронуть rb.linearVelocity (или выдать обманчивый
        // collision.relativeVelocity).
        preCollisionVelocity = rb.linearVelocity;
    }

    void FaceDirection(Vector2 direction)
    {
        // Остриё клинка смотрит в мировой +Y при повороте 0.
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f;
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    void DrawTrajectoryPreview(Vector2 dir)
    {
        if (trajectoryLine == null) return;

        Vector2 origin = transform.position;
        // Препятствия и враги находятся на разных слоях, поэтому здесь нужны обе маски - стена
        // отражает превью, хитбокс врага его просто обрывает (настоящий нож при контакте
        // исчезает, а не отскакивает, см. DealDamage/OnCollisionEnter2D).
        RaycastHit2D hit1 = Physics2D.Raycast(origin, dir, previewMaxDistance, obstacleMask | enemyMask);

        // Блокировка должна использовать тот же самый луч, что и рисуется - проверка от позиции
        // игрока (другая точка отсчёта, чем у удерживаемого ножа, который находится на расстоянии
        // holdDistance) позволяла двум проверкам расходиться: например, компактное препятствие вроде
        // колонны может попасть под луч от игрока, пока реально нарисованный луч от ножа его
        // обходит, или наоборот. Если встать вплотную к чему-то и бросить нож прямо в это, он
        // отскочит почти мгновенно - это эксплойт, а не настоящий бросок. Превью всё равно
        // рисуется (чтобы нож визуально оставался готов), просто красным цветом, показывая,
        // что бросок не сработает.
        throwBlocked = hit1.collider != null && hit1.distance < minTrajectoryDistance;

        bool hitEnemy = hit1.collider != null && hit1.collider.GetComponentInChildren<HealthBar>() != null;

        if (hit1.collider == null || hitEnemy)
        {
            Color directColor = throwBlocked ? blockedTrajectoryColor : normalTrajectoryColor;
            trajectoryLine.startColor = directColor;
            trajectoryLine.endColor = directColor;
            Vector2 endPoint = hit1.collider != null ? hit1.point : origin + dir * previewMaxDistance;
            trajectoryLine.positionCount = 2;
            trajectoryLine.SetPosition(0, origin);
            trajectoryLine.SetPosition(1, endPoint);
            trajectoryLine.enabled = true;
            return;
        }

        Vector2 reflectDir = Vector2.Reflect(dir, hit1.normal);
        Vector2 secondOrigin = hit1.point + reflectDir * 0.01f;
        RaycastHit2D hit2 = Physics2D.Raycast(secondOrigin, reflectDir, previewMaxDistance, obstacleMask | enemyMask);
        Vector2 secondEnd = hit2.collider != null ? hit2.point : secondOrigin + reflectDir * previewMaxDistance;

        // Угол - отражённый участок тут же упирается в ДРУГУЮ стену ближе собственной физической
        // длины ножа, не оставляя места его реальному телу (в отличие от нулевой ширины луча
        // превью) физически отойти от первой поверхности до того, как вторая уже тут. Такое же
        // обращение, как и со слишком близким броском: полная блокировка, а не только превью -
        // реальные тесты отскоков показали, что такая геометрия не отражает чисто.
        bool cornerRisk = hit2.collider != null && Vector2.Distance(hit1.point, hit2.point) < knifeLength * cornerRiskLengthFraction;
        if (cornerRisk) throwBlocked = true;
        Color lineColor = throwBlocked ? blockedTrajectoryColor : normalTrajectoryColor;
        trajectoryLine.startColor = lineColor;
        trajectoryLine.endColor = lineColor;

        trajectoryLine.positionCount = 3;
        trajectoryLine.SetPosition(0, origin);
        trajectoryLine.SetPosition(1, hit1.point);
        trajectoryLine.SetPosition(2, secondEnd);
        trajectoryLine.enabled = true;
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        if (DealDamage(collision.collider))
        {
            // Попадание во врага обрывает бросок на месте вместо рикошета от него - исчезновение
            // ножа также возвращает TestScript.flyingKnife в null (== в Unity считает уничтоженный
            // объект равным null), позволяя бросающему прочитать ResultingCooldown и сразу начать
            // (более короткий) кулдаун попадания.
            HitEnemy = true;
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
        // его вдоль нормали на полной скорости, вместо того чтобы дать ножу застопориться о поверхность.
        reflected = reflected.sqrMagnitude < 0.01f
            ? normal * speed
            : reflected.normalized * speed;

        rb.linearVelocity = reflected;
        preCollisionVelocity = reflected;
        FaceDirection(reflected);
        transform.position += (Vector3)(normal * 0.05f);
        bounceCount++;
    }

    bool DealDamage(Collider2D other)
    {
        HealthBar healthBar = other.GetComponentInChildren<HealthBar>();
        if (healthBar == null) return false;

        // bounceCount - число отскоков, случившихся ДО этого попадания, поэтому свежий бросок
        // (0 отскоков) всё равно наносит один базовый удар: отскоки * база + база.
        healthBar.currentHealth -= bounceCount * baseDamage + baseDamage;
        return true;
    }
}
