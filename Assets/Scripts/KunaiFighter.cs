using UnityEngine;
using Pathfinding;

// Дальнобойный враг, переписанный по письменной спецификации (Техническое задание - Кунайщик, 2026-09-03).
// Два независимых цикла действий работают каждый кадр и никогда не трогают состояние друг друга:
//   - UpdateThrowing: доступен ли бросок ПРЯМО СЕЙЧАС оттуда, где я сейчас стою? Если да и
//     кулдаун прошёл - стреляем; действие «выстрелил и забыл», не важно, стоит ли боец на месте
//     или идёт.
//   - UpdateMovement: держать точку стояния на кольце вокруг игрока. Выбирает ОДНУ точку один раз
//     (раз в секунду, или сразу, если она перестала работать) и просто задаёт
//     ai.destination/ai.canMove - тот же самый механизм движения, что использует мечник (AIPath +
//     общий EnemyChaseStop) - и идёт туда без повторного поиска на ходу, в соответствии с
//     требованием, что никаких поисков/вычислений не происходит, пока идёт подход к зафиксированной точке.
// Прицеливание идёт по 3 фиксированным точкам на теле игрока (голова, оба плеча) тонкими (нулевой
// ширины) проверками линии, вместо толстой проверки столкновения - спецификация прямо требует линию
// в один пиксель и решает «у игрока есть ширина» тем, что даёт ему 3 дискретные точки прицеливания.
// Рикошеты используют метод зеркального отражения именно от 4 граничных стен уровня (а не от
// произвольных ближайших стен) - отражение точки тела через известную осе-параллельную линию
// стены это простое переворачивание координаты, достаточно дешёвое, чтобы держать небольшой
// постоянный кэш (не более 4 слотов), а не выводить кандидатов заново при каждой проверке.
public class KunaiFighter : MonoBehaviour
{
    public Transform player;

    [Tooltip("The 3 aim points on the player's body to throw at: head, left shoulder, right shoulder. Thin line-of-sight checks target these directly instead of the player's center.")]
    public Transform[] playerAimPoints = new Transform[3];

    public LayerMask obstacleMask;

    [Tooltip("The radius of the ring around the player where standing points are searched for. Max throw range is 2x this, per spec. Since the standing point is always this far from the player, walking to it and holding it is what keeps the fighter at range - no separate retreat logic needed.")]
    public float standRadius = 4.5f;

    [Tooltip("Hard floor on how close this ranged unit may ever end up to the player, regardless of which movement branch is driving it - normal ring-holding already keeps it at standRadius, but approachingPlayer (see UpdateMovement) aims straight at the player's live position while no shot exists anywhere, and that alone must never be allowed to walk it into melee contact. One tile, not a suggestion: checked unconditionally at the end of UpdateMovement, after every branch has already picked its destination.")]
    public float minPlayerDistance = 1f;

    [Tooltip("Точки на кольце, путь к которым проходит ближе этого расстояния от игрока, выбираются в последнюю очередь (иначе боец бежит прямо на игрока, обходя его с другой стороны).")]
    public float pathPlayerClearance = 2f;

    [Tooltip("Standing points are (re)searched at most this often, and immediately if the current one stops working.")]
    public float positionSearchInterval = 1f;

    [Tooltip("If the fighter is standing right at its committed point, off cooldown, and still can't find any shot for this long, stop waiting and immediately hunt for a point with a guaranteed direct (no-ricochet) line instead. Covers the case where the exact spot it walked to no longer has the shot it was chosen for (target moved, or arrival landed slightly off the validated spot).")]
    public float stuckAtPointAbandonTime = 0.5f;

    [Tooltip("Independent of any internal reasoning about WHY - if a real kunai simply hasn't left this fighter's hand for this long, that alone is reason enough to force a direct-shot-guaranteed reposition. Point selection is checked at exact candidate coordinates, but AIPath's own arrival tolerance means the fighter can settle a little off that exact spot - close enough to a wall corner, and a point that looked completely clear on paper can turn out blocked in practice. This watchdog doesn't try to diagnose which internal check missed it; it just notices nothing is happening and fixes that.")]
    public float maxTimeWithoutThrow = 2.5f;

    [Tooltip("How often path progress is sampled for the movement watchdog below.")]
    public float positionProgressCheckInterval = 0.4f;

    [Tooltip("Drop in ai.remainingDistance below this over one check interval counts as no real progress, regardless of how much the AI thinks it's trying to move. Measured along the PATH, not straight-line position - rounding a corner normally wobbles side to side for a moment, and a raw-displacement check can misread that wobble as being stuck (confirmed live: it aborted a route right as the fighter was about to slip through a narrow gap, because net straight-line movement over that one interval happened to be small even though real progress along the path was happening).")]
    public float positionProgressThreshold = 0.15f;

    [Tooltip("Same watchdog philosophy as maxTimeWithoutThrow, applied to actual movement instead of throwing: the pathfinding graph's own walkability doesn't always match the real Collider2D closely enough, and a route that looks completely valid on the graph can still wedge the fighter against real geometry it never predicted - AIPath keeps reporting a valid path and a nonzero desired velocity while the real Rigidbody2D goes nowhere. Rather than diagnose which check the graph/physics mismatch slipped past, this just notices no real progress is happening toward the current destination for this long and abandons it for a fresh one, wherever that leads.")]
    public float positionStuckAbandonTime = 1f;

    public float throwCooldown = 1.5f;
    public GameObject kunaiPrefab;
    public float kunaiSpeed = 10f;

    [Tooltip("Matches Kunai.prefab's ObstacleSensor child collider - a thin, dedicated hitbox used only against Obstacles, kept separate from the thicker collider used against the Player. A zero-width line can mathematically slip past a corner the real kunai still clips; this models that real (now intentionally thin) obstacle collision instead of guessing.")]
    public float kunaiRadius = 0.02f;

    [Tooltip("Physical length of the thrown kunai (its visible sprite, not kunaiRadius - that's a tiny obstacle-hitbox radius, unrelated to visual size). Auto-filled from kunaiPrefab's own sprite in Awake if one is set; this default is just a fallback. Used only by the corner-throw leniency below.")]
    public float kunaiLength = 0.58f;

