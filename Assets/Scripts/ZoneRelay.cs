using System;
using UnityEngine;

// Пересылает вход игрока в триггер владельцу: у каждой зоны свой дочерний объект с коллайдером.
public class ZoneRelay : MonoBehaviour
{
    public Action<Collider2D> onEnter;

    void OnTriggerEnter2D(Collider2D other)
    {
        onEnter?.Invoke(other);
    }

    void OnTriggerStay2D(Collider2D other)
    {
        // Игрок мог получить ключ, уже стоя в зоне двери.
        onEnter?.Invoke(other);
    }
}
