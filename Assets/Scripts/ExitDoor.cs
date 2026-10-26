using System;
using UnityEngine;

// Выходная дверь уровня. Стоит в проёме стены; локальный +Y двери смотрит НАРУЖУ из комнаты.
// Закрытая дверь - сплошная стена. Игрок с ключом, подошедший к ней со стороны комнаты (зона «Open»),
// открывает её: дверь перестаёт быть препятствием и меняет спрайт. Войдя в зону «Exit» за открытой
// дверью, игрок уходит на следующий уровень (LevelManager.Complete).
// Дочерние зоны - триггеры с ZoneRelay; их положение и размер правятся прямо в префабе.
public class ExitDoor : MonoBehaviour
{
    [Tooltip("Сплошной коллайдер закрытой двери (на слое Obstacles, чтобы враги и нож считали её стеной).")]
    public Collider2D solid;
    [Tooltip("Зона со стороны комнаты: игрок с ключом, зашедший сюда, открывает дверь.")]
    public ZoneRelay openZone;
    [Tooltip("Зона за дверью: игрок, зашедший сюда при открытой двери, переходит на следующий уровень.")]
    public ZoneRelay exitZone;
    public SpriteRenderer sprite;
    public Sprite closedSprite;
    public Sprite openSprite;

    public bool IsOpen { get; private set; }

    void Awake()
    {
        if (openZone != null) openZone.onEnter = OnOpenZone;
        if (exitZone != null) exitZone.onEnter = OnExitZone;
    }

    void OnOpenZone(Collider2D other)
    {
        if (IsOpen || !IsPlayer(other)) return;
        var level = LevelManager.Instance;
        if (level != null && (level.HasKey || level.NoKeyNeeded)) Open();
    }

    void OnExitZone(Collider2D other)
    {
        if (!IsOpen || !IsPlayer(other)) return;
        if (LevelManager.Instance != null) LevelManager.Instance.Complete();
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        if (solid != null) solid.enabled = false;
        if (sprite != null && openSprite != null) sprite.sprite = openSprite;
        if (LevelManager.Instance != null) LevelManager.Instance.NotifyDoorOpened();
    }

    static bool IsPlayer(Collider2D other)
    {
        return other.GetComponentInParent<PlayerHealth>() != null;
    }
}
