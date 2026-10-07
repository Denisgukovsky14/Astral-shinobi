using UnityEngine;
using Pathfinding;

// Дальнобойный враг, общий дизайн как у KunaiFighter (полную философию двух циклов см. в шапке
// того файла), но упрощённый под спецификацию лучника: вообще без рикошетов - каждый выстрел это
// прямая линия к одной из 3 точек прицеливания игрока, и ничего больше. Это сворачивает различие
// KunaiFighter «прямой выстрел / допускающий рикошеты» в одну проверку выстрела, которая
// используется везде (TryFindShot), так как здесь существует только один вид выстрела.
public class ArcherFighter : MonoBehaviour
{
    public Transform player;

    [Tooltip("The 3 aim points on the player's body to shoot at: head, left shoulder, right shoulder. Thin line-of-sight checks target these directly instead of the player's center.")]
    public Transform[] playerAimPoints = new Transform[3];

    public LayerMask obstacleMask;

    [Tooltip("Kunai fighter's own default ring radius (4.5) - the baseline this archer's own distance multiplies from.")]
    public float baseStandRadius = 4.5f;

    [Tooltip("How much farther than baseStandRadius this archer holds itself from the player, on top of it always sniping from standRadius away in the first place - it stays safely behind closer-range units rather than sharing their exact ring.")]
    public float standRadiusMultiplier = 2.6f;

    float standRadius => baseStandRadius * standRadiusMultiplier;

    [Tooltip("Hard floor on how close this ranged unit may ever end up to the player, regardless of which movement branch is driving it - normal ring-holding already keeps it at standRadius, but approachingPlayer (see UpdateMovement) aims straight at the player's live position while no shot exists anywhere, and that alone must never be allowed to walk it into melee contact.")]
    public float minPlayerDistance = 1f;

    [Tooltip("Standing points are (re)searched at most this often, and immediately if the current one stops working.")]
    public float positionSearchInterval = 1f;

    [Tooltip("If the fighter is standing right at its committed point, off cooldown, and still can't find any shot for this long, stop waiting and immediately hunt for a fresh point instead. Covers the case where the exact spot it walked to no longer has the shot it was chosen for (target moved, or arrival landed slightly off the validated spot).")]
    public float stuckAtPointAbandonTime = 0.5f;

    [Tooltip("Independent of any internal reasoning about WHY - if a real arrow simply hasn't left this fighter's hand for this long, that alone is reason enough to force a reposition. Point selection is checked at exact candidate coordinates, but AIPath's own arrival tolerance means the fighter can settle a little off that exact spot - close enough to a wall corner, and a point that looked completely clear on paper can turn out blocked in practice. This watchdog doesn't try to diagnose which internal check missed it; it just notices nothing is happening and fixes that.")]
    public float maxTimeWithoutThrow = 2.5f;

    [Tooltip("How often path progress is sampled for the movement watchdog below.")]
    public float positionProgressCheckInterval = 0.4f;

    [Tooltip("Drop in ai.remainingDistance below this over one check interval counts as no real progress, regardless of how much the AI thinks it's trying to move. Measured along the PATH, not straight-line position - rounding a corner normally wobbles side to side for a moment, and a raw-displacement check can misread that wobble as being stuck.")]
    public float positionProgressThreshold = 0.15f;

    [Tooltip("Same watchdog philosophy as maxTimeWithoutThrow, applied to actual movement instead of shooting: the pathfinding graph's own walkability doesn't always match the real Collider2D closely enough, and a route that looks completely valid on the graph can still wedge the fighter against real geometry it never predicted.")]
    public float positionStuckAbandonTime = 1f;

    public float throwCooldown = 1.5f;
    public GameObject arrowPrefab;

    [Tooltip("Twice the kunai fighter's own throw speed (10) - arrows fly fast, per spec.")]
    public float arrowSpeed = 20f;

    [Tooltip("Matches Arrow.prefab's ObstacleSensor child collider - a thin, dedicated hitbox used only against Obstacles, kept separate from the thicker collider used against the Player. A zero-width line can mathematically slip past a corner the real arrow still clips; this models that real (intentionally thin) obstacle collision instead of guessing.")]
    public float arrowRadius = 0.02f;

    [Tooltip("Extra radius added on top of arrowRadius for aim-time obstacle checks only - the real arrow still flies at arrowRadius, this just makes the validation more conservative than reality (discrete physics steps vs a smooth continuous sweep don't always agree right at a corner graze).")]
    public float throwClearanceMargin = 0.05f;

    float ThrowCheckRadius => arrowRadius + throwClearanceMargin;

    public float faceRotationSpeed = 720f;

    [Tooltip("Extra clearance (added on top of the fighter's own collider size) required from any obstacle before a point counts as safe to stand on. The pathfinding graph's own 'walkable' flag alone isn't enough - its erosion buffer and the real Collider2D don't line up exactly, so a graph-walkable point can still have the real collider just barely touching a wall.")]
    public float clearanceMargin = 0.1f;

