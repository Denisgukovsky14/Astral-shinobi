using UnityEngine;
using Pathfinding;

public class EnemyChaseStop : MonoBehaviour
{

    // Эксперименты 

    [Header("Dash on stuck")]
    public float dashDistance = 2.5f;          // длина рывка
    public float dashCooldown = 0.8f;          // интервал между рывками
    private float lastDashTime = -10f;

    public Collider2D playerTouchZone;
    public LayerMask obstacleMask;
    public float stuckCheckInterval = 0.3f;
    public float stuckDistanceThreshold = 0.05f;
    public float faceRotationSpeed = 360f;

    private IAstarAI ai;
    private AIPath aiPath;
    private Rigidbody2D rb;
    private Collider2D col;
    private CapsuleCollider2D capsuleCol;
    private AntiTricker antiTricker;
    private EnemyAttackZone attackZone;
    // Дальнобойные юниты сами владеют ai.canMove (выставляют каждый кадр) - остановка по касанию для них не нужна
    // и конфликтует: попав в зону касания игрока, лучник/кунайщик навсегда застревал с canMove=false.
    private bool isRangedUnit;
    private CombatAgent combatAgent;
    private Vector2 lastCheckedPosition;
    private float stuckTimer;
    private bool insideTouchZone;
    public float slideTurnSpeed = 12f; // радианы/сек - как быстро может меняться направление скольжения
    [Tooltip("Extra multiplier on the sideways slide-along-the-surface push, on top of the agent's current speed. The high-speed dashes cover a lot of ground per frame, so the plain speed-scaled push often isn't enough lateral distance to actually clear an obstacle it clips mid-dash - it just grinds along instead of getting past.")]
    public float slidePushMultiplier = 2.5f;

    [Tooltip("Hard cap (units/sec) on the slide push's actual speed, applied AFTER slidePushMultiplier. The push scales with the agent's current ai.maxSpeed, which briefly spikes several times higher during an AntiTricker dash - uncapped, a dash that gets stuck against an obstacle can slide at 100+ units/sec for as long as contact lasts, which is what flung the enemy off the level once. This caps it back to roughly the original, pre-dash-speed-increases feel regardless of how high any dash boosts maxSpeed. (In practice this whole slide/depenetration path is now skipped entirely while AntiTricker is countering - kept as a safety margin for normal, non-dash contact.)")]
    public float maxSlideSpeed = 20f;

    [Tooltip("Bottom-left and top-right corners of the playable area, with a small margin inside the boundary walls. Whatever fires this fast-moving Kinematic enemy into a wall/player collision hard enough to launch it - the exact physics mechanism has been elusive to pin down precisely, tried multiple targeted fixes already - this is a blunt, unconditional last resort: if the enemy is ever found outside this box, snap it straight back inside, every single frame, regardless of cause.")]
    public Vector2 levelBoundsMin = new Vector2(-14.7f, -3.3f);
    public Vector2 levelBoundsMax = new Vector2(4.8f, 10.3f);

    [Tooltip("Caps how fast the manual push-out (below) may move the agent, in units/sec. A deep overlap (e.g. from a dash ending inside geometry) used to get shoved out fully in a single frame, which read as a hard bounce off the wall - this spreads that same correction over a few frames instead, so it reads as sliding to the surface rather than being kicked off it.")]
    public float maxDepenetrationSpeed = 6f;

    [Tooltip("How far (degrees) the enemy may turn its shoulder away from its normal facing to shrink its own silhouette and squeeze through a passage narrower than its collider - same idea as a person turning sideways in a tight doorway. Only kicks in once genuinely stuck (in contact AND not making progress), never during ordinary flow-around-an-obstacle contact.")]
    public float maxSqueezeAngle = 20f;
    public float squeezeRotationSpeed = 150f;

    private readonly Collider2D[] overlapBuffer = new Collider2D[8];
    private readonly RaycastHit2D[] tunnelHitsBuffer = new RaycastHit2D[8];
    private ContactFilter2D obstacleFilter;
    private bool inContact;
    private bool blockedForSqueeze;
    private float squeezeAngle;
    private float squeezeSign = 1f;
    private Vector3 slideDir;
    private Vector2 previousFramePosition;
    private readonly Collider2D[] finalOverlapBuffer = new Collider2D[8];

