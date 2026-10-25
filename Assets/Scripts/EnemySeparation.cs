using UnityEngine;
using Pathfinding;

// Враги не проходят сквозь друг друга: они Kinematic, а Kinematic-тела друг с другом физически не
// сталкиваются, поэтому расталкиваем вручную. Если два врага ближе, чем сумма их радиусов, каждый
// получает небольшой толчок от соседа (ai.Move, тот же механизм, что у скольжения вдоль стен в
// EnemyChaseStop) плюс боковую составляющую в сторону своего движения - так идущий враг не упирается
// в соседа лбом, а соскальзывает и обходит его. Если сосед стоит на месте (не может двигаться),
// отдаётся ему меньше: уступает в основном тот, кто идёт.
// Не вмешивается, пока AntiTricker ведёт рывок, и пока canMove выключен (иначе толчок накопился бы
// и выплеснулся разом, см. комментарий в EnemyChaseStop).
[RequireComponent(typeof(AIPath))]
public class EnemySeparation : MonoBehaviour
{
    [Tooltip("Радиус врага для расталкивания: враги сближаются не ближе чем на сумму двух радиусов.")]
    public float radius = 0.45f;

    [Tooltip("Максимальная скорость выталкивания, юнитов/сек.")]
    public float maxPushSpeed = 4f;

    [Tooltip("Насколько сильно толчок уходит вбок, в сторону своего движения (0 - только отталкивание, 1 - равно отталкиванию): это и заставляет обходить соседа.")]
    public float slideBias = 0.9f;

    private AIPath ai;
    private AntiTricker antiTricker;

    void Awake()
    {
        ai = GetComponent<AIPath>();
        antiTricker = GetComponent<AntiTricker>();
    }

    void Update()
    {
        if (!ai.canMove || (antiTricker != null && antiTricker.IsCountering)) return;

        Vector2 self = transform.position;
        Vector2 desired = ai.desiredVelocity;
        Vector2 push = Vector2.zero;

        foreach (var other in CombatAgent.All)
        {
            if (other == null || other.gameObject == gameObject) continue;
            var sep = other.GetComponent<EnemySeparation>();
            float min = radius + (sep != null ? sep.radius : radius);

            Vector2 off = self - (Vector2)other.transform.position;
            float d = off.magnitude;
            if (d >= min) continue;

            Vector2 n = d > 0.001f ? off / d : Random.insideUnitCircle.normalized;
            Vector2 t = new Vector2(-n.y, n.x);
            if (Vector2.Dot(t, desired) < 0f) t = -t;

            // Сосед, который стоит (canMove выключен), сам не уступит - мне достаётся вся уступка.
            bool otherCanYield = other.Ai != null && other.Ai.canMove;
            float share = otherCanYield ? 0.5f : 1f;
            push += (n + t * slideBias) * ((min - d) * share);
        }

        if (push.sqrMagnitude < 0.000001f) return;

        float maxStep = maxPushSpeed * Time.deltaTime;
        if (push.magnitude > maxStep) push = push.normalized * maxStep;
        ai.Move(new Vector3(push.x, push.y, 0f));
    }
}