    float MaxThrowRange => standRadius * 2f;

    private AIPath ai;
    private CapsuleCollider2D bodyCollider;
    private float cooldownTimer;
    private float repathTimer;
    private float samplePhase;
    private Vector2? committedDestination;
    private bool lastThrowCheckFailed;
    private float stuckAtPointTimer;
    private bool seekingDirectShot;
    private bool approachingPlayer;
    private float timeSinceLastThrow;
    private float lastRemainingDistance;
    private float progressCheckTimer;
    private float positionStuckTimer;
    private bool progressCheckInitialized;
    private Vector2 lastDestinationForProgressCheck;
    private AntiTricker antiTricker;

    void Awake()
    {
        ai = GetComponent<AIPath>();
        bodyCollider = GetComponent<CapsuleCollider2D>();
        antiTricker = GetComponent<AntiTricker>();
        // Дальнобойный юнит целится в цель, а не по направлению собственного движения - стандартный
        // автоповорот AIPath (смотреть по ходу движения) здесь неверен, поэтому поворот управляется вручную.
        ai.enableRotation = false;

        // Та же причина, что и в KunaiFighter - взгляд на игрока, а не по направлению движения,
        // активно конфликтует с этой настройкой (она тормозит всякий раз, когда «не смотрю туда, куда
        // иду», а это как раз обычное состояние этого юнита).
        ai.slowWhenNotFacingTarget = false;

        // AIPath.gravity по умолчанию означает «брать из Physics2D.gravity автоматически» - реальная
        // сила вниз в игре с видом сверху, где нет понятия «вниз». Полную историю см. в
        // KunaiFighter.Awake (именно это вызывало заклинивание у нижней границы карты).
        ai.gravity = Vector3.zero;

        // Конвенция 2D в этом проекте: «+Y - вперёд при повороте 0» (см. FaceTowards ниже) -
        // AIPath по умолчанию использует 3D-конвенцию ZAxisForward, которая молча схлопывает управление
        // этого юнита в ноль, как только его поворот (независимо направляемый на игрока) расходится
        // с направлением движения.
        ai.orientation = OrientationMode.YAxisForward;
    }

    void Update()
    {
        Vector2 selfPos = transform.position;
        Vector2 playerPos = player.position;

        // EnemyChaseStop сам возвращает это в true каждый кадр, когда не касается препятствия -
        // для этого дальнобойного юнита это тоже нужно подтверждать каждый кадр, иначе значение молча
        // откатывается вскоре после Awake и остаётся откатанным при обычном (бесконтактном) движении.
        ai.enableRotation = false;

        timeSinceLastThrow += Time.deltaTime;

        UpdateThrowing(selfPos);

        // Пока AntiTricker владеет рывком, он напрямую управляет ai.destination/canMove/поворотом
        // каждый кадр (см. AntiTricker.UpdateMovement) - собственная логика движения/поворота этого
        // компонента не должна трогать их в том же кадре, иначе они дерутся за одни и те же поля каждый
        // тик. Та же граница исключительного владения, что EnemyChaseStop уже соблюдает для мечника.
        if (antiTricker == null || !antiTricker.IsCountering)
        {
            UpdateMovement(selfPos, playerPos);
            FaceTowards(playerPos);
        }
    }

    // Независимое действие №1: стрелять, когда есть выстрел и кулдаун позволяет. Никогда не
    // читает и не пишет состояние движения - выстрел может произойти, стоит ли боец на месте
    // или идёт к своей зафиксированной точке.
    void UpdateThrowing(Vector2 selfPos)
    {
        cooldownTimer -= Time.deltaTime;
        if (cooldownTimer > 0f) return;

        if (!TryFindShot(selfPos, out Vector2 direction))
        {
            // Питает восстановление «застрял в точке» в UpdateMovement ниже - имеет смысл только здесь,
            // вне кулдауна, так как это единственный момент, когда эта проверка реально идёт каждый кадр.
            lastThrowCheckFailed = true;
            return;
        }
        lastThrowCheckFailed = false;

        Debug.Log($"[ArcherFighter] Shot from {selfPos} direction {direction}");

        FireArrow(selfPos, direction);
        cooldownTimer = throwCooldown;
        timeSinceLastThrow = 0f;
    }

