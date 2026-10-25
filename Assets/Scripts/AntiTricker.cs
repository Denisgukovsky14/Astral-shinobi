using UnityEngine;
using Pathfinding;

// Противодействует классическому «кружению вокруг укрытия»: враг, который лишь гонится за текущей
// позицией игрока, будет бесконечно плестись позади того, кто обегает одно и то же препятствие
// с похожей угловой скоростью - он всегда на пол-круга позади и никогда реально не догоняет.
// Отслеживает, насколько игрок повернулся вокруг того препятствия, рядом с которым он сейчас
// находится; как только это превышает настраиваемый порог, враг фиксирует ОДИН рывок по дуге
// вокруг опорного препятствия (как ближняя половина эллипса, прижатая к его краю) к точке,
// диаметрально ПРОТИВОПОЛОЖНОЙ игроку на его текущей орбите - не гонится за ним (это всегда
// короткий путь, всё равно отстающий в ту сторону, куда кружат), а идёт длинным путём вокруг,
// чтобы встретить его лоб в лоб, пока он продолжает двигаться навстречу. И это не двухфазный
// «разворот, потом удар» - просто один быстрый изогнутый перехват: двухфазная версия (обход на
// дальнюю сторону, затем второй рывок обратно) была опробована и упрощена после повторных живых
// тестов, показавших, что игрок воспринимает второе обязательство как дополнительный шанс
// увернуться. Версия с прямым рывком на игрока тоже была опробована и оставлена закомментированной
// в StartDash() на потом - она хорошо рвёт вперёд, но реальный бросок по прямой в цель, которая ещё
// близко и движется, реалистично привёл бы к спотыканию или замешательству; это правильная форма
// для короткого добивающего удара вблизи, а не для сближения на большую дистанцию, пока игрок кружит.
public class AntiTricker : MonoBehaviour
{
    public Transform player;
    public LayerMask obstacleMask;

    [Tooltip("Насколько долго игрок должен покружить вокруг препятствия, прежде чем враг среагирует. 1 = почти мгновенно, 3 = даёт больше всего покружить. Дробные значения (например 1.5) считаются как среднее между соседними порогами из mercyThresholdDegrees.")]
    [Range(1f, 3f)]
    public float mercyLevel = 2f;

    [Tooltip("Порог срабатывания в градусах суммарного накопленного угла вокруг препятствия, один порог на каждый уровень мягкости (индекс 0 = уровень 1). 360° = один полный круг.")]
    public float[] mercyThresholdDegrees = { 70f, 540f, 900f };

    [Tooltip("Насколько близко должно быть препятствие (слой Obstacles) к игроку, чтобы враг вообще начал считать его 'тем, вокруг чего кружат'.")]
    public float pivotSearchRadius = 3f;

    [Tooltip("Максимальный размер препятствия (по любой из осей его bounds), чтобы оно вообще годилось на роль 'того, вокруг чего кружат'. Колонны ~1x1 юнит проходят, длинные стены (например 10x0.19) - нет: у стены нет смысла в дуге вокруг неё, а её transform.position может быть далеко от игрока вдоль стены, из-за чего рывок улетал непонятно куда.")]
    public float maxPivotSize = 1.5f;

    [Tooltip("Максимальная длительность рывка (сек) - если враг не долетел раньше, рывок всё равно закончится по истечении этого времени.")]
    public float dashDuration = 0.8f;

    [Tooltip("Скорость (градусы/сек), с которой накопленный угол кружения затухает, если игрок сейчас не кружит - не даёт старому кружению 'зависнуть в памяти' и сработать позже на пустом месте.")]
    public float angleDecayPerSecond = 25f;

    [Tooltip("Во сколько раз быстрее обычной скорости враг пролетает дугу рывка (реальная длительность рывка считается из длины дуги и этой скорости, dashDuration - лишь потолок сверху). Игрок бежит всегда с одной и той же скоростью - при таком множителе враг гарантированно догоняет, даже если игрок сразу заметит рывок и попробует отбежать.")]
    public float dashSpeedMultiplier = 6f;