    [Tooltip("A bounce whose corner-clearance is tighter than this fraction of the kunai's own length is treated as a graze the real kunai bounces past rather than a miss - same concept and default as the player's own Knife.cornerRiskLengthFraction, but the enemy gets leniency (shot still fires) instead of a block, so it doesn't camp refusing every tight-corner ricochet forever.")]
    public float cornerLeniencyFraction = 0.6f;

    [Tooltip("Extra radius added on top of kunaiRadius for aim-time obstacle checks only - the real kunai still flies at kunaiRadius, this just makes the validation more conservative than reality. Confirmed live: a thrown kunai grazed the exact top-left corner of a column and bounced off it, even though the aim-time check (a continuous CircleCast swept at exactly kunaiRadius) called that same corner clear - the real projectile moves in discrete physics steps (fixedDeltaTime chunks) rather than a smooth continuous sweep, and right at a razor's-edge corner graze the two don't always agree. Checking with a bit more radius than the real kunai actually needs means a shot that's genuinely borderline gets rejected (and the fighter repositions) instead of gambled on.")]
    public float throwClearanceMargin = 0.05f;

    float ThrowCheckRadius => kunaiRadius + throwClearanceMargin;

    [Tooltip("A direct throw is provably safe: check a line to the player, and what you checked is exactly what flies, no physics in between to disagree with. A ricochet predicts a single ideal bounce off the far wall, then the real kunai's second leg has to actually reach the target - validated below with an overlap check spanning that whole leg (a real physics overlap test, not a swept cast), which is what closed the remaining gap.")]
    public bool enableRicochets = true;

    public float faceRotationSpeed = 720f;

    [Tooltip("Extra clearance (added on top of the fighter's own collider size) required from any obstacle before a point counts as safe to stand on. The pathfinding graph's own 'walkable' flag alone isn't enough - its erosion buffer and the real Collider2D don't line up exactly, so a graph-walkable point can still have the real collider just barely touching a wall. That contact hands movement over to EnemyChaseStop's own slide-along-the-wall correction (built for a melee unit brushing past obstacles on its way to the player), which fights this component's own destination every frame it's in contact - that fight is what actually looked like the fighter shaking in place at a wall and never closing in.")]
    public float clearanceMargin = 0.1f;

    float MaxThrowRange => standRadius * 2f;

    // wallIndex: 0=верх, 1=низ, 2=лево, 3=право (собственный граничный прямоугольник уровня).
    struct Extrapolation
    {
        public bool valid;
        public int aimPointIndex;
        public int wallIndex;
        public Vector2 mirroredPoint;
    }

    private AIPath ai;
    private EnemyChaseStop chaseStop;
    private CapsuleCollider2D bodyCollider;
    private float cooldownTimer;
    private CombatAgent agent;
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
    private readonly Extrapolation[] extrapolations = new Extrapolation[4];
    private ContactFilter2D obstacleFilter;
    private readonly Collider2D[] cornerOverlapBuffer = new Collider2D[8];

    void Awake()
    {
        ai = GetComponent<AIPath>();
        agent = GetComponent<CombatAgent>();
        chaseStop = GetComponent<EnemyChaseStop>();
        bodyCollider = GetComponent<CapsuleCollider2D>();
        // Дальнобойный юнит целится в цель, а не по направлению собственного движения - стандартный
        // автоповорот AIPath (смотреть по ходу движения) здесь неверен, поэтому поворот управляется вручную.
        ai.enableRotation = false;

        // Дальнобойный юнит, смотрящий на ИГРОКА, а не по ходу движения, - как раз то, на что эта
        // настройка заставляет тормозить: она считает «не смотрю туда, куда иду» поводом тормозить, что
        // верно для обычного врага, бегущего вперёд, но активно конфликтует с юнитом, намеренно
        // целящимся не по собственному курсу. Подтверждено вживую: при включённой настройке собственное
        // управление AIPath выдавало ровно нулевую скорость бесконечно, несмотря на валидный путь и
        // разумный steeringTarget - движения не было НИ В КАКУЮ сторону, даже в неправильную.
        ai.slowWhenNotFacingTarget = false;

        // AIPath.gravity был оставлен на значении по умолчанию (NaN,NaN,NaN), что означает «брать из
        // Physics2D.gravity автоматически» - то есть реальная сила (0,-9.81) вниз применялась к собственному
        // управлению агента каждый кадр поверх построения пути, в игре с видом сверху, где нет понятия
        // «вниз». Подтверждено напрямую: при временно отключённой реакции EnemyChaseStop на контакт
        // (его единственном противодействии) ai.velocity показывала чистые (0,-4.45,0), пока агент стоял
        // у нижней стены уровня с целью явно на севере - его тянуло прямо сквозь пол. Это и есть реальная
        // причина, по которой он постоянно оказывался заклиненным у нижней границы карты, какая бы точка
        // ни выбиралась и как бы аккуратно он туда ни шёл - вовсе не баг путей или назначения.
        ai.gravity = Vector3.zero;

        // AIPath по умолчанию использует ZAxisForward (3D-конвенцию) - конвенция 2D во всём проекте
        // «+Y - вперёд при повороте 0» (см. FaceTowards ниже и ту же конвенцию в
        // EnemyChaseStop/EnemyAttackZone). Несовпадение безвредно для ближнего юнита, чей поворот остаётся
        // синхронизирован с направлением движения (enableRotation=true), но поворот этого юнита независимо
        // направляется на игрока - как только они расходятся, внутреннее управление AIPath (которое
        // выводит свой вектор «вперёд» не из той оси) может молча схлопнуться в нулевой вектор.
        // Подтверждено вживую: сохранённая в сцене позиция спавна давала desiredVelocity = (0,0,0)
        // бесконечно при полностью валидном пути и всех остальных нормальных настройках - переключение
        // на YAxisForward заставило его двигаться сразу, без других изменений.
        ai.orientation = OrientationMode.YAxisForward;

        obstacleFilter = new ContactFilter2D();
        obstacleFilter.SetLayerMask(obstacleMask);
        obstacleFilter.useTriggers = false;

        if (kunaiPrefab != null)
        {
            var kunaiSprite = kunaiPrefab.GetComponentInChildren<SpriteRenderer>();
            if (kunaiSprite != null && kunaiSprite.sprite != null)
                kunaiLength = kunaiSprite.sprite.bounds.size.y * kunaiSprite.transform.localScale.y;
        }
    }

