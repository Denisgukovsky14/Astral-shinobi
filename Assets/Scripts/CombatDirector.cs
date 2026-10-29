using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;

// Coordination_ver1: простая координация врагов через токены на атаку, привязанные к кольцам
// вокруг игрока. Директор врагов НЕ двигает и НЕ стреляет за них - он только выдаёт разрешения.
//
// Кольца и роли (кольцо врага задаёт CombatAgent.ring):
//   Melee (ближнее, радиус = точка ожидания) - мечник. Единственный токен «в бой»: его владелец идёт
//        на игрока и бьёт, остальные мечники держатся на радиусе ожидания (цель AIDestinationSetter
//        переключается на «точку ожидания»), пока токен не перейдёт к ним. После удара токен
//        переходит к другому ожидающему, так что мечники бьют по очереди.
//   Mid (среднее) - кунайщик, Far (дальнее) - лучник. Токен выдаётся на каждый бросок/выстрел и
//        возвращается через holdTime; пока он занят, второй стрелок того же кольца ждёт.
// Общие правила для всех колец:
//   - между началом любых двух атак минимум globalAttackSpacing секунд (игрок успевает реагировать);
//   - пока мечник замахивается/бьёт, стрелки не стреляют (meleeBlocksRanged);
//   - стрелок получает токен, только если он не дальше радиуса своего кольца от игрока.
// Маршруты по очерёдности (мечники): ранг 0 - главный путь (владелец токена идёт на игрока), ранг 1 -
// второй путь (первый в очереди ждёт на фланге), ранг 2 - третий... Как только боец выбывает (смерть),
// директор в тот же кадр сдвигает всех на ступень: второй становится главным и идёт на игрока, третий
// занимает второй путь, и так далее - никто не ждёт очередного тика и ничего не переключается вручную.
// Группа и фланги (отделение): в бою одновременно не больше maxGroupSize врагов, остальные дремлют на
// месте (пополнение - только окликом, см. ниже). Бойцы группы расходятся по разным сторонам игрока: ждущий мечник идёт в
// слот на другой стороне (не бежит следом за тем, кто уже на маршруте), стрелки выбирают точки так,
// чтобы не вставать на одну сторону с остальными (SpreadPenalty). Если свободного маршрута нет,
// враг идёт по занятому - расталкивание (EnemySeparation) разводит их физически.
// Дремлющие враги и оклик: в бою воюют только бойцы группы, остальные (CombatAgent.startDormant и все,
// кто не поместился в лимит) дремлют на месте. Пока в группе меньше maxGroupSize бойцов и на сцене есть
// живой дремлющий, один боец группы бежит в его радиус активации (CombatAgent.activationRadius) и окликает
// его - тот просыпается и вместе с группой идёт на игрока. Гонцом посылают наименее опасного для игрока
// по порядку: кунайщик - лучник - мечник. Зон ответственности (кто кого может звать) пока нет: звать
// можно любого дремлющего на карте.
// Враги по-прежнему сами решают, как двигаться и куда целиться; отказ в токене лишь откладывает
// бросок/удар (повтор через CombatAgent.RetryDelay), не трогая их кулдауны и логику движения.
public class CombatDirector : MonoBehaviour
{
    public static CombatDirector Instance { get; private set; }

    [Serializable]
    public class RingSettings
    {
        [Tooltip("Melee: радиус точки ожидания (на такой дистанции от игрока ждут мечники без токена). Mid/Far: максимальная дистанция от игрока, с которой стрелок кольца вообще получает токен.")]
        public float radius = 5f;
        [Tooltip("Сколько токенов в кольце: сколько врагов этого кольца могут атаковать одновременно.")]
        public int tokens = 1;
        [Tooltip("Mid/Far: сколько секунд токен занят после начала атаки. Melee: сколько секунд после удара владелец держит токен, прежде чем отдать его ожидающему.")]
        public float holdTime = 0.7f;
    }

    public Transform player;
    public LayerMask obstacleMask;

    [Header("Группа и фланги")]
    [Tooltip("Максимум врагов, которые одновременно воюют (отделение). Остальные на карте дремлют, пока их не окликнут.")]
    public int maxGroupSize = 4;
    [Tooltip("Минимальный угол вокруг игрока между двумя бойцами группы: ближе - считаются идущими с одной стороны (на одном маршруте).")]
    public float flankMinAngle = 55f;
    [Tooltip("Сколько секунд после оклика дремлющий «просыпается», прежде чем вступить в бой.")]
    public float callWakeDelay = 0.6f;
    [Tooltip("Если гонец не добежал до дремлющего за это время, поручение отменяется (и может быть дано другому).")]
    public float callTimeout = 25f;
    [Tooltip("Штраф (в юнитах расстояния) стрелку за точку, вставшую на одну сторону с другим бойцом.")]
    public float spreadPenalty = 8f;

