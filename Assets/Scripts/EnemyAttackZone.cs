using UnityEngine;
using Pathfinding;

// Невидимая зона «сюда попал бы меч» перед врагом - пока не настоящая атака, а просто
// полукруг (проверка дистанции + скалярное произведение с направлением вперёд, а не реальная
// форма коллайдера), нужный чтобы измерять, как часто игрок получил бы удар, если бы враг
// действительно замахивался - чтобы оценить, насколько легко от него сейчас убежать.
// Как только игрок вошёл в зону, враг замирает на месте на windupTime (замах) - это и есть шанс
// игрока выйти из зоны; если к концу замаха он всё ещё в зоне - удар засчитывается, иначе мимо.
// Исключение - первый удар после рывка AntiTricker: рывок сбрасывает кулдаун и замах, и удар
// происходит мгновенно. Движением враг сам не управляет, кроме остановки на время замаха
// (EnemyChaseStop в это время не трогает canMove - см. IsWindingUp).
// Анимация меча чисто визуальная и на логику не влияет: она только показывает замах и удар.
public class EnemyAttackZone : MonoBehaviour
{
    public Transform player;
    public float range = 1.2f;
    public float swingCooldown = 0.8f;

    [Tooltip("Урон удара в долях от полного хп игрока (1 = всё хп). 0.5 - два удара убивают.")]
    public float damage = 0.5f;

    [Tooltip("Obstacles layer, used only for the wall-blocking check below.")]
    public LayerMask obstacleMask;

    [Tooltip("Obstacles up to this size (either bounds axis) don't block a hit - a sword swing can reach around a column the enemy is standing right next to. Anything bigger (walls) blocks the hit outright - the enemy shouldn't be able to land a hit through a solid wall just because the player is standing close on the other side of it, within this zone's range.")]
    public float maxPassableObstacleSize = 1.5f;

    [Tooltip("На сколько радиус, в котором удар засчитывается в КОНЦЕ замаха, больше range (иначе достаточно сделать шаг назад и удар всегда мимо: игрок успевает отойти на ~0.4 за 0.1 с). Спастись можно, только отскочив дальше этого радиуса или уйдя из-под удара вбок/за спину.")]
    public float hitRangeBonus = 0.5f;

    [Tooltip("Замах перед ударом, в секундах. Враг на это время замирает; не успел выйти из зоны - удар засчитан.")]
    public float windupTime = 0.1f;

    [Header("Анимация меча (только визуал)")]
    [Tooltip("Меч, который отводится назад и бьёт вперёд. Если не назначен, ищется Visual/Sword.")]
    public Transform sword;

    [Tooltip("Где у меча рукоять, в ЛОКАЛЬНЫХ координатах спрайта меча (клинок вверх, рукоять внизу). Вокруг этой точки меч поворачивается.")]
    public Vector2 handleOffset = new Vector2(0f, -0.55f);

    [Tooltip("Насколько градусов меч отводится назад в замахе (знак задаёт сторону отвода).")]
    public float windupAngle = -45f;

    [Tooltip("На сколько градусов от исходного положения меч уходит вперёд при ударе.")]
    public float strikeAngle = 75f;

    [Tooltip("Сколько длится визуальный взмах вперёд.")]
    public float strikeVisualTime = 0.06f;

    [Tooltip("Сколько меч возвращается в исходное положение после удара.")]
    public float returnVisualTime = 0.12f;

    public int PotentialHitCount { get; private set; }

    // Все реальные удары мечников по игроку (не парированные), для надписи на экране (PotentialHitCounterUI).
    public static int TotalHits;
    public int ParriedCount { get; private set; }

    private float cooldownTimer;
    private float windupTimer;
    private bool windingUp;
    // Взводится на время рывка AntiTricker: первый удар после рывка идёт без замаха.
    private bool instantStrikeArmed;
    private AIPath ai;
    // Удар должен пройти, но игрок смотрит на врага: идёт мини-игра парирования (время стоит).
    private bool awaitingParry;
    public bool IsWindingUp => windingUp;

    // Все живые мечники: при начале парирования те из них, чей удар уже летит (идёт замах), попадают
    // в ту же итерацию и отбиваются тем же парированием (см. JoinAllInFlight).
    private static readonly System.Collections.Generic.List<EnemyAttackZone> All = new System.Collections.Generic.List<EnemyAttackZone>();
    private readonly RaycastHit2D[] lineHits = new RaycastHit2D[8];
    private ContactFilter2D obstacleFilter;

    private AntiTricker antiTricker;
    // Координация (CombatDirector): без токена мечник не начинает атаку. Нет агента/директора - бьёт как раньше.
    private CombatAgent agent;

    private enum Anim { Idle, Windup, Strike, Return }
    private Anim anim;
    private float animTimer;
    private float animFromAngle;
    private Transform restParent;
    private Vector3 restLocalPos;
    private float restLocalRot;

