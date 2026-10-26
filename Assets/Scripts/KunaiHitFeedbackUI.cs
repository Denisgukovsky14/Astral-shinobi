using UnityEngine;
using TMPro;

// Надпись «Кунайщик попал в вас: N раз» в левом верхнем углу: считает кунаи, реально попавшие в игрока
// (Kunai.TotalHits). Тот же принцип «опрос и вспышка», что и в PotentialHitCounterUI.
public class KunaiHitFeedbackUI : MonoBehaviour
{
    public float flashDuration = 0.4f;

    private TextMeshProUGUI text;
    private int lastShown = -1;
    private Color normalColor;
    private float flashTimer;

    void Awake()
    {
        text = GetComponent<TextMeshProUGUI>();
        text.textWrappingMode = TextWrappingModes.NoWrap;
        normalColor = text.color;
        // Статический счётчик переживает перезапуск сцены - на старте уровня обнуляем.
        Kunai.TotalHits = 0;
    }

    void Update()
    {
        int hits = Kunai.TotalHits;
        if (hits != lastShown)
        {
            bool first = lastShown < 0;
            lastShown = hits;
            text.text = "Кунайщик попал в вас: " + hits + " " + RuText.Times(hits);
            if (!first)
            {
                text.color = Color.red;
                flashTimer = flashDuration;
            }
        }

        if (flashTimer > 0f)
        {
            flashTimer -= Time.unscaledDeltaTime;
            if (flashTimer <= 0f) text.color = normalColor;
        }
    }
}