    void Update()
    {
        Vector2 selfPos = transform.position;
        Vector2 playerPos = player.position;

        // EnemyChaseStop сам возвращает это в true каждый кадр, когда не касается препятствия
        // (верно для ближнего юнита, для которого он строился и который как раз хочет собственный
        // поворот AIPath) - для этого дальнобойного юнита это тоже нужно подтверждать каждый кадр, иначе
        // значение молча откатывается вскоре после Awake и остаётся откатанным при обычном движении
        // без контакта.
        ai.enableRotation = false;

        timeSinceLastThrow += Time.deltaTime;

        if (enableRicochets) RefreshExtrapolations();
        UpdateThrowing(selfPos);
        UpdateMovement(selfPos, playerPos);
        FaceTowards(playerPos);
    }

    // Независимое действие №1: стрелять, когда есть выстрел и кулдаун позволяет. Никогда не
    // читает и не пишет состояние движения - бросок может произойти, стоит ли боец на месте или
    // идёт к своей зафиксированной точке (пункт 7 спецификации).
    void UpdateThrowing(Vector2 selfPos)
    {
        cooldownTimer -= Time.deltaTime;
        if (cooldownTimer > 0f) return;

        if (!TryFindBestShot(selfPos, out Vector2 direction, logThisCheck: true))
        {
            // Питает восстановление «застрял в точке» в UpdateMovement ниже - имеет смысл только здесь,
            // вне кулдауна, так как это единственный момент, когда эта проверка реально идёт каждый кадр.
            lastThrowCheckFailed = true;
            return;
        }
        lastThrowCheckFailed = false;

        // Координация: выстрел есть, но бросать можно только с разрешения директора (токен кольца).
        // Отказ лишь откладывает бросок - кулдаун и движение не трогаются, повтор через RetryDelay.
        if (agent != null && !agent.TryStartAttack())
        {
            cooldownTimer = CombatAgent.RetryDelay;
            // Выстрел есть, просто ждём разрешения - это не «давно не бросал из-за плохой позиции»,
            // поэтому сторож maxTimeWithoutThrow (перестановка в поисках выстрела) не должен сработать.
            timeSinceLastThrow = 0f;
            return;
        }

        // Оставлено навсегда (один дешёвый лог на бросок, а не на кадр): единственный способ поймать
        // плохой бросок - знать, откуда именно и в каком направлении он случился в тот самый момент;
        // гадать координаты постфактум на большом уровне тратит куда больше времени, чем стоит этот лог.
        // Если когда-нибудь увидят кунай, летящий в стену, в Консоли будут точные начало/направление
        // броска, который это вызвал, без необходимости воспроизводить.
        Debug.Log($"[KunaiFighter] Throw from {selfPos} direction {direction}");

        FireKunai(selfPos, direction);
        cooldownTimer = throwCooldown;
        timeSinceLastThrow = 0f;
    }

