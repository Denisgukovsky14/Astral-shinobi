using UnityEngine;
using TMPro;

// Счётчик «+N» в левом верхнем углу для кунаев, реально попавших в игрока - настоящий контакт
// снаряда, в отличие от счётчика только-для-измерения «Could've hit you» из EnemyAttackZone
// (ближний бой). Тот же паттерн «опрос и вспышка», что и в PotentialHitCounterUI, для
// визуальной согласованности.
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
        normalColor = text.color;
        text.text = "Kunai hits: " + Kunai.TotalHits;
        lastShown = Kunai.TotalHits;
    }

    void Update()
    {
        if (Kunai.TotalHits != lastShown)
        {
            lastShown = Kunai.TotalHits;
            text.text = "Kunai hits: " + lastShown;
            text.color = Color.red;
            flashTimer = flashDuration;
        }

        if (flashTimer > 0f)
        {
            flashTimer -= Time.deltaTime;
            if (flashTimer <= 0f) text.color = normalColor;
        }
    }
}