    [Tooltip("Ускорение (ai.maxAcceleration) на время рывка. По умолчанию AIPath сам разгоняется до максимума примерно за 0.4 секунды НЕЗАВИСИМО от того, насколько высокий maxSpeed - именно это, а не сам предел скорости, было реальным узким местом. Большое значение здесь заставляет врага набирать полную скорость почти мгновенно.")]
    public float dashAcceleration = 200f;

    [Tooltip("Скорость поворота (градусы/сек) во время рывка - большая, но не бесконечная, чтобы начальный разворот был почти мгновенным, но при этом не 'телепортировал' угол на каждом мелком изломе дуги.")]
    public float snapRotationSpeed = 2000f;

    [Tooltip("На каком расстоянии (юниты) до точки назначения рывок считается завершённым (враг долетел).")]
    public float arrivalDistance = 0.3f;

    [Tooltip("На сколько юнитов дуга рывка проходит дальше от препятствия, чем сама текущая орбита врага/игрока вокруг него. Дуга огибает столб примерно по этому радиусу - слишком маленькое значение может задеть препятствие, слишком большое сделает рывок неоправданно длинным крюком.")]
    public float curveClearance = 0.5f;

    [Tooltip("Если живой игрок отбежал от точки, куда сейчас летит рывок, дальше этого расстояния (юниты) - рывок немедленно отменяется, враг сразу возвращается к обычному чейзу вместо того, чтобы долетать и бить мечом в пустое место.")]
    public float maxPlayerEscapeDistance = 3f;

    [Tooltip("ЭКСПЕРИМЕНТ: если включено, сразу по окончании первого рывка (к противоположной точке) враг делает ВТОРОЙ рывок, тоже по дуге, из точки, где закончился первый - на живую позицию игрока в этот момент. Причина: растягивать одну дугу на всю дистанцию сразу выглядит неестественно и всё ещё цепляет колонну; два более коротких прыжка подряд выглядят реалистичнее и дают дуге больше шансов обогнуть препятствие.")]
    public bool secondArcLeg = true;

    private bool dashing;
    private bool onSecondLeg;
    public bool IsCountering => dashing;

    private AIDestinationSetter destinationSetter;
    private AIPath ai;
    private Rigidbody2D rb;
    private EnemyChaseStop chaseStop;
    private EnemyAttackZone attackZone;
    private CombatAgent combatAgent;
    private float previousArcT;
    private Transform pivotObstacle;
    private float lastPlayerAngle;
    private float accumulatedAngle;
    private float dashTimer;
    private float effectiveDashDuration;
    private Vector2 triggerPlayerPos;
    private Vector2 escapeReferencePos;
    private float capturedMaxSpeed;
    private float capturedMaxAcceleration;
    private AutoRepathPolicy capturedAutoRepath;
    private Vector2 curveStart;
    private Vector2 curveEnd;
    private bool arcHasPivot;
    private Vector2 arcPivotPos;
    private float arcStartAngle;
    private float arcAngleDelta;
    private float arcR0;
    private float arcR1;
    private float arcBowHeight;

    void Awake()
    {
        destinationSetter = GetComponent<AIDestinationSetter>();
        ai = GetComponent<AIPath>();
        rb = GetComponent<Rigidbody2D>();
        chaseStop = GetComponent<EnemyChaseStop>();
        attackZone = GetComponent<EnemyAttackZone>();
        combatAgent = GetComponent<CombatAgent>();
    }

