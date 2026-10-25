using System;
using UnityEngine;

// Здоровье игрока: 1 = 100% хп. Урон задаётся долями от этой единицы (мечник 0.5, кунай 0.3,
// стрела 0.6). Когда хп доходит до нуля, игрок погибает: время останавливается, а DeathScreen
// показывает экран смерти.
public class PlayerHealth : MonoBehaviour
{
    public static bool IsDead { get; private set; }

    public float maxHealth = 1f;

    [Tooltip("Бессмертие для тестов: урон не снимает хп (попадания по-прежнему считаются врагами).")]
    public bool godMode = false;

    [Tooltip("ЗАДЕЛ НА БУДУЩЕЕ, пока нигде не используется: сколько секунд неуязвимости после попадания.")]
    public float invulnerabilityTime = 0f;

    public float Health { get; private set; }
    public event Action Died;

    void Awake()
    {
        IsDead = false;
        Health = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        if (IsDead || godMode || amount <= 0f) return;

        Health = Mathf.Max(0f, Health - amount);
        if (Health > 0f) return;

        IsDead = true;
        Time.timeScale = 0f;
        Died?.Invoke();
    }
}