    [Header("Общее")]
    [Tooltip("Как часто директор пересчитывает токены и точки ожидания, в секундах.")]
    public float tickInterval = 0.15f;
    [Tooltip("Минимальная пауза между началом любых двух атак в группе.")]
    public float globalAttackSpacing = 0.8f;
    [Tooltip("Пока мечник замахивается или бьёт, стрелки не стреляют.")]
    public bool meleeBlocksRanged = true;

    [Header("Кольца")]
    public RingSettings melee = new RingSettings { radius = 3.2f, tokens = 1, holdTime = 0.6f };
    public RingSettings mid = new RingSettings { radius = 9.5f, tokens = 1, holdTime = 0.7f };
    public RingSettings far = new RingSettings { radius = 16f, tokens = 1, holdTime = 1f };

    [Header("Мечники: ротация токена")]
    [Tooltip("Если владелец токена не атакует дольше этого времени (не может дойти, застрял), токен уходит другому.")]
    public float meleeMaxHold = 6f;
    [Tooltip("После отдачи токена прежний владелец не получает его снова это время, пока есть другие ожидающие.")]
    public float meleeRotateBan = 1.5f;

    [Header("Отладка")]
    public bool showOverlay = true;
    public bool drawRings = true;
    public int meleeAttacks, midAttacks, farAttacks;
    public int refusedRanged;
    [Tooltip("Минимальная пауза между атаками, реально случившаяся за игру.")]
    public float minObservedGap = float.MaxValue;
    public int maxSimultaneousMelee;

    private float nextTick;
    private float lastStart = -999f;
    private readonly List<float> midBusy = new List<float>();
    private readonly List<float> farBusy = new List<float>();
    private readonly List<CombatAgent> meleeAgents = new List<CombatAgent>();
    private bool groupFormed;
    private readonly List<CombatAgent> waitingQueue = new List<CombatAgent>();
    // Какой угол занимал слот каждого ранга в прошлый раз: сдвинувшийся на ступень боец сначала пробует
    // слот, который освободился на его новой ступени («третий занимает второй путь»).
    private readonly float[] rankSlot = new float[16];
    private readonly bool[] rankSlotKnown = new bool[16];

    public int ActiveCount
    {
        get { int n = 0; foreach (var a in CombatAgent.All) if (a != null && a.IsActive) n++; return n; }
    }

    void Awake()
    {
        Instance = this;
        if (player == null)
        {
            var health = FindFirstObjectByType<PlayerHealth>();
            if (health != null) player = health.transform;
        }
        if (obstacleMask.value == 0) obstacleMask = LayerMask.GetMask("Obstacles");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (player == null || Time.time < nextTick) return;
        nextTick = Time.time + tickInterval;
        Tick();
    }

    // --- Ближний бой: токен «в бой» и точки ожидания ---