    // Независимое действие №2: держать зафиксированную точку на кольце вокруг игрока, точно как
    // мечник держит свою цель на игроке - просто ai.destination + ai.canMove, предоставляя всё
    // остальное проверенному движению, коллизиям и обработке застревания у AIPath/EnemyChaseStop.
    // Никакого собственного состояния отступления и собственной телепортации при застревании -
    // точка кольца И ЕСТЬ поведение «держи дистанцию»: она всегда в standRadius от игрока, поэтому
    // дойти до неё и держаться естественно удерживает бойца вне ближнего боя. Намеренно НЕ ищет
    // заново в пути или по прибытии - только раз в секунду (или по проверке устаревания ниже)
    // выбирается новая, ровно по спецификации («точки строятся каждую секунду»). Перепланирование в
    // тот же миг, когда обнаружено прибытие, раньше вызывало заметное метание из стороны в сторону:
    // прибытие AIPath всегда лишь приблизительное (в пределах arrivalThreshold, а не точное), и
    // выстрел, валидный из точной кандидатной точки, может не получиться из позиции примерно в полюнита от неё
    // у угла - поэтому он постоянно перескакивал на новую точку каждый раз, когда достигнутая
    // сразу не оправдывалась. Выдерживание того же односекундного окна, которое UpdateThrowing
    // уже использует для повторных проверок на выстрел, полностью это исправляет.
    void UpdateMovement(Vector2 selfPos, Vector2 playerPos)
    {
        // Сторож движения, та же философия, что и сторож броска ниже: проходимость графа путей не
        // всегда достаточно точно совпадает с реальным Collider2D, и AIPath может продолжать сообщать
        // валидный путь и ненулевую желаемую скорость, пока реальный Rigidbody2D никуда не движется -
        // заклинен о геометрию, которую граф не учёл (подтверждено вживую: стоит перекрытый стеной,
        // canMove=true, hasPath=true, ai.velocity ненулевая, rb.velocity прижата к нулю). И здесь
        // никаких попыток диагностировать, какую проверку обошло расхождение граф/физика - просто
        // замечаем, что реального чистого прогресса к тому месту, куда нужно, нет слишком долго, и
        // полностью бросаем это назначение ради нового, куда бы оно ни привело. Имеет приоритет над
        // всем ниже, включая активный seekingDirectShot - точка для прямого выстрела бесполезна, если
        // до неё физически не добраться.
        if (UpdatePositionProgressWatchdog())
        {
            seekingDirectShot = false;
            approachingPlayer = false;
            committedDestination = FindStandingPoint(selfPos, playerPos);
            repathTimer = positionSearchInterval;
            ai.SearchPath();
        }
        // Восстановление «застрял в точке», та же философия «решение-снимок», что и обычный
        // зафиксированный ход по спецификации («не думай, просто иди туда»): именно эта точка была
        // выбрана ПОТОМУ, что из неё был выстрел - если боец реально стоит там, вне кулдауна, и всё
        // равно не находит выстрела, он полностью переходит в seekingDirectShot вместо ожидания обычной
        // кадансовой переоценки. Пока ищет, любые другие вычисления ниже пропускаются целиком (ни таймера
        // перепоиска, ни проверки устаревания, ни повторного рассмотрения рикошетов) - он просто идёт.
        // ЕДИНСТВЕННОЕ, что заканчивает поиск - когда прямой выстрел действительно становится возможен,
        // проверяется заново каждый кадр оттуда, где он сейчас находится, а не «прибыл в точку». Если это
        // открывается на ходу, он выходит тут же, и UpdateThrowing (который уже идёт каждый кадр
        // независимо от состояния движения) берёт управление на себя.
        else if (seekingDirectShot)
        {
            if (HasDirectShot(selfPos))
            {
                seekingDirectShot = false;
                repathTimer = 0f; // принудительно переоценить в следующий тик, без устаревшей оставшейся точки
            }
            else if (timeSinceLastThrow >= maxTimeWithoutThrow)
            {
                // Сторож применим и здесь - сама цель поиска может оказаться недостижимой или иначе не
                // оправдаться. Пробуем другую прямую точку, а не ищем ту же вечно; если вообще нет ни одной -
                // откатываемся к обычному поиску, допускающему рикошеты.
                committedDestination = FindStandingPoint(selfPos, playerPos, directOnly: true);
                if (!committedDestination.HasValue)
                {
                    seekingDirectShot = false;
                    committedDestination = FindStandingPoint(selfPos, playerPos);
                    repathTimer = positionSearchInterval;
                }
                timeSinceLastThrow = 0f;
            }
        }
        // Ни одна точка кольца вокруг игрока сейчас вообще не работает (все проверенные ниже углы
        // оказались пусты) - вместо того чтобы замереть в ожидании, пока игрок забредёт в точку, видимую
        // с кольца, просто сокращаем дистанцию до него напрямую. Выходит в тот же момент, когда становится
        // возможен ЛЮБОЙ выстрел - та же самая проверка TryFindBestShot, которую UpdateThrowing и так
        // гоняет каждый кадр независимо от состояния движения, так что это покрывает и рикошеты, а не
        // только прямые линии: сближение - лишь средство снова открыть выстрел, а не самоцель, поэтому нет
        // причин ограничивать, что считается «открытым».
        else if (approachingPlayer)
        {
            if (TryFindBestShot(selfPos, out _))
            {
                approachingPlayer = false;
                repathTimer = 0f; // принудительно выбрать свежую зафиксированную точку сразу в следующий тик
            }
            else
            {
                // Обновляется с той же раз-в-секунду каденцией, что и обычная зафиксированная точка, а не
                // каждый кадр - постоянное перенацеливание на точную живую позицию игрока меняло бы
                // committedDestination каждый тик, что засыпало бы обнаружение смены цели в
                // UpdatePositionProgressWatchdog постоянными сбросами его окна прогресса и он никогда бы по-настоящему
                // не ловил реальное заклинивание.
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

            // Независимый сторож, намеренно вообще не привязанный к reachedDestination/lastThrowCheckFailed:
            // выбор точки проверяется в точных кандидатных координатах, но собственная толерантность прибытия
            // AIPath может усадить бойца чуть в стороне от этого места - достаточно близко к углу стены, и
            // всё выше может честно считать, что всё в порядке, пока кунай на деле давно не вылетал. Этот
            // сторож не пытается диагностировать почему; он просто замечает, что ничего не бросали слишком
            // долго, и применяет то же исправление независимо от того, что думает остальное состояние.
            bool watchdogTriggered = timeSinceLastThrow >= maxTimeWithoutThrow;

            if (stuckAtPointTimer >= stuckAtPointAbandonTime || watchdogTriggered)
            {
                Vector2? directPoint = FindStandingPoint(selfPos, playerPos, directOnly: true);
                if (directPoint.HasValue)
                {
                    seekingDirectShot = true;
                    committedDestination = directPoint;
                }
                else
                {
                    // Ни одна точка на кольце сейчас не имеет простой прямой линии - откатываемся к обычному
                    // поиску, допускающему рикошеты, вместо того чтобы оставить его вообще ни с чем.
                    committedDestination = FindStandingPoint(selfPos, playerPos);
                    repathTimer = positionSearchInterval;
                }
                stuckAtPointTimer = 0f;
                // Не даём срабатывать заново каждый кадр, пока свежей точке нужно время оправдаться - реальный
                // бросок (или следующий цикл застревания/сторожа) сбросит это как надо.
                timeSinceLastThrow = 0f;
            }
            else
            {
                repathTimer -= Time.deltaTime;
                // Зафиксированная точка валидна только относительно того места, где игрок был, когда её выбрали -
                // если игрок с тех пор сильно отдалился (рывок или просто обычное движение при долгом подходе),
                // точка может оказаться совсем не рядом с ним. Без этой проверки боец продолжал бы идти к ней до
                // целой секунды в любом случае - именно так он и оказывается заклиненным у стены не на той стороне
                // карты, гонясь за точкой, которая имела смысл для СТАРОЙ позиции игрока, но не для текущей.
                bool stale = committedDestination.HasValue && Vector2.Distance(playerPos, committedDestination.Value) > standRadius * 1.5f;
                if (committedDestination == null || repathTimer <= 0f || stale)
                {
                    // Точка всё ещё хорошая (проходима, из неё есть бросок, дистанция до игрока в норме) - держим её,
                    // а не перескакиваем каждую секунду на ближайшую из новых случайных: в тесном уровне с колоннами
                    // такие перескоки гоняли бойца через всю комнату, в том числе вплотную к игроку.
                    if (!stale && committedDestination.HasValue && StillGoodStandingPoint(committedDestination.Value, playerPos))
                    {
                        repathTimer = positionSearchInterval;
                    }
                    else
                    {
                        committedDestination = FindStandingPoint(selfPos, playerPos);
                        repathTimer = positionSearchInterval;
                    }
                }
            }

            // Ни прямой перебор выше, ни поиск с рикошетами не нашли ни одной рабочей точки на всём кольце -
            // игрок полностью скрыт от всего этого радиуса (глубокая ниша, широкое препятствие), а не только
            // от пары углов, опробованных в этом цикле. Стоять и ждать, пока игрок забредёт в точку, видимую
            // с кольца, выглядело бы так, будто боец выключился - вместо этого сокращаем до него дистанцию,
            // см. ветку approachingPlayer выше.
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

        // Безусловный пол, проверяется последним независимо от того, какая ветка выше выбрала точку
        // назначения: дальнобойному юниту врезаться вплотную к игроку никогда недопустимо, даже на один
        // кадр, пока approachingPlayer сближается с точной живой позицией игрока, когда выстрела ещё нигде
        // нет. Достаточно просто отказаться делать следующий шаг (а не вычислять запасную точку) - он
        // перестаёт приближаться в тот миг, как оказывается так близко, а собственное расталкивание
        // EnemyChaseStop справляется дальше, если игрок сам закрывает остаток зазора.
        // Пол действует только пока боец идёт К игроку: если точка назначения дальше от игрока, чем он сам
        // сейчас, он уходит - и останавливать его нельзя, иначе, оказавшись впритык, он застрял бы навсегда.
        if (Vector2.Distance(selfPos, playerPos) < minPlayerDistance &&
            (!committedDestination.HasValue || Vector2.Distance(committedDestination.Value, playerPos) < Vector2.Distance(selfPos, playerPos)))
            ai.canMove = false;
    }

    // Сэмплирует ai.remainingDistance (оставшееся расстояние ВДОЛЬ ПУТИ, а не по прямой) каждые
    // positionProgressCheckInterval секунд и сообщает true, как только оно перестало существенно
    // сокращаться в течение positionStuckAbandonTime, пока боец реально пытался двигаться (canMove) и
    // ещё не прибыл. Подтверждено вживую, почему это не может быть сырое смещение позиции: обход угла
    // или протискивание мимо препятствия обычно ненадолго раскачивает из стороны в сторону, и проверка
    // по прямой может принять совершенно реальный прогресс за «застрял» просто потому, что замер
    // пришёлся на середину качания - поймали, как это обрывало маршрут буквально в момент, когда боец
    // собирался проскользнуть в узкий проём. Расстояние вдоль пути не обманывается боковым качанием так,
    // как позиция по прямой.
    bool UpdatePositionProgressWatchdog()
    {
        // Смена цели (от самого этого сторожа, обычного цикла перепоиска или проверки устаревания) заставляет
        // remainingDistance скачком отражать длину нового пути - сравнение через этот скачок читалось бы
        // либо как огромный фальшивый «прогресс», либо как огромный фальшивый регресс, ни то ни другое
        // не имеет смысла. Обнаруживаем смену и даём свежей цели чистое, полностью сброшенное окно, вместо
        // того чтобы судить её по числам старой цели.
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

    // Единственный авторитет по вопросу «может ли боец реально здесь стоять» - спрашивает настоящий
    // граф путей, а не аппроксимирует вручную настроенным отступом от границы, который покрывал
    // только внешние стены и оставлял каждую колонну подверженной той же проблеме: точка, которую
    // рейкасты считают чистой, всё равно может попасть в непроходимый буфер эрозии стены или колонны,
    // где управление AIPath ломается (сообщает о движении в одну сторону, а дрейфует в другую - именно
    // это и заставляло его застревать, дрожа у стены, никогда не приближаясь).
    // Путь прямо к точке на кольце часто идёт через игрока (точка на противоположной стороне от
    // него), и боец сам упирается в игрока вплотную. Такие точки отбрасываются в последнюю очередь:
    // штраф к расстоянию, так что они выбираются, только если ничего другого нет.
    float PathPenalty(Vector2 from, Vector2 to, Vector2 playerPos)
    {
        Vector2 seg = to - from;
        float len2 = seg.sqrMagnitude;
        float t = len2 > 0.0001f ? Mathf.Clamp01(Vector2.Dot(playerPos - from, seg) / len2) : 0f;
        return Vector2.Distance(playerPos, from + seg * t) < pathPlayerClearance ? 100f : 0f;
    }

    bool StillGoodStandingPoint(Vector2 point, Vector2 playerPos)
    {
        if (Mathf.Abs(Vector2.Distance(point, playerPos) - standRadius) > standRadius * 0.35f) return false;
        return IsWalkable(point) && TryFindBestShot(point, out _);
    }

    bool IsWalkable(Vector2 point)
    {
        var info = AstarPath.active.GetNearest(point);
        if (info.node == null || !info.node.Walkable) return false;
        return HasPhysicalClearance(point);
    }

    // Собственный флаг «проходимо» у графа - не вся картина: его буфер эрозии и реальный Collider2D
    // совпадают не точно, поэтому проходимая по графу точка всё равно может оказаться такой, где
    // реальный коллайдер бойца едва касается стены, когда он там стоит. Этот контакт и передаёт
    // управление собственной коррекции скольжения вдоль стены у EnemyChaseStop каждый кадр, постоянно
    // конфликтуя с назначением этого компонента - здесь проверяется реальная физическая форма с запасом
    // сверху, чтобы выбранная точка никогда не оказывалась достаточно близко для этого.
    bool HasPhysicalClearance(Vector2 point)
    {
        if (bodyCollider == null) return true;
        Vector2 size = bodyCollider.size + Vector2.one * (clearanceMargin * 2f);
        return Physics2D.OverlapCapsule(point, size, bodyCollider.direction, 0f, obstacleMask) == null;
    }

    // Сэмплирует до 3 точек на кольце вокруг игрока (пункт 2 спецификации) и оставляет ту, из которой
    // бросок уже доступен, ближайшую к бойцу первой. Углы сэмплирования понемногу вращаются при
    // каждом вызове, так что повторные поиски (каждую секунду) в итоге покрывают всё кольцо, вместо
    // того чтобы всегда проверять одни и те же 3 места.
    //
    // directOnly - путь восстановления «застрял в точке» (см. UpdateMovement): запускается только когда
    // реально застряли, поэтому может позволить себе более плотный разовый перебор (8 фиксированных
    // углов вместо 3 вращающихся) без добавления стоимости каждый кадр - всё равно далеко не «рейкаст
    // на каждый градус», а просто разовый всплеск дешёвых проверок, за который остальное время не
    // платится. Требует именно HasDirectShot (без математики рикошета и без неопределённости физики
    // после проверки), а не TryFindBestShot, так как прямая линия - единственный вид выстрела, который
    // сам не может быть причиной застревания бойца.
    Vector2? FindStandingPoint(Vector2 selfPos, Vector2 playerPos, bool directOnly = false)
    {
        if (directOnly)
        {
            float bestDirectDist = float.MaxValue;
            Vector2? bestDirect = null;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                Vector2 candidate = playerPos + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * standRadius;

                if (!IsWalkable(candidate)) continue;
                if (!HasDirectShot(candidate)) continue;

                float d = Vector2.Distance(selfPos, candidate) + PathPenalty(selfPos, candidate, playerPos);
                if (d < bestDirectDist) { bestDirectDist = d; bestDirect = candidate; }
            }
            return bestDirect;
        }

        samplePhase = (samplePhase + 47f) % 360f;
        float bestDist = float.MaxValue;
        Vector2? best = null;

        for (int i = 0; i < 3; i++)
        {
            float angle = (samplePhase + i * 120f) * Mathf.Deg2Rad;
            Vector2 candidate = playerPos + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * standRadius;

            if (!IsWalkable(candidate)) continue;
            if (!TryFindBestShot(candidate, out _)) continue;

            float d = Vector2.Distance(selfPos, candidate) + PathPenalty(selfPos, candidate, playerPos);
            if (d < bestDist) { bestDist = d; best = candidate; }
        }

        return best;
    }

    // Дешёвая проверка без рикошетов, используемая только восстановлением «застрял в точке» выше:
    // есть ли простая, ничем не заслонённая прямая линия к какой-либо из точек прицеливания игрока
    // отсюда? Без математики отражения, без разрыва «реальная физика после проверки» - как раз тот
    // вид выстрела, который гарантированно сработает, если это возвращает true, в чём и весь смысл
    // отката к ней.
    bool HasDirectShot(Vector2 origin)
    {
        for (int a = 0; a < playerAimPoints.Length; a++)
        {
            if (playerAimPoints[a] == null) continue;
            Vector2 target = playerAimPoints[a].position;
            if (Vector2.Distance(origin, target) > MaxThrowRange) continue;
            if (IsLineClear(origin, target)) return true;
        }
        return false;
    }

    void FaceTowards(Vector2 worldPoint)
    {
        Vector2 toPoint = worldPoint - (Vector2)transform.position;
        if (toPoint.sqrMagnitude < 0.0001f) return;
        float targetAngle = Mathf.Atan2(toPoint.y, toPoint.x) * Mathf.Rad2Deg - 90f;
        float next = Mathf.MoveTowardsAngle(transform.eulerAngles.z, targetAngle, faceRotationSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, 0f, next);
    }

    // Проверка почти нулевой ширины - НЕ старая толстая линия «ширина игрока» (эта концепция
    // ушла, заменена 3 дискретными точками прицеливания по спецификации). Этот радиус вместо этого
    // моделирует реальный физический размер брошенного куная: действительно нулевая по ширине линия
    // может математически проскочить мимо угла, который реальный кунай реального размера всё же
    // заденет - именно это заставило брошенный кунай попасть в колонну, за которой стоял игрок,
    // несмотря на то, что линия «выглядела» чистой. queriesStartInColliders=false по-прежнему
    // действует: каст, стартующий рядом с посторонней геометрией, не должен сообщать ложную блокировку.
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

    // Та же задача, что и у IsLineClear (чист ли второй участок рикошета), но построена так, как
    // прямо попросил пользователь: уложить невидимую пробу в форме бокс-коллайдера от края до края по
    // всему участку (по центру между двумя точками, повёрнутую в соответствии, толщиной с реальный
    // кунай) и спросить у физики Unity, что реально пересекается с ней прямо сейчас - настоящий тест
    // перекрытия против тех же самых коллайдеров, которых коснётся брошенный кунай, а не охватывающий каст.
    //
    // Намеренно нет параметра ignoreCollider - вся раскладка стен уровня (внешняя граница И каждая
    // внутренняя перегородка лабиринта) запечена в ОДИН CompositeCollider2D. «Исключить конкретный
    // коллайдер, от которого отскочили» здесь бессмысленно: этот коллайдер И ЕСТЬ весь лабиринт, поэтому
    // его исключение молча исключало и все остальные стены уровня. Подтверждено вживую: рикошет
    // был признан чистым, одобрен, брошен и сразу ударился о совершенно другую стену лабиринта,
    // лежащую прямо поперёк этого самого участка - потому что та стена и стена, от которой он
    // отскочил, - буквально один и тот же C#-объект. Настоящее исправление геометрическое, а не по
    // ссылке: `from` уже должна начинаться по-настоящему вне формы композита (см. расстояние смещения у
    // вызывающего), так что эта проба никогда не нуждается ни в каких исключениях - любое перекрытие
    // вдоль участка это реальный, другой кусок стены, блокирующий выстрел.
    bool IsRicochetLegClear(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.001f) return true;

        Vector2 center = (from + to) * 0.5f;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        Vector2 boxSize = new Vector2(dist, ThrowCheckRadius * 2f);

        return Physics2D.OverlapBox(center, boxSize, angle, obstacleFilter, cornerOverlapBuffer) == 0;
    }

    // Проверяет каждый маршрут-кандидат из точки отсчёта - прямые линии к каждой из 3 точек
    // прицеливания на теле, плюс рикошет через каждую сейчас закэшированную экстраполяцию стены - и
    // возвращает тот допустимый, у которого самый короткий общий путь (пункт 4 спецификации: «смотрит
    // на кратчайший путь»).
    // logThisCheck: true только для реальной живой попытки броска (UpdateThrowing) - FindStandingPoint
    // тоже вызывает это, раз в ~секунду, из гипотетической позиции-кандидата вовсе не рядом с текущим
    // местом бойца, чтобы проверить, был бы у этого кандидата вообще выстрел. Оба вызова используют
    // один и тот же метод, и оба раньше логировали одинаково - при взгляде на консоль это выглядело как
    // проверка стены для готового вот-вот вылететь выстрела, тогда как часто это был посторонний
    // поиск позиции, и именно отсюда путаница «пишет True, а всё равно бросает», о которой сообщали
    // вживую. Теперь логирует только реальная попытка броска.
    bool TryFindBestShot(Vector2 origin, out Vector2 direction, bool logThisCheck = false)
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

        if (!enableRicochets) return found;

        for (int i = 0; i < extrapolations.Length; i++)
        {
            if (!extrapolations[i].valid) continue;
            if (TryEvaluateExtrapolation(origin, extrapolations[i], out Vector2 dir, out float length, logThisCheck) && length < bestLength)
            {
                bestLength = length;
                direction = dir;
                found = true;
            }
        }

        return found;
    }