    void Update()
    {
        if (dashing) { UpdateDash(); return; }

        Transform nearestObstacle = FindNearestObstacle(player.position);
        if (nearestObstacle != pivotObstacle)
        {
            pivotObstacle = nearestObstacle;
            accumulatedAngle = 0f;
            if (pivotObstacle != null) lastPlayerAngle = AngleAround(pivotObstacle.position, player.position);
            return;
        }

        if (pivotObstacle == null) return;

        float currentAngle = AngleAround(pivotObstacle.position, player.position);
        float delta = Mathf.DeltaAngle(lastPlayerAngle, currentAngle);
        lastPlayerAngle = currentAngle;

        accumulatedAngle += delta;
        accumulatedAngle = Mathf.MoveTowards(accumulatedAngle, 0f, angleDecayPerSecond * Time.deltaTime);

        // Координация: ближний боец без токена не имеет права на рывок (он ждёт на расстоянии).
        if (Mathf.Abs(accumulatedAngle) >= GetThreshold() && (combatAgent == null || combatAgent.AllowsCounterDash)) StartDash();
    }

    float GetThreshold()
    {
        int maxIndex = mercyThresholdDegrees.Length - 1;
        float level = Mathf.Clamp(mercyLevel, 1f, maxIndex + 1);
        int lower = Mathf.Clamp(Mathf.FloorToInt(level) - 1, 0, maxIndex);
        int upper = Mathf.Clamp(lower + 1, 0, maxIndex);
        float t = level - Mathf.Floor(level);
        return Mathf.Lerp(mercyThresholdDegrees[lower], mercyThresholdDegrees[upper], t);
    }

    void StartDash()
    {
        dashing = true;
        onSecondLeg = false;
        dashTimer = 0f;
        accumulatedAngle = 0f;
        if (destinationSetter != null) destinationSetter.enabled = false;

        // Фиксируется один раз, здесь, и больше не перечитывается до конца рывка - собственная
        // скорость игрока не может перегнать преимущество рывка по скорости за время приземления, так что
        // отслеживать его в полёте не нужно (раньше именно ради этого существовала вторая фаза: реагировать
        // на то, куда игрок успел сместиться, пока медленный рывок за ним тянулся. Этот достаточно быстр,
        // поэтому одной зафиксированной цели хватает).
        triggerPlayerPos = player.position;

        capturedMaxSpeed = ai.maxSpeed;
        capturedMaxAcceleration = ai.maxAcceleration;
        capturedAutoRepath = ai.autoRepath;
        ai.enableRotation = false;
        ai.canMove = false;
        ai.maxSpeed = capturedMaxSpeed * dashSpeedMultiplier;
        ai.maxAcceleration = dashAcceleration;
        // См. аналогичный комментарий, который раньше стоял рядом в StartFinishingDash: собственный
        // autoRepath у AIPath запускает фоновый перепоиск пути просто по таймеру, независимо от того,
        // сдвинулась ли цель - здесь это всё равно неважно, так как движение полностью ручное (canMove
        // false на весь рывок), но на время рывка он отключён, чтобы ничто другое не трогало состояние
        // построения путей, пока этот скрипт владеет движением.
        ai.autoRepath = new AutoRepathPolicy { mode = AutoRepathPolicy.Mode.Never };

        Vector2 fromHere = rb.position;

        // ВЕРСИЯ «РЫВОК НА ИГРОКА» (работает хорошо, оставить на потом - пользователь прямо попросил не
        // удалять, а просто отложить): целится точно туда, где игрок стоял в момент срабатывания. Хороший
        // рывок «сократить дистанцию и ударить», когда уже близко, но довод пользователя остаётся в силе:
        // живой человек не бросается по прямой на цель, которая ещё близко и движется - он бы споткнулся
        // или замешкался. Это правильная форма для короткого добивающего удара, а не для сближения на
        // большую дистанцию, пока игрок кружит. Ограничение дистанции здесь не нужно, так как цель всегда -
        // просто реальная позиция игрока, а не вычисленная точка, которая может оказаться неверной.
        // Vector2 target = triggerPlayerPos;

        // ВЕРСИЯ «ВСТРЕТИТЬ С ПРОТИВОПОЛОЖНОЙ СТОРОНЫ» (активна): та же геометрия, что проверялась ранее
        // в этой сессии для первой ноги двухфазного дизайна - точка, диаметрально противоположная игроку
        // на его текущей орбите вокруг опоры. Короткий путь (рывок прямо на игрока) всегда означает
        // догон сзади, всё равно отставая в ту сторону, куда он кружит; длинный путь вокруг означает
        // прибытие с *другой* стороны и встречу лоб в лоб, пока он продолжает двигаться навстречу, вместо
        // погони за его хвостом. Знак направления кружения не нужен - «диаметрально противоположная»
        // определена независимо от того, в какую сторону он кружил, и привязано это к углу ИГРОКА вокруг
        // опоры (а не самого врага, который численно нестабилен именно тогда, когда враг прижат к
        // препятствию - а обычно срабатывает как раз тогда).
        Vector2 target = triggerPlayerPos;
        if (pivotObstacle != null)
        {
            float playerAngle = AngleAround(pivotObstacle.position, triggerPlayerPos);
            float radius = Vector2.Distance(pivotObstacle.position, triggerPlayerPos);
            float oppositeAngle = playerAngle + 180f;
            Vector2 dir = new Vector2(Mathf.Cos(oppositeAngle * Mathf.Deg2Rad), Mathf.Sin(oppositeAngle * Mathf.Deg2Rad));
            target = (Vector2)pivotObstacle.position + dir * radius;
        }

        escapeReferencePos = triggerPlayerPos;
        BeginLeg(fromHere, target);
    }

