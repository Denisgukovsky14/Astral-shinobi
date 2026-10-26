using System.Collections.Generic;
using UnityEngine;
using Pathfinding;

// Кольцо вокруг игрока, к которому «приписан» враг: от него зависит, какой пул токенов на атаку
// он использует (см. CombatDirector).
public enum CombatRing { Melee, Mid, Far }

// Сторона врага в координации (Coordination_ver1). Тонкая прослойка между врагом и CombatDirector:
// врага не двигает и его внутреннее поведение не переписывает, а только
//   1) спрашивает у директора разрешение (токен) перед началом атаки - TryStartAttack;
//   2) для ближнего боя (Melee) по команде директора переключает цель AIDestinationSetter между
//      игроком (есть токен: идёт и бьёт) и «точкой ожидания» на удалении от игрока (нет токена).
// Если на сцене нет CombatDirector, TryStartAttack всегда разрешает - враг ведёт себя как раньше.
[DisallowMultipleComponent]
public class CombatAgent : MonoBehaviour
{
    public static readonly List<CombatAgent> All = new List<CombatAgent>();

    public CombatRing ring = CombatRing.Melee;

    // Через сколько секунд повторять запрос после отказа (чтобы не гонять дорогую проверку выстрела каждый кадр).
    public const float RetryDelay = 0.1f;

    // Только ближний бой: сейчас у агента право идти на игрока и бить.
    public bool HasToken { get; internal set; }
    public bool IsAttacking { get; private set; }

    // Группа (отделение): в бою участвуют не больше CombatDirector.maxGroupSize врагов, остальные - резерв.
    // activeFrom - с какого момента вызванный из резерва враг начинает действовать (пауза «на подмогу»).
    public bool InGroup { get; internal set; }
    internal float activeFrom;
    internal bool reserveApplied;

    [Header("Дремлющий враг")]
    [Tooltip("Дремлет с самого начала: в группу сам не входит, пока его не окликнет боец группы (или игрок не подойдёт вплотную).")]
    public bool startDormant;
    [Tooltip("Радиус активации дремлющего: боец группы, добежавший до него на это расстояние, окликает его и тот просыпается. Игрок, подошедший ближе этого расстояния, тоже будит его.")]
    public float activationRadius = 3f;

    // Оклик: боец группы, которого послали будить дремлющего (callTarget), и время начала поручения.
    // Пока он бежит, его обычное поведение (позиция на кольце, атаки) выключено.
    internal CombatAgent callTarget;
    internal float callStartTime;
    internal bool beingCalled; // дремлющего уже кто-то бежит окликать
    public bool IsCalling => callTarget != null;
    // Резервист, к которому подошёл игрок: воюет сам, вне лимита группы (радиус - activationRadius).
    internal bool provoked;

    // Фланговый слот ожидающего мечника: угол вокруг игрока, куда он идёт (см. CombatDirector.UpdateHoldPoint).
    internal bool slotValid;
    internal float slotAngle;
    internal Vector2 holdCenter;

    // Очерёдность на маршрут (только ближний бой): 0 - главный путь (владелец токена, идёт на игрока),
    // 1 - второй путь (первый в очереди, уже стоит на фланге), 2 - третий и т.д. Выбывает боец - все ниже
    // по списку мгновенно сдвигаются на ступень вверх (CombatDirector.OnAgentRemoved).
    public int RouteRank { get; internal set; }

    internal float tokenSince;
    // Когда токен был у агента в последний раз: токен переходит к тому, кто ждёт дольше всех.
    internal float lastHadToken = -1f;
    internal float banUntil;
    internal float lastAttackFinished = -999f;
    internal float lastRequestTime = -999f;
    internal float waitingSince = -1f;

    private AIDestinationSetter setter;
    private AntiTricker antiTricker;
    private Transform holdPoint;

    public Transform HoldPoint => holdPoint;
    public AIPath Ai { get; private set; }