    // Независимое действие №2: держать зафиксированную точку на кольце вокруг игрока. Та же
    // структура, что и KunaiFighter.UpdateMovement - полное обоснование каждого сторожа см. в том
    // файле - только без различия «рикошет или прямой», так как каждый выстрел здесь одного вида.
    void UpdateMovement(Vector2 selfPos, Vector2 playerPos)
    {
        if (UpdatePositionProgressWatchdog())
        {
            seekingDirectShot = false;
            approachingPlayer = false;
            committedDestination = FindStandingPoint(selfPos, playerPos);
            repathTimer = positionSearchInterval;
            ai.SearchPath();
        }
        else if (seekingDirectShot)
        {
            if (TryFindShot(selfPos, out _))
            {
                seekingDirectShot = false;
                repathTimer = 0f; // принудительно переоценить в следующий тик, без устаревшей оставшейся точки
            }
            else if (timeSinceLastThrow >= maxTimeWithoutThrow)
            {
                committedDestination = FindStandingPoint(selfPos, playerPos, denseSearch: true);
                if (!committedDestination.HasValue)
                {
                    seekingDirectShot = false;
                    committedDestination = FindStandingPoint(selfPos, playerPos);
                    repathTimer = positionSearchInterval;
                }
                timeSinceLastThrow = 0f;
            }
        }
        // Ни одна точка на кольце вокруг игрока сейчас вообще не работает - вместо того чтобы
        // замереть в ожидании, пока игрок забредёт в точку, которую видно с кольца, просто сокращаем
        // дистанцию до него напрямую. Выходит в тот же момент, когда становится возможен выстрел,
        // а не по прибытии.
        else if (approachingPlayer)
        {
            if (TryFindShot(selfPos, out _))
            {
                approachingPlayer = false;
                repathTimer = 0f;
            }
            else
            {
                repathTimer -= Time.deltaTime;
                if (repathTimer <= 0f)
                {
                    committedDestination = playerPos;
                    repathTimer = positionSearchInterval;
                }
            }
        }
        else
        {
            bool arrivedButStuck = committedDestination.HasValue && ai.reachedDestination && lastThrowCheckFailed;
            stuckAtPointTimer = arrivedButStuck ? stuckAtPointTimer + Time.deltaTime : 0f;

            bool watchdogTriggered = timeSinceLastThrow >= maxTimeWithoutThrow;

            if (stuckAtPointTimer >= stuckAtPointAbandonTime || watchdogTriggered)
            {
                Vector2? densePoint = FindStandingPoint(selfPos, playerPos, denseSearch: true);
                if (densePoint.HasValue)
                {
                    seekingDirectShot = true;
                    committedDestination = densePoint;
                }
                else
                {
                    committedDestination = FindStandingPoint(selfPos, playerPos);
                    repathTimer = positionSearchInterval;
                }
                stuckAtPointTimer = 0f;
                timeSinceLastThrow = 0f;
            }
            else
            {
                repathTimer -= Time.deltaTime;
                bool stale = committedDestination.HasValue && Vector2.Distance(playerPos, committedDestination.Value) > standRadius * 1.5f;
                if (committedDestination == null || repathTimer <= 0f || stale)
                {
                    committedDestination = FindStandingPoint(selfPos, playerPos);
                    repathTimer = positionSearchInterval;
                }
            }

            if (!committedDestination.HasValue)
            {
                approachingPlayer = true;
                committedDestination = playerPos;
                repathTimer = positionSearchInterval;
            }
        }

        if (committedDestination.HasValue)
        {
            ai.canMove = true;
            ai.destination = committedDestination.Value;
        }
        else
        {
            ai.canMove = false;
        }

        // Безусловный пол, проверяется последним независимо от того, какая ветка выше выбрала
        // точку назначения: дальнобойному юниту врезаться вплотную к игроку никогда недопустимо.
        if (Vector2.Distance(selfPos, playerPos) < minPlayerDistance) ai.canMove = false;
    }

    bool UpdatePositionProgressWatchdog()
    {
        Vector2 currentDest = committedDestination ?? default;
        if (!progressCheckInitialized || currentDest != lastDestinationForProgressCheck)
        {
            lastRemainingDistance = ai.remainingDistance;
            lastDestinationForProgressCheck = currentDest;
            progressCheckInitialized = true;
            progressCheckTimer = 0f;
            positionStuckTimer = 0f;
            return false;
        }

        progressCheckTimer += Time.deltaTime;
        if (progressCheckTimer < positionProgressCheckInterval) return false;

        float progressed = lastRemainingDistance - ai.remainingDistance;
        bool noProgress = ai.canMove && !ai.reachedDestination && progressed < positionProgressThreshold;
        positionStuckTimer = noProgress ? positionStuckTimer + progressCheckTimer : 0f;

        lastRemainingDistance = ai.remainingDistance;
        progressCheckTimer = 0f;

        if (positionStuckTimer < positionStuckAbandonTime) return false;

        positionStuckTimer = 0f;
        return true;
    }

    bool IsWalkable(Vector2 point)
    {
        var info = AstarPath.active.GetNearest(point);
        if (info.node == null || !info.node.Walkable) return false;
        return HasPhysicalClearance(point);
    }