    // Предназначено вызываться только сразу после внешней телепортации этого агента (инструменты
    // редактора, логика спавна и т. д.) - защита от туннелирования ниже отслеживает движение между
    // кадрами через previousFramePosition, которое обновляется только изнутри её собственного
    // LateUpdate. Телепорт извне этого потока оставляет его устаревшим, поэтому дельта самого
    // следующего реального кадра (старое запомненное место -> новое телепортированное) читается как
    // огромный, возможно пересекающий стену скачок, и защита «исправляет» его обратно к старой позиции -
    // реальный, многократно подтверждённый артефакт тестирования, не игровой баг, но стоящий того,
    // чтобы исправить его один раз как следует, вместо того чтобы заново открывать его в каждом будущем
    // тесте, телепортирующем агента такого типа.
    public void SyncPositionTracking()
    {
        previousFramePosition = ai.position;
    }

    [Tooltip("How deep an overlap with an obstacle has to be, at the very end of a frame, before the guaranteed last-resort push-out below kicks in. Set well above what ordinary sliding contact ever produces (a few hundredths of a unit) so it only fires on a genuine embed - ending up partway or fully inside a wall - never on normal grazing contact.")]
    public float emergencyDepenetrationThreshold = 0.1f;

    [Tooltip("If the tunneling guard (below) corrects the agent this many times within tunnelStreakResetGap seconds of each other, it's not a one-off clip - AIPath's own steering is repeatedly aiming it straight through the same thin obstacle every frame, getting slapped back every time without ever picking a different route (this reads as the enemy freezing in place for a moment, since each correction resets AIPath's internal velocity tracking). Forcing a fresh path search breaks that loop by making it consider a genuinely different route instead of retrying the identical blocked step.")]
    public int tunnelRepathThreshold = 3;
    public float tunnelStreakResetGap = 0.5f;
    private int consecutiveTunnelHits;
    private float lastTunnelHitTime;


    void Awake()
    {
        ai = GetComponent<IAstarAI>();
        aiPath = GetComponent<AIPath>();
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<Collider2D>();
        capsuleCol = col as CapsuleCollider2D;
        antiTricker = GetComponent<AntiTricker>();
        attackZone = GetComponent<EnemyAttackZone>();
        isRangedUnit = GetComponent<KunaiFighter>() != null || GetComponent<ArcherFighter>() != null;
        combatAgent = GetComponent<CombatAgent>();
        lastCheckedPosition = transform.position;
        previousFramePosition = rb.position;

        obstacleFilter = new ContactFilter2D();
        obstacleFilter.SetLayerMask(obstacleMask);
        obstacleFilter.useTriggers = false;
    }

    // Прогоняет собственную капсулу агента вдоль прямого отрезка из `from` в `to` и, если этот
    // отрезок пересёк бы препятствие, возвращает точку, оттянутую назад чуть не доходя до пересечения,
    // вместо `to`. Общий с движением рывка AntiTricker (см. там UpdateMovement) - рывок идёт по
    // изогнутой дуге, а не по прямой, и один большой покадровый скачок вдоль этой дуги может вылезти
    // туда, где прямая хорда между её началом и концом никогда не коснётся, поэтому сам рывок вызывает
    // это несколько раз за кадр по нескольким точкам, снятым вдоль собственной кривой, а не один раз
    // на всё движение кадра, как делает обычное преследование.
    public Vector2 CheckAndCorrectTunnel(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.001f || capsuleCol == null) return to;