    // Агент занят и его нельзя «переключать»: идёт атака или рывок AntiTricker.
    public bool Busy => IsAttacking || (antiTricker != null && antiTricker.IsCountering);

    // Ближний боец без токена (и директор есть на сцене): должен держаться на расстоянии, а не стоять
    // вплотную. EnemyChaseStop смотрит на это, чтобы остановка по касанию не приморозила его к игроку.
    public bool IsWithdrawing => ring == CombatRing.Melee && !HasToken && CombatDirector.Instance != null && IsActive;

    // Боец в строю: в группе и уже «подоспел» (прошла пауза вызова). Без директора на сцене - всегда да.
    public bool IsActive => CombatDirector.Instance == null || provoked || (InGroup && Time.time >= activeFrom);

    // Резерв: ждёт на месте, не атакует и не двигается, пока в группе нет свободного места.
    public bool IsReserve => !IsActive;

    // Штраф за близость по углу вокруг игрока к другим бойцам группы: стрелки выбирают точки так, чтобы
    // не вставать на одну сторону с остальными (фланги), а не стоять кучей.
    public float SpreadPenalty(Vector2 point)
    {
        var d = CombatDirector.Instance;
        return d != null ? d.SpreadPenalty(this, point) : 0f;
    }

    public bool IsCrowded(Vector2 point)
    {
        var d = CombatDirector.Instance;
        return d != null && d.SpreadPenalty(this, point) > d.spreadPenalty * 0.5f;
    }

    // AntiTricker ближнего бойца делает рывок на игрока, поэтому без токена он запрещён.
    public bool AllowsCounterDash => CombatDirector.Instance == null || (IsActive && !IsCalling && (ring != CombatRing.Melee || HasToken));

    // Стрелок-гонец бежит к дремлющему: направляем его сами (его обычная логика позиции в это время не работает).
    internal void DriveCall()
    {
        if (Ai == null || callTarget == null) return;
        Ai.enableRotation = true;
        Ai.isStopped = false;
        Ai.canMove = true;
        Ai.destination = callTarget.transform.position;
    }

    void Awake()
    {
        Ai = GetComponent<AIPath>();
        setter = GetComponent<AIDestinationSetter>();
        antiTricker = GetComponent<AntiTricker>();

        if (ring == CombatRing.Melee)
        {
            holdPoint = new GameObject(name + "_HoldPoint").transform;
            holdPoint.position = transform.position;
        }
    }

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    void OnDisable()
    {
        bool hadRole = HasToken || RouteRank >= 0;
        All.Remove(this);
        HasToken = false;
        IsAttacking = false;
        InGroup = false;
        provoked = false;
        if (callTarget != null) callTarget.beingCalled = false;
        callTarget = null;
        beingCalled = false;

        // Выбыл (смерть и т.п.): директор сразу, не дожидаясь очередного тика, переназначает маршруты.
        var director = CombatDirector.Instance;
        if (hadRole && director != null && gameObject.scene.isLoaded) director.OnAgentRemoved(this);
    }

    void OnDestroy()
    {
        if (holdPoint != null) Destroy(holdPoint.gameObject);
    }

    // Спросить разрешение начать атаку. Мечник зовёт перед замахом, стрелки - перед выстрелом.
    // forced: пропустить общую паузу между атаками (мгновенный удар после рывка AntiTricker).
    public bool TryStartAttack(bool forced = false)
    {
        var director = CombatDirector.Instance;
        if (director == null) return true;
        return director.TryStartAttack(this, forced);
    }

    internal void BeginAttack()
    {
        IsAttacking = true;
    }

    // Атака закончилась (удар нанесён/отбит/отменён). Безопасно звать повторно.
    public void AttackFinished()
    {
        if (!IsAttacking) return;
        IsAttacking = false;
        lastAttackFinished = Time.time;
    }

    internal void SetTarget(Transform target)
    {
        if (setter != null && setter.target != target) setter.target = target;
    }
}