    // ЭКСПЕРИМЕНТ: срабатывает сразу, когда первая нога прибыла, вместо завершения манёвра - вторая
    // дуга, заново посчитанная с нуля (свой t=0..1, а не продолжение первой), стартующая оттуда, где
    // первая нога реально закончилась, и нацеленная на ЖИВУЮ позицию игрока в этот момент. Две
    // коротких дуги подряд выглядят лучше, чем одна длинная, растянутая на ту же общую дистанцию,
    // и каждая отдельная дуга получает свой честный шанс обогнуть препятствие, вместо того чтобы
    // требовать от одной длинной дуги сделать всё за один взмах.
    void StartSecondLeg()
    {
        onSecondLeg = true;
        dashTimer = 0f;

        Vector2 fromHere = rb.position;
        Vector2 target = player.position;
        // Проверка побега для этой ноги должна реагировать на то, что игрок убегает от цели ИМЕННО ЭТОЙ
        // ноги, а не от позиции в момент срабатывания (она теперь устарела - одна первая нога может
        // занимать заметную долю секунды, достаточно, чтобы кружащий игрок законно ушёл дальше, и это
        // не должно считаться «побегом»).
        escapeReferencePos = target;

        BeginLeg(fromHere, target);
    }

    // Общее для обеих ног: фиксирует геометрию дуги и длительность, выведенную из скорости, для рывка
    // из `from` в `to`. Выделено, когда второй ноге понадобилась точно такая же настройка, стартующая
    // из другой точки.
    void BeginLeg(Vector2 from, Vector2 to)
    {
        ai.destination = to;
        curveStart = from;
        curveEnd = to;
        previousArcT = 0f;
        PrepareArc(from, to);

        // Движение во время рывка полностью ручное (ai.Teleport, управляется dashTimer/duration - см.
        // UpdateMovement) и вообще никогда не обращается к ai.maxSpeed, поэтому без этого
        // dashSpeedMultiplier был бы чистым украшением, и каждый рывок занимал бы ровно dashDuration
        // независимо от дистанции - нормально для коротких прыжков, под которые это настраивалось, но
        // по-настоящему длинный рывок (враг ещё подходит через всю комнату, когда обнаруживается кружение)
        // либо полз бы в замедленной съёмке, либо, при коротком времени, преодолевал бы расстояние так
        // быстро, что читался бы как мгновенная телепортация. Вместо этого реальная длительность
        // выводится из длины самой дуги и целевой скорости, так что рывок движется примерно с одной и
        // той же ощущаемой скоростью независимо от дистанции - dashDuration становится потолком (как и
        // говорит его собственная подсказка), а не обычным случаем.
        float arcLength = EstimateArcLength();
        float targetSpeed = capturedMaxSpeed * dashSpeedMultiplier;
        effectiveDashDuration = targetSpeed > 0.01f ? Mathf.Clamp(arcLength / targetSpeed, 0.1f, dashDuration) : dashDuration;

        UpdateMovement();
    }