    // Имея закэшированную экстраполяцию (точка тела, отражённая через одну из 4 граничных стен
    // уровня), вычисляет реальную точку отскока для ЭТОЙ конкретной начальной точки - где прямая от
    // начальной точки к отражённой точке пересекает реальную линию стены - и проверяет её от
    // начала до конца: точка отскока должна лежать в пределах ширины/высоты уровня (а не за углом),
    // там действительно должна быть реальная стена (а не проём/дверь - «правило отражения»), и второй
    // участок (от точки отскока до реальной цели) должен быть чист.
    bool TryEvaluateExtrapolation(Vector2 origin, Extrapolation ext, out Vector2 direction, out float pathLength, bool logThisCheck = false)
    {
        direction = default;
        pathLength = 0f;

        var aimPoint = playerAimPoints[ext.aimPointIndex];
        if (aimPoint == null) return false;

        Vector2 target = aimPoint.position;
        Vector2 mirrored = ext.mirroredPoint;
        Vector2 boundsMin = chaseStop.levelBoundsMin;
        Vector2 boundsMax = chaseStop.levelBoundsMax;
        bool horizontal = ext.wallIndex == 0 || ext.wallIndex == 1;
        Vector2 bounce;

        if (horizontal)
        {
            float wallY = ext.wallIndex == 0 ? boundsMax.y : boundsMin.y;
            float denom = mirrored.y - origin.y;
            if (Mathf.Abs(denom) < 0.0001f) return false;
            float t = (wallY - origin.y) / denom;
            if (t <= 0.02f || t >= 0.98f) return false;
            float bx = origin.x + t * (mirrored.x - origin.x);
            if (bx < boundsMin.x || bx > boundsMax.x) return false;
            bounce = new Vector2(bx, wallY);
        }
        else
        {
            float wallX = ext.wallIndex == 2 ? boundsMin.x : boundsMax.x;
            float denom = mirrored.x - origin.x;
            if (Mathf.Abs(denom) < 0.0001f) return false;
            float t = (wallX - origin.x) / denom;
            if (t <= 0.02f || t >= 0.98f) return false;
            float by = origin.y + t * (mirrored.y - origin.y);
            if (by < boundsMin.y || by > boundsMax.y) return false;
            bounce = new Vector2(wallX, by);
        }

        Vector2 toBounce = bounce - origin;
        float distToBounce = toBounce.magnitude;

        // Подтверждаем, что в точке отскока действительно стоит реальная стена (каст чуть дальше неё) -
        // проём или дверь прямо там означает нарушение правила отражения, как и собственная проверка кэша.
        // И здесь CircleCast(kunaiRadius) - первый участок это реальная траектория полёта, как и у прямого
        // выстрела, и может задеть угол, мимо которого проскользнула бы линия нулевой ширины.
        bool prevQ = Physics2D.queriesStartInColliders;
        Physics2D.queriesStartInColliders = false;
        RaycastHit2D wallHit = Physics2D.CircleCast(origin, ThrowCheckRadius, toBounce.normalized, distToBounce + 0.15f, obstacleMask);
        Physics2D.queriesStartInColliders = prevQ;
        if (wallHit.collider == null) return false;
        if (Vector2.Distance(wallHit.point, bounce) > 0.2f) return false;

        // Подтверждено вживую: одной этой проверки позиции недостаточно. Стены уровня - один
        // CompositeCollider2D, и внутренняя стена лабиринта может физически примыкать к внешней границе
        // как раз там, где попадает математическая точка отскока - попадание регистрируется практически в
        // правильной ПОЗИЦИИ (это реальный угол, лежащий ровно на линии границы), но реальный кунай
        // отражается от настоящей поверхности этого угла, а не от плоской внешней стены, которую
        // предполагала математика. Наблюдалось напрямую: выстрел, нацеленный на отскок от нижней стены и
        // возврат вверх к игроку, вместо этого вернулся перевёрнутым ПО ОБЕИМ осям, то есть ударил
        // перпендикулярную стену в том углу, а не плоскую. Реальная нормаль стены в точке попадания
        // должна совпадать с плоской стеной, которую предполагает это отражение, а не просто быть близкой
        // по позиции - нормаль угла смотрит в другую сторону, даже если он стоит точно на линии границы.
        if (Vector2.Dot(wallHit.normal, WallNormal(ext.wallIndex)) < 0.9f) return false;

        // Сдвинута далеко от собственной поверхности композитной стены - намеренно больше любого радиуса
        // проверки ниже, так что эта точка ГЕОМЕТРИЧЕСКИ гарантированно лежит вне стены, от которой
        // только что отскочили, а не просто «вероятно вне её». Эта гарантия и позволяет каждой проверке
        // дальше считать ЛЮБОЕ перекрытие реальным другим препятствием - см. IsRicochetLegClear, почему
        // попытка определить «тот же коллайдер» вообще не работает на геометрии этого уровня.
        Vector2 clearanceOrigin = bounce + WallNormal(ext.wallIndex) * 0.2f;

        // Подтверждено вживую: вторая, перпендикулярная стена может физически примыкать к границе прямо
        // в этом же углу (внутренние стены лабиринта, сходящиеся с внешней, здесь совершенно обычное
        // дело) - она не пересекает прямую линию второго участка этого выстрела, поэтому
        // IsRicochetLegClear ниже её никогда не видит, но собственная ширина реального куная и дискретные
        // физические шаги означают, что он всё равно может задеть этот угол на самом следующем шаге. Раньше
        // это было безусловной блокировкой при любом контакте так близко; теперь это вместо этого
        // поблажка, та же идея, что и обработка бросков в угол у игрока в Knife.cs - измеряем реальный
        // зазор, а не просто обнаруживаем контакт, и если он уже, чем cornerLeniencyFraction от
        // собственной длины куная, считаем это касанием, мимо которого кунай отскакивает, а не промахом,
        // и выстрел всё равно разрешаем. Смещение clearanceOrigin уже гарантирует, что он вне стены, от
        // которой только что отскочили, поэтому всё найденное здесь - реальный, другой кусок стены.
        int cornerOverlapCount = Physics2D.OverlapCircle(clearanceOrigin, kunaiLength * cornerLeniencyFraction, obstacleFilter, cornerOverlapBuffer);
        for (int i = 0; i < cornerOverlapCount; i++)
        {
            Vector2 closest = Physics2D.ClosestPoint(clearanceOrigin, cornerOverlapBuffer[i]);
            float cornerGap = Vector2.Distance(clearanceOrigin, closest);
            if (logThisCheck)
                Debug.Log($"[KunaiFighter] Corner near reflection point (gap={cornerGap:F3}, leniency={kunaiLength * cornerLeniencyFraction:F3}) - allowed anyway");
        }

        // Та же причина, что и у проверки угла выше - смещение clearanceOrigin уже гарантирует, что он
        // начинается вне поверхности каждой стены (включая ту, от которой только что отскочили), поэтому
        // здесь вообще не нужны исключения: любое перекрытие на всём участке до героя это реальный, другой
        // кусок стены, блокирующий выстрел. См. IsRicochetLegClear, почему исключение «коллайдера, от
        // которого отскочили» по ссылке на этом уровне не работает (весь лабиринт - один CompositeCollider2D).
        bool legClear = IsRicochetLegClear(clearanceOrigin, target);
        if (logThisCheck)
            Debug.Log($"[KunaiFighter] Wall between reflection point and hero? {!legClear} (bounce={clearanceOrigin} hero={target})");
        if (!legClear) return false;

        float distSecondLeg = Vector2.Distance(clearanceOrigin, target);
        if (distToBounce + distSecondLeg > MaxThrowRange) return false;

        direction = toBounce.normalized;
        pathLength = distToBounce + distSecondLeg;
        return true;
    }