    void Tick()
    {
        float now = Time.time;
        UpdateGroup(now);
        UpdateCalls(now);

        meleeAgents.Clear();
        foreach (var a in CombatAgent.All)
        {
            if (a == null || a.ring != CombatRing.Melee) continue;
            if (!a.IsActive)
            {
                // Резерв: стоит на месте (isStopped), без токена. Стрелки-резервисты сами замирают в своих Update.
                a.HasToken = false;
                if (a.Ai != null) a.Ai.isStopped = true;
                a.reserveApplied = true;
                continue;
            }
            if (a.reserveApplied)
            {
                a.reserveApplied = false;
                if (a.Ai != null) a.Ai.isStopped = false;
            }
            if (a.IsCalling)
            {
                // Мечник-гонец бежит к дремлющему: вне токенов и очереди, цель - дремлющий.
                if (a.HasToken) { a.HasToken = false; a.lastHadToken = now; }
                a.slotValid = false;
                a.SetTarget(a.callTarget.transform);
                continue;
            }
            meleeAgents.Add(a);
        }

        // 1. Отдать токен: владелец закончил атаку и выдержал holdTime, либо слишком долго не атакует.
        foreach (var a in meleeAgents)
        {
            if (!a.HasToken || a.Busy) continue;
            if (!HasOtherWaiter(a)) continue;

            bool afterAttack = a.lastAttackFinished > a.tokenSince && now - a.lastAttackFinished >= melee.holdTime;
            bool tooLong = now - a.tokenSince > meleeMaxHold;
            if (afterAttack || tooLong)
            {
                a.HasToken = false;
                a.slotValid = false;
                a.lastHadToken = now;
                a.banUntil = now + meleeRotateBan;
            }
        }

        // 2. Раздать свободные токены: тому, кто ждёт дольше всех (при равенстве - ближайшему к игроку);
        //    забаненный (только что отдавший токен) идёт в последнюю очередь.
        int holders = 0;
        foreach (var a in meleeAgents) if (a.HasToken) holders++;
        while (holders < melee.tokens)
        {
            CombatAgent best = null;
            float bestScore = float.MaxValue;
            foreach (var a in meleeAgents)
            {
                if (a.HasToken) continue;
                float score = QueueScore(a, now);
                if (score < bestScore) { bestScore = score; best = a; }
            }
            if (best == null) break;
            best.HasToken = true;
            best.slotValid = false;
            best.tokenSince = now;
            best.lastHadToken = now;
            holders++;
        }
        if (holders > maxSimultaneousMelee) maxSimultaneousMelee = holders;

        // 3. Ранги: владелец токена - главный путь (0), остальные по очереди на токен - второй, третий...
        //    Очередь та же, что и порядок выдачи токена (дольше всех ждёт - первый).
        waitingQueue.Clear();
        foreach (var a in meleeAgents)
        {
            if (a.HasToken) a.RouteRank = 0;
            else waitingQueue.Add(a);
        }
        waitingQueue.Sort((x, y) => QueueScore(x, now).CompareTo(QueueScore(y, now)));
        for (int i = 0; i < waitingQueue.Count; i++)
        {
            var a = waitingQueue[i];
            int rank = i + 1;
            if (a.RouteRank != rank) a.slotValid = false; // сменил ступень - слот выбирается заново
            a.RouteRank = rank;
        }

        // 4. Применить: владелец идёт на игрока, остальные - в слоты своих маршрутов (по порядку рангов).
        foreach (var a in meleeAgents)
            if (a.HasToken) a.SetTarget(player);
        foreach (var a in waitingQueue)
        {
            UpdateHoldPoint(a);
            a.SetTarget(a.HoldPoint);
        }
    }

    // Порядок очереди на токен/маршрут: дольше всех без токена - первый; только что отдавший токен - в конец.
    float QueueScore(CombatAgent a, float now)
    {
        return a.lastHadToken * 1000f + Vector2.Distance(a.transform.position, player.position) + (now < a.banUntil ? 1e6f : 0f);
    }

    // Боец выбыл (смерть и т.п.). Если он держал главный путь или место в очереди - переназначаем сразу, в этот же кадр.
    public void OnAgentRemoved(CombatAgent removed)
    {
        if (player == null || !isActiveAndEnabled) return;
        nextTick = Time.time + tickInterval;
        Tick();
    }

    bool HasOtherWaiter(CombatAgent self)
    {
        foreach (var a in meleeAgents) if (a != self && !a.HasToken) return true;
        return false;
    }

    // --- Группа ---

    // Формирует отделение при старте: в бой идут ближайшие к игроку недремлющие враги (с поправкой на
    // разнообразие колец), но не больше maxGroupSize. Все остальные дремлют. Дальше состав сам не
    // пополняется: убыль восполняется только окликом (UpdateCalls). Игрок, подошедший к дремлющему
    // ближе его радиуса активации, будит его: тот воюет сам, вне лимита группы.
    void UpdateGroup(float now)
    {
        foreach (var a in CombatAgent.All)
            if (a != null && !a.provoked && !a.IsActive && Vector2.Distance(a.transform.position, player.position) < a.activationRadius)
            {
                a.provoked = true;
                a.beingCalled = false;
            }

        if (groupFormed || CombatAgent.All.Count == 0) return;
        groupFormed = true;

        int count = 0;
        while (count < maxGroupSize)
        {
            CombatAgent best = null;
            float bestScore = float.MaxValue;
            foreach (var a in CombatAgent.All)
            {
                if (a == null || a.InGroup || a.startDormant || a.provoked) continue;
                int sameRing = 0;
                foreach (var g in CombatAgent.All) if (g != null && g.InGroup && g.ring == a.ring) sameRing++;
                float score = Vector2.Distance(a.transform.position, player.position) + sameRing * 4f;
                if (score < bestScore) { bestScore = score; best = a; }
            }
            if (best == null) break;

            best.InGroup = true;
            best.slotValid = false;
            best.activeFrom = now;
            count++;
        }
    }