    void Awake()
    {
        obstacleFilter = new ContactFilter2D();
        obstacleFilter.SetLayerMask(obstacleMask);
        obstacleFilter.useTriggers = false;

        antiTricker = GetComponent<AntiTricker>();
        ai = GetComponent<AIPath>();
        agent = GetComponent<CombatAgent>();

        if (sword == null)
        {
            Transform found = transform.Find("Visual/Sword");
            if (found != null) sword = found;
        }
        if (sword != null)
        {
            restParent = sword.parent;
            restLocalPos = sword.localPosition;
            restLocalRot = sword.localEulerAngles.z;
        }
    }

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    void OnDisable()
    {
        All.Remove(this);
        windingUp = false;
        ResetSword();
    }

    void Update()
    {
        if (awaitingParry) return;

        // Рывок AntiTricker сбрасывает всё: замах отменяется, кулдаун обнуляется, первый удар после
        // него мгновенный. Движением в это время владеет AntiTricker - canMove не трогаем.
        if (antiTricker != null && antiTricker.IsCountering)
        {
            windingUp = false;
            cooldownTimer = 0f;
            instantStrikeArmed = true;
            ResetSword();
            if (agent != null) agent.AttackFinished();
            return;
        }

        UpdateAnimation();

        if (windingUp)
        {
            windupTimer -= Time.deltaTime;
            if (windupTimer > 0f) return;

            // Замах закончился: попадание, только если игрок так и остался в зоне.
            windingUp = false;
            // isStopped тоже: остановку по касанию EnemyChaseStop выставил ДО замаха, а выход игрока
            // из зоны касания посреди замаха он (по IsWindingUp) пропустил - без сброса здесь враг
            // навсегда оставался бы с isStopped=true и стоял на месте. Если игрок всё ещё в зоне
            // касания, EnemyChaseStop сам снова остановит врага со следующего кадра.
            if (ai != null) { ai.isStopped = false; ai.canMove = true; }
            Strike(PlayerInReach(range + hitRangeBonus));
            return;
        }

        cooldownTimer -= Time.deltaTime;
        if (cooldownTimer > 0f || !PlayerInReach()) return;

        // Разрешение на атаку от директора. Мгновенный удар после рывка идёт вне общей паузы,
        // но токен всё равно нужен (рывок без токена AntiTricker и так не делает).
        if (agent != null && !agent.TryStartAttack(instantStrikeArmed)) return;

        if (instantStrikeArmed)
        {
            instantStrikeArmed = false;
            Strike(true);
            return;
        }

        windingUp = true;
        windupTimer = windupTime;
        if (ai != null) ai.canMove = false;
        StartAnim(Anim.Windup, 0f);
    }

    void Strike(bool hit)
    {
        cooldownTimer = swingCooldown;
        StartAnim(Anim.Strike, anim == Anim.Windup ? CurrentWindupAngle() : windupAngle);

        if (!hit)
        {
            if (agent != null) agent.AttackFinished();
            return;
        }

        // Парирование включается только когда урон действительно проходит. Если игрок смотрит на
        // врага, засчитывание откладывается до конца мини-игры.
        ParrySystem parry = ParrySystem.Instance;
        Collider2D body = GetComponent<Collider2D>();
        Vector2 point = body != null ? body.ClosestPoint(player.position) : (Vector2)transform.position;
        if (parry != null && parry.TryBegin(point, OnParryResolved))
        {
            awaitingParry = true;
            JoinAllInFlight();
        }
        else if (parry != null && parry.TryJoin(point, OnParryResolved)) awaitingParry = true; // другой мечник уже начал парирование
        else
        {
            RegisterHit();
            if (agent != null) agent.AttackFinished();
        }
    }

    // Начато парирование: остальные мечники, у кого замах уже идёт и игрок в зоне, бьют в ту же итерацию -
    // их удары разрешаются вместе с этим (а шарик ускоряется за каждого). Время уже стоит, поэтому
    // дожидаться окончания их замаха нельзя - иначе их удары прошли бы уже после парирования.
    void JoinAllInFlight()
    {
        foreach (var other in new System.Collections.Generic.List<EnemyAttackZone>(All))
        {
            if (other == this || !other.windingUp || other.awaitingParry) continue;
            if (!other.PlayerInReach(other.range + other.hitRangeBonus)) continue;

            other.windingUp = false;
            if (other.ai != null) { other.ai.isStopped = false; other.ai.canMove = true; }
            other.Strike(true);
        }
    }

    // Удар прошёл: счётчик и настоящий урон по игроку.
    void RegisterHit()
    {
        PotentialHitCount++;
        TotalHits++;
        var health = player.GetComponent<PlayerHealth>();
        if (health != null) health.TakeDamage(damage);
    }

    void OnParryResolved(bool parried)
    {
        awaitingParry = false;
        if (parried) ParriedCount++;
        else RegisterHit();
        if (agent != null) agent.AttackFinished();
    }