    // Настраивает настоящую полярную развёртку вокруг опоры вместо кривой Безье. Квадратичная
    // Безье остаётся лишь внутри *треугольника* своих трёх контрольных точек - она не описывает
    // настоящую круговую дугу, поэтому даже при контрольной точке, отодвинутой далеко за пределы
    // препятствия, кривая всё равно может на середине вернуться ближе к опоре, чем задумано
    // (подтверждено вживую: «рывок идёт в нужном направлении, просто иногда бьётся о колонну»).
    // Полярная развёртка - более сильная гарантия: угол вокруг опоры интерполируется линейно
    // (кратчайшим путём, через DeltaAngle), а радиус - гладкая выпуклость, равная ровно r0/r1 на
    // концах (совпадая с реальными расстояниями врага и цели, так что нет скачка в начале или в конце)
    // и достигающая пика комфортно за пределами препятствия в середине - поскольку радиус только
    // растёт от того конца, чьё расстояние было меньше, ближайшая к опоре точка всей траектории -
    // это t=0 или t=1 (там, где враг/цель реально уже находятся), но никогда не между ними, что как
    // раз и нужно для настоящего огибания препятствия.
    void PrepareArc(Vector2 from, Vector2 to)
    {
        arcHasPivot = pivotObstacle != null;
        if (!arcHasPivot) return;

        arcPivotPos = pivotObstacle.position;
        arcStartAngle = AngleAround(arcPivotPos, from);
        float endAngle = AngleAround(arcPivotPos, to);
        arcAngleDelta = Mathf.DeltaAngle(arcStartAngle, endAngle);
        arcR0 = Vector2.Distance(arcPivotPos, from);
        arcR1 = Vector2.Distance(arcPivotPos, to);

        // Выпуклость ровно такая, чтобы радиус в середине превышал больший из радиусов концов на
        // curveClearance - если начало и конец уже и так вращаются на одной и той же дистанции
        // (обычный случай), это сводится к ровно curveClearance сверх этого общего радиуса; если враг
        // начал далеко от опоры, она немного растёт для компенсации, не раздувая всю дугу до той дальней
        // дистанции, как это делал старый радиус контрольной точки.
        float midpointRadius = Mathf.Lerp(arcR0, arcR1, 0.5f);
        arcBowHeight = Mathf.Max(arcR0, arcR1) + curveClearance - midpointRadius;
    }

    // Дешёвая оценка длины дуги для темпа (см. effectiveDashDuration) - суммирует прямые отрезки
    // между несколькими точками, снятыми вдоль реальной дуги; достаточно близко для рывка короче
    // секунды, без замкнутого интеграла длины полярной дуги.
    float EstimateArcLength()
    {
        const int samples = 8;
        Vector2 prev = EvaluateArc(0f);
        float length = 0f;
        for (int i = 1; i <= samples; i++)
        {
            Vector2 cur = EvaluateArc((float)i / samples);
            length += Vector2.Distance(prev, cur);
            prev = cur;
        }
        return length;
    }

    Vector2 EvaluateArc(float t)
    {
        if (!arcHasPivot) return Vector2.Lerp(curveStart, curveEnd, t);

        float radius = Mathf.Lerp(arcR0, arcR1, t) + arcBowHeight * Mathf.Sin(Mathf.PI * t);
        float angle = arcStartAngle + arcAngleDelta * t;
        Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        return arcPivotPos + dir * radius;
    }