    Vector2 WallNormal(int wallIndex)
    {
        switch (wallIndex)
        {
            case 0: return Vector2.down;   // нормаль верхней стены внутрь смотрит вниз, в комнату
            case 1: return Vector2.up;     // нормаль нижней стены внутрь смотрит вверх
            case 2: return Vector2.right;  // нормаль левой стены внутрь смотрит вправо
            default: return Vector2.left;  // нормаль правой стены внутрь смотрит влево
        }
    }

    // Дешёвая проверка, не зависящая от бросающего: действительно ли прямо от этой точки тела в этом
    // направлении есть стена (а не проём, где уровень продолжается в другую комнату)? Используется
    // только чтобы решить, какие комбинации (точка прицеливания, стена) стоит держать в небольшом
    // постоянном кэше - полная проверка, зависящая от позиции бросающего, происходит в
    // TryEvaluateExtrapolation в момент броска.
    bool CheckWallExists(Vector2 point, int wallIndex, out Vector2 mirrored)
    {
        Vector2 boundsMin = chaseStop.levelBoundsMin;
        Vector2 boundsMax = chaseStop.levelBoundsMax;
        Vector2 dir;
        float dist;

        switch (wallIndex)
        {
            case 0: dir = Vector2.up; dist = boundsMax.y - point.y; mirrored = new Vector2(point.x, 2f * boundsMax.y - point.y); break;
            case 1: dir = Vector2.down; dist = point.y - boundsMin.y; mirrored = new Vector2(point.x, 2f * boundsMin.y - point.y); break;
            case 2: dir = Vector2.left; dist = point.x - boundsMin.x; mirrored = new Vector2(2f * boundsMin.x - point.x, point.y); break;
            default: dir = Vector2.right; dist = boundsMax.x - point.x; mirrored = new Vector2(2f * boundsMax.x - point.x, point.y); break;
        }

        if (dist <= 0.1f) return false;

        bool prev = Physics2D.queriesStartInColliders;
        Physics2D.queriesStartInColliders = false;
        RaycastHit2D hit = Physics2D.Raycast(point, dir, dist + 0.3f, obstacleMask);
        Physics2D.queriesStartInColliders = prev;

        return hit.collider != null && Mathf.Abs(hit.distance - dist) < 0.3f;
    }