    // --- Оклик дремлющих ---

    // Кого послать гонцом: чем менее опасен для игрока, тем раньше (кунайщик, лучник, мечник).
    static int CallPriority(CombatAgent a)
    {
        return a.ring == CombatRing.Mid ? 0 : (a.ring == CombatRing.Far ? 1 : 2);
    }

    static void EndCall(CombatAgent caller)
    {
        if (caller.callTarget != null) caller.callTarget.beingCalled = false;
        caller.callTarget = null;
    }

    // Событие «в группе недостаточно бойцов»: если на сцене есть живые дремлющие - отправить за ними гонца.
    // Гонец добежал до радиуса активации - дремлющий просыпается и вступает в группу.
    void UpdateCalls(float now)
    {
        // Текущие поручения: завершить, прервать или оставить.
        bool missionRunning = false;
        foreach (var caller in CombatAgent.All)
        {
            if (caller == null || caller.callTarget == null) continue;
            var target = caller.callTarget;
            bool invalid = target.InGroup || target.IsActive || !caller.IsActive || now - caller.callStartTime > callTimeout;
            if (invalid) { EndCall(caller); continue; }

            if (Vector2.Distance(caller.transform.position, target.transform.position) <= target.activationRadius)
            {
                // Добежал: окликнул - дремлющий просыпается и идёт на игрока вместе с группой.
                target.InGroup = true;
                target.slotValid = false;
                target.activeFrom = now + callWakeDelay;
                EndCall(caller);
                continue;
            }
            missionRunning = true;
        }
        if (missionRunning || PlayerHealth.IsDead) return;

        int inGroup = 0;
        foreach (var a in CombatAgent.All) if (a != null && a.InGroup) inGroup++;
        if (inGroup >= maxGroupSize || inGroup == 0) return;

        // Есть ли кого будить, и кто из группы пойдёт.
        CombatAgent caller2 = null;
        CombatAgent dormant = null;
        float bestDist = float.MaxValue;
        int bestPriority = int.MaxValue;
        foreach (var c in CombatAgent.All)
        {
            if (c == null || !c.InGroup || !c.IsActive || c.IsAttacking) continue;
            int pr = CallPriority(c);
            foreach (var d in CombatAgent.All)
            {
                if (d == null || d.InGroup || d.IsActive || d.beingCalled) continue;
                float dist = Vector2.Distance(c.transform.position, d.transform.position);
                if (pr < bestPriority || (pr == bestPriority && dist < bestDist))
                {
                    bestPriority = pr; bestDist = dist; caller2 = c; dormant = d;
                }
            }
        }
        if (caller2 == null) return;

        caller2.callTarget = dormant;
        caller2.callStartTime = now;
        dormant.beingCalled = true;
    }

    // --- Фланги ---

    static float AngleOf(Vector2 from, Vector2 to)
    {
        Vector2 d = to - from;
        return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
    }

    // С какой стороны от игрока боец (для ждущего мечника - сторона его слота, он уже идёт туда).
    float MemberAngle(CombatAgent o, Vector2 p)
    {
        if (o.ring == CombatRing.Melee && !o.HasToken && o.slotValid) return o.slotAngle;
        return AngleOf(p, o.transform.position);
    }

    float MinSeparation(CombatAgent self, float angle, Vector2 p, int maxRank = int.MaxValue)
    {
        float min = 180f;
        foreach (var o in CombatAgent.All)
        {
            if (o == null || o == self || !o.IsActive) continue;
            // Слот ранга r подстраивается под главный путь и слоты младших рангов, но не под старших:
            // старшие сами подстроятся под него (иначе слоты перетягивали бы друг друга).
            if (o.ring == CombatRing.Melee && !o.HasToken && o.RouteRank > maxRank) continue;
            min = Mathf.Min(min, Mathf.Abs(Mathf.DeltaAngle(angle, MemberAngle(o, p))));
        }
        return min;
    }

    // Штраф стрелку за точку, которая по углу вокруг игрока слишком близко к другому бойцу группы.
    public float SpreadPenalty(CombatAgent self, Vector2 point)
    {
        if (player == null) return 0f;
        Vector2 p = player.position;
        if ((point - p).sqrMagnitude < 0.01f) return 0f;

        float angle = AngleOf(p, point);
        float penalty = 0f;
        foreach (var o in CombatAgent.All)
        {
            if (o == null || o == self || !o.IsActive) continue;
            float diff = Mathf.Abs(Mathf.DeltaAngle(angle, MemberAngle(o, p)));
            if (diff < flankMinAngle) penalty += (1f - diff / flankMinAngle) * spreadPenalty;
        }
        return penalty;
    }