    void UpdateDash()
    {
        if (PlayerEscaped()) { EndDash(); return; }

        // Игрок оказался в зоне удара посреди рывка (проскочил перед носом, пока враг летит к
        // старой точке): рывок обрывается, враг возвращается к обычному поведению и просто бьёт.
        if (attackZone != null && attackZone.PlayerWithinRange()) { EndDash(); return; }

        dashTimer += Time.deltaTime;
        UpdateMovement();

        bool arrived = Vector2.Distance(rb.position, curveEnd) < arrivalDistance;
        if (dashTimer >= effectiveDashDuration || arrived)
        {
            if (secondArcLeg && !onSecondLeg) StartSecondLeg();
            else EndDash();
        }
    }

    void EndDash()
    {
        dashing = false;
        onSecondLeg = false;
        ai.enableRotation = true;
        ai.canMove = true;
        ai.isStopped = false; // остановка по касанию могла быть выставлена до рывка, а её сброс при выходе из зоны во время рывка пропущен
        ai.maxSpeed = capturedMaxSpeed;
        ai.maxAcceleration = capturedMaxAcceleration;
        ai.autoRepath = capturedAutoRepath;
        if (destinationSetter != null) destinationSetter.enabled = true;

        // Во всём рывке направление взгляда намеренно смотрит вдоль касательной самой кривой (см.
        // UpdateMovement/FaceDirection), то есть вдоль направления движения, а не в стандартную боевую
        // ориентацию, которую проверяет EnemyAttackZone (transform.up). Мгновенно поворачиваемся лицом
        // к игроку в момент окончания рывка, чтобы не было паузы между «прибыл» и «правильно
        // ориентирован, чтобы реально нанести удар».
        SnapToFace(player.position);

        // Отслеживание угла приостанавливается на весь рывок (Update() уходит в UpdateDash() вместо
        // этого), но игрок всё это время продолжает кружить. Без этого сброса отслеживание возобновилось
        // бы и сразу увидело один огромный накопившийся скачок, мгновенно пересекло бы порог снова и
        // сработало бы прямо подряд - именно это и оказалось «волчком», когда баг впервые проявился.
        accumulatedAngle = 0f;
        if (pivotObstacle != null) lastPlayerAngle = AngleAround(pivotObstacle.position, player.position);
    }

    // Каждый кадр: ставит агента туда, где должна быть полярная дуга (см. PrepareArc/EvaluateArc)
    // от начала рывка до его зафиксированной цели при текущем прогрессе рывка. Использует
    // ai.Teleport (сразу пишет simulatedPosition), а не ai.Move: Move() лишь накапливает дельту,
    // которую применяет собственный FinalizeMovement у AIPath, а этот шаг заблокирован за
    // canMove - при canMove false на весь рывок дельта каждого кадра копилась бы неиспользованной,
    // и вся накопленная очередь выплеснулась бы в момент возврата canMove в true (уже подтверждено
    // один раз в этой сессии: позиции на десятки юнитов в стороне появлялись именно так). Teleport
    // пишет позицию напрямую и вообще не зависит от canMove.
    //
    // Рывок движется в несколько раз быстрее обычного преследования, поэтому один кадр может
    // покрыть большой кусок дуги - проверка только начальной и конечной точек кадра по прямой
    // (то, что делает собственная защита от туннелирования EnemyChaseStop при обычном движении)
    // может пропустить стену, через которую проходит настоящая КРИВАЯ, но которой никогда не касается
    // прямая хорда между этими двумя концами. Вместо этого берём несколько точек вдоль настоящей
    // кривой между прогрессом прошлого кадра и этого и проверяем каждый меньший подотрезок отдельно
    // той же защитой, что использует обычное движение - подтверждено вживую: именно эта дыра
    // позволяла врагу пролетать насквозь через стену в рывке, чего проверка начала/конца по прямой
    // никогда не отмечала.
    void UpdateMovement()
    {
        float t = effectiveDashDuration > 0.0001f ? Mathf.Clamp01(dashTimer / effectiveDashDuration) : 1f;

        const int subSteps = 6;
        Vector2 point = rb.position;
        float reachedT = previousArcT;
        for (int i = 1; i <= subSteps; i++)
        {
            float subT = Mathf.Lerp(previousArcT, t, (float)i / subSteps);
            Vector2 nextPoint = EvaluateArc(subT);
            Vector2 corrected = chaseStop != null ? chaseStop.CheckAndCorrectTunnel(point, nextPoint) : nextPoint;
            if (corrected != nextPoint)
            {
                // Что-то задели на этом подотрезке - прекращаем двигаться дальше в этом кадре, а не
                // проскакиваем это. dashTimer продолжает тикать независимо, так что рывок, физически заблокированный
                // таким образом, просто нормально истекает по времени, а не туннелирует.
                point = corrected;
                break;
            }
            point = nextPoint;
            reachedT = subT;
        }
        previousArcT = reachedT;

        ai.Teleport(point, false);

        // Касательная через малый шаг вперёд численно - проще и менее подвержено ошибкам, чем
        // дифференцировать полярные выражения радиуса/угла вручную, и достаточно точно для управления
        // направлением взгляда.
        Vector2 tangent = EvaluateArc(Mathf.Min(1f, reachedT + 0.02f)) - point;
        FaceDirection(tangent);
    }

