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
    public bool IsWithdrawing => ring == CombatRing.Melee && !HasToken && CombatDirector.Instance != null;

    // AntiTricker ближнего бойца делает рывок на игрока, поэтому без токена он запрещён.
    public bool AllowsCounterDash => ring != CombatRing.Melee || HasToken || CombatDirector.Instance == null;

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
        All.Remove(this);
        HasToken = false;
        IsAttacking = false;
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