        Vector2 dir = delta / dist;
        int hitCount = Physics2D.CapsuleCast(from, capsuleCol.size, capsuleCol.direction,
            rb.rotation, dir, obstacleFilter, tunnelHitsBuffer, dist);
        float closest = float.MaxValue;
        bool tunneled = false;
        for (int i = 0; i < hitCount; i++)
        {
            // Фильтрация по дистанции попадания (игнорировать всё около 0 как «уже касается, а не пересекает»)
            // выглядела правильно, но ломала как раз самый важный случай: после того как одна коррекция
            // ставит агента вплотную к поверхности стены, движение следующего кадра начинает тот же
            // кадровый прогон уже касаясь её - CapsuleCast правильно сообщает это попадание на дистанции ~0,
            // а фильтр по дистанции выбрасывал его как «просто контакт покоя», позволяя агенту пройти прямо
            // сквозь стену, к которой он уже был прижат. Подтверждено вживую: серия нескольких реальных
            // пересечений у стены лабиринта, все с попаданием CapsuleCast ровно в 0.0000. Что на самом деле
            // отличает настоящее пересечение от безвредного контакта покоя/скольжения - не то, как далеко
            // вдоль прогона легло попадание, а то, по какую сторону от собственной поверхности этого попадания
            // оказывается точка назначения: позади нормали (внутри препятствия) означает, что этот отрезок
            // прошёл сквозь него независимо от дистанции попадания; всё ещё перед нормалью (снаружи) означает,
            // что просто скользит мимо.
            float side = Vector2.Dot(to - tunnelHitsBuffer[i].point, tunnelHitsBuffer[i].normal);
            if (side < -0.02f && tunnelHitsBuffer[i].distance < closest)
            {
                closest = tunnelHitsBuffer[i].distance;
                tunneled = true;
            }
        }

        // Запасной вариант: простой линейный каст нулевой ширины вдоль того же самого отрезка,
        // независимый от прогона капсулой выше. Найдено вживую, неоднократно: когда капсула стоит под
        // углом по диагонали к собственному направлению движения, CapsuleCast может целиком промахнуться
        // мимо стены, лежащей ровно на центральной линии отрезка - известная категория ограничений прогона
        // тонкого коллайдера в 2D-физических движках, подтверждено здесь напрямую (ручной CapsuleCast
        // по записанному реальному пересечению вернул только постороннее попадание сбоку, но никогда стену,
        // которую простой Linecast на тех же двух точках находил сразу). У тонкого луча нет этой проблемы,
        // так как у него нет ширины, которую можно неверно прогнать, поэтому он ловит как раз те случаи,
        // которые прогон капсулой выше иногда пропускает - вместе они надёжнее, чем каждый по отдельности.
        if (!tunneled)
        {
            RaycastHit2D lineHit = Physics2D.Linecast(from, to, obstacleMask);
            if (lineHit.collider != null)
            {
                float lineDist = lineHit.fraction * dist;
                if (lineDist < closest) { closest = lineDist; tunneled = true; }
            }
        }