    // Проверяется каждый кадр какой бы ноги ни было: если живой игрок убежал далеко от
    // escapeReferencePos - позиции, на которой основана цель ЭТОЙ ноги (triggerPlayerPos для первой
    // ноги, живой снимок в начале второй) - произошло что-то неожиданное, и завершение было бы
    // нацелено на решение, которое уже неактуально. Намеренно никогда не сравнивается с
    // ai.destination - противоположная цель первой ноги по построению намеренно далека от игрока,
    // поэтому сравнение с ней заставляло бы это срабатывать на каждом рывке.
    bool PlayerEscaped()
    {
        return Vector2.Distance(player.position, escapeReferencePos) > maxPlayerEscapeDistance;
    }

    // Мгновенная боевая коррекция направления (transform.up на цель) - стандартная конвенция «-90»,
    // используемая во всём остальном проекте (EnemyChaseStop.FaceTarget и т. д.), в отличие от
    // «боковой» конвенции FaceDirection, действующей только на рывке.
    void SnapToFace(Vector2 target)
    {
        Vector2 toTarget = target - rb.position;
        if (toTarget.sqrMagnitude < 0.0001f) return;
        rb.rotation = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - 90f;
    }

    void FaceDirection(Vector2 toTarget)
    {
        // Намеренно нетривиальная отсечка - чем ближе к нулю, тем сильнее малейшее дрожание позиции
        // раскачивает получающийся угол.
        if (toTarget.sqrMagnitude < 0.01f) return;
        // Лицом по ходу рывка подставляется широкая ось капсулы (0.86, локальный X) на пути всего, что
        // встретится. Отказ от обычной «-90» лицевой коррекции направляет локальный X вдоль направления
        // рывка - тело идёт боком, узким краем вперёд.
        float angle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;
        rb.MoveRotation(Mathf.MoveTowardsAngle(rb.rotation, angle, snapRotationSpeed * Time.deltaTime));
    }

    Transform FindNearestObstacle(Vector2 point)
    {
        Collider2D[] hits = Physics2D.OverlapCircleAll(point, pivotSearchRadius, obstacleMask);
        Transform nearest = null;
        float bestDist = float.MaxValue;
        foreach (var h in hits)
        {
            // Длинные граничные стены (например 10x0.19 юнита) технически могут оказаться «ближайшими по
            // расстоянию до transform.position», когда игрок где-то около их середины - даже вплотную
            // к поверхности стены - несмотря на то, что эта точка не имеет отношения к тому, где игрок
            // реально стоит относительно стены. Опорой для дуги годятся только компактные препятствия
            // размером с колонну.
            Bounds b = h.bounds;
            if (b.size.x > maxPivotSize || b.size.y > maxPivotSize) continue;

            float d = Vector2.Distance(point, h.transform.position);
            if (d < bestDist) { bestDist = d; nearest = h.transform; }
        }
        return nearest;
    }

    float AngleAround(Vector2 center, Vector2 point)
    {
        Vector2 d = point - center;
        return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
    }
}
