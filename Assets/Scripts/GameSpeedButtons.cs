using UnityEngine;
using UnityEngine.UI;

// Скорость игры для разработчика: общий множитель времени, на который домножают timeScale все, кто
// его меняет (прицеливание, парирование, заморозка старта). Так выбранная кнопками скорость не
// сбивается замедлением при прицеливании и возвращается после парирования.
public static class GameSpeed
{
    public static float Multiplier = 1f;

    // Базовый timeScale с учётом множителя - там, где в игре «обычное» время.
    public static float Scaled(float baseScale) { return baseScale * Multiplier; }

    // Применяет множитель сразу, если игра сейчас идёт: не поверх заморозки старта, мини-игры
    // парирования и смерти (они сами вернут нужную скорость, когда закончатся).
    public static void Set(float multiplier)
    {
        Multiplier = multiplier;
        if (StartFreeze.Waiting || ParrySystem.Active || PlayerHealth.IsDead || LevelManager.Finished) return;
        Time.timeScale = multiplier;
        Time.fixedDeltaTime = 0.02f * multiplier;
    }
}

// Три кнопки 0.5 / 0.75 / 1 у правого края экрана посередине по вертикали: скорость течения времени,
// чтобы разобрать, как по игроку проходят удары и снаряды. Строится в коде под Canvas. Показываются
// только в редакторе и в отладочных сборках (в релизе скрыты).
public class GameSpeedButtons : MonoBehaviour
{
    public float[] speeds = { 0.5f, 0.75f, 1f };
    public Vector2 buttonSize = new Vector2(120f, 90f);
    public float spacing = 14f;
    public float rightMargin = 30f;

    private Image[] images;
    private static readonly Color Normal = new Color(0f, 0f, 0f, 0.55f);
    private static readonly Color Selected = new Color(0.3f, 1f, 0f, 0.85f);

    void Awake()
    {
        if (!Application.isEditor && !Debug.isDebugBuild) return;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        images = new Image[speeds.Length];
        float totalHeight = speeds.Length * buttonSize.y + (speeds.Length - 1) * spacing;

        for (int i = 0; i < speeds.Length; i++)
        {
            float speed = speeds[i];
            var go = new GameObject("SpeedButton_" + speed, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = buttonSize;
            // сверху вниз: 0.5, 0.75, 1 - по центру правого края
            rt.anchoredPosition = new Vector2(-rightMargin, totalHeight * 0.5f - buttonSize.y * 0.5f - i * (buttonSize.y + spacing));

            images[i] = go.GetComponent<Image>();
            go.GetComponent<Button>().onClick.AddListener(() => Choose(speed));

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            labelGo.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)labelGo.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var text = labelGo.GetComponent<Text>();
            text.font = font;
            text.text = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            text.fontSize = 44;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
        }
        Refresh();
    }

    void Choose(float speed)
    {
        GameSpeed.Set(speed);
        Refresh();
    }

    void Refresh()
    {
        for (int i = 0; i < images.Length; i++)
            images[i].color = Mathf.Approximately(speeds[i], GameSpeed.Multiplier) ? Selected : Normal;
    }
}