        if (!tunneled) return to;
        return from + dir * Mathf.Max(0f, closest - 0.02f);
    }

    // Безусловное, гарантированное последнее средство - выполняется в самом конце каждого
    // LateUpdate, какая бы ветка ни привела сюда (рывок, замер, обычное преследование, что угодно).
    // Все остальные защиты в этом файле пытаются ПРЕДОТВРАТИТЬ пересечение до того, как оно случится,
    // предсказывая движение кадра (прогон, выборка кривой) - принципиально надёжного предсказания,
    // как выяснилось, не существует: 2D-физический прогон может пропустить тонкую стену при
    // определённых углах капсулы независимо от того, сколько разных проверок прогоном/лучом наслоить
    // друг на друга (многократно подтверждено, с реальной ценой, за всю эту сессию). Это делает
    // обратное: ему всё равно, как агент сюда попал и что почти сработало - оно просто спрашивает
    // «вложена ли капсула сейчас внутрь препятствия глубже, чем когда-либо бывает обычный касательный
    // контакт», и если да, сразу выталкивает её обратно вдоль реально измеренного кратчайшего
    // расстояния разделения, немедленно, без очереди. Предсказание может быть неверным; прямое
    // измерение «перекрываюсь ли я и насколько» - не может.
    void EmergencyDepenetration()
    {
        int n = col.Overlap(obstacleFilter, finalOverlapBuffer);
        Vector2 worstCorrection = Vector2.zero;
        float worstMagnitude = 0f;
        for (int i = 0; i < n; i++)
        {
            ColliderDistance2D d = Physics2D.Distance(col, finalOverlapBuffer[i]);
            if (!d.isOverlapped) continue;
            Vector2 correction = d.normal * d.distance;
            float mag = correction.magnitude;
            if (mag > worstMagnitude) { worstMagnitude = mag; worstCorrection = correction; }
        }

        if (worstMagnitude > emergencyDepenetrationThreshold)
        {
            ai.Teleport(rb.position + worstCorrection * 1.05f, false);
            Physics2D.SyncTransforms();
            previousFramePosition = ai.position;
        }
    }

    void LateUpdate()
    {
        // AIPath двигает агента, прямо записывая transform.position (не через API Rigidbody2D), и
        // Unity НЕ распространяет это автоматически в собственный физический transform
        // Rigidbody2D/коллайдера до следующей синхронизации с фиксированным шагом (Physics2D.autoSyncTransforms
        // по умолчанию выключен). Каждая проверка ниже - Overlap, CapsuleCast, Distance - опрашивает
        // ФИЗИЧЕСКУЮ сторону, поэтому без этого вызова они все смотрели бы туда, где коллайдер был на
        // последнем физическом шаге, а не туда, куда AIPath реально поставил агента в этом кадре. Это
        // устаревшее окно - ровно то место, где пересечение стены остаётся необнаруженным: логическая
        // позиция уже может быть по ту сторону тонкой стены, пока физический коллайдер ещё не догнал,
        // так что каждый запрос коллизий ниже не видит никакого пересечения, пока эти двое внезапно
        // не пересинхронизируются позже, что читается как резкий беспричинный скачок - первопричина
        // и туннелирования, и замираний (решения ниже принимались на основе этого же устаревшего состояния).
        Physics2D.SyncTransforms();

        // Защита от туннелирования выполняется ПЕРВОЙ, на сырой неограниченной позиции, до того как
        // граничная сетка ниже вообще к ней притронется. Раньше она шла после граничного зажима, который
        // тихо «отмывал» именно те улики, что нужны этой защите: если реальное движение кадра унесло агента
        // за границу уровня (levelBoundsMin/Max стоит с небольшим запасом внутри этой стены), зажим сразу
        // возвращал координату Y/X обратно в границы ещё до того, как эта проверка видела сырое значение -
        // поэтому движение, уже полностью прошедшее сквозь граничную стену, регистрировалось как
        // «никакого пересечения», каждый раз, потому что к моменту её выполнения ловить уже было нечего.
        // Подтверждено напрямую: сырой линейный каст между последовательными реальными позициями нашёл
        // 61 реальное пересечение граничной стены за одну игровую сессию, и каждое было невидимо для этой
        // защиты в прежнем порядке именно потому, что зажим уже стёр позицию по другую сторону к моменту
        // собственного расчёта frameDist у защиты.
        //
        // Всё остальное здесь проверяет перекрытие только В ТЕКУЩЕЙ позиции, раз в кадр - если движение
        // одного кадра (быстрый рывок, большой толчок скольжения, собственное управление AIPath, что
        // угодно) покрывает больше пути, чем толщина тонкой стены, агент может оказаться чисто по другую
        // сторону сплошной геометрии, ни разу не зарегистрировав «касание» ни с одной стороны, и ничто
        // другое этого не поймает. Прогоняем собственную капсулу агента точно по пути, которым он прошёл
        // с прошлого кадра - если бы этот путь упёрся в препятствие (граничная стена включена - она на
        // том же слое Obstacles), оттягиваем его назад чуть не доходя до этого попадания, вместо того
        // чтобы оставить там, где он оказался.
        //
        // Это должен быть именно Physics2D.CapsuleCast с явной точкой отсчёта, а не col.Cast() - Cast()
        // всегда прогоняет вперёд от ТЕКУЩЕГО реального transform коллайдера, но к этому моменту агент
        // уже перемещён (телепортирован/сдвинут) в новую позицию этого кадра, поэтому каст «вперёд»
        // оттуда проверяет вообще не ту сторону и никогда не находит стену, которую он только что прошёл
        // и которая теперь позади. Только каст с реальной явной точкой отсчёта (последнее заведомо
        // хорошее место) может заново пройти отрезок, который реально только что пересекли.
        Vector2 pos = ai.position;
        Vector2 correctedPos = CheckAndCorrectTunnel(previousFramePosition, pos);
        if (correctedPos != pos)
        {
            ai.Teleport(correctedPos, false);
            Physics2D.SyncTransforms();
            pos = correctedPos;

            if (Time.time - lastTunnelHitTime > tunnelStreakResetGap) consecutiveTunnelHits = 0;
            consecutiveTunnelHits++;
            lastTunnelHitTime = Time.time;
            if (consecutiveTunnelHits >= tunnelRepathThreshold)
            {
                ai.SearchPath();
                consecutiveTunnelHits = 0;
            }
        }
        previousFramePosition = pos;

        // Граничная страховочная сетка, теперь ПОСЛЕ защиты от туннелирования выше: это чистое последнее
        // средство на случай, если оказались вне игровой зоны по причине, ДРУГОЙ, чем пересечение стены,
        // которую уже обрабатывает защита выше (плохая цель рывка, какой-нибудь будущий баг) - больше не
        // первая линия обороны от самой граничной стены, так как защита теперь ловит её напрямую, как
        // любое другое препятствие.
        Vector2 clamped = new Vector2(
            Mathf.Clamp(pos.x, levelBoundsMin.x, levelBoundsMax.x),
            Mathf.Clamp(pos.y, levelBoundsMin.y, levelBoundsMax.y));
        if (clamped != pos)
        {
            ai.Teleport(clamped, false);
            Physics2D.SyncTransforms();
            pos = clamped;
            previousFramePosition = pos;
        }

        // AntiTricker полностью владеет движением, избеганием столкновений и поворотом на всё время
        // собственного манёвра (он добирается через собственные пути AIPath, которые и так сами обходят
        // препятствия). Этот компонент вообще ничего другого не делает, пока это активно - ни выталкивания,
        // ни скольжения, ни остановки по касанию, ни перепоиска при застревании - так что во время
        // рывка врага ведёт ровно одна вещь, никогда не две. Два предыдущих бага происходили из-за того,
        // что эта граница была размытой, а не абсолютной: (1) толчок скольжения/выталкивания ниже вызывает
        // ai.Move(), который реально применяется только когда выполняется собственный шаг движения
        // AIPath, а он заблокирован за canMove - пока canMove был false (остановка по зоне касания, до
        // того как она сама была ограничена) коррекция каждого кадра копилась неиспользованной, и вся
        // накопленная очередь выплёскивалась разом в тот миг, как canMove снова становился true
        // (подтверждено по записи живой игры: 24 замороженных кадра, затем телепорт на 7.5 юнита, пока
        // ai.velocity ненадолго показывала 375 юнитов/сек); (2) даже при canMove, остающемся true на
        // рывке в прямом режиме, этот же толчок скольжения всё равно сдвигает позицию в сторону по
        // ai.steeringTarget поверх того, что собственное построение пути рывка и так делает к той же
        // цели - две нескоординированные силы, которые не борются за цель назначения, но борются за
        // покадровый путь туда, что читается именно как враг, разрываемый между рывком и чем-то ещё,
        // хотя ai.destination не трогает ничего, кроме самого рывка.
        if (antiTricker != null && antiTricker.IsCountering) { slideDir = Vector3.zero; squeezeAngle = 0f; EmergencyDepenetration(); return; }
        if (!ai.canMove) { slideDir = Vector3.zero; squeezeAngle = 0f; aiPath.enableRotation = true; EmergencyDepenetration(); return; }

        // Враг Kinematic, а Kinematic-тела никогда физически не выталкиваются из статичных (без
        // Rigidbody) коллайдеров вроде колонн - это разрешение в 2D-физике просто не происходит, поэтому
        // ничто не мешает капсуле погрузиться в такой, пока она проходит мимо. Выталкиваем вручную,
        // используя реальные формы коллайдеров (капсула против полигона), а не приближение кругом -
        // круг, достаточно маленький, чтобы поместиться в короткую ось капсулы, всё равно позволяет углам/
        // длинной оси капсулы погрузиться в препятствие незамеченными, чего как раз и не ловила проверка
        // фиксированного радиуса.
        int count = col.Overlap(obstacleFilter, overlapBuffer);
        inContact = count > 0;
        if (count == 0) { slideDir = Vector3.zero; squeezeAngle = 0f; aiPath.enableRotation = true; EmergencyDepenetration(); return; }

        // AIPath хранит собственную авторитетную позицию внутри (simulatedPosition) и каждый кадр
        // перезаписывает из неё transform.position - прямая правка transform здесь просто снова
        // перезаписалась бы в следующем кадре. ai.Teleport() тоже сработал бы, но он сбрасывает историю
        // позиций AIPath (используемую для расчёта его собственной скорости) на новую позицию при каждом
        // вызове, что обнуляет скорость агента в каждом скорректированном кадре - когда агент постоянно
        // прижат к препятствию, это каждый кадр, так что он никогда не мог бы набрать скорость, чтобы
        // отвернуть (пробовали, наблюдали, как он намертво застревал на месте, дрожа). ai.Move() - API,
        // действительно предназначенный для этого: внешнее смещение (ветер/отбрасывание/конвейеры),
        // которое вливается в собственный следующий шаг движения агента, а не сбрасывает его состояние.
        Vector3 correction = Vector3.zero;
        Vector3 normalSum = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            ColliderDistance2D d = Physics2D.Distance(col, overlapBuffer[i]);
            if (d.isOverlapped)
            {
                correction += (Vector3)(d.normal * d.distance);
                normalSum += (Vector3)d.normal;
            }
        }
        if (correction != Vector3.zero)
        {
            float maxStep = maxDepenetrationSpeed * Time.deltaTime;
            if (correction.magnitude > maxStep) correction = correction.normalized * maxStep;
            ai.Move(correction);
        }

        // Выталкивание прямо из препятствия не даёт капсуле погрузиться, но не помогает агенту реально
        // пройти мимо препятствия - если управление продолжает целить в то же место, его просто выталкивает,
        // и он сразу идёт обратно, что читается как бодание об него. Скользим вбок вдоль поверхности
        // (касательная к нормали контакта) туда, куда он реально пытается идти, чтобы контакт превратился в
        // обтекание препятствия, а не отскок от него.
        //
        // ai.steeringTarget может скачком измениться в тот миг, когда перепоиск находит, что другая сторона
        // препятствия короче (подтверждено логированием: он прыгал на 3+ юнита за один кадр, пока он
        // кружил вокруг этой колонны) - реакция на это сырое значение на полной силе каждый кадр
        // превращалась в ровно тот быстрый резонанс «влево-вправо», который это должно было устранить.
        // Вместо этого поворачиваем направление скольжения к сырой цели с ограниченной угловой скоростью,
        // та же идея, что и у VisualRotationSmoother: смена цели всё равно отслеживается, просто за
        // несколько кадров, а не мгновенным разворотом тяги.
        if (normalSum != Vector3.zero)
        {
            Vector3 normal = normalSum.normalized;
            Vector3 desiredDir = (Vector3)ai.steeringTarget - transform.position;
            desiredDir.z = 0f;
            Vector3 tangent = desiredDir - Vector3.Dot(desiredDir, normal) * normal;
            if (tangent.sqrMagnitude > 0.0001f)
            {
                Vector3 rawTangent = tangent.normalized;
                slideDir = slideDir.sqrMagnitude > 0.0001f
                    ? Vector3.RotateTowards(slideDir, rawTangent, slideTurnSpeed * Time.deltaTime, 10f).normalized
                    : rawTangent;
                float pushSpeed = Mathf.Min(ai.maxSpeed * slidePushMultiplier, maxSlideSpeed);
                ai.Move(slideDir * pushSpeed * Time.deltaTime);
            }

            // «Сжатие плечом»: только когда «blockedForSqueeze» действительно сработал (контакт И
            // нет реального прогресса за последнее окно stuckCheckInterval, задаётся в Update()) - простой
            // касательный контакт при обтекании препятствия обычно никогда не должен это запускать, иначе
            // каждый обычный проход мимо колонны ёрзал бы без причины. Направление поворота выбирается один
            // раз, по тому, в какую сторону от нормали контакта склоняется желаемое направление в момент
            // первого застревания - и удерживается фиксированным до конца сжатия, а не пересчитывается
            // каждый кадр. Зажатая между двумя препятствиями сразу (проход уже, чем коллайдер может
            // физически пройти даже при максимальном угле сжатия), суммарная нормаль обоих контактов
            // частично гасится и меняет знак от крошечного покадрового шума глубины проникновения, который
            // шёл прямо в проверку знака здесь и заставлял врага метать плечо туда-сюда вместо того, чтобы
            // зафиксировать один наклон - подтверждено напрямую: искусственно слишком узкий зазор заставлял
            // его носиться по несколько юнитов за кадр с качанием поворота более чем на 300 градусов между
            // замерами.
            if (blockedForSqueeze && Mathf.Approximately(squeezeAngle, 0f) && desiredDir.sqrMagnitude > 0.0001f)
            {
                float cross = normal.x * desiredDir.y - normal.y * desiredDir.x;
                squeezeSign = cross >= 0f ? 1f : -1f;
            }
            float targetSqueeze = blockedForSqueeze ? squeezeSign * maxSqueezeAngle : 0f;
            squeezeAngle = Mathf.MoveTowards(squeezeAngle, targetSqueeze, squeezeRotationSpeed * Time.deltaTime);

            if (Mathf.Abs(squeezeAngle) > 0.5f && desiredDir.sqrMagnitude > 0.0001f)
            {
                aiPath.enableRotation = false;
                // Та же конвенция «перед смотрит в +Y при повороте 0», что и везде.
                float baseAngle = Mathf.Atan2(desiredDir.y, desiredDir.x) * Mathf.Rad2Deg - 90f;
                rb.MoveRotation(baseAngle + squeezeAngle);
            }
            else
            {
                aiPath.enableRotation = true;
            }
        }
        else
        {
            squeezeAngle = 0f;
            aiPath.enableRotation = true;
        }

        
        EmergencyDepenetration();
    }

    void Update()
    {

         // ===== ВСТАВЬ ЛОГИ СЮДА =====
    if (Time.frameCount % 60 == 0) // раз в секунду (при 60 FPS)
    {
        Debug.Log($"[AIPath DEBUG] " +
                  $"pos={transform.position} | " +
                  $"destination={ai.destination} | " +
                  $"reachedDestination={ai.reachedDestination} | " +
                  $"reachedEndOfPath={ai.reachedEndOfPath} | " +
                  $"canMove={ai.canMove} | " +
                  $"isStopped={ai.isStopped} | " +
                  $"pathPending={ai.pathPending} | " +
                  $"hasPath={ai.hasPath} | " +
                  $"velocity={ai.velocity.magnitude:F2} | " +
                  $"remainingDistance={ai.remainingDistance:F2}");
    }
    // ===== КОНЕЦ ЛОГОВ =====

        UpdateTouchStop();

        // Страховка: принудительный свежий поиск пути, если агент давно не добился прогресса, хотя
        // ему разрешено двигаться. Дешёвая страховка от того, что преследователь пути когда-либо
        // заклинится о геометрию препятствия. Полностью пропускается во время рывка - AntiTricker
        // исключительно владеет построением путей для собственной зафиксированной цели, пока активен
        // (см. комментарий в LateUpdate про ту же границу).
        if (!ai.canMove || IsCountering()) { blockedForSqueeze = false; return; }

        stuckTimer += Time.deltaTime;
        if (stuckTimer < stuckCheckInterval) return;

        // Не считаем «касание препятствия» признаком «путь сломан» - перепоиск не исправляет локальную
        // коллизию, а принудительный запуск его, пока агент в середине манёвра вокруг чего-то, лишь
        // даёт (возможно, всё ещё движущейся) цели ещё один шанс перевернуть, какая сторона выглядит
        // короче, - именно это и заставляло его менять направление и снова и снова врезаться в
        // препятствие, вместо того чтобы закончить поворот.
        float moved = Vector2.Distance(transform.position, lastCheckedPosition);
        bool notMakingProgress = moved < stuckDistanceThreshold && !ai.reachedDestination;
        if (notMakingProgress && !inContact) ai.SearchPath();

        // Противоположное условие: реально заклинен о препятствие, а не просто между поисками путей -
        // именно этого ждёт сжатие плечом в LateUpdate, чтобы никогда не срабатывать на простом
        // касательном контакте при обычном обтекании укрытия.
        blockedForSqueeze = notMakingProgress && inContact;

       

        lastCheckedPosition = transform.position;
        stuckTimer = 0f;
    }

    void UpdateTouchStop()
    {
        if (isRangedUnit) return;

        // Координация: ближний боец без токена отходит на дистанцию ожидания (его цель - точка ожидания,
        // её выставляет CombatDirector). Остановка по касанию приморозила бы его вплотную к игроку, поэтому
        // здесь она снимается; дойдя до точки, он разворачивается лицом к игроку.
        if (combatAgent != null && combatAgent.IsWithdrawing)
        {
            if (insideTouchZone) { ai.isStopped = false; ai.canMove = true; }
            if (!IsCountering() && playerTouchZone != null && ai.velocity.magnitude < 0.3f)
                FaceTarget(playerTouchZone.transform.position);
            return;
        }

        if (!insideTouchZone) return;

        // AntiTricker фиксирует собственную цель и поворот на время рывка и имеет собственные проверки
        // дистанции прибытия, заточенные под манёвр. Эта заморозка зоны касания рассчитана на обычное
        // преследование (остановиться, когда достаточно близко, чтобы враг не пробегал мимо игрока) -
        // во время рывка быстрый проход рядом/вокруг игрока снова и снова входит в этот триггер и выходит
        // из него, и срабатывание здесь так же снова и снова сбрасывало бы ai.canMove в false посреди
        // рывка, намертво замораживая врага на кусок собственной длительности манёвра и выглядя именно
        // как нерешительные остановки и перезапуски рывка. Подтверждено напрямую: логирование ai.canMove
        // во время манёвра AntiTricker показало многокадровые заморозки, ровно совпадающие с окнами
        // «зона касания + линия видимости» посреди рывка, задолго до того, как собственная проверка
        // прибытия рывка закончила бы его.
        if (IsCountering() || IsWindingUp()) return;

        // Геометрически находиться внутри триггера недостаточно - колонна между ними всё равно может
        // перекрывать его, и это заморозило бы врага вплотную к колонне вместо того, чтобы дать
        // завершить путь вокруг неё. Останавливаемся только когда есть чистая линия к игроку; иначе
        // продолжаем двигаться, чтобы можно было обойти.
        bool hasLineOfSight = !Physics2D.Linecast(transform.position, playerTouchZone.transform.position, obstacleMask);

        ai.isStopped = hasLineOfSight;
        ai.canMove = !hasLineOfSight;

        // AIPath трогает поворот только пока реально ведёт движение (canMove=true), поэтому когда
        // враг заморожен здесь, больше ничто его не повернёт - делаем это сами, непрерывно, как задел
        // под будущий удар в ближнем бою, который нужно навести на игрока.
        if (hasLineOfSight) FaceTarget(playerTouchZone.transform.position);
    }

    // AntiTricker фиксирует собственную цель на время рывка - больше ничто в этом компоненте не
    // должно трогать построение путей или состояние движения, пока он активен, он владеет ими
    // исключительно, пока сам не закончит манёвр.
    bool IsCountering()
    {
        return antiTricker != null && antiTricker.IsCountering;
    }

    // На время замаха удара враг стоит на месте, и canMove принадлежит EnemyAttackZone (по его
    // окончании он сам вернёт движение, а остановку по касанию этот компонент пересчитает заново).
    bool IsWindingUp()
    {
        return attackZone != null && attackZone.IsWindingUp;
    }

    void FaceTarget(Vector2 target)
    {
        Vector2 toTarget = target - rb.position;
        if (toTarget.sqrMagnitude < 0.0001f) return;

        // Та же конвенция «перед смотрит в +Y при повороте 0», что используется везде в этом проекте.
        float targetAngle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - 90f;
        float nextAngle = Mathf.MoveTowardsAngle(rb.rotation, targetAngle, faceRotationSpeed * Time.deltaTime);
        rb.MoveRotation(nextAngle);
    }

   

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other == playerTouchZone) insideTouchZone = true;
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (other == playerTouchZone)
        {
            insideTouchZone = false;
            // Та же граница, что и везде в этом компоненте: пока AntiTricker противодействует, он
            // исключительно владеет canMove/isStopped - событие выхода из триггера посреди рывка (враг
            // проходит мимо игрока во время манёвра) не должно сбрасывать их у него из-под ног.
            if (IsCountering() || IsWindingUp()) return;
            ai.isStopped = false;
            ai.canMove = true;
        }
    }
}
