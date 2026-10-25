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
        meleeAgents.Clear();
        foreach (var a in CombatAgent.All)
            if (a != null && a.ring == CombatRing.Melee) meleeAgents.Add(a);

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
                float score = a.lastHadToken * 1000f + Vector2.Distance(a.transform.position, player.position) + (now < a.banUntil ? 1e6f : 0f);
                if (score < bestScore) { bestScore = score; best = a; }
            }
            if (best == null) break;
            best.HasToken = true;
            best.tokenSince = now;
            best.lastHadToken = now;
            holders++;
        }
        if (holders > maxSimultaneousMelee) maxSimultaneousMelee = holders;

        // 3. Применить: владелец идёт на игрока, остальные - к точке ожидания.
        foreach (var a in meleeAgents)
        {
            if (a.HasToken)
            {
                a.SetTarget(player);
            }
            else
            {
                UpdateHoldPoint(a);
                a.SetTarget(a.HoldPoint);
            }
        }
    }

    bool HasOtherWaiter(CombatAgent self)
    {
        foreach (var a in meleeAgents) if (a != self && !a.HasToken) return true;
        return false;
    }

    // Ждущий мечник стоит на месте, пока находится в «поясе ожидания» вокруг игрока; если игрок
    // подошёл слишком близко или враг далеко - выбирает новую точку на радиусе ожидания по линии
    // «игрок -> враг» (остаётся на своей стороне, не мечется).
    void UpdateHoldPoint(CombatAgent a)
    {
        Vector2 p = player.position;
        Vector2 e = a.transform.position;
        Vector2 off = e - p;
        float d = off.magnitude;

        bool inBand = d >= melee.radius - 0.5f && d <= melee.radius + 1.5f;
        if (inBand && a.HoldPoint != null && Vector2.Distance(a.HoldPoint.position, e) < 1f) return;

        Vector2 baseDir = d > 0.01f ? off / d : UnityEngine.Random.insideUnitCircle.normalized;
        Vector2 chosen = p + baseDir * melee.radius;
        bool found = false;
        // Своя сторона, затем всё шире в обе стороны: первая проходимая точка с видимостью на игрока.
        for (int i = 0; i < 9 && !found; i++)
        {
            float angle = ((i + 1) / 2) * 25f * (i % 2 == 0 ? 1f : -1f);
            Vector2 dir = Quaternion.Euler(0f, 0f, angle) * baseDir;
            Vector2 cand = p + dir * melee.radius;
            if (!IsWalkable(cand)) continue;
            if (Physics2D.Linecast(p, cand, obstacleMask)) continue;
            chosen = cand;
            found = true;
        }
        if (!found) chosen = NearestWalkable(chosen);

        a.HoldPoint.position = chosen;
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

        if (a.ring == CombatRing.Melee)
        {
            if (!a.HasToken) return false;
            Register(a, now);
            if (!forced && now - lastStart < globalAttackSpacing) return false;
            if (!forced && HasOlderWaiter(a, now)) return false;
            a.BeginAttack();
            a.waitingSince = -1f;
            RegisterStart(a.ring, now);
            return true;
        }

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

    void OnGUI()
    {
        if (!showOverlay) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        style.normal.textColor = Color.white;
        string text = $"Coordination_ver1  melee/mid/far attacks: {meleeAttacks}/{midAttacks}/{farAttacks}  refused ranged: {refusedRanged}\n";
        foreach (var a in CombatAgent.All)
        {
            if (a == null) continue;
            string state = a.ring == CombatRing.Melee ? (a.HasToken ? "TOKEN" : "waiting") : "ranged";
            if (a.IsAttacking) state += " ATTACK";
            text += $"{a.name} [{a.ring}] {state}\n";
        }
        GUI.Label(new Rect(10, 10, 700, 200), text, style);
    }

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
