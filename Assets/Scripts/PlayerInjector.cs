using UnityEngine;
using Pathfinding;

// Висит на игроке. Перед стартом уровня (Awake с самым ранним порядком) раздаёт ссылки на игрока
// всем врагам на сцене, у которых они не назначены. Нужен потому, что префаб не может хранить
// ссылки на объекты сцены: свежий враг, перетащенный из Prefabs, иначе приходит с пустыми полями.
// Уже назначенные вручную поля не трогает. Враги, появившиеся уже ПОСЛЕ старта (спавн), не
// охватываются - им можно вызвать Inject() вручную.
[DefaultExecutionOrder(-1000)]
public class PlayerInjector : MonoBehaviour
{
    [Tooltip("Триггер-коллайдер игрока для остановки врага по касанию. Если не назначен, ищется дочерний объект TouchZone.")]
    public Collider2D touchZone;

    [Tooltip("Точки прицеливания по телу игрока (голова, плечи). Если не назначены, ищутся дочерние AimHead, AimShoulderLeft, AimShoulderRight.")]
    public Transform[] aimPoints;

    [Header("Границы уровня")]
    [Tooltip("Если включено, всем врагам на сцене выставляются эти границы уровня (EnemyChaseStop.levelBoundsMin/Max: по ним зажимается позиция врага и считаются рикошеты кунайщика). Без этого у врагов остаются границы старого уровня.")]
    public bool overrideEnemyBounds = false;
    public Vector2 levelBoundsMin = new Vector2(-9f, -4.85f);
    public Vector2 levelBoundsMax = new Vector2(9f, 4.85f);

    void Awake()
    {
        if (touchZone == null)
        {
            Transform t = transform.Find("TouchZone");
            if (t != null) touchZone = t.GetComponent<Collider2D>();
        }
        if (aimPoints == null || aimPoints.Length == 0)
        {
            aimPoints = new Transform[]
            {
                transform.Find("AimHead"), transform.Find("AimShoulderLeft"), transform.Find("AimShoulderRight")
            };
        }

        foreach (var k in FindObjectsByType<KunaiFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Inject(k);
        foreach (var a in FindObjectsByType<ArcherFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Inject(a);
        foreach (var s in FindObjectsByType<AntiTricker>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (s.player == null) s.player = transform;
        foreach (var z in FindObjectsByType<EnemyAttackZone>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (z.player == null) z.player = transform;
        foreach (var d in FindObjectsByType<AIDestinationSetter>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (d.target == null) d.target = transform;
        foreach (var d in FindObjectsByType<SmoothedDestinationSetter>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (d.target == null) d.target = transform;
        foreach (var c in FindObjectsByType<EnemyChaseStop>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c.playerTouchZone == null) c.playerTouchZone = touchZone;
            ApplyBounds(c);
        }
    }

    void ApplyBounds(EnemyChaseStop c)
    {
        if (!overrideEnemyBounds) return;
        c.levelBoundsMin = levelBoundsMin;
        c.levelBoundsMax = levelBoundsMax;
    }

    public void Inject(KunaiFighter k)
    {
        if (k.player == null) k.player = transform;
        if (!HasAim(k.playerAimPoints)) k.playerAimPoints = (Transform[])aimPoints.Clone();
        var c = k.GetComponent<EnemyChaseStop>();
        if (c != null && c.playerTouchZone == null) c.playerTouchZone = touchZone;
        if (c != null) ApplyBounds(c);
    }

    public void Inject(ArcherFighter a)
    {
        if (a.player == null) a.player = transform;
        if (!HasAim(a.playerAimPoints)) a.playerAimPoints = (Transform[])aimPoints.Clone();
        var c = a.GetComponent<EnemyChaseStop>();
        if (c != null && c.playerTouchZone == null) c.playerTouchZone = touchZone;
        if (c != null) ApplyBounds(c);
    }

    static bool HasAim(Transform[] pts)
    {
        if (pts == null || pts.Length == 0) return false;
        foreach (var p in pts) if (p == null) return false;
        return true;
    }
}
