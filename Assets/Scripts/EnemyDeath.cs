using System.Collections.Generic;
using UnityEngine;
using Pathfinding;

public class EnemyDeath : MonoBehaviour
{
    // Живые враги на сцене (включая дремлющих) и событие смерти: по ним LevelManager понимает, что
    // убит последний враг, и роняет ключ от выходной двери.
    public static readonly List<EnemyDeath> Alive = new List<EnemyDeath>();
    public static event System.Action<Vector3> Died;

    public HealthBar healthBar;
    public SpriteRenderer bodySprite;
    public Sprite deadSprite;
    public Transform sword;

    private bool isDead;

    void OnEnable()
    {
        if (!isDead && !Alive.Contains(this)) Alive.Add(this);
    }

    void OnDisable()
    {
        Alive.Remove(this);
    }

    void Update()
    {
        if (isDead || healthBar.currentHealth > 0f) return;
        isDead = true;
        Alive.Remove(this);

        GetComponent<AIPath>().canMove = false;
        // Мёртвый враг ничего не делает: выключаем все остальные скрипты на нём (бой, бросок,
        // погоню, AntiTricker и т.д.), чтобы он не бил, не стрелял и не двигался. Сам EnemyDeath
        // остаётся, он ничего больше не делает после isDead.
        foreach (var behaviour in GetComponents<MonoBehaviour>())
            if (behaviour != this) behaviour.enabled = false;
        GetComponent<Collider2D>().enabled = false;
        GetComponent<Rigidbody2D>().simulated = false;

        bodySprite.sprite = deadSprite;

        // Сглаживатель Visual каждый кадр тянет поворот обратно к повороту (теперь замершего)
        // корня - отключаем его, чтобы случайный угол падения действительно сохранился.
        VisualRotationSmoother smoother = bodySprite.GetComponent<VisualRotationSmoother>();
        if (smoother != null) smoother.enabled = false;
        bodySprite.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

        // Роняем удерживаемое оружие рядом с телом вместо того, чтобы держать его поднятым -
        // сначала отсоединяем его, чтобы оно перестало наследовать поворот сглаживателя Visual.
        // Необязательно: не у каждого типа врага есть видимое оружие в руке (например, дальнобойный
        // юнит, который просто бросает боеприпасы).
        if (sword != null)
        {
            sword.SetParent(null, true);
            sword.position = transform.position + new Vector3(0.3f, -0.1f, 0f);
            sword.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        }

        healthBar.gameObject.SetActive(false);

        Died?.Invoke(transform.position);
    }
}