    // Ждущий мечник идёт в слот на радиусе ожидания, на стороне, максимально далёкой по углу от остальных
    // бойцов группы (в первую очередь от того, кто уже на маршруте к игроку): так он обходит препятствие с
    // другой стороны, а не бежит следом. Слот держится, пока от других достаточно далеко и игрок не
    // ушёл далеко от места, где слот выбирали; иначе выбирается заново.
    void UpdateHoldPoint(CombatAgent a)
    {
        Vector2 p = player.position;
        Vector2 e = a.transform.position;

        int rank = Mathf.Clamp(a.RouteRank, 0, rankSlot.Length - 1);
        bool slotOk = a.slotValid && MinSeparation(a, a.slotAngle, p, a.RouteRank) >= flankMinAngle;
        bool playerMoved = Vector2.Distance(a.holdCenter, p) > 0.8f;
        if (slotOk && !playerMoved && a.HoldPoint != null)
        {
            rankSlot[rank] = a.slotAngle;
            rankSlotKnown[rank] = true;
            return;
        }

        float current = a.slotValid ? a.slotAngle : (rankSlotKnown[rank] ? rankSlot[rank] : AngleOf(p, e));
        float bestScore = float.MinValue;
        float bestAngle = current;
        Vector2 bestPoint = Vector2.zero;
        bool found = false;

        for (int i = 0; i < 24; i++)
        {
            float ang = current + i * 15f;
            Vector2 cand = p + new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad)) * melee.radius;
            if (!IsWalkable(cand)) continue;
            if (Physics2D.Linecast(p, cand, obstacleMask)) continue;