    bool IsComboActive(int aimIndex, int wallIndex)
    {
        for (int i = 0; i < extrapolations.Length; i++)
            if (extrapolations[i].valid && extrapolations[i].aimPointIndex == aimIndex && extrapolations[i].wallIndex == wallIndex)
                return true;
        return false;
    }

    bool TryFindNewExtrapolation(out Extrapolation result)
    {
        for (int a = 0; a < playerAimPoints.Length; a++)
        {
            if (playerAimPoints[a] == null) continue;
            for (int w = 0; w < 4; w++)
            {
                if (IsComboActive(a, w)) continue;
                if (CheckWallExists(playerAimPoints[a].position, w, out Vector2 mirrored))
                {
                    result = new Extrapolation { valid = true, aimPointIndex = a, wallIndex = w, mirroredPoint = mirrored };
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    // Держит не более 4 экстраполяций живыми (пункт 6 спецификации): перепроверяет каждый активный
    // слот каждый кадр (дёшево - один рейкаст на слот) и сразу восполняет любые сломавшиеся, вместо
    // ожидания периодического перебора.
    void RefreshExtrapolations()
    {
        for (int i = 0; i < extrapolations.Length; i++)
        {
            if (!extrapolations[i].valid) continue;
            var aimPoint = playerAimPoints[extrapolations[i].aimPointIndex];
            if (aimPoint == null || !CheckWallExists(aimPoint.position, extrapolations[i].wallIndex, out Vector2 mirrored))
                extrapolations[i].valid = false;
            else
                extrapolations[i].mirroredPoint = mirrored;
        }

        for (int i = 0; i < extrapolations.Length; i++)
        {
            if (extrapolations[i].valid) continue;
            if (TryFindNewExtrapolation(out Extrapolation fresh)) extrapolations[i] = fresh;
        }
    }

    void FireKunai(Vector2 origin, Vector2 direction)
    {
        if (kunaiPrefab == null) return;
        GameObject obj = Instantiate(kunaiPrefab, origin, Quaternion.identity);
        var kunai = obj.GetComponent<Kunai>();
        kunai.speed = kunaiSpeed;
        kunai.Launch(direction);
    }
}
