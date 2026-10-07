using UnityEngine;

// Невидимая зона «сюда попал бы меч» перед врагом - пока не настоящая атака, а просто
// полукруг (проверка дистанции + скалярное произведение с направлением вперёд, а не реальная
// форма коллайдера), нужный чтобы измерять, как часто игрок получил бы удар, если бы враг
// действительно замахивался - чтобы оценить, насколько легко от него сейчас убежать.
public class EnemyAttackZone : MonoBehaviour
{
    public Transform player;
    public float range = 1.2f;
    public float swingCooldown = 0.8f;

    [Tooltip("Obstacles layer, used only for the wall-blocking check below.")]
    public LayerMask obstacleMask;

    [Tooltip("Obstacles up to this size (either bounds axis) don't block a hit - a sword swing can reach around a column the enemy is standing right next to. Anything bigger (walls) blocks the hit outright - the enemy shouldn't be able to land a hit through a solid wall just because the player is standing close on the other side of it, within this zone's range.")]
    public float maxPassableObstacleSize = 1.5f;

    public int PotentialHitCount { get; private set; }

    private float cooldownTimer;
    private readonly RaycastHit2D[] lineHits = new RaycastHit2D[8];
    private ContactFilter2D obstacleFilter;

    void Awake()
    {
        obstacleFilter = new ContactFilter2D();
        obstacleFilter.SetLayerMask(obstacleMask);
        obstacleFilter.useTriggers = false;
    }

    void Update()
    {
        cooldownTimer -= Time.deltaTime;
        if (cooldownTimer > 0f) return;

        Vector2 toPlayer = (Vector2)player.position - (Vector2)transform.position;
        if (toPlayer.sqrMagnitude > range * range) return;

        // Та же конвенция, что и во всём проекте: «перед смотрит в +Y при повороте 0».
        if (Vector2.Dot(transform.up, toPlayer.normalized) <= 0f) return;

        if (BlockedByWall()) return;

        PotentialHitCount++;
        cooldownTimer = swingCooldown;
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