    bool PlayerInReach() { return PlayerInReach(range); }

    bool PlayerInReach(float radius)
    {
        // Та же конвенция, что и во всём проекте: «перед смотрит в +Y при повороте 0».
        Vector2 toPlayer = (Vector2)player.position - (Vector2)transform.position;
        return Vector2.Dot(transform.up, toPlayer.normalized) > 0f && PlayerWithinRange(radius);
    }

    // Дистанция + стена, БЕЗ учёта куда смотрит враг: AntiTricker во время рывка идёт боком к
    // игроку и по этой проверке обрывает рывок, как только игрок оказался вплотную.
    public bool PlayerWithinRange() { return PlayerWithinRange(range); }

    bool PlayerWithinRange(float radius)
    {
        Vector2 toPlayer = (Vector2)player.position - (Vector2)transform.position;
        return toPlayer.sqrMagnitude <= radius * radius && !BlockedByWall();
    }

    // --- Анимация меча: чисто визуальная, логику не задерживает ---

    void StartAnim(Anim next, float fromAngle)
    {
        anim = next;
        animTimer = 0f;
        animFromAngle = fromAngle;
    }

    float CurrentWindupAngle()
    {
        float t = Mathf.Clamp01(animTimer / Mathf.Max(windupTime, 0.0001f));
        return Mathf.Lerp(0f, windupAngle, Mathf.SmoothStep(0f, 1f, t));
    }

    void UpdateAnimation()
    {
        if (anim == Anim.Idle) return;
        animTimer += Time.deltaTime;

        switch (anim)
        {
            case Anim.Windup:
                ApplySwordAngle(CurrentWindupAngle());
                break;
            case Anim.Strike:
            {
                float t = Mathf.Clamp01(animTimer / Mathf.Max(strikeVisualTime, 0.0001f));
                ApplySwordAngle(Mathf.Lerp(animFromAngle, strikeAngle, t));
                if (t >= 1f) StartAnim(Anim.Return, strikeAngle);
                break;
            }
            case Anim.Return:
            {
                float t = Mathf.Clamp01(animTimer / Mathf.Max(returnVisualTime, 0.0001f));
                ApplySwordAngle(Mathf.Lerp(animFromAngle, 0f, Mathf.SmoothStep(0f, 1f, t)));
                if (t >= 1f) { anim = Anim.Idle; ResetSword(); }
                break;
            }
        }
    }

    // Поворачивает меч на delta градусов ВОКРУГ РУКОЯТИ (а не вокруг центра спрайта, где у катаны
    // пивот): точка хвата остаётся на месте, клинок описывает дугу, как в руке. Работает только
    // пока меч висит на теле - после смерти EnemyDeath отцепляет его, и трогать его нельзя.
    void ApplySwordAngle(float delta)
    {
        if (sword == null || sword.parent != restParent) return;

        Vector2 grip = (Vector2)restLocalPos + (Vector2)(Quaternion.Euler(0f, 0f, restLocalRot) * handleOffset);
        Vector2 fromGrip = (Vector2)restLocalPos - grip;
        Vector2 rotated = Quaternion.Euler(0f, 0f, delta) * fromGrip;

        sword.localPosition = new Vector3(grip.x + rotated.x, grip.y + rotated.y, restLocalPos.z);
        sword.localRotation = Quaternion.Euler(0f, 0f, restLocalRot + delta);
    }

    void ResetSword()
    {
        anim = Anim.Idle;
        if (sword == null || sword.parent != restParent) return;
        sword.localPosition = restLocalPos;
        sword.localRotation = Quaternion.Euler(0f, 0f, restLocalRot);
    }

    // Препятствие размером с колонну между врагом и игроком не блокирует удар - стоя вплотную к
    // колонне, меч всё равно может достать вокруг неё. Препятствие размером со стену - блокирует:
    // досягаемость не должна пробивать сплошную геометрию уровня только потому, что игрок стоит
    // рядом по другую сторону в пределах дальности.
    bool BlockedByWall()
    {
        int count = Physics2D.Linecast(transform.position, player.position, obstacleFilter, lineHits);
        for (int i = 0; i < count; i++)
        {
            Bounds b = lineHits[i].collider.bounds;
            if (b.size.x > maxPassableObstacleSize || b.size.y > maxPassableObstacleSize) return true;
        }
        return false;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0f, 0f, 0.3f);
        Vector3 forward = transform.up;
        int segments = 16;
        Vector3 prev = transform.position + forward * range;
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(-90f, 90f, t) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(forward.x * Mathf.Cos(angle) - forward.y * Mathf.Sin(angle),
                                       forward.x * Mathf.Sin(angle) + forward.y * Mathf.Cos(angle), 0f);
            Vector3 point = transform.position + dir * range;
            Gizmos.DrawLine(prev, point);
            prev = point;
        }
    }
}
