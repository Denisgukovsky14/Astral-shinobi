using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Значки для кнопок, нарисованные в коде (готовых спрайтов в проекте пока нет): треугольник «играть/заново»
// и крестик «выйти». Спрайты белые, цвет задаётся через Image.color / переходы кнопки.
public static class UIShapes
{
    const int Size = 128;
    static Sprite triangle;
    static Sprite cross;

    public static Sprite Triangle()
    {
        if (triangle == null) triangle = Make(InTriangle);
        return triangle;
    }

    public static Sprite Cross()
    {
        if (cross == null) cross = Make(InCross);
        return cross;
    }

    // Кнопка-значок по центру родителя со смещением pos. Anchor - центр родителя.
    public static Button IconButton(string name, Transform parent, Sprite sprite, Vector2 pos, float size, UnityAction onClick)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(size, size);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        var btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);
        return btn;
    }

    static bool InTriangle(float u, float v)
    {
        // Вершины (0.2, 0.1), (0.2, 0.9), (0.9, 0.5): остриё вправо.
        if (u < 0.2f || u > 0.9f) return false;
        return Mathf.Abs(v - 0.5f) <= 0.4f * (0.9f - u) / 0.7f;
    }

    static bool InCross(float u, float v)
    {
        if (u < 0.15f || u > 0.85f || v < 0.15f || v > 0.85f) return false;
        const float half = 0.075f;
        return Mathf.Abs(u - v) < half * 1.4142f || Mathf.Abs(u + v - 1f) < half * 1.4142f;
    }

    // Рисует фигуру в текстуру 128x128 с простым сглаживанием краёв (3x3 выборки на пиксель).
    static Sprite Make(Func<float, float, bool> inside)
    {
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                        if (inside((x + (sx + 0.5f) / 3f) / Size, (y + (sy + 0.5f) / 3f) / Size)) hit++;
                px[y * Size + x] = new Color32(255, 255, 255, (byte)(hit * 255 / 9));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