            // Разнесённость важнее всего (до 120°), при равной разнесённости - ближе к прежней стороне (без метаний).
            float score = Mathf.Min(MinSeparation(a, ang, p, a.RouteRank), 120f) - 0.15f * Mathf.Abs(Mathf.DeltaAngle(ang, current));
            if (score > bestScore) { bestScore = score; bestAngle = ang; bestPoint = cand; found = true; }
        }

        if (!found)
        {
            bestPoint = NearestWalkable(p + new Vector2(Mathf.Cos(current * Mathf.Deg2Rad), Mathf.Sin(current * Mathf.Deg2Rad)) * melee.radius);
            bestAngle = current;
        }

        a.slotAngle = bestAngle;
        a.slotValid = true;
        rankSlot[rank] = bestAngle;
        rankSlotKnown[rank] = true;
        a.holdCenter = p;
        a.HoldPoint.position = bestPoint;
    }

    static bool IsWalkable(Vector2 point)
    {
        if (AstarPath.active == null) return true;
        var nn = AstarPath.active.GetNearest(point, NNConstraint.Default);
        return nn.node != null && nn.node.Walkable && Vector2.Distance(nn.position, point) < 0.4f;
    }

    static Vector2 NearestWalkable(Vector2 point)
    {
        if (AstarPath.active == null) return point;
        var nn = AstarPath.active.GetNearest(point, NNConstraint.Default);
        return nn.node != null ? (Vector2)nn.position : point;
    }

    // --- Разрешение на атаку ---

    public bool TryStartAttack(CombatAgent a, bool forced)
    {
        if (player == null) return true;
        float now = Time.time;

        // Ближний боец, до которого игрок дотянулся (EnemyAttackZone зовёт сюда, только когда игрок уже в
        // зоне удара), бьёт всегда и ничего не ждёт: ни токена, ни паузы, ни очереди, ни резерва. Токен
        // решает лишь, кто ИДЁТ на игрока; стоять перед мечником и не получать удара нельзя. Его удар
        // не занимает общую паузу (она только между стрелками), но пока он замахивается, стрелки уступают.
        if (a.ring == CombatRing.Melee)
        {
            a.BeginAttack();
            meleeAttacks++;
            return true;
        }

        if (!a.IsActive) return false; // резервист-стрелок не стреляет, пока не вступил в бой

        // Стрелок.
        Register(a, now);
        var settings = a.ring == CombatRing.Mid ? mid : far;
        var busy = a.ring == CombatRing.Mid ? midBusy : farBusy;
        busy.RemoveAll(t => t <= now);

        if (meleeBlocksRanged && AnyMeleeAttacking()) return Refuse();
        if (Vector2.Distance(a.transform.position, player.position) > settings.radius) return Refuse();
        if (now - lastStart < globalAttackSpacing) return Refuse();
        if (busy.Count >= settings.tokens) return Refuse();
        if (HasOlderWaiter(a, now)) return Refuse();

        busy.Add(now + settings.holdTime);
        a.waitingSince = -1f;
        RegisterStart(a.ring, now);
        return true;
    }

    // Агент просит разрешение: запоминаем, с какого момента он ждёт (для справедливой очереди).
    static void Register(CombatAgent a, float now)
    {
        a.lastRequestTime = now;
        if (a.waitingSince < 0f) a.waitingSince = now;
    }

    // Справедливая очередь на всю группу (а не внутри одного кольца): первым идёт тот, кто ждёт
    // дольше. Ждущим считается только агент, который недавно просил И реально мог бы атаковать
    // (токен/радиус/пул) - иначе заблокированный по другой причине враг держал бы очередь вечно.
    bool HasOlderWaiter(CombatAgent a, float now)
    {
        foreach (var other in CombatAgent.All)
        {
            if (other == a || now - other.lastRequestTime > 0.3f) continue;
            if (other.waitingSince < 0f || other.waitingSince >= a.waitingSince - 0.01f) continue;
            if (CouldAttack(other, now)) return true;
        }
        return false;
    }

    bool CouldAttack(CombatAgent a, float now)
    {
        if (a.ring == CombatRing.Melee) return a.HasToken;
        var settings = a.ring == CombatRing.Mid ? mid : far;
        var busy = a.ring == CombatRing.Mid ? midBusy : farBusy;
        int used = 0;
        foreach (float t in busy) if (t > now) used++;
        return used < settings.tokens && Vector2.Distance(a.transform.position, player.position) <= settings.radius;
    }

    bool Refuse()
    {
        refusedRanged++;
        return false;
    }

    bool AnyMeleeAttacking()
    {
        foreach (var a in CombatAgent.All) if (a.ring == CombatRing.Melee && a.IsAttacking) return true;
        return false;
    }

    void RegisterStart(CombatRing ring, float now)
    {
        float gap = now - lastStart;
        if (lastStart > 0f && gap < minObservedGap) minObservedGap = gap;
        lastStart = now;
        if (ring == CombatRing.Melee) meleeAttacks++;
        else if (ring == CombatRing.Mid) midAttacks++;
        else farAttacks++;
    }

    // --- Отладка ---

#if UNITY_EDITOR // в билде оверлей не нужен: OnGUI на телефоне заметно тормозит
    void OnGUI()
    {
        if (!showOverlay) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        style.normal.textColor = Color.white;
        string text = $"Coordination_ver1  group {ActiveCount}/{maxGroupSize}  melee/mid/far attacks: {meleeAttacks}/{midAttacks}/{farAttacks}  refused ranged: {refusedRanged}\n";
        foreach (var a in CombatAgent.All)
        {
            if (a == null) continue;
            string state = a.IsCalling ? "ГОНЕЦ -> " + a.callTarget.name : (a.IsReserve ? "ДРЕМЛЕТ" : (a.ring == CombatRing.Melee ? (a.HasToken ? "TOKEN (путь 1)" : "путь " + (a.RouteRank + 1)) : "ranged"));
            if (a.IsAttacking) state += " ATTACK";
            text += $"{a.name} [{a.ring}] {state}\n";
        }
        GUI.Label(new Rect(10, 10, 700, 200), text, style);
    }
#endif

    void OnDrawGizmos()
    {
        if (!drawRings || player == null) return;
        DrawCircle(player.position, melee.radius, new Color(1f, 0.3f, 0.3f, 0.8f));
        DrawCircle(player.position, mid.radius, new Color(1f, 0.8f, 0.2f, 0.5f));
        DrawCircle(player.position, far.radius, new Color(0.3f, 0.6f, 1f, 0.4f));
        foreach (var a in CombatAgent.All)
            if (a != null && a.HoldPoint != null && !a.HasToken)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(a.HoldPoint.position, 0.2f);
                Gizmos.DrawLine(a.transform.position, a.HoldPoint.position);
            }
    }

    static void DrawCircle(Vector3 center, float radius, Color color)
    {
        Gizmos.color = color;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= 48; i++)
        {
            float a = i * Mathf.PI * 2f / 48f;
            Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
