using UnityEngine;
using UnityEngine.UI;

// Полоска хп игрока вверху по центру экрана. Висит на Canvas, строится в коде.
public class PlayerHealthBar : MonoBehaviour
{
    public PlayerHealth playerHealth;
    public Vector2 position = new Vector2(0f, -50f);
    public Vector2 size = new Vector2(600f, 40f);
    public Color backColor = new Color(0f, 0f, 0f, 0.6f);
    public Color fillColor = new Color(0.85f, 0.15f, 0.15f, 1f);

    private RectTransform fill;

    void Awake()
    {
        var back = NewImage("HealthBarBack", transform, backColor);
        RectTransform rt = back.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = position;
        rt.sizeDelta = size;

        var f = NewImage("HealthBarFill", rt, fillColor);
        fill = f.rectTransform;
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = Vector2.one;
        fill.offsetMin = new Vector2(4f, 4f);
        fill.offsetMax = new Vector2(-4f, -4f);
    }

    void Update()
    {
        if (playerHealth == null) return;
        float t = playerHealth.maxHealth > 0f ? Mathf.Clamp01(playerHealth.Health / playerHealth.maxHealth) : 0f;
        fill.anchorMax = new Vector2(t, 1f);
    }

    static Image NewImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var im = go.GetComponent<Image>();
        im.color = color;
        im.raycastTarget = false;
        return im;
    }
}
