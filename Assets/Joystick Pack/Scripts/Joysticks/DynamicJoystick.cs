using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class DynamicJoystick : Joystick
{
    public float MoveThreshold { get { return moveThreshold; } set { moveThreshold = Mathf.Abs(value); } }

    [SerializeField] private float moveThreshold = 1;

    // Не даёт основанию джойстика выйти за рамку самого объекта (рамка = зона джойстика на экране): палец
    // может быть где угодно, но круг остаётся целиком внутри зоны. Основание должно быть привязано к
    // левому нижнему углу (anchors 0,0), как в префабе Dynamic_Movement_Joystick.
    [SerializeField] private bool clampToZone = true;

    private RectTransform zone;

    protected override void Start()
    {
        MoveThreshold = moveThreshold;
        zone = GetComponent<RectTransform>();
        base.Start();
        background.gameObject.SetActive(false);
    }

    private void ClampToZone()
    {
        if (!clampToZone || zone == null) return;
        Vector2 half = background.sizeDelta / 2f;
        Vector2 size = zone.rect.size;
        Vector2 pos = background.anchoredPosition;
        pos.x = Mathf.Clamp(pos.x, half.x, Mathf.Max(half.x, size.x - half.x));
        pos.y = Mathf.Clamp(pos.y, half.y, Mathf.Max(half.y, size.y - half.y));
        background.anchoredPosition = pos;
    }

    public override void OnPointerDown(PointerEventData eventData)
    {
        background.anchoredPosition = ScreenPointToAnchoredPosition(eventData.position);
        ClampToZone();
        background.gameObject.SetActive(true);
        base.OnPointerDown(eventData);
    }

    public override void OnPointerUp(PointerEventData eventData)
    {
        background.gameObject.SetActive(false);
        base.OnPointerUp(eventData);
    }

    protected override void HandleInput(float magnitude, Vector2 normalised, Vector2 radius, Camera cam)
    {
        if (magnitude > moveThreshold)
        {
            Vector2 difference = normalised * (magnitude - moveThreshold) * radius;
            background.anchoredPosition += difference;
            ClampToZone();
        }
        base.HandleInput(magnitude, normalised, radius, cam);
    }
}