    bool HasPhysicalClearance(Vector2 point)
    {
        if (bodyCollider == null) return true;
        Vector2 size = bodyCollider.size + Vector2.one * (clearanceMargin * 2f);
        return Physics2D.OverlapCapsule(point, size, bodyCollider.direction, 0f, obstacleMask) == null;
    }

    // Сэмплирует точки на кольце вокруг игрока и оставляет ту, из которой выстрел уже доступен,
    // ближайшую к бойцу первой. denseSearch - путь восстановления при застревании (см.
    // UpdateMovement): запускается только когда реально застряли, поэтому может позволить себе более
    // плотный разовый перебор (8 фиксированных углов вместо 3 вращающихся) без добавления стоимости
    // каждый кадр.
    Vector2? FindStandingPoint(Vector2 selfPos, Vector2 playerPos, bool denseSearch = false)
    {
        if (denseSearch)
        {
            float bestDenseDist = float.MaxValue;
            Vector2? bestDense = null;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                Vector2 candidate = playerPos + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * standRadius;

                if (!IsWalkable(candidate)) continue;
                if (!TryFindShot(candidate, out _)) continue;

                float d = Vector2.Distance(selfPos, candidate);
                if (d < bestDenseDist) { bestDenseDist = d; bestDense = candidate; }
            }
            return bestDense;
        }

        samplePhase = (samplePhase + 47f) % 360f;
        float bestDist = float.MaxValue;
        Vector2? best = null;

        for (int i = 0; i < 3; i++)
        {
            float angle = (samplePhase + i * 120f) * Mathf.Deg2Rad;
            Vector2 candidate = playerPos + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * standRadius;

            if (!IsWalkable(candidate)) continue;
            if (!TryFindShot(candidate, out _)) continue;

            float d = Vector2.Distance(selfPos, candidate);
            if (d < bestDist) { bestDist = d; best = candidate; }
        }

        return best;
    }

    void FaceTowards(Vector2 worldPoint)
    {
        Vector2 toPoint = worldPoint - (Vector2)transform.position;
        if (toPoint.sqrMagnitude < 0.0001f) return;
        // Та же конвенция «-90», что и в KunaiFighter/Kunai/Knife: это вращает ВЕСЬ корень, а спрайт
        // тела Visual (Enemy_Alive) по умолчанию смотрит вверх (+Y), как и каждый другой спрайт
        // персонажа в проекте. Удерживаемый лук (BowVisual, дочерний к Visual) по умолчанию смотрит
        // вправо (+X) - у него свой фиксированный ЛОКАЛЬНЫЙ поворот +90 (заданный на префабе), именно
        // чтобы скомпенсировать это смещение обратно, так что он всё равно визуально смотрит туда же,
        // куда направлен весь Visual в целом.
        float targetAngle = Mathf.Atan2(toPoint.y, toPoint.x) * Mathf.Rad2Deg - 90f;
        float next = Mathf.MoveTowardsAngle(transform.eulerAngles.z, targetAngle, faceRotationSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, 0f, next);
    }

    // Проверка почти нулевой ширины, моделирующая реальный физический размер стрелы (arrowRadius) -
    // действительно нулевая по ширине линия может математически проскочить мимо угла, который
    // реальная стрела реального размера всё же заденет.
    bool IsLineClear(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.001f) return true;

        bool prev = Physics2D.queriesStartInColliders;
        Physics2D.queriesStartInColliders = false;
        bool clear = Physics2D.CircleCast(from, ThrowCheckRadius, delta / dist, dist, obstacleMask).collider == null;
        Physics2D.queriesStartInColliders = prev;
        return clear;
    }

    // Единственная проверка выстрела - прямая, ничем не заслонённая линия к любой из 3 точек
    // прицеливания игрока, при нескольких подходящих выбирается ближайшая. Без рикошетов, по
    // спецификации.
    bool TryFindShot(Vector2 origin, out Vector2 direction)
    {
        direction = default;
        float bestLength = float.MaxValue;
        bool found = false;

        for (int a = 0; a < playerAimPoints.Length; a++)
        {
            if (playerAimPoints[a] == null) continue;
            Vector2 target = playerAimPoints[a].position;
            float dist = Vector2.Distance(origin, target);
            if (dist > MaxThrowRange) continue;
            if (!IsLineClear(origin, target)) continue;

            if (dist < bestLength) { bestLength = dist; direction = (target - origin).normalized; found = true; }
        }

        return found;
    }

    void FireArrow(Vector2 origin, Vector2 direction)
    {
        if (arrowPrefab == null) return;
        GameObject obj = Instantiate(arrowPrefab, origin, Quaternion.identity);
        var arrow = obj.GetComponent<Arrow>();
        arrow.speed = arrowSpeed;
        arrow.Launch(direction);
    }
}
