using UnityEngine;
using TMPro;

// Надпись «Мечник ударил вас: N раз» в левом верхнем углу: считает реальные удары всех мечников
// (EnemyAttackZone.TotalHits; парированные удары не в счёте). При каждом новом ударе вспыхивает красным.
public class PotentialHitCounterUI : MonoBehaviour
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
        EnemyAttackZone.TotalHits = 0;
    }

    void Update()
    {
        int hits = EnemyAttackZone.TotalHits;
        if (hits != lastShown)
        {
            bool first = lastShown < 0;
            lastShown = hits;
            text.text = "Мечник ударил вас: " + hits + " " + RuText.Times(hits);
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